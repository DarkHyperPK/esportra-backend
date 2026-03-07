using Esportra.Contracts.Requests;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: process-match-result, verify-match-result, scan-recent-matches Edge Functions.
///
/// Phase 1 status:
///   - scan-recent-matches  → STUB (Phase 2: requires Riot API + full match verification logic)
///   - process-match-result → STUB (Phase 2: requires Riot API + team PUUID mapping)
///   - finalize-match-result → STUB (delegates to finalize_match_locked RPC)
/// </summary>
public static class MatchEndpoints
{
    public static void MapMatchEndpoints(this WebApplication app)
    {
        // ── POST /api/matches/scan ────────────────────────────────────────────
        // Replaces: scan-recent-matches Edge Function
        // TODO Phase 2: Implement Riot API scan + PUUID team mapping
        app.MapPost("/api/matches/scan", async (
            [FromBody] ScanRecentMatchesRequest req,
            IDbConnectionFactory               db,
            CancellationToken                  ct) =>
        {
            // Phase 2 stub — still served by Supabase Edge Function during migration
            await Task.CompletedTask;
            return Results.Ok(new
            {
                migrated  = false,
                message   = "scan-recent-matches pending Phase 2 migration (Riot API integration)",
                matchId   = req.MatchId,
                mapName   = req.MapName,
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/process ───────────────────────────────
        // Replaces: process-match-result Edge Function
        // TODO Phase 2: Implement full match result processing with Riot API verification
        app.MapPost("/api/matches/{matchId}/process", async (
            string                         matchId,
            [FromBody] ProcessMatchResultRequest req,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            // Phase 2 stub — most complex function, requires:
            //   1. Riot API team PUUID verification
            //   2. Score extraction + winner determination
            //   3. MVP detection
            //   4. brkt_match_games update
            //   5. finalize_match_locked RPC call on series completion
            //   6. Notifications to both captains
            await Task.CompletedTask;
            return Results.Ok(new
            {
                migrated = false,
                message  = "process-match-result pending Phase 2 migration (Riot API integration)",
                matchId,
                reportId = req.ReportId,
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/finalize ──────────────────────────────
        // Replaces: finalize-match-result (called internally by process-match-result)
        // This delegates to the finalize_match_locked Supabase RPC during migration.
        app.MapPost("/api/matches/{matchId}/finalize", async (
            string               matchId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            // Call the existing finalize_match_locked RPC directly via DB
            // This RPC handles optimistic locking and bracket advancement trigger
            var result = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn,
                "SELECT public.finalize_match_locked(@matchId) AS result",
                new { matchId });

            return Results.Ok(new { success = true, matchId, result });
        }).RequireAuthorization("Authenticated");
    }
}
