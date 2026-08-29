using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Sweeps open/published/check_in/ongoing tournaments every 5 minutes and advances
/// status based on start_date / end_date so tournaments nobody is actively fetching
/// still progress correctly (M7 audit finding).
/// </summary>
[Queue("default")]
[DisableConcurrentExecution(timeoutInSeconds: 60)]
public sealed class TournamentStatusReconciliationJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> notifHub,
    TournamentWinnerService winnerService,
    PlacementResolutionService placementResolution,
    ILogger<TournamentStatusReconciliationJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        await ((System.Data.Common.DbConnection)conn).OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        var startedIds = (await conn.QueryAsync<Guid>(
            """
            UPDATE tournaments
            SET status = 'ongoing'::tournament_status, updated_at = NOW()
            WHERE status IN ('open', 'published', 'check_in')
              AND start_date IS NOT NULL AND start_date <= @now
            RETURNING id
            """,
            new { now }, tx)).AsList();

        var completedIds = (await conn.QueryAsync<Guid>(
            """
            UPDATE tournaments
            SET status = 'completed'::tournament_status, updated_at = NOW()
            WHERE status = 'ongoing'
              AND end_date IS NOT NULL AND end_date <= @now
            RETURNING id
            """,
            new { now }, tx)).AsList();

        tx.Commit();

        if (startedIds.Count > 0)
            logger.LogInformation("[TournamentStatusReconciliation] Transitioned {Count} tournament(s) to ongoing.", startedIds.Count);

        if (completedIds.Count > 0)
            logger.LogInformation("[TournamentStatusReconciliation] Transitioned {Count} tournament(s) to completed.", completedIds.Count);

        foreach (var tournamentId in startedIds)
        {
            await notifHub.Clients.All.SendAsync("TournamentStatusChanged",
                new { tournamentId, status = "ongoing" }, ct);
        }

        foreach (var tournamentId in completedIds)
        {
            await ResolveWinnerAndPlacementsAsync(conn, tournamentId, ct);
            await notifHub.Clients.All.SendAsync("TournamentStatusChanged",
                new { tournamentId, status = "completed" }, ct);
        }
    }

    private async Task ResolveWinnerAndPlacementsAsync(
        System.Data.IDbConnection conn, Guid tournamentId, CancellationToken ct)
    {
        try
        {
            var winnerId = await ResolveCompletionWinnerAsync(conn, tournamentId);
            if (winnerId.HasValue)
            {
                using var txWinner = conn.BeginTransaction();
                await winnerService.SetWinnerAsync(conn, txWinner, tournamentId, winnerId.Value,
                    reason: "auto-completed by reconciliation job", ct);
                txWinner.Commit();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[TournamentStatusReconciliation] Winner resolution failed for tournament {Id}.", tournamentId);
        }

        try
        {
            await placementResolution.ResolveAsync(tournamentId, force: false, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[TournamentStatusReconciliation] Placement resolution failed for tournament {Id}.", tournamentId);
        }
    }

    private static async Task<Guid?> ResolveCompletionWinnerAsync(
        System.Data.IDbConnection conn, Guid tournamentId)
    {
        var stageCounts = await conn.QuerySingleAsync<dynamic>(
            """
            SELECT
                COUNT(*)::int AS total,
                COUNT(*) FILTER (WHERE status = 'completed')::int AS completed
            FROM tournament_stages
            WHERE tournament_id = @tournamentId
            """,
            new { tournamentId });

        int stageTotal = (int)stageCounts.total;
        int stageCompleted = (int)stageCounts.completed;
        if (stageTotal == 0 || stageTotal != stageCompleted) return null;

        return await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT m.winner_id
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            JOIN tournament_stages s ON s.id = v.stage_id
            WHERE s.tournament_id = @tournamentId
              AND s.stage_order = (
                  SELECT MAX(stage_order) FROM tournament_stages WHERE tournament_id = @tournamentId
              )
              AND m.status = 'completed'
              AND m.winner_id IS NOT NULL
              AND m.bracket_type = 'final'
            ORDER BY m.round_index DESC, m.match_number DESC
            LIMIT 1
            """,
            new { tournamentId });
    }
}
