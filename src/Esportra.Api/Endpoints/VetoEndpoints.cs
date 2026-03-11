using Esportra.Contracts.Auth;
using Esportra.Core.Match;
using Esportra.Api.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// REST endpoints for map veto operations.
/// Wraps VetoDbService + broadcasts to VetoHub group after each mutation.
/// Replaces client-side supabase.rpc/insert calls in useMapVetoMachine.ts.
/// </summary>
public static class VetoEndpoints
{
    public static void MapVetoEndpoints(this WebApplication app)
    {
        // ── GET /api/veto/{matchId} ──────────────────────────────────────────
        app.MapGet("/api/veto/{matchId}", async (
            Guid            matchId,
            VetoDbService   veto,
            CancellationToken ct) =>
        {
            var state = await veto.GetAsync(matchId, ct);
            return state is null ? Results.NotFound() : Results.Ok(state);
        });

        // ── POST /api/veto/{matchId}/init ────────────────────────────────────
        app.MapPost("/api/veto/{matchId}/init", async (
            Guid                      matchId,
            [FromBody] VetoInitRequest req,
            HttpContext                ctx,
            VetoDbService              veto,
            IHubContext<VetoHub>       hub,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var result = await veto.InitAsync(
                matchId, req.TournamentId,
                req.Team1Id, req.Team2Id,
                req.BestOf, req.Game ?? "valorant", ct);

            await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                .SendAsync(VetoHubEvents.StateSync, result, ct);

            return Results.Ok(result);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/veto/{matchId}/ban ─────────────────────────────────────
        app.MapPost("/api/veto/{matchId}/ban", async (
            Guid                      matchId,
            [FromBody] VetoActionRequest req,
            HttpContext                ctx,
            VetoDbService              veto,
            IHubContext<VetoHub>       hub,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                var result = await veto.BanMapAsync(matchId, req.MapId, userCtx.UserId, ct);
                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.VetoAction, result, ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/veto/{matchId}/pick ────────────────────────────────────
        app.MapPost("/api/veto/{matchId}/pick", async (
            Guid                      matchId,
            [FromBody] VetoActionRequest req,
            HttpContext                ctx,
            VetoDbService              veto,
            IHubContext<VetoHub>       hub,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                var result = await veto.PickMapAsync(matchId, req.MapId, userCtx.UserId, ct);
                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.VetoAction, result, ct);

                if (result.Status == "completed")
                    await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                        .SendAsync(VetoHubEvents.VetoComplete, result, ct);

                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/veto/{matchId}/pick-side ───────────────────────────────
        app.MapPost("/api/veto/{matchId}/pick-side", async (
            Guid                         matchId,
            [FromBody] VetoPickSideRequest req,
            HttpContext                   ctx,
            VetoDbService                 veto,
            IHubContext<VetoHub>          hub,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                var result = await veto.PickSideAsync(matchId, req.MapId, req.Side, userCtx.UserId, ct);
                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.VetoAction, result, ct);

                if (result.Status == "completed")
                    await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                        .SendAsync(VetoHubEvents.VetoComplete, result, ct);

                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/veto/{matchId}/reset ───────────────────────────────────
        app.MapPost("/api/veto/{matchId}/reset", async (
            Guid                 matchId,
            HttpContext           ctx,
            VetoDbService         veto,
            IHubContext<VetoHub>  hub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            await veto.ResetAsync(matchId, ct);

            await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                .SendAsync(VetoHubEvents.VetoReset, matchId, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }
}

public sealed record VetoInitRequest(
    Guid    TournamentId,
    Guid?   Team1Id,
    Guid?   Team2Id,
    int     BestOf,
    string? Game = "valorant");

public sealed record VetoActionRequest(string MapId);

public sealed record VetoPickSideRequest(string MapId, string Side);
