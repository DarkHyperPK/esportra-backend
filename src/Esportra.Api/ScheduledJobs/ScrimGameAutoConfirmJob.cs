using Dapper;
using Esportra.Api.Endpoints;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Confirms a reported scrim map the other team never answered, so a team that goes quiet cannot stall
/// the series. A disputed map is never touched: that is a real conflict, not a missing answer.
/// </summary>
public sealed class ScrimGameAutoConfirmJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> hub,
    ILogger<ScrimGameAutoConfirmJob> logger)
{
    private const int SweepBatch = 50;

    private sealed record DueGame
    {
        public Guid SessionId { get; init; }
        public int GameNumber { get; init; }
    }

    private sealed record GameScores
    {
        public int T1 { get; init; }
        public int T2 { get; init; }
    }

    private sealed record SessionInfo
    {
        public Guid Team1Id { get; init; }
        public Guid Team2Id { get; init; }
        public string? Team1Name { get; init; }
        public string? Team2Name { get; init; }
        public int BestOf { get; init; }
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var due = (await conn.QueryAsync<DueGame>(
            """
            SELECT g.session_id AS SessionId, g.game_number AS GameNumber
            FROM public.scrim_session_games g
            JOIN public.scrim_sessions s ON s.id = g.session_id
            WHERE g.status = 'reported' AND g.accept_by <= now() AND s.status = 'in_progress'
            ORDER BY g.accept_by
            LIMIT @batch
            """,
            new { batch = SweepBatch })).ToList();

        foreach (var game in due)
        {
            try { await ConfirmAsync(conn, game, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[ScrimGameAutoConfirm] Could not confirm game {Game} of {Session}.", game.GameNumber, game.SessionId);
            }
        }
    }

    private async Task ConfirmAsync(System.Data.IDbConnection conn, DueGame game, CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        // Re-check under the row lock: the other team may have answered since the sweep read it.
        var locked = await conn.QuerySingleOrDefaultAsync<GameScores>(
            """
            SELECT team1_score AS T1, team2_score AS T2 FROM public.scrim_session_games
            WHERE session_id = @SessionId AND game_number = @GameNumber AND status = 'reported' AND accept_by <= now()
            FOR UPDATE
            """,
            game, tx);
        if (locked is null) return;

        await conn.ExecuteAsync(
            """
            UPDATE public.scrim_session_games
            SET status = 'confirmed', winner_slot = @winner, auto_confirmed = TRUE, confirmed_at = now(), updated_at = now()
            WHERE session_id = @SessionId AND game_number = @GameNumber
            """,
            new { game.SessionId, game.GameNumber, winner = ScrimSeries.WinnerSlot(locked.T1, locked.T2) }, tx);

        var session = await conn.QuerySingleAsync<SessionInfo>(
            """
            SELECT ss.team1_id AS Team1Id, ss.team2_id AS Team2Id, t1.name AS Team1Name, t2.name AS Team2Name,
                   COALESCE(ss.best_of, 1) AS BestOf
            FROM public.scrim_sessions ss
            JOIN public.teams t1 ON t1.id = ss.team1_id
            JOIN public.teams t2 ON t2.id = ss.team2_id
            WHERE ss.id = @SessionId
            """,
            game, tx);
        var (t1, t2, decided) = await ScrimGameEndpoints.SettleSeriesAsync(conn, tx, game.SessionId, session.BestOf);
        await conn.ExecuteAsync(
            "UPDATE public.scrim_sessions SET result_reported_by_team1 = FALSE, result_reported_by_team2 = FALSE WHERE id = @SessionId",
            game, tx);
        tx.Commit();

        var message = decided
            ? $"Series complete: {session.Team1Name} {t1}–{t2} {session.Team2Name}."
            : $"Game {game.GameNumber} was confirmed automatically — no answer in time. Series: {t1}–{t2}.";
        foreach (var team in new[] { session.Team1Id, session.Team2Id })
            await ScrimTeamNotifier.SendAsync(conn, hub, team, game.SessionId, "scrim_result_confirmed",
                decided ? "Series complete" : "Map confirmed automatically", message);
    }
}
