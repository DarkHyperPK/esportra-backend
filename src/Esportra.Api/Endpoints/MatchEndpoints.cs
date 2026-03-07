using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Core.Match;
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

        // ── Veto endpoints (Phase 2) ──────────────────────────────────────────

        // GET /api/veto/{matchId}
        app.MapGet("/api/veto/{matchId}", async (
            string         matchId,
            VetoDbService  vetoSvc,
            CancellationToken ct) =>
        {
            var veto = await vetoSvc.GetAsync(matchId, ct);
            return veto is null ? Results.NotFound() : Results.Ok(veto);
        }).RequireAuthorization("Authenticated");

        // POST /api/veto/{matchId}/init  (organizer)
        app.MapPost("/api/veto/{matchId}/init", async (
            string              matchId,
            [FromBody] VetoInitRequest req,
            HttpContext         ctx,
            VetoDbService       vetoSvc,
            CancellationToken   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var veto = await vetoSvc.InitAsync(
                matchId, req.TournamentId,
                req.Team1Id, req.Team2Id,
                req.BestOf, req.Game, ct);

            return Results.Ok(veto);
        }).RequireAuthorization("Organizer");

        // POST /api/veto/{matchId}/ban
        app.MapPost("/api/veto/{matchId}/ban", async (
            string             matchId,
            [FromBody] VetoBanRequest req,
            HttpContext        ctx,
            VetoDbService      vetoSvc,
            CancellationToken  ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var veto = await vetoSvc.GetAsync(matchId, ct);
            if (veto is null) return Results.NotFound();

            var state  = VetoEngine.DeriveState(veto);
            var result = VetoEngine.ValidateTransition(state, VetoEvent.BanMap,
                new TurnContext(veto.CurrentTeamId, userCtx.Roles.Contains("organizer"),
                    userCtx.UserId, true),
                req.MapId, veto);

            if (!result.Ok) return Results.BadRequest(new { error = result.Reason });

            var updated = await vetoSvc.BanMapAsync(matchId, req.MapId, userCtx.UserId, ct);
            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // POST /api/veto/{matchId}/pick
        app.MapPost("/api/veto/{matchId}/pick", async (
            string              matchId,
            [FromBody] VetoPickRequest req,
            HttpContext         ctx,
            VetoDbService       vetoSvc,
            CancellationToken   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var veto = await vetoSvc.GetAsync(matchId, ct);
            if (veto is null) return Results.NotFound();

            var state  = VetoEngine.DeriveState(veto);
            var result = VetoEngine.ValidateTransition(state, VetoEvent.PickMap,
                new TurnContext(veto.CurrentTeamId, userCtx.Roles.Contains("organizer"),
                    userCtx.UserId, true),
                req.MapId, veto);

            if (!result.Ok) return Results.BadRequest(new { error = result.Reason });

            var updated = await vetoSvc.PickMapAsync(matchId, req.MapId, userCtx.UserId, ct);
            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // POST /api/veto/{matchId}/pick-side
        app.MapPost("/api/veto/{matchId}/pick-side", async (
            string                  matchId,
            [FromBody] VetoPickSideRequest req,
            HttpContext             ctx,
            VetoDbService           vetoSvc,
            CancellationToken       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var veto = await vetoSvc.GetAsync(matchId, ct);
            if (veto is null) return Results.NotFound();

            var state  = VetoEngine.DeriveState(veto);
            var result = VetoEngine.ValidateTransition(state, VetoEvent.PickSide,
                new TurnContext(veto.CurrentTeamId, userCtx.Roles.Contains("organizer"),
                    userCtx.UserId, true),
                req.MapId, veto);

            if (!result.Ok) return Results.BadRequest(new { error = result.Reason });

            var updated = await vetoSvc.PickSideAsync(matchId, req.MapId, req.Side, userCtx.UserId, ct);
            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // POST /api/veto/{matchId}/reset  (organizer only)
        app.MapPost("/api/veto/{matchId}/reset", async (
            string         matchId,
            HttpContext    ctx,
            VetoDbService  vetoSvc,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null || !userCtx.Roles.Contains("organizer"))
                return Results.Forbid();

            await vetoSvc.ResetAsync(matchId, ct);
            return Results.Ok(new { message = "Veto reset." });
        }).RequireAuthorization("Organizer");
    }
}
