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
/// All mutation endpoints enforce server-side FSM validation and authorization.
/// </summary>
public static class VetoEndpoints
{
    public static void MapVetoEndpoints(this WebApplication app)
    {
        // ── GET /api/veto/{matchId} ──────────────────────────────────────────
        // S4: Require auth — veto state includes team strategy info
        app.MapGet("/api/veto/{matchId}", async (
            Guid            matchId,
            VetoDbService   veto,
            CancellationToken ct) =>
        {
            var state = await veto.GetAsync(matchId, ct);
            if (state is null)
            {
                // Fallback: maybe matchId is actually a veto id
                state = await veto.GetByIdAsync(matchId, ct);
            }
            return state is null ? Results.NotFound() : Results.Ok(state);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/veto/{matchId}/init ────────────────────────────────────
        app.MapPost("/api/veto/{matchId}/init", async (
            Guid                      matchId,
            [FromBody] VetoInitRequest req,
            HttpContext                ctx,
            VetoDbService              veto,
            IHubContext<VetoHub>       hub,
            ILogger<VetoDbService>    logger,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                // Idempotent: if veto already exists, return it
                var existing = await veto.GetAsync(matchId, ct);
                if (existing is not null)
                    return Results.Ok(existing);

                // S2: Only organizer or captain can init
                var isOrg = await veto.IsOrganizerAsync(userCtx.UserIdGuid, req.TournamentId, ct);
                if (!isOrg)
                {
                    bool isCaptain = false;
                    if (req.Team1Id.HasValue)
                        isCaptain = await veto.IsTeamCaptainAsync(userCtx.UserIdGuid, req.Team1Id.Value, ct);
                    if (!isCaptain && req.Team2Id.HasValue)
                        isCaptain = await veto.IsTeamCaptainAsync(userCtx.UserIdGuid, req.Team2Id.Value, ct);
                    if (!isCaptain)
                        return Results.Json(new { error = "FORBIDDEN: only organizer or team captain can init veto" }, statusCode: 403);
                }

                var result = await veto.InitAsync(
                    matchId, req.TournamentId,
                    req.Team1Id, req.Team2Id,
                    req.BestOf, req.Game ?? "valorant", ct);

                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.StateSync, result, ct);

                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Veto init failed for match {MatchId}", matchId);
                return Results.BadRequest(new { error = "Couldn't start the map veto. Please try again." });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Veto init unexpected error for match {MatchId}", matchId);
                // On conflict (duplicate), try to return existing
                var fallback = await veto.GetAsync(matchId, ct);
                if (fallback is not null) return Results.Ok(fallback);
                return Results.Json(new { error = "We couldn't start the map veto. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/veto/{matchId}/ban ─────────────────────────────────────
        // S1+D2: FSM + captain auth enforced in VetoDbService
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
                var result = await veto.BanMapAsync(matchId, req.MapId, userCtx.UserIdGuid, ct);
                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.VetoAction, result, ct);
                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "You don't have permission to perform this veto action." }, statusCode: 403);
            }
            catch (InvalidOperationException ex)
            {
                // Domain exceptions from VetoDbService use CONFLICT prefix for state conflicts
                return ex.Message.StartsWith("CONFLICT")
                    ? Results.Conflict(new { error = "The map veto was updated. Please refresh and try again." })
                    : Results.BadRequest(new { error = "This veto action is not valid right now." });
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
                var result = await veto.PickMapAsync(matchId, req.MapId, userCtx.UserIdGuid, ct);
                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.VetoAction, result, ct);

                if (result.Status == "completed")
                    await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                        .SendAsync(VetoHubEvents.VetoComplete, result, ct);

                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "You don't have permission to perform this veto action." }, statusCode: 403);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.StartsWith("CONFLICT")
                    ? Results.Conflict(new { error = "The map veto was updated. Please refresh and try again." })
                    : Results.BadRequest(new { error = "This veto action is not valid right now." });
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
                var result = await veto.PickSideAsync(matchId, req.MapId, req.Side, userCtx.UserIdGuid, ct);
                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.VetoAction, result, ct);

                if (result.Status == "completed")
                    await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                        .SendAsync(VetoHubEvents.VetoComplete, result, ct);

                return Results.Ok(result);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "You don't have permission to perform this veto action." }, statusCode: 403);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.StartsWith("CONFLICT")
                    ? Results.Conflict(new { error = "The map veto was updated. Please refresh and try again." })
                    : Results.BadRequest(new { error = "This veto action is not valid right now." });
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/veto/{matchId}/reset ───────────────────────────────────
        // S3: Organizer-only
        app.MapPost("/api/veto/{matchId}/reset", async (
            Guid                 matchId,
            HttpContext           ctx,
            VetoDbService         veto,
            IHubContext<VetoHub>  hub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // S3: Verify organizer
            var existing = await veto.GetAsync(matchId, ct);
            if (existing is null) return Results.NotFound();

            if (!await veto.IsOrganizerAsync(userCtx.UserIdGuid, existing.TournamentId, ct))
                return Results.Json(new { error = "FORBIDDEN: only organizer can reset veto" }, statusCode: 403);

            await veto.ResetAsync(matchId, ct);

            await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                .SendAsync(VetoHubEvents.VetoReset, matchId, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/veto/{matchId}/update ───────────────────────────────────
        // S2: Organizer-only (this is a raw state override, not a normal action)
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

            // Resolve veto (matchId may be veto.id or match_id)
            var existing = await veto.GetAsync(matchId, ct)
                ?? await veto.GetByIdAsync(matchId, ct);
            if (existing is null) return Results.NotFound();

            // S2: Only organizer can use raw update
            if (!await veto.IsOrganizerAsync(userCtx.UserIdGuid, existing.TournamentId, ct))
                return Results.Json(new { error = "FORBIDDEN: only organizer can update veto directly" }, statusCode: 403);

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE match_map_vetos SET
                    status                = COALESCE(@Status, status),
                    best_of               = COALESCE(@BestOf, best_of),
                    current_team_id       = COALESCE(@CurrentTeamId, current_team_id),
                    current_action        = COALESCE(@CurrentAction, current_action),
                    current_action_number = COALESCE(@CurrentActionNumber, current_action_number),
                    team1_banned_maps     = COALESCE(@Team1BannedMaps, team1_banned_maps),
                    team2_banned_maps     = COALESCE(@Team2BannedMaps, team2_banned_maps),
                    team1_picked_maps     = COALESCE(@Team1PickedMapsJson::jsonb, team1_picked_maps),
                    team2_picked_maps     = COALESCE(@Team2PickedMapsJson::jsonb, team2_picked_maps),
                    selected_map_id       = COALESCE(@SelectedMapId, selected_map_id),
                    turn_started_at       = COALESCE(@TurnStartedAt, turn_started_at)
                WHERE id = @vetoId
                """,
                new
                {
                    vetoId = existing.Id,
                    req.Status,
                    req.BestOf,
                    req.CurrentTeamId,
                    req.CurrentAction,
                    req.CurrentActionNumber,
                    Team1BannedMaps     = req.Team1BannedMaps,
                    Team2BannedMaps     = req.Team2BannedMaps,
                    Team1PickedMapsJson = req.Team1PickedMaps is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.Team1PickedMaps, Esportra.Core.JsonDefaults.SnakeCase)
                        : (string?)null,
                    Team2PickedMapsJson = req.Team2PickedMaps is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.Team2PickedMaps, Esportra.Core.JsonDefaults.SnakeCase)
                        : (string?)null,
                    req.SelectedMapId,
                    req.TurnStartedAt,
                });

            var state = await veto.GetAsync(existing.MatchId, ct);

            if (state is not null)
            {
                await hub.Clients.Group(VetoHub.VetoGroup(state.MatchId.ToString()))
                    .SendAsync(VetoHubEvents.StateSync, (object)state, ct);
            }

            return state is null ? Results.NotFound() : Results.Ok(state);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/veto/token/{token} ──────────────────────────────────────
        // S5: Token-based access uses cryptographic tokens (not UUIDs)
        app.MapGet("/api/veto/token/{token}", async (
            string               token,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            // S5: Look up by cryptographic link token (not by match_id/team2_id)
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
                WHERE mmv.team1_link_token = @token OR mmv.team2_link_token = @token
                """,
                new { token });

            if (row is null) return Results.NotFound(new { error = "Map veto not found." });

            // Determine which team this token belongs to
            string? teamSide = row.team1_link_token == token ? "team1" : "team2";

            return Results.Ok(new
            {
                match_id         = row.match_id,
                tournament_id    = row.tournament_id,
                team1_id         = row.team1_id,
                team2_id         = row.team2_id,
                best_of          = row.best_of,
                status           = row.status,
                stage_id         = (object?)null,
                team_side        = teamSide,
                team1_link_token = (string?)row.team1_link_token,
                team2_link_token = (string?)row.team2_link_token,
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
    Guid?        SelectedMapId       = null,
    string?      TurnStartedAt       = null);
