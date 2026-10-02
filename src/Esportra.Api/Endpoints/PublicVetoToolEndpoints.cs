using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using Esportra.Core.Match;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

internal static partial class PublicToolEndpoints
{
    private static void MapPublicVetoEndpoints(WebApplication app)
    {
        app.MapPost("/api/tools/map-veto", async (
            PublicVetoCreateRequest req,
            IDbConnectionFactory db) =>
        {
            var built = await CreatePublicVetoAsync(db, req);
            return built.Error is not null ? Results.BadRequest(new { error = built.Error }) : Results.Ok(built.Payload);
        }).AllowAnonymous();

        app.MapGet("/api/tools/map-veto/host/{token}", async (string token, IDbConnectionFactory db) =>
        {
            var session = await LoadPublicVetoByTokenAsync(db, token, "host");
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).AllowAnonymous();

        app.MapGet("/api/tools/map-veto/team/{token}", async (string token, IDbConnectionFactory db) =>
        {
            var session = await LoadPublicVetoByTokenAsync(db, token, "team");
            return session is null ? Results.NotFound() : Results.Ok(session);
        }).AllowAnonymous();

        app.MapGet("/api/tools/map-veto/{token}/history", async (string token, IDbConnectionFactory db) =>
        {
            var history = await LoadPublicVetoHistoryAsync(db, token);
            return history is null ? Results.NotFound() : Results.Ok(history);
        }).AllowAnonymous();

        app.MapPost("/api/tools/map-veto/team/{token}/ban", async (
            string token,
            PublicVetoActionRequest req,
            IDbConnectionFactory db,
            IHubContext<VetoHub> hub) =>
            await ApplyPublicVetoActionAsync(db, hub, token, "ban", req.MapId, null)).AllowAnonymous();

        app.MapPost("/api/tools/map-veto/team/{token}/pick", async (
            string token,
            PublicVetoActionRequest req,
            IDbConnectionFactory db,
            IHubContext<VetoHub> hub) =>
            await ApplyPublicVetoActionAsync(db, hub, token, "pick", req.MapId, null)).AllowAnonymous();

        app.MapPost("/api/tools/map-veto/team/{token}/pick-side", async (
            string token,
            PublicVetoPickSideRequest req,
            IDbConnectionFactory db,
            IHubContext<VetoHub> hub) =>
            await ApplyPublicVetoActionAsync(db, hub, token, "pick_side", req.MapId, req.Side)).AllowAnonymous();

        app.MapPost("/api/tools/map-veto/host/{token}/toss", async (
            string token,
            IDbConnectionFactory db,
            IHubContext<VetoHub> hub,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM public.public_veto_sessions WHERE host_token = @token AND expires_at > now()",
                new { token });
            if (row is null) return Results.NotFound();
            if ((string)row.status != "pending_toss")
                return Results.Json(new { error = "Toss has already been resolved for this session." }, statusCode: 409);
            var winnerId = (RandomNumberGenerator.GetBytes(1)[0] & 1) == 0 ? (Guid)row.team1_id : (Guid)row.team2_id;
            var winnerName = winnerId == (Guid)row.team1_id ? (string)row.team1_name : (string)row.team2_name;
            var sessionId = (Guid)row.id;
            await conn.ExecuteAsync(
                """
                UPDATE public.public_veto_sessions
                   SET toss_winner_team_id = @winnerId,
                       toss_completed_at = now(),
                       status = 'toss_choice_pending',
                       updated_at = now()
                 WHERE id = @id
                """,
                new { id = sessionId, winnerId });
            await BroadcastPublicVetoTossResultAsync(hub, sessionId, winnerId, winnerName, ct);
            return Results.Ok(await LoadPublicVetoByTokenAsync(db, token, "host"));
        }).AllowAnonymous();

        app.MapPost("/api/tools/map-veto/team/{token}/toss-choice", async (
            string token,
            PublicVetoTossChoiceRequest req,
            IDbConnectionFactory db,
            IHubContext<VetoHub> hub,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT * FROM public.public_veto_sessions
                WHERE (team1_token = @token OR team2_token = @token) AND expires_at > now()
                """,
                new { token });
            if (row is null) return Results.NotFound();
            if ((string)row.status == "pending_toss")
                return Results.Json(new { error = "Toss has not been run yet." }, statusCode: 409);
            if ((string)row.status != "toss_choice_pending")
                return Results.Json(new { error = "Choice has already been made." }, statusCode: 409);
            var callerTeamId = token == (string)row.team1_token ? (Guid)row.team1_id : (Guid)row.team2_id;
            if (callerTeamId != (Guid)row.toss_winner_team_id)
                return Results.Json(new { error = "Only the toss winner can make this choice." }, statusCode: 403);
            var winnerId = (Guid)row.toss_winner_team_id;
            var opponentId = winnerId == (Guid)row.team1_id ? (Guid)row.team2_id : (Guid)row.team1_id;
            var firstActorId = req.GoFirst ? winnerId : opponentId;
            string[] mapPool = row.selected_map_pool;
            var firstStep = VetoSequences.GetStep((int)row.best_of, 1, (string)row.game, mapPool.Length);
            if (firstStep is null) return Results.BadRequest(new { error = "Invalid veto sequence." });
            var secondActorId = firstActorId == (Guid)row.team1_id ? (Guid)row.team2_id : (Guid)row.team1_id;
            var currentTeamId = firstStep.Team == "T1" ? firstActorId : secondActorId;
            var sessionId = (Guid)row.id;
            await conn.ExecuteAsync(
                """
                UPDATE public.public_veto_sessions
                   SET toss_first_actor_team_id = @firstActorId,
                       current_team_id = @currentTeamId,
                       current_action = @action,
                       current_action_number = 1,
                       status = 'in_progress',
                       updated_at = now()
                 WHERE id = @id
                """,
                new { id = sessionId, firstActorId, currentTeamId, action = firstStep.Action });
            await BroadcastPublicVetoUpdatedAsync(hub, sessionId, ct);
            return Results.Ok(await LoadPublicVetoByTokenAsync(db, token, "team"));
        }).AllowAnonymous();

        app.MapPost("/api/tools/map-veto/host/{token}/reset", async (
            string token,
            IDbConnectionFactory db,
            IHubContext<VetoHub> hub) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM public.public_veto_sessions WHERE host_token = @token AND expires_at > now()",
                new { token });
            if (row is null) return Results.NotFound();
            var sessionId = (Guid)row.id;
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(
                """
                UPDATE public.public_veto_sessions
                   SET status = 'pending_toss',
                       toss_winner_team_id = NULL,
                       toss_completed_at = NULL,
                       toss_first_actor_team_id = NULL,
                       current_team_id = NULL,
                       current_action = NULL,
                       current_action_number = 1,
                       team1_banned_maps = '{}',
                       team2_banned_maps = '{}',
                       team1_picked_maps = '[]'::jsonb,
                       team2_picked_maps = '[]'::jsonb,
                       selected_map_id = NULL,
                       completed_at = NULL,
                       updated_at = now()
                 WHERE id = @id;
                DELETE FROM public.public_veto_actions WHERE session_id = @id;
                """,
                new { id = sessionId },
                tx);
            tx.Commit();
            await BroadcastPublicVetoResetAsync(hub, sessionId);
            return Results.Ok(await LoadPublicVetoByTokenAsync(db, token, "host"));
        }).AllowAnonymous();
    }

    private static async Task<ToolResult<object>> CreatePublicVetoAsync(IDbConnectionFactory db, PublicVetoCreateRequest req)
    {
        var game = string.IsNullOrWhiteSpace(req.Game) ? "valorant" : req.Game.Trim();
        var bestOf = req.BestOf is 1 or 3 or 5 ? req.BestOf : 1;
        var team1 = string.IsNullOrWhiteSpace(req.Team1Name) ? "Team 1" : req.Team1Name.Trim();
        var team2 = string.IsNullOrWhiteSpace(req.Team2Name) ? "Team 2" : req.Team2Name.Trim();
        if (team1.Equals(team2, StringComparison.OrdinalIgnoreCase))
            return ToolResult<object>.Fail("Team names must be different.");

        using var conn = db.CreateConnection();
        var mapPool = (req.MapIds ?? [])
            .Select(m => (m ?? "").Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mapPool.Length == 0)
        {
            var config = VetoSequences.GetGameConfig(game);
            mapPool = (await conn.QueryAsync<string>(
                """
                SELECT id::text
                FROM public.game_maps
                WHERE game ILIKE @game AND is_active IS TRUE
                ORDER BY map_name
                LIMIT @limit
                """,
                new { game, limit = config.MapPoolSize })).ToArray();
        }
        var id = Guid.NewGuid();
        var team1Id = Guid.NewGuid();
        var team2Id = Guid.NewGuid();
        var hostToken = GenerateToken();
        var team1Token = GenerateToken();
        var team2Token = GenerateToken();

        await conn.ExecuteAsync(
            """
            INSERT INTO public.public_veto_sessions
                (id, game, best_of, team1_name, team2_name, team1_id, team2_id,
                 current_action_number, status,
                 selected_map_pool, host_token, team1_token, team2_token)
            VALUES
                (@id, @game, @bestOf, @team1, @team2, @team1Id, @team2Id,
                 1, 'pending_toss',
                 @mapPool, @hostToken, @team1Token, @team2Token)
            """,
            new { id, game, bestOf, team1, team2, team1Id, team2Id, mapPool, hostToken, team1Token, team2Token });

        return ToolResult<object>.Ok(new
        {
            session = await LoadPublicVetoByTokenAsync(db, hostToken, "host"),
            hostToken,
            team1Token,
            team2Token
        });
    }

    private static async Task<object?> LoadPublicVetoByTokenAsync(IDbConnectionFactory db, string token, string role)
    {
        using var conn = db.CreateConnection();
        var where = role == "host"
            ? "host_token = @token"
            : "(team1_token = @token OR team2_token = @token)";
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            $"""
            SELECT *
            FROM public.public_veto_sessions
            WHERE {where} AND expires_at > now()
            """,
            new { token });
        return row is null ? null : await ToPublicVetoResponseAsync(conn, row, token);
    }

    private static async Task BroadcastPublicVetoUpdatedAsync(IHubContext<VetoHub> hub, Guid sessionId, CancellationToken ct = default) =>
        await hub.Clients.Group(VetoHub.PublicToolVetoGroup(sessionId.ToString()))
            .SendAsync(VetoHubEvents.PublicVetoUpdated, new { sessionId = sessionId.ToString() }, ct);

    private static async Task BroadcastPublicVetoResetAsync(IHubContext<VetoHub> hub, Guid sessionId, CancellationToken ct = default) =>
        await hub.Clients.Group(VetoHub.PublicToolVetoGroup(sessionId.ToString()))
            .SendAsync(VetoHubEvents.PublicVetoReset, new { sessionId = sessionId.ToString() }, ct);

    private static async Task BroadcastPublicVetoTossResultAsync(
        IHubContext<VetoHub> hub, Guid sessionId, Guid winnerTeamId, string winnerTeamName, CancellationToken ct = default) =>
        await hub.Clients.Group(VetoHub.PublicToolVetoGroup(sessionId.ToString()))
            .SendAsync(VetoHubEvents.PublicVetoTossResult,
                new { sessionId = sessionId.ToString(), winnerTeamId, winnerTeamName }, ct);

    /// <summary>
    /// Maps T1/T2 sequence role to a concrete team ID, respecting toss first-actor when set.
    /// Falls back to entry order (team1_id = T1) for legacy sessions where toss was not run.
    /// </summary>
    private static Guid ResolveTeamForSide(dynamic row, string side)
    {
        if (row.toss_first_actor_team_id is Guid firstActor)
        {
            Guid t1 = row.team1_id;
            return side == "T1" ? firstActor : (firstActor == t1 ? (Guid)row.team2_id : t1);
        }

        return side == "T1" ? (Guid)row.team1_id : (Guid)row.team2_id;
    }

    private static async Task<IResult> ApplyPublicVetoActionAsync(
        IDbConnectionFactory db,
        IHubContext<VetoHub> hub,
        string token,
        string action,
        string mapId,
        string? side)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT *
            FROM public.public_veto_sessions
            WHERE (team1_token = @token OR team2_token = @token) AND expires_at > now()
            """,
            new { token });
        if (row is null) return Results.NotFound(new { error = "Map veto not found." });
        if ((string)row.status == "pending_toss")
            return Results.Json(new { error = "Toss has not been run yet." }, statusCode: 409);
        if ((string)row.status == "toss_choice_pending")
            return Results.Json(new { error = "Toss winner has not made their choice yet." }, statusCode: 409);
        if ((string)row.status != "in_progress")
            return Results.BadRequest(new { error = "This veto is already completed." });

        var teamSide = row.team1_token == token ? "team1" : "team2";
        Guid actingTeamId = teamSide == "team1" ? (Guid)row.team1_id : (Guid)row.team2_id;
        if ((Guid)row.current_team_id != actingTeamId) return Results.Json(new { error = "This team link cannot act on the current turn." }, statusCode: 403);
        if ((string)row.current_action != action) return Results.BadRequest(new { error = "This action is not valid right now." });

        string[] mapPool = row.selected_map_pool;
        if (!mapPool.Contains(mapId)) return Results.BadRequest(new { error = "Map is not in the veto pool." });

        var used = new HashSet<string>(((string[])row.team1_banned_maps).Concat((string[])row.team2_banned_maps), StringComparer.OrdinalIgnoreCase);
        var t1Picked = JsonSerializer.Deserialize<List<PickedMapDto>>((string)row.team1_picked_maps, JsonOptions) ?? [];
        var t2Picked = JsonSerializer.Deserialize<List<PickedMapDto>>((string)row.team2_picked_maps, JsonOptions) ?? [];
        foreach (var picked in t1Picked.Concat(t2Picked)) used.Add(picked.MapId);
        if (used.Contains(mapId) && action != "pick_side") return Results.BadRequest(new { error = "Map has already been used." });

        var sequence = VetoSequences.GetSequence((int)row.best_of, (string)row.game, mapPool.Length);
        var current = sequence.FirstOrDefault(s => s.ActionNumber == (int)row.current_action_number);
        var next = sequence.FirstOrDefault(s => s.ActionNumber == (int)row.current_action_number + 1);
        var completed = next is null;
        var selectedMapId = (string?)row.selected_map_id;
        if (action == "ban")
        {
            if (teamSide == "team1") row.team1_banned_maps = ((string[])row.team1_banned_maps).Append(mapId).ToArray();
            else row.team2_banned_maps = ((string[])row.team2_banned_maps).Append(mapId).ToArray();
        }
        else if (action == "pick")
        {
            var entry = new PickedMapDto(mapId, null);
            if (teamSide == "team1") t1Picked.Add(entry);
            else t2Picked.Add(entry);
        }
        else
        {
            if (current?.IsDecider == true)
            {
                var remaining = mapPool.FirstOrDefault(m => !used.Contains(m) && !m.Equals(mapId, StringComparison.OrdinalIgnoreCase));
                selectedMapId = used.Contains(mapId) ? remaining ?? mapId : mapId;
                mapId = selectedMapId;
            }
            var t1Existing = t1Picked.LastOrDefault(p => p.MapId == mapId);
            var t2Existing = t2Picked.LastOrDefault(p => p.MapId == mapId);
            if (t1Existing is not null)
                t1Picked[t1Picked.IndexOf(t1Existing)] = t1Existing with { Side = side };
            else if (t2Existing is not null)
                t2Picked[t2Picked.IndexOf(t2Existing)] = t2Existing with { Side = side };
        }

        Guid? nextTeamId = null;
        if (next is not null) nextTeamId = ResolveTeamForSide(row, next.Team);
        await conn.ExecuteAsync(
            """
            UPDATE public.public_veto_sessions
               SET status = @status,
                   current_team_id = @nextTeamId,
                   current_action = @nextAction,
                   current_action_number = @nextActionNumber,
                   team1_banned_maps = @team1Bans,
                   team2_banned_maps = @team2Bans,
                   team1_picked_maps = @team1Picks::jsonb,
                   team2_picked_maps = @team2Picks::jsonb,
                   selected_map_id = @selectedMapId,
                   completed_at = CASE WHEN @completed THEN now() ELSE completed_at END,
                   updated_at = now()
             WHERE id = @id;
            INSERT INTO public.public_veto_actions
                (session_id, team_id, team_side, action_type, map_id, action_number, side)
            VALUES (@id, @teamId, @teamSide, @action, @mapId, @actionNumber, @side)
            """,
            new
            {
                id = (Guid)row.id,
                status = completed ? "completed" : "in_progress",
                nextTeamId,
                nextAction = next?.Action,
                nextActionNumber = next?.ActionNumber ?? (int)row.current_action_number,
                team1Bans = (string[])row.team1_banned_maps,
                team2Bans = (string[])row.team2_banned_maps,
                team1Picks = JsonSerializer.Serialize(t1Picked, JsonOptions),
                team2Picks = JsonSerializer.Serialize(t2Picked, JsonOptions),
                selectedMapId,
                completed,
                teamId = actingTeamId,
                teamSide,
                action,
                mapId,
                actionNumber = (int)row.current_action_number,
                side
            });
        var sessionId = (Guid)row.id;
        var response = await LoadPublicVetoByTokenAsync(db, token, "team");
        await BroadcastPublicVetoUpdatedAsync(hub, sessionId);
        return Results.Ok(response);
    }

    private static async Task<object?> ToPublicVetoResponseAsync(System.Data.IDbConnection conn, dynamic row, string token)
    {
        string[] pool = row.selected_map_pool;
        var maps = (await conn.QueryAsync(
            """
            SELECT id::text AS id, game, map_name, map_image_url, is_active
            FROM public.game_maps
            WHERE id::text = ANY(@ids)
            ORDER BY map_name
            """,
            new { ids = pool })).ToArray();
        var role = row.host_token == token ? "host"
            : row.team1_token == token ? "team1"
            : row.team2_token == token ? "team2"
            : "viewer";
        Guid t1Id = row.team1_id;
        Guid t2Id = row.team2_id;

        // Security: ephemeral team UUIDs are only needed by the host for display/admin purposes.
        // Team and viewer roles receive null to avoid leaking session-internal identifiers.
        bool? isTossWinner = role is "team1" or "team2"
            ? (row.toss_winner_team_id is Guid tw
                ? (role == "team1" ? tw == t1Id : tw == t2Id)
                : false)
            : (bool?)null;

        string? tossFirstActorName = row.toss_first_actor_team_id is Guid fa
            ? (fa == t1Id ? (string)row.team1_name : (string)row.team2_name)
            : null;

        string? currentTeamSide = row.current_team_id is Guid ct
            ? (ct == t1Id ? "team1" : "team2")
            : null;

        return new
        {
            id = row.id,
            game = row.game,
            bestOf = row.best_of,
            team1Name = row.team1_name,
            team2Name = row.team2_name,
            team1Id = role == "host" ? (Guid?)t1Id : null,
            team2Id = role == "host" ? (Guid?)t2Id : null,
            status = row.status,
            currentTeamId = row.current_team_id,
            currentAction = row.current_action,
            currentActionNumber = row.current_action_number,
            team1BannedMaps = row.team1_banned_maps,
            team2BannedMaps = row.team2_banned_maps,
            team1PickedMaps = JsonSerializer.Deserialize<List<PickedMapDto>>((string)row.team1_picked_maps, JsonOptions) ?? [],
            team2PickedMaps = JsonSerializer.Deserialize<List<PickedMapDto>>((string)row.team2_picked_maps, JsonOptions) ?? [],
            selectedMapId = row.selected_map_id,
            selectedMapPool = pool,
            maps,
            hostToken = role == "host" ? row.host_token : null,
            team1Token = role == "host" ? row.team1_token : null,
            team2Token = role == "host" ? row.team2_token : null,
            role,
            expiresAt = row.expires_at,
            startedAt = row.started_at,
            completedAt = row.completed_at,
            tossWinnerTeamId = row.toss_winner_team_id,
            tossWinnerName = row.toss_winner_team_id is Guid wn
                ? (wn == t1Id ? (string)row.team1_name : (string)row.team2_name)
                : null,
            tossCompletedAt = row.toss_completed_at,
            tossFirstActorTeamId = row.toss_first_actor_team_id,
            isTossWinner,
            tossFirstActorName,
            currentTeamSide
        };
    }

    private static async Task<object[]?> LoadPublicVetoHistoryAsync(IDbConnectionFactory db, string token)
    {
        using var conn = db.CreateConnection();
        var sessionId = await conn.ExecuteScalarAsync<Guid?>(
            """
            SELECT id
            FROM public.public_veto_sessions
            WHERE (host_token = @token OR team1_token = @token OR team2_token = @token)
              AND expires_at > now()
            """,
            new { token });
        if (sessionId is null) return null;
        return (await conn.QueryAsync(
            """
            SELECT a.action_number,
                   a.team_side,
                   a.team_id,
                   CASE WHEN a.team_side = 'team1' THEN s.team1_name ELSE s.team2_name END AS team_name,
                   a.action_type AS action,
                   a.map_id,
                   gm.map_name,
                   gm.map_image_url,
                   a.side,
                   a.created_at
            FROM public.public_veto_actions a
            JOIN public.public_veto_sessions s ON s.id = a.session_id
            LEFT JOIN public.game_maps gm ON gm.id::text = a.map_id
            WHERE a.session_id = @sessionId
            ORDER BY a.action_number
            """,
            new { sessionId })).ToArray();
    }
}

public sealed record PublicVetoCreateRequest(string? Game, int BestOf, string? Team1Name, string? Team2Name, string[]? MapIds);
public sealed record PublicVetoActionRequest(string MapId);
public sealed record PublicVetoPickSideRequest(string MapId, string Side);
public sealed record PublicVetoTossChoiceRequest(bool GoFirst);
public sealed record PickedMapDto(string MapId, string? Side);
