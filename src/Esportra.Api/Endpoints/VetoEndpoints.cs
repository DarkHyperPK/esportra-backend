using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
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

        // ── PUT /api/veto/{matchId}/update ───────────────────────────────────────
        app.MapPut("/api/veto/{matchId}/update", async (
            Guid                        matchId,
            [FromBody] VetoUpdateRequest req,
            HttpContext                  ctx,
            IDbConnectionFactory         db,
            VetoDbService                veto,
            IHubContext<VetoHub>         hub,
            CancellationToken           ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE match_map_vetos SET
                    status                = COALESCE(@Status, status),
                    best_of               = COALESCE(@BestOf, best_of),
                    current_team_id       = @CurrentTeamId,
                    current_action        = @CurrentAction,
                    current_action_number = COALESCE(@CurrentActionNumber, current_action_number),
                    team1_banned_maps     = @Team1BannedMaps,
                    team2_banned_maps     = @Team2BannedMaps,
                    team1_picked_maps     = @Team1PickedMapsJson::jsonb,
                    team2_picked_maps     = @Team2PickedMapsJson::jsonb,
                    selected_map_id       = @SelectedMapId
                WHERE match_id = @matchId
                """,
                new
                {
                    matchId,
                    req.Status,
                    req.BestOf,
                    req.CurrentTeamId,
                    req.CurrentAction,
                    req.CurrentActionNumber,
                    Team1BannedMaps     = req.Team1BannedMaps ?? [],
                    Team2BannedMaps     = req.Team2BannedMaps ?? [],
                    Team1PickedMapsJson = System.Text.Json.JsonSerializer.Serialize(req.Team1PickedMaps ?? []),
                    Team2PickedMapsJson = System.Text.Json.JsonSerializer.Serialize(req.Team2PickedMaps ?? []),
                    req.SelectedMapId,
                });

            var state = await veto.GetAsync(matchId, ct);

            if (state is not null)
                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.StateSync, (object)state, ct);

            return state is null ? Results.NotFound() : Results.Ok(state);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/veto/token/{token} ──────────────────────────────────────
        // Token-based access for shareable team links (no auth required).
        // team1 token = matchId string, team2 token = team2Id string.
        app.MapGet("/api/veto/token/{token}", async (
            string               token,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            if (!Guid.TryParse(token, out var tokenGuid))
                return Results.BadRequest(new { error = "Invalid token format" });

            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT mmv.*,
                       m.status     AS match_status,
                       m.best_of    AS match_best_of,
                       t.game       AS tournament_game,
                       team1.id     AS t1_id,
                       team1.name   AS t1_name,
                       team2.id     AS t2_id,
                       team2.name   AS t2_name
                FROM match_map_vetos mmv
                JOIN brkt_matches m  ON m.id  = mmv.match_id
                JOIN tournaments  t  ON t.id  = mmv.tournament_id
                LEFT JOIN teams team1 ON team1.id = mmv.team1_id
                LEFT JOIN teams team2 ON team2.id = mmv.team2_id
                WHERE mmv.match_id = @tokenGuid OR mmv.team2_id = @tokenGuid
                """,
                new { tokenGuid });

            if (row is null) return Results.NotFound(new { error = "Veto session not found" });

            string? matchIdStr = row.match_id?.ToString();
            string? team2IdStr = row.team2_id?.ToString();

            return Results.Ok(new
            {
                match_id         = row.match_id,
                tournament_id    = row.tournament_id,
                team1_id         = row.team1_id,
                team2_id         = row.team2_id,
                best_of          = row.best_of,
                status           = row.status,
                stage_id         = (object?)null,
                team1_link_token = matchIdStr,
                team2_link_token = team2IdStr,
                match = new
                {
                    status  = row.match_status,
                    best_of = row.match_best_of,
                    team1   = new { id = row.t1_id, name = row.t1_name },
                    team2   = new { id = row.t2_id, name = row.t2_name },
                },
                tournament = new { game = row.tournament_game },
            });
        });
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

public sealed record VetoUpdateRequest(
    string?      Status              = null,
    int?         BestOf              = null,
    Guid?        CurrentTeamId       = null,
    string?      CurrentAction       = null,
    int?         CurrentActionNumber = null,
    string[]?    Team1BannedMaps     = null,
    string[]?    Team2BannedMaps     = null,
    PickedMap[]? Team1PickedMaps     = null,
    PickedMap[]? Team2PickedMaps     = null,
    Guid?        SelectedMapId       = null);
