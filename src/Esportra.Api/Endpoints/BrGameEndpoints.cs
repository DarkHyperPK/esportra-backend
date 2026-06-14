using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Br;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Esportra.Api.Endpoints;

public static class BrGameEndpoints
{
    public static void MapBrGameEndpoints(this WebApplication app)
    {
        app.MapGet("/api/lobbies/{lobbyId}/games", async (
            Guid lobbyId,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var stageId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT stage_id FROM br_lobbies WHERE id = @lobbyId",
                new { lobbyId });
            if (stageId is null || !await CanViewBrStageAsync(conn, stageId.Value))
                return Results.NotFound();

            var games = await conn.QueryAsync<dynamic>(
                """
                SELECT g.id, g.lobby_id, g.game_number, g.map, g.status,
                       g.scheduled_at, g.started_at, g.completed_at, g.created_at,
                       g.queue_timer_minutes, g.queue_started_at,
                       (SELECT COUNT(*) FROM br_lobby_results rr WHERE rr.game_id = g.id) AS result_count,
                       (SELECT COUNT(*) FROM br_lobby_evidence re WHERE re.game_id = g.id) AS evidence_count
                FROM br_games g
                WHERE g.lobby_id = @lobbyId
                ORDER BY g.game_number
                """,
                new { lobbyId });

            return Results.Ok(games);
        });

        app.MapPost("/api/lobbies/{lobbyId}/games", async (
            Guid lobbyId,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService catalog,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var context = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT l.stage_id, t.settings, ts.config AS stage_config, t.game
                FROM br_lobbies l
                JOIN tournament_stages ts ON ts.id = l.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE l.id = @lobbyId
                """,
                new { lobbyId });

            if (context is null)
                return Results.NotFound();

            var stageId = (Guid)context.stage_id;
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var gamesPerLobby = BrConfigService.ResolveGamesPerLobby(
                context.settings, context.stage_config) ?? 6;
            var catalogBrConfig = string.IsNullOrWhiteSpace(context.game as string)
                ? null
                : (await catalog.GetGameAsync(((string)context.game).Trim(), ct))?.BrConfig;

            var gameIds = await BrGameRepository.EnsureGamesForLobbyAsync(
                conn,
                lobbyId,
                gamesPerLobby,
                context.settings,
                context.stage_config,
                catalogBrConfig);

            var games = await conn.QueryAsync<dynamic>(
                """
                SELECT g.id, g.lobby_id, g.game_number, g.map, g.status,
                       g.scheduled_at, g.started_at, g.completed_at, g.created_at,
                       g.queue_timer_minutes, g.queue_started_at
                FROM br_games g
                WHERE g.lobby_id = @lobbyId
                ORDER BY g.game_number
                """,
                new { lobbyId });

            return Results.Ok(new { created = gameIds.Count, games });
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/br/games/{gameId}", async (
            Guid gameId,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var game = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.id, g.lobby_id, g.game_number, g.map, g.status,
                       g.scheduled_at, g.started_at, g.completed_at, g.created_at,
                       g.queue_timer_minutes, g.queue_started_at,
                       l.stage_id, l.wave_number, l.lobby_index, l.lobby_code
                FROM br_games g
                JOIN br_lobbies l ON l.id = g.lobby_id
                WHERE g.id = @gameId
                """,
                new { gameId });

            if (game is null)
                return Results.NotFound();

            var stageId = (Guid)game.stage_id;
            if (!await CanViewBrStageAsync(conn, stageId))
                return Results.NotFound();

            return Results.Ok(game);
        });

        app.MapPatch("/api/br/games/{gameId}", async (
            Guid gameId,
            [FromBody] JsonElement body,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService catalog,
            IHubContext<BRHub> brHub,
            BrScheduleNotificationService scheduleNotify,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var context = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.id, g.lobby_id, g.game_number, g.status AS game_status, g.scheduled_at,
                       l.stage_id, l.status AS lobby_status, l.lobby_code,
                       t.settings, ts.config AS stage_config, t.game
                FROM br_games g
                JOIN br_lobbies l ON l.id = g.lobby_id
                JOIN tournament_stages ts ON ts.id = l.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE g.id = @gameId
                """,
                new { gameId });

            if (context is null)
                return Results.NotFound();

            var stageId = (Guid)context.stage_id;
            var lobbyId = (Guid)context.lobby_id;
            DateTimeOffset? previousScheduledAt = context.scheduled_at switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(dt),
                null => null,
                _ => null,
            };
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            string? mapValue = null;
            var mapProvided = false;
            if (body.TryGetProperty("map", out var mapProp))
            {
                mapProvided = true;
                if (mapProp.ValueKind != JsonValueKind.Null)
                {
                    mapValue = mapProp.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(mapValue))
                        mapValue = null;
                }
            }

            string? statusValue = null;
            if (body.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
                statusValue = statusProp.GetString()?.Trim().ToLowerInvariant();

            DateTimeOffset? scheduledAt = null;
            var scheduleProvided = false;
            if (body.TryGetProperty("scheduledAt", out var scheduleProp))
            {
                scheduleProvided = true;
                if (scheduleProp.ValueKind != JsonValueKind.Null)
                {
                    var raw = scheduleProp.GetString();
                    if (raw is not null && !DateTimeOffset.TryParse(raw, out _))
                        return Results.BadRequest(new { error = "Invalid scheduledAt format." });
                    if (raw is not null)
                        scheduledAt = DateTimeOffset.Parse(raw);
                }
            }

            DateTimeOffset? startedAt = null;
            var startedProvided = false;
            if (body.TryGetProperty("startedAt", out var startedProp))
            {
                startedProvided = true;
                if (startedProp.ValueKind != JsonValueKind.Null)
                {
                    var raw = startedProp.GetString();
                    if (raw is not null && !DateTimeOffset.TryParse(raw, out _))
                        return Results.BadRequest(new { error = "Invalid startedAt format." });
                    if (raw is not null)
                        startedAt = DateTimeOffset.Parse(raw);
                }
            }

            int? queueTimerMinutes = null;
            var queueProvided = false;
            if (body.TryGetProperty("queueTimerMinutes", out var qtmProp))
            {
                queueProvided = true;
                if (qtmProp.ValueKind == JsonValueKind.Null)
                {
                    queueTimerMinutes = null;
                }
                else if (qtmProp.TryGetInt32(out var parsedQueue) && parsedQueue >= 0 && parsedQueue <= 180)
                {
                    queueTimerMinutes = parsedQueue == 0 ? null : parsedQueue;
                }
                else
                {
                    return Results.BadRequest(new { error = "queueTimerMinutes must be between 0 and 180." });
                }
            }

            if (mapProvided && mapValue is not null)
            {
                var gameName = context.game as string;
                var catalogBrConfig = string.IsNullOrWhiteSpace(gameName)
                    ? null
                    : (await catalog.GetGameAsync(gameName.Trim(), ct))?.BrConfig;
                var mapConfig = BrConfigService.ResolveMapConfig(
                    context.settings, context.stage_config, catalogBrConfig);
                if (!BrConfigService.ValidateMapInPool(mapConfig, mapValue, out string? mapError))
                    return Results.BadRequest(new { error = mapError });
            }

            if (statusValue == "active")
            {
                var lobbyStatus = (string)context.lobby_status;
                if (!string.Equals(lobbyStatus, "active", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { error = "Start the lobby with a code before starting games." });
                }

                var lobbyCode = context.lobby_code as string;
                if (string.IsNullOrWhiteSpace(lobbyCode))
                {
                    return Results.BadRequest(new { error = "Lobby code is required before starting a game." });
                }

                var gameName = context.game as string;
                var catalogBrConfig = string.IsNullOrWhiteSpace(gameName)
                    ? null
                    : (await catalog.GetGameAsync(gameName.Trim(), ct))?.BrConfig;
                var mapConfig = BrConfigService.ResolveMapConfig(
                    context.settings, context.stage_config, catalogBrConfig);
                var gameNumber = Convert.ToInt32(context.game_number);
                var effectiveMap = BrConfigService.ResolveMapForGame(mapConfig, gameNumber, mapProvided ? mapValue : null);
                if (BrConfigService.RequiresExplicitMapForGame(mapConfig)
                    && string.IsNullOrWhiteSpace(effectiveMap))
                {
                    return Results.BadRequest(new { error = $"Map is required before starting game {gameNumber}." });
                }

                if (!string.IsNullOrWhiteSpace(effectiveMap))
                {
                    if (!BrConfigService.ValidateMapInPool(mapConfig, effectiveMap, out string? startMapError))
                        return Results.BadRequest(new { error = startMapError });
                }

                if (!mapProvided && !string.IsNullOrWhiteSpace(effectiveMap))
                {
                    mapValue = effectiveMap;
                    mapProvided = true;
                }
            }

            using var tx = conn.BeginTransaction();
            try
            {
                if (scheduleProvided)
                {
                    var (tournamentStart, tournamentEnd, _) =
                        await StageCompletionHelper.GetTournamentWindowForStageAsync(conn, stageId, tx);
                    var scheduleWindowError = TournamentTimelineValidator.ValidateTimestampWithinWindow(
                        scheduledAt,
                        tournamentStart,
                        tournamentEnd,
                        "Game schedule");
                    if (scheduleWindowError is not null)
                    {
                        tx.Rollback();
                        return Results.BadRequest(new { error = scheduleWindowError });
                    }

                    if (scheduledAt is not null)
                    {
                        var gameNumber = Convert.ToInt32(context.game_number);
                        var sequentialError = await TournamentTimelineValidator.ValidateBrGameScheduleOrderAsync(
                            conn, lobbyId, gameId, gameNumber, scheduledAt, tx);
                        if (sequentialError is not null)
                        {
                            tx.Rollback();
                            return Results.BadRequest(new { error = sequentialError });
                        }
                    }
                }

                if (statusValue == "active")
                {
                    var otherActiveGame = await conn.ExecuteScalarAsync<bool>(
                        """
                        SELECT EXISTS (
                            SELECT 1 FROM br_games
                            WHERE lobby_id = @lobbyId
                              AND status = 'active'
                              AND id <> @gameId
                        )
                        """,
                        new { lobbyId, gameId },
                        tx);

                    if (otherActiveGame)
                    {
                        tx.Rollback();
                        return Results.BadRequest(new { error = "Another game in this lobby is already live. Complete it before starting the next game." });
                    }

                    var (tournamentStart, tournamentEnd, _) = await StageCompletionHelper.GetTournamentWindowForStageAsync(conn, stageId, tx);
                    var liveError = await TournamentTimelineValidator.EnsureTournamentLiveAsync(conn, stageId, tx);
                    if (liveError is not null)
                    {
                        tx.Rollback();
                        return Results.BadRequest(new { error = liveError });
                    }

                    var liveWindowError = TournamentTimelineValidator.ValidateTimestampWithinWindow(
                        DateTimeOffset.UtcNow,
                        tournamentStart,
                        tournamentEnd,
                        "Starting a game");
                    if (liveWindowError is not null)
                    {
                        tx.Rollback();
                        return Results.BadRequest(new { error = liveWindowError });
                    }
                }
                var updated = await conn.QuerySingleAsync<dynamic>(
                    """
                    UPDATE br_games
                    SET map = CASE WHEN @mapProvided THEN @map ELSE map END,
                        status = COALESCE(@status, status),
                        scheduled_at = CASE WHEN @scheduleProvided THEN @scheduledAt ELSE scheduled_at END,
                        queue_timer_minutes = CASE
                            WHEN @queueProvided THEN @queueTimerMinutes
                            ELSE queue_timer_minutes
                        END,
                        queue_started_at = CASE
                            WHEN COALESCE(@status, status) IN ('completed', 'pending') THEN NULL
                            WHEN @queueProvided AND COALESCE(@queueTimerMinutes, 0) = 0 THEN NULL
                            WHEN @status = 'active' AND COALESCE(
                                CASE WHEN @queueProvided THEN @queueTimerMinutes ELSE queue_timer_minutes END, 0) = 0 THEN NULL
                            WHEN COALESCE(@status, status) = 'active'
                                 AND COALESCE(
                                     CASE WHEN @queueProvided THEN @queueTimerMinutes ELSE queue_timer_minutes END, 0) > 0
                                 AND (@status = 'active'
                                      OR (@queueProvided AND COALESCE(@queueTimerMinutes, 0) > 0))
                                THEN NOW()
                            ELSE queue_started_at
                        END,
                        started_at = CASE
                            WHEN @startedProvided THEN @startedAt
                            WHEN @status = 'active' THEN COALESCE(started_at, now())
                            ELSE started_at
                        END,
                        completed_at = CASE
                            WHEN @status = 'completed' THEN COALESCE(completed_at, now())
                            WHEN @status IS NOT NULL AND @status <> 'completed' THEN NULL
                            ELSE completed_at
                        END
                    WHERE id = @gameId
                    RETURNING id, lobby_id, game_number, map, status, scheduled_at, started_at, completed_at,
                              queue_timer_minutes, queue_started_at, created_at
                    """,
                    new
                    {
                        gameId,
                        mapProvided,
                        map = mapValue,
                        status = statusValue,
                        scheduleProvided,
                        scheduledAt,
                        startedProvided,
                        startedAt,
                        queueProvided,
                        queueTimerMinutes,
                    },
                    tx);

                await BrGameRouteHelper.SyncLobbyStatusFromGamesAsync(conn, lobbyId, tx);
                tx.Commit();

                var groupId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    SELECT lg.group_id
                    FROM br_lobby_groups lg
                    WHERE lg.lobby_id = @lobbyId
                    ORDER BY lg.group_id
                    LIMIT 1
                    """,
                    new { lobbyId });

                var payload = new
                {
                    stageId = stageId.ToString(),
                    groupId = groupId?.ToString(),
                    lobbyId = lobbyId.ToString(),
                    gameId = gameId.ToString(),
                    gameNumber = Convert.ToInt32(updated.game_number),
                    status = (string)updated.status,
                    map = (string?)updated.map,
                    queueTimerMinutes = updated.queue_timer_minutes is not null
                        ? Convert.ToInt32(updated.queue_timer_minutes)
                        : (int?)null,
                    queueStartedAt = updated.queue_started_at is not null
                        ? ((DateTimeOffset)updated.queue_started_at).ToString("o")
                        : (string?)null,
                };

                await BrBroadcastHelper.BroadcastAsync(
                    brHub, BRHubEvents.GameUpdated, stageId, groupId, lobbyId, gameId, payload, ct);

                if (statusValue == "completed")
                {
                    await BrBroadcastHelper.BroadcastAsync(
                        brHub, BRHubEvents.GameCompleted, stageId, groupId, lobbyId, gameId, payload, ct);
                }

                if (scheduleProvided)
                {
                    await scheduleNotify.DispatchGameScheduleChangedAsync(
                        gameId, previousScheduledAt, scheduledAt, ct);
                }

                return Results.Ok(updated);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        app.MapPost("/api/br/games/{gameId}/reset", async (
            Guid gameId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<BRHub> brHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var context = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.id, g.lobby_id, g.game_number, g.map, g.status, g.scheduled_at,
                       l.stage_id
                FROM br_games g
                JOIN br_lobbies l ON l.id = g.lobby_id
                WHERE g.id = @gameId
                """,
                new { gameId });

            if (context is null)
                return Results.NotFound(new { error = "Game not found." });

            var stageId = (Guid)context.stage_id;
            var lobbyId = (Guid)context.lobby_id;
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var guardError = await BrGameService.ValidateResetAllowedAsync(conn, gameId);
            if (guardError is not null)
                return Results.Conflict(new { error = guardError });

            using var tx = conn.BeginTransaction();
            try
            {
                await BrGameService.ResetGameAsync(conn, gameId, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            var groupId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT lg.group_id
                FROM br_lobby_groups lg
                WHERE lg.lobby_id = @lobbyId
                ORDER BY lg.group_id
                LIMIT 1
                """,
                new { lobbyId });

            var resetGame = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT g.id, g.lobby_id, g.game_number, g.map, g.status, g.scheduled_at,
                       g.queue_timer_minutes, g.queue_started_at
                FROM br_games g
                WHERE g.id = @gameId
                """,
                new { gameId });

            var payload = new
            {
                stageId = stageId.ToString(),
                groupId = groupId?.ToString(),
                lobbyId = lobbyId.ToString(),
                gameId = gameId.ToString(),
                gameNumber = Convert.ToInt32(resetGame.game_number),
                status = "pending",
                map = (string?)resetGame.map,
                queueTimerMinutes = resetGame.queue_timer_minutes is not null
                    ? Convert.ToInt32(resetGame.queue_timer_minutes)
                    : (int?)null,
                queueStartedAt = (string?)null,
            };

            await BrBroadcastHelper.BroadcastAsync(
                brHub, BRHubEvents.GameReset, stageId, groupId, lobbyId, gameId, payload, ct);
            await BrBroadcastHelper.BroadcastAsync(
                brHub, BRHubEvents.LeaderboardUpdated, stageId, groupId, lobbyId, null, new { stageId = stageId.ToString() }, ct);

            return Results.Ok(resetGame);
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/br/games/{gameId}/results", async (
            Guid gameId,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var hasParticipant = await BrSchemaRepository.ColumnExistsAsync(conn, "br_lobby_results", "participant_id");

            var results = await conn.QueryAsync<dynamic>(
                hasParticipant
                    ? """
                      SELECT rr.id,
                             COALESCE(rr.team_id, rr.participant_id) AS team_id,
                             rr.placement, rr.kills,
                             rr.placement_points, rr.kill_points, rr.total_points,
                             CASE
                                 WHEN rr.team_id IS NOT NULL THEN t.name
                                 ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                             END AS team_name,
                             CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url
                      FROM br_lobby_results rr
                      LEFT JOIN teams t ON t.id = rr.team_id
                      LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                      LEFT JOIN profiles p ON p.id = tp.user_id
                      WHERE rr.game_id = @gameId
                      ORDER BY rr.placement
                      """
                    : """
                      SELECT rr.id, rr.team_id AS team_id, rr.placement, rr.kills,
                             rr.placement_points, rr.kill_points, rr.total_points,
                             t.name AS team_name, t.logo_url AS logo_url
                      FROM br_lobby_results rr
                      LEFT JOIN teams t ON t.id = rr.team_id
                      WHERE rr.game_id = @gameId
                      ORDER BY rr.placement
                      """,
                new { gameId });

            return Results.Ok(results);
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/br/games/{gameId}/evidence", async (
            Guid gameId,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var gameInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.lobby_id, l.stage_id, ts.tournament_id, t.team_size
                FROM br_games g
                JOIN br_lobbies l ON l.id = g.lobby_id
                JOIN tournament_stages ts ON ts.id = l.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE g.id = @gameId
                """,
                new { gameId });

            if (gameInfo is null)
                return Results.NotFound();

            var lobbyId = (Guid)gameInfo.lobby_id;
            var stageId = (Guid)gameInfo.stage_id;
            var tournamentId = (Guid)gameInfo.tournament_id;
            var isSolo = Convert.ToInt32(gameInfo.team_size ?? 1) == 1;

            var isStaff = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            isStaff = isStaff || StaffAuthHelper.IsPlatformAdmin(userCtx);

            Guid? viewerTeamId = null;
            Guid? viewerParticipantId = null;
            if (!isStaff)
            {
                var access = await ResolveGameEntityAccessAsync(
                    conn, lobbyId, tournamentId, userCtx.UserIdGuid, isSolo);
                viewerTeamId = access.TeamId;
                viewerParticipantId = access.ParticipantId;
                if (viewerTeamId is null && viewerParticipantId is null)
                    return Results.Forbid();
            }

            var payload = await BrGameRouteHelper.ListLobbyEvidenceAsync(
                conn,
                lobbyId,
                isStaff,
                viewerTeamId,
                viewerParticipantId,
                gameId: gameId);

            return Results.Ok(payload);
        }).RequireAuthorization("Authenticated");

        app.MapPut("/api/br/games/{gameId}/evidence", async (
            Guid gameId,
            [FromBody] JsonElement body,
            HttpContext ctx,
            IDbConnectionFactory db,
            IConfiguration config) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(supabaseUrl))
                return Results.BadRequest(new { error = "Supabase storage is not configured." });

            if (!TryNormalizeEvidenceImageUrl(body, supabaseUrl, out var imageUrl, out var imageUrlError))
                return Results.BadRequest(new { error = imageUrlError });

            int? placement = null;
            if (body.TryGetProperty("placement", out var placementProp) && placementProp.ValueKind != JsonValueKind.Null)
            {
                if (!placementProp.TryGetInt32(out var placementValue) || placementValue < 1)
                    return Results.BadRequest(new { error = "placement must be >= 1." });
                placement = placementValue;
            }

            int? kills = null;
            if (body.TryGetProperty("kills", out var killsProp) && killsProp.ValueKind != JsonValueKind.Null)
            {
                if (!killsProp.TryGetInt32(out var killsValue) || killsValue < 0)
                    return Results.BadRequest(new { error = "kills must be >= 0." });
                kills = killsValue;
            }

            using var conn = db.CreateConnection();
            var gameInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.lobby_id, g.status AS game_status, l.status AS lobby_status,
                       ts.tournament_id, t.team_size
                FROM br_games g
                JOIN br_lobbies l ON l.id = g.lobby_id
                JOIN tournament_stages ts ON ts.id = l.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE g.id = @gameId
                """,
                new { gameId });

            if (gameInfo is null)
                return Results.NotFound();

            if ((string)gameInfo.lobby_status != "active")
                return Results.Conflict(new { error = "Evidence can only be submitted while the lobby is live." });
            if ((string)gameInfo.game_status != "active")
                return Results.Conflict(new { error = "Evidence can only be submitted while the game is live." });

            var lobbyId = (Guid)gameInfo.lobby_id;
            var tournamentId = (Guid)gameInfo.tournament_id;
            var isSolo = Convert.ToInt32(gameInfo.team_size ?? 1) == 1;

            var access = await ResolveGameEntityAccessAsync(
                conn, lobbyId, tournamentId, userCtx.UserIdGuid, isSolo);
            if (access.TeamId is null && access.ParticipantId is null)
                return Results.Forbid();

            var alreadyExists = access.ParticipantId is not null
                ? await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM br_lobby_evidence WHERE game_id = @gameId AND participant_id = @participantId)",
                    new { gameId, participantId = access.ParticipantId })
                : await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM br_lobby_evidence WHERE game_id = @gameId AND team_id = @teamId)",
                    new { gameId, teamId = access.TeamId });

            if (alreadyExists)
                return Results.Conflict(new { error = "Evidence has already been submitted for this game." });

            try
            {
                if (access.ParticipantId is not null)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_lobby_evidence (
                            lobby_id, game_id, team_id, participant_id, image_url, submitted_by, submitted_at,
                            placement, kills, reviewed
                        )
                        VALUES (@lobbyId, @gameId, NULL, @participantId, @imageUrl, @submittedBy, NOW(), @placement, @kills, FALSE)
                        """,
                        new
                        {
                            lobbyId,
                            gameId,
                            participantId = access.ParticipantId,
                            imageUrl,
                            submittedBy = userCtx.UserIdGuid,
                            placement,
                            kills,
                        });
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_lobby_evidence (
                            lobby_id, game_id, team_id, participant_id, image_url, submitted_by, submitted_at,
                            placement, kills, reviewed
                        )
                        VALUES (@lobbyId, @gameId, @teamId, NULL, @imageUrl, @submittedBy, NOW(), @placement, @kills, FALSE)
                        """,
                        new
                        {
                            lobbyId,
                            gameId,
                            teamId = access.TeamId,
                            imageUrl,
                            submittedBy = userCtx.UserIdGuid,
                            placement,
                            kills,
                        });
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "Evidence has already been submitted for this game." });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }

    private sealed record GameEntityAccess(Guid? TeamId, Guid? ParticipantId);

    private static async Task<GameEntityAccess> ResolveGameEntityAccessAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid tournamentId,
        Guid userId,
        bool isSolo)
    {
        if (isSolo)
        {
            var participantId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT tp.id
                FROM tournament_participants tp
                JOIN br_group_teams bgt ON bgt.participant_id = tp.id
                JOIN br_lobby_groups lg ON lg.group_id = bgt.group_id
                WHERE lg.lobby_id = @lobbyId
                  AND tp.tournament_id = @tournamentId
                  AND tp.user_id = @userId
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                LIMIT 1
                """,
                new { lobbyId, tournamentId, userId });
            return new GameEntityAccess(null, participantId);
        }

        var teamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT tm.team_id
            FROM team_members tm
            JOIN tournament_participants tp ON tp.team_id = tm.team_id
            JOIN br_group_teams bgt ON bgt.team_id = tm.team_id
            JOIN br_lobby_groups lg ON lg.group_id = bgt.group_id
            WHERE lg.lobby_id = @lobbyId
              AND tp.tournament_id = @tournamentId
              AND tm.user_id = @userId
              AND tm.is_active = TRUE
              AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
            LIMIT 1
            """,
            new { lobbyId, tournamentId, userId });
        return new GameEntityAccess(teamId, null);
    }

    private static bool TryNormalizeEvidenceImageUrl(
        JsonElement body,
        string supabaseUrl,
        out string imageUrl,
        out string? error)
    {
        imageUrl = string.Empty;
        error = null;

        if (!body.TryGetProperty("imageUrl", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
        {
            error = "imageUrl is required.";
            return false;
        }

        var raw = urlProp.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "imageUrl cannot be empty.";
            return false;
        }

        if (!raw.StartsWith(supabaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            error = "imageUrl must be hosted on the configured Supabase storage.";
            return false;
        }

        imageUrl = raw;
        return true;
    }

    private static async Task<bool> CanViewBrStageAsync(IDbConnection conn, Guid stageId) =>
        await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM public.tournament_stages s
                JOIN public.tournaments t ON t.id = s.tournament_id
                WHERE s.id = @stageId
                  AND t.deleted_at IS NULL
            )
            """,
            new { stageId });
}
