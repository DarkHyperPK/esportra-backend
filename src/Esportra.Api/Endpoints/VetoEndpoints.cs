using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Match;
using Esportra.Infrastructure.Integrations;
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

        // ── GET /api/veto/{matchId}/history ────────────────────────────────────
        app.MapGet("/api/veto/{matchId}/history", async (
            Guid            matchId,
            VetoDbService   veto,
            IConfiguration  config,
            CancellationToken ct) =>
        {
            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/');
            var history = await veto.GetEnrichedHistoryAsync(matchId, ct);
            var enriched = history
                .Select(entry => entry with
                {
                    MapImageUrl = Esportra.Core.Games.R6MapCatalog.ResolveImageUrl(
                        Esportra.Core.Games.R6MapCatalog.GameName,
                        entry.MapName,
                        entry.MapImageUrl,
                        supabaseUrl),
                })
                .ToList();
            return Results.Ok(enriched);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/veto/{matchId}/init ────────────────────────────────────
        app.MapPost("/api/veto/{matchId}/init", async (
            Guid                      matchId,
            [FromBody] VetoInitRequest req,
            HttpContext                ctx,
            VetoDbService              veto,
            IDbConnectionFactory       db,
            GameCatalogService         gameCatalog,
            IHubContext<VetoHub>       hub,
            ILogger<VetoDbService>    logger,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                var existing = await veto.GetAsync(matchId, ct);
                if (existing is not null)
                {
                    var needsRestart =
                        string.Equals(existing.Status, "pending", StringComparison.OrdinalIgnoreCase)
                        && existing.CurrentTeamId is null
                        && existing.CurrentActionNumber == 0;

                    if (!needsRestart)
                        return Results.Ok(existing);
                }

                using var conn = db.CreateConnection();
                var tournament = await conn.QuerySingleOrDefaultAsync<TournamentVetoGateRow>(
                    """
                    SELECT game,
                           game_mode AS GameMode,
                           team_size AS TeamSize,
                           settings::text AS SettingsJson
                    FROM public.tournaments
                    WHERE id = @tournamentId
                    """,
                    new { tournamentId = req.TournamentId });

                if (tournament is null)
                    return Results.NotFound(new { error = "Tournament was not found." });

                var supportsMapVeto = await gameCatalog.SupportsMapVetoAsync(
                    tournament.Game,
                    tournament.GameMode,
                    tournament.TeamSize,
                    conn);

                if (!supportsMapVeto || IsMapVetoDisabled(tournament.SettingsJson))
                {
                    return Results.BadRequest(new
                    {
                        error = "Map veto is not enabled for this tournament."
                    });
                }

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
                await BroadcastHistoryAsync(hub, veto, matchId, ct);

                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Veto init failed for match {MatchId}", matchId);
                return Results.BadRequest(new { error = "Couldn't start the map veto. Please try again." });
            }
            catch (GameCatalogValidationException ex)
            {
                logger.LogWarning(ex, "Veto init catalog validation failed for match {MatchId}", matchId);
                return Results.BadRequest(new { error = ex.Message });
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
                await BroadcastHistoryAsync(hub, veto, matchId, ct);
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
            IDbConnectionFactory       db,
            IDatHostService            dathost,
            IHubContext<MatchHub>      matchHub,
            IConfiguration             config,
            ILogger<VetoDbService>     vetoLogger,
            ILogger<DatHostService>    serverLogger,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                var result = await veto.PickMapAsync(matchId, req.MapId, userCtx.UserIdGuid, ct);
                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.VetoAction, result, ct);
                await BroadcastHistoryAsync(hub, veto, matchId, ct);

                if (result.Status == "completed")
                {
                    await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                        .SendAsync(VetoHubEvents.VetoComplete, result, ct);

                    // Auto-provision game server for CS2 matches (use CancellationToken.None — request scope dies after response)
                    _ = Task.Run(() => GameServerEndpoints.AutoProvisionServerAsync(
                        matchId, db, dathost, matchHub, config, serverLogger, CancellationToken.None));
                }

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
            catch (Exception ex)
            {
                vetoLogger.LogError(ex, "Veto pick failed for match {MatchId}", matchId);
                return Results.Json(new
                {
                    error = "Something went wrong. Please try again or contact support if the issue persists.",
                    detail = ex.Message,
                    inner = ex.InnerException?.Message,
                }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/veto/{matchId}/pick-side ───────────────────────────────
        app.MapPost("/api/veto/{matchId}/pick-side", async (
            Guid                         matchId,
            [FromBody] VetoPickSideRequest req,
            HttpContext                   ctx,
            VetoDbService                 veto,
            IHubContext<VetoHub>          hub,
            IDbConnectionFactory          db,
            IDatHostService               dathost,
            IHubContext<MatchHub>         matchHub,
            IConfiguration                config,
            ILogger<DatHostService>       serverLogger,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                var result = await veto.PickSideAsync(matchId, req.MapId, req.Side, userCtx.UserIdGuid, ct);
                await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                    .SendAsync(VetoHubEvents.VetoAction, result, ct);
                await BroadcastHistoryAsync(hub, veto, matchId, ct);

                if (result.Status == "completed")
                {
                    await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
                        .SendAsync(VetoHubEvents.VetoComplete, result, ct);

                    // Auto-provision game server for CS2 matches (use CancellationToken.None — request scope dies after response)
                    _ = Task.Run(() => GameServerEndpoints.AutoProvisionServerAsync(
                        matchId, db, dathost, matchHub, config, serverLogger, CancellationToken.None));
                }

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
            await BroadcastHistoryAsync(hub, veto, matchId, ct);

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
            VetoDbService        veto,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            // S5: Look up by cryptographic link token (not by match_id/team2_id)
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT mmv.match_id,
                       mmv.team1_link_token,
                       mmv.team2_link_token,
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

            var state = await veto.GetAsync((Guid)row.match_id, ct);
            if (state is null) return Results.NotFound(new { error = "Map veto not found." });

            // Determine which team this token belongs to
            string? teamSide = row.team1_link_token == token ? "team1" : "team2";

            return Results.Ok(new
            {
                id               = state.Id,
                match_id         = state.MatchId,
                tournament_id    = state.TournamentId,
                team1_id         = state.Team1Id,
                team2_id         = state.Team2Id,
                best_of          = state.BestOf,
                status           = state.Status,
                current_team_id  = state.CurrentTeamId,
                current_action   = state.CurrentAction,
                current_action_number = state.CurrentActionNumber,
                team1_banned_maps = state.Team1BannedMaps,
                team2_banned_maps = state.Team2BannedMaps,
                team1_picked_maps = state.Team1PickedMaps,
                team2_picked_maps = state.Team2PickedMaps,
                selected_map_id   = state.SelectedMapId,
                selected_map_pool = state.SelectedMapPool,
                started_at        = state.StartedAt,
                completed_at      = state.CompletedAt,
                game              = state.Game,
                stage_id         = (object?)null,
                team_side        = teamSide,
                team1_link_token = state.Team1LinkToken,
                team2_link_token = state.Team2LinkToken,
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

        app.MapGet("/api/veto/token/{token}/history", async (
            string token,
            IDbConnectionFactory db,
            VetoDbService veto,
            IConfiguration config,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var tokenContext = await ResolveTokenContextAsync(conn, token);
            if (tokenContext is null) return Results.NotFound(new { error = "Map veto not found." });

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/');
            var history = await veto.GetEnrichedHistoryAsync(tokenContext.MatchId, ct);
            var enriched = history
                .Select(entry => entry with
                {
                    MapImageUrl = Esportra.Core.Games.R6MapCatalog.ResolveImageUrl(
                        Esportra.Core.Games.R6MapCatalog.GameName,
                        entry.MapName,
                        entry.MapImageUrl,
                        supabaseUrl),
                })
                .ToList();
            return Results.Ok(enriched);
        });

        app.MapPost("/api/veto/token/{token}/ban", async (
            string token,
            [FromBody] VetoActionRequest req,
            IDbConnectionFactory db,
            VetoDbService veto,
            IHubContext<VetoHub> hub,
            CancellationToken ct) =>
            await HandleTokenActionAsync(token, req.MapId, null, "ban", db, veto, hub, ct));

        app.MapPost("/api/veto/token/{token}/pick", async (
            string token,
            [FromBody] VetoActionRequest req,
            IDbConnectionFactory db,
            VetoDbService veto,
            IHubContext<VetoHub> hub,
            CancellationToken ct) =>
            await HandleTokenActionAsync(token, req.MapId, null, "pick", db, veto, hub, ct));

        app.MapPost("/api/veto/token/{token}/pick-side", async (
            string token,
            [FromBody] VetoPickSideRequest req,
            IDbConnectionFactory db,
            VetoDbService veto,
            IHubContext<VetoHub> hub,
            CancellationToken ct) =>
            await HandleTokenActionAsync(token, req.MapId, req.Side, "pick-side", db, veto, hub, ct));

        // ── Public read-only veto (no auth, no token links) ─────────────────
        app.MapGet("/api/public/veto/{matchId:guid}", async (
            Guid matchId,
            VetoDbService veto,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var access = await ResolvePublicVetoAccessAsync(db, matchId, ct);
            if (access is null) return Results.NotFound();

            var state = await veto.GetAsync(matchId, ct);
            if (state is null) return Results.NotFound();

            return Results.Ok(ToPublicVetoState(state));
        }).AllowAnonymous();

        app.MapGet("/api/public/veto/{matchId:guid}/history", async (
            Guid matchId,
            VetoDbService veto,
            IDbConnectionFactory db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var access = await ResolvePublicVetoAccessAsync(db, matchId, ct);
            if (access is null) return Results.NotFound();

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/');
            var history = await veto.GetEnrichedHistoryAsync(matchId, ct);
            var enriched = history
                .Select(entry => entry with
                {
                    MapImageUrl = Esportra.Core.Games.R6MapCatalog.ResolveImageUrl(
                        Esportra.Core.Games.R6MapCatalog.GameName,
                        entry.MapName,
                        entry.MapImageUrl,
                        supabaseUrl),
                })
                .ToList();
            return Results.Ok(enriched);
        }).AllowAnonymous();
    }

    private static async Task<IResult> HandleTokenActionAsync(
        string token,
        string mapId,
        string? side,
        string action,
        IDbConnectionFactory db,
        VetoDbService veto,
        IHubContext<VetoHub> hub,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var tokenContext = await ResolveTokenContextAsync(conn, token);
        if (tokenContext is null) return Results.NotFound(new { error = "Map veto not found." });

        try
        {
            var result = action switch
            {
                "ban" => await veto.BanMapForTeamTokenAsync(tokenContext.MatchId, mapId, tokenContext.TeamSide, ct),
                "pick" => await veto.PickMapForTeamTokenAsync(tokenContext.MatchId, mapId, tokenContext.TeamSide, ct),
                "pick-side" => await veto.PickSideForTeamTokenAsync(tokenContext.MatchId, mapId, side ?? "", tokenContext.TeamSide, ct),
                _ => throw new InvalidOperationException("Unsupported veto action.")
            };

            await hub.Clients.Group(VetoHub.VetoGroup(tokenContext.MatchId.ToString()))
                .SendAsync(VetoHubEvents.VetoAction, result, ct);
            await BroadcastHistoryAsync(hub, veto, tokenContext.MatchId, ct);

            if (result.Status == "completed")
            {
                await hub.Clients.Group(VetoHub.VetoGroup(tokenContext.MatchId.ToString()))
                    .SendAsync(VetoHubEvents.VetoComplete, result, ct);
            }

            return Results.Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Json(new { error = "This team link cannot act on the current turn." }, statusCode: 403);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.StartsWith("CONFLICT")
                ? Results.Conflict(new { error = "The map veto was updated. Please refresh and try again." })
                : Results.BadRequest(new { error = "This veto action is not valid right now." });
        }
    }

    private static async Task<TokenVetoContext?> ResolveTokenContextAsync(
        System.Data.IDbConnection conn,
        string token)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT mmv.match_id,
                   CASE WHEN mmv.team1_link_token = @token THEN 'team1' ELSE 'team2' END AS team_side,
                   EXISTS (
                       SELECT 1
                       FROM tournament_participants tp
                       WHERE tp.tournament_id = mmv.tournament_id
                         AND tp.is_mock = TRUE
                   ) AS has_mock_participants
            FROM match_map_vetos mmv
            WHERE mmv.team1_link_token = @token OR mmv.team2_link_token = @token
            """,
            new { token });

        return row is null
            ? null
            : new TokenVetoContext((Guid)row.match_id, (string)row.team_side, (bool)row.has_mock_participants);
    }

    private static async Task BroadcastHistoryAsync(
        IHubContext<VetoHub> hub,
        VetoDbService veto,
        Guid matchId,
        CancellationToken ct)
    {
        var history = await veto.GetEnrichedHistoryAsync(matchId, ct);
        await hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()))
            .SendAsync(VetoHubEvents.VetoHistoryUpdated, history, ct);
    }

    private static async Task<PublicVetoAccess?> ResolvePublicVetoAccessAsync(
        IDbConnectionFactory db,
        Guid matchId,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<PublicVetoAccessRow>(
            """
            SELECT t.settings::text AS SettingsJson
            FROM public.match_map_vetos mmv
            JOIN public.tournaments t ON t.id = mmv.tournament_id
            WHERE mmv.match_id = @matchId
              AND t.deleted_at IS NULL
            """,
            new { matchId });

        if (row is null || IsMapVetoDisabled(row.SettingsJson))
            return null;

        return new PublicVetoAccess(matchId);
    }

    private static object ToPublicVetoState(MatchMapVeto state) => new
    {
        state.Id,
        state.MatchId,
        state.TournamentId,
        state.Team1Id,
        state.Team2Id,
        state.BestOf,
        state.Status,
        state.CurrentTeamId,
        state.CurrentAction,
        state.CurrentActionNumber,
        state.Team1BannedMaps,
        state.Team2BannedMaps,
        state.Team1PickedMaps,
        state.Team2PickedMaps,
        state.SelectedMapId,
        state.SelectedMapPool,
        state.StartedAt,
        state.CompletedAt,
        state.Game,
    };

    private static bool IsMapVetoDisabled(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("mapVetoEnabled", out var value)
                && value.ValueKind == JsonValueKind.False;
        }
        catch
        {
            return false;
        }
    }

    private sealed record TournamentVetoGateRow(
        string Game,
        string? GameMode,
        int? TeamSize,
        string? SettingsJson);

    private sealed record TokenVetoContext(
        Guid MatchId,
        string TeamSide,
        bool HasMockParticipants);

    private sealed record PublicVetoAccessRow(string? SettingsJson);

    private sealed record PublicVetoAccess(Guid MatchId);
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
