using System.Data;
using Dapper;
using Esportra.Api.ScheduledJobs;

namespace Esportra.Api.Services.ProviderMatches;

/// <summary>
/// Called when a scrim or tournament result that names a Riot match is saved. It never fails the report: analytics
/// are an addition to reporting, so any error here is logged and the report goes through.
/// </summary>
public static class ProviderMatchIntake
{
    public static async Task TryScrimGameAsync(
        IDbConnection conn, IProviderMatchIngestTrigger trigger, ILogger logger, Guid sessionId, int gameNumber,
        Guid team1Id, Guid team2Id, string? riotMatchId, int team1Score, int team2Score, string? mapName, Guid? actor)
    {
        if (string.IsNullOrWhiteSpace(riotMatchId)) return;
        try
        {
            var gameId = await conn.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM public.scrim_session_games WHERE session_id = @sessionId AND game_number = @gameNumber",
                new { sessionId, gameNumber });
            if (gameId is null) return;
            await RegisterAsync(conn, trigger, new ProviderMatchRegistration(
                ProviderMatchContext.ScrimGame, gameId.Value, null, gameNumber, riotMatchId.Trim(),
                team1Id, team2Id, team1Score, team2Score, mapName, actor));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not register Riot match for scrim session {SessionId} game {Game}", sessionId, gameNumber);
        }
    }

    public static async Task TryTournamentGameAsync(
        IDbConnection conn, IProviderMatchIngestTrigger trigger, ILogger logger, Guid matchId, int gameNumber,
        string? riotMatchId, int team1Score, int team2Score, string? mapName, Guid? actor)
    {
        if (string.IsNullOrWhiteSpace(riotMatchId)) return;
        try
        {
            var teams = await conn.QuerySingleOrDefaultAsync<(Guid? Team1, Guid? Team2)>(
                "SELECT team1_id, team2_id FROM public.brkt_matches WHERE id = @matchId", new { matchId });
            if (teams.Team1 is null || teams.Team2 is null) return;
            await RegisterAsync(conn, trigger, new ProviderMatchRegistration(
                ProviderMatchContext.TournamentGame, ProviderMatchRegistry.TournamentGameContextId(matchId, gameNumber), matchId, gameNumber,
                riotMatchId.Trim(), teams.Team1.Value, teams.Team2.Value, team1Score, team2Score, mapName, actor));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not register Riot match for tournament match {MatchId} game {Game}", matchId, gameNumber);
        }
    }

    public static async Task RegisterAsync(IDbConnection conn, IProviderMatchIngestTrigger trigger, ProviderMatchRegistration registration)
    {
        var result = await ProviderMatchRegistry.RegisterAsync(conn, registration);
        if (result is { NeedsIngest: true }) trigger.Queue(result.AssociationId);
    }
}
