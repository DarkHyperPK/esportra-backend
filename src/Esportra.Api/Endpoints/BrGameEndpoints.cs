using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

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
                       (SELECT COUNT(*) FROM br_lobby_results rr WHERE rr.game_id = g.id) AS result_count,
                       (SELECT COUNT(*) FROM br_lobby_evidence re WHERE re.game_id = g.id) AS evidence_count
                FROM br_games g
                WHERE g.lobby_id = @lobbyId
                ORDER BY g.game_number
                """,
                new { lobbyId });

            return Results.Ok(games);
        });

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
                       l.stage_id, l.wave_number, l.lobby_index, l.lobby_code,
                       l.queue_timer_minutes, l.queue_started_at
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
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var context = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.id, g.lobby_id, g.game_number, g.status AS game_status,
                       l.stage_id, l.status AS lobby_status,
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

            if (mapProvided && mapValue is not null)
            {
                var gameName = context.game as string;
                var catalogBrConfig = string.IsNullOrWhiteSpace(gameName)
                    ? null
                    : (await catalog.GetGameAsync(gameName.Trim(), ct))?.BrConfig;
                var mapConfig = BattleRoyaleConfigResolver.ResolveMapConfig(
                    context.settings, context.stage_config, catalogBrConfig);
                if (!BattleRoyaleConfigResolver.ValidateMapInPool(mapConfig, mapValue, out string? mapError))
                    return Results.BadRequest(new { error = mapError });
            }

            using var tx = conn.BeginTransaction();
            try
            {
                var updated = await conn.QuerySingleAsync<dynamic>(
                    """
                    UPDATE br_games
                    SET map = CASE WHEN @mapProvided THEN @map ELSE map END,
                        status = COALESCE(@status, status),
                        scheduled_at = CASE WHEN @scheduleProvided THEN @scheduledAt ELSE scheduled_at END,
                        started_at = CASE WHEN @startedProvided THEN @startedAt ELSE started_at END,
                        completed_at = CASE
                            WHEN @status = 'completed' THEN COALESCE(completed_at, now())
                            WHEN @status IS NOT NULL AND @status <> 'completed' THEN NULL
                            ELSE completed_at
                        END
                    WHERE id = @gameId
                    RETURNING id, lobby_id, game_number, map, status, scheduled_at, started_at, completed_at, created_at
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
                };

                await BrBroadcastHelper.BroadcastAsync(
                    brHub, BRHubEvents.GameUpdated, stageId, groupId, lobbyId, gameId, payload, ct);

                if (statusValue == "completed")
                {
                    await BrBroadcastHelper.BroadcastAsync(
                        brHub, BRHubEvents.GameCompleted, stageId, groupId, lobbyId, gameId, payload, ct);
                }

                return Results.Ok(updated);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/br/games/{gameId}/results", async (
            Guid gameId,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var hasParticipant = await ColumnExistsAsync(conn, "br_lobby_results", "participant_id");

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

    private static async Task<bool> ColumnExistsAsync(IDbConnection conn, string tableName, string columnName) =>
        await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                  AND column_name = @columnName
            )
            """,
            new { tableName, columnName });
}
