using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Core.Match;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: process-match-result, verify-match-result, scan-recent-matches Edge Functions.
///
/// Phase 3 additions: veto actions now broadcast to VetoHub group after each state change.
/// </summary>
public static class MatchEndpoints
{
    public static void MapMatchEndpoints(this WebApplication app)
    {
        // ── POST /api/matches/scan ────────────────────────────────────────────
        app.MapPost("/api/matches/scan", async (
            [FromBody] ScanRecentMatchesRequest req,
            IDbConnectionFactory               db,
            CancellationToken                  ct) =>
        {
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
        app.MapPost("/api/matches/{matchId}/process", async (
            string                         matchId,
            [FromBody] ProcessMatchResultRequest req,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
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
        app.MapPost("/api/matches/{matchId}/finalize", async (
            string               matchId,
            IDbConnectionFactory db,
            IHubContext<MatchHub> matchHub,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            var result = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn,
                "SELECT public.finalize_match_locked(@matchId) AS result",
                new { matchId });

            // Notify match group that status changed
            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, status = "completed", result },
                    ct);

            return Results.Ok(new { success = true, matchId, result });
        }).RequireAuthorization("Authenticated");

        // ── Veto endpoints ────────────────────────────────────────────────────

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
            IHubContext<VetoHub> vetoHub,
            CancellationToken   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var veto = await vetoSvc.InitAsync(
                matchId, req.TournamentId,
                req.Team1Id, req.Team2Id,
                req.BestOf, req.Game, ct);

            await vetoHub.Clients
                .Group(VetoHub.VetoGroup(matchId))
                .SendAsync(VetoHubEvents.VetoAction, veto, ct);

            return Results.Ok(veto);
        }).RequireAuthorization("Organizer");

        // POST /api/veto/{matchId}/ban
        app.MapPost("/api/veto/{matchId}/ban", async (
            string             matchId,
            [FromBody] VetoBanRequest req,
            HttpContext        ctx,
            VetoDbService      vetoSvc,
            IHubContext<VetoHub> vetoHub,
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

            await vetoHub.Clients
                .Group(VetoHub.VetoGroup(matchId))
                .SendAsync(VetoHubEvents.VetoAction, updated, ct);

            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // POST /api/veto/{matchId}/pick
        app.MapPost("/api/veto/{matchId}/pick", async (
            string              matchId,
            [FromBody] VetoPickRequest req,
            HttpContext         ctx,
            VetoDbService       vetoSvc,
            IHubContext<VetoHub> vetoHub,
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

            // Check if veto is now complete
            var newState = VetoEngine.DeriveState(updated);
            if (newState == VetoState.Complete)
            {
                await vetoHub.Clients
                    .Group(VetoHub.VetoGroup(matchId))
                    .SendAsync(VetoHubEvents.VetoComplete, updated.Team1PickedMaps, ct);
            }
            else
            {
                await vetoHub.Clients
                    .Group(VetoHub.VetoGroup(matchId))
                    .SendAsync(VetoHubEvents.VetoAction, updated, ct);
            }

            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // POST /api/veto/{matchId}/pick-side
        app.MapPost("/api/veto/{matchId}/pick-side", async (
            string                  matchId,
            [FromBody] VetoPickSideRequest req,
            HttpContext             ctx,
            VetoDbService           vetoSvc,
            IHubContext<VetoHub>    vetoHub,
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

            await vetoHub.Clients
                .Group(VetoHub.VetoGroup(matchId))
                .SendAsync(VetoHubEvents.VetoAction, updated, ct);

            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // POST /api/veto/{matchId}/reset  (organizer only)
        app.MapPost("/api/veto/{matchId}/reset", async (
            string              matchId,
            HttpContext         ctx,
            VetoDbService       vetoSvc,
            IHubContext<VetoHub> vetoHub,
            CancellationToken   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null || !userCtx.Roles.Contains("organizer"))
                return Results.Forbid();

            await vetoSvc.ResetAsync(matchId, ct);

            await vetoHub.Clients
                .Group(VetoHub.VetoGroup(matchId))
                .SendAsync(VetoHubEvents.VetoReset, new { matchId }, ct);

            return Results.Ok(new { message = "Veto reset." });
        }).RequireAuthorization("Organizer");
    }
}
