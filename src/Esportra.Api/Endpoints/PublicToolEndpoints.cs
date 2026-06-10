using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Bracket;
using Esportra.Core.Match;

namespace Esportra.Api.Endpoints;

public static class PublicToolEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapPublicToolEndpoints(this WebApplication app)
    {
        MapBracketToolEndpoints(app);
        MapPublicVetoEndpoints(app);
    }

    private static void MapBracketToolEndpoints(WebApplication app)
    {
        app.MapPost("/api/tools/brackets/preview", (
            PublicBracketRequest req,
            CancellationToken ct) =>
        {
            var result = BuildToolBracketPayload(req);
            return result.Error is not null ? Results.BadRequest(new { error = result.Error }) : Results.Ok(result.Payload);
        }).AllowAnonymous();

        app.MapPost("/api/tools/brackets", async (
            PublicBracketRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var result = BuildToolBracketPayload(req);
            if (result.Error is not null || result.Payload is null)
                return Results.BadRequest(new { error = result.Error ?? "Invalid bracket." });

            var id = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            var title = string.IsNullOrWhiteSpace(req.Title) ? "Untitled bracket" : req.Title.Trim();
            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(
                """
                INSERT INTO public.public_tool_brackets
                    (id, owner_user_id, title, format, best_of, status, visibility)
                VALUES (@id, @owner, @title, @format, @bestOf, 'active', 'private')
                """,
                new { id, owner = userCtx.UserIdGuid, title, format = result.Payload.Format, bestOf = result.Payload.BestOf },
                tx);
            await conn.ExecuteAsync(
                """
                INSERT INTO public.public_tool_bracket_versions
                    (id, bracket_id, version_number, status, graph_json)
                VALUES (@id, @bracketId, 1, 'active', @graph::jsonb)
                """,
                new { id = versionId, bracketId = id, graph = JsonSerializer.Serialize(result.Payload, JsonOptions) },
                tx);
            tx.Commit();

            return Results.Ok(new { id, versionId });
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/tools/brackets/mine", async (
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync(
                """
                SELECT b.id, b.title, b.format, b.best_of, b.status, b.visibility, b.share_token,
                       b.created_at, b.updated_at
                FROM public.public_tool_brackets b
                WHERE b.owner_user_id = @owner
                ORDER BY b.updated_at DESC
                """,
                new { owner = userCtx.UserIdGuid });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/tools/brackets/{id:guid}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var item = await LoadBracketAsync(db, id, userCtx.UserIdGuid);
            return item is null ? Results.NotFound() : Results.Ok(ToBracketResponse(item));
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/tools/brackets/share/{token}", async (
            string token,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT b.id, b.title, b.format, b.best_of, b.status, b.visibility, b.share_token,
                       v.graph_json
                FROM public.public_tool_brackets b
                JOIN LATERAL (
                    SELECT graph_json
                    FROM public.public_tool_bracket_versions
                    WHERE bracket_id = b.id AND status = 'active'
                    ORDER BY version_number DESC
                    LIMIT 1
                ) v ON TRUE
                WHERE b.share_token = @token AND b.visibility = 'unlisted'
                """,
                new { token });
            return row is null ? Results.NotFound() : Results.Ok(ToBracketResponse(row));
        }).AllowAnonymous();

        app.MapPatch("/api/tools/brackets/{id:guid}/matches/{matchId:guid}", async (
            Guid id,
            Guid matchId,
            PublicBracketMatchUpdateRequest req,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT b.id, v.id AS version_id, v.graph_json
                FROM public.public_tool_brackets b
                JOIN public.public_tool_bracket_versions v ON v.bracket_id = b.id AND v.status = 'active'
                WHERE b.id = @id AND b.owner_user_id = @owner
                """,
                new { id, owner = userCtx.UserIdGuid });
            if (row is null) return Results.NotFound();

            var payload = JsonSerializer.Deserialize<PublicBracketPayload>((string)row.graph_json, JsonOptions);
            if (payload is null) return Results.BadRequest(new { error = "Stored bracket is invalid." });

            var updated = UpdateToolMatch(payload, matchId, req);
            if (updated.Error is not null) return Results.BadRequest(new { error = updated.Error });

            await conn.ExecuteAsync(
                """
                UPDATE public.public_tool_bracket_versions
                   SET graph_json = @graph::jsonb
                 WHERE id = @versionId;
                UPDATE public.public_tool_brackets
                   SET updated_at = now()
                 WHERE id = @id;
                """,
                new { graph = JsonSerializer.Serialize(updated.Payload, JsonOptions), versionId = (Guid)row.version_id, id });

            return Results.Ok(updated.Payload);
        }).RequireAuthorization("Authenticated");

        app.MapPost("/api/tools/brackets/{id:guid}/reset", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var item = await LoadBracketAsync(db, id, userCtx.UserIdGuid);
            if (item is null) return Results.NotFound();

            var current = JsonSerializer.Deserialize<PublicBracketPayload>((string)item.graph_json, JsonOptions);
            if (current is null) return Results.BadRequest(new { error = "Stored bracket is invalid." });

            var reset = BuildToolBracketPayload(new PublicBracketRequest(
                current.Title,
                current.Format,
                current.Teams.Select(t => t.Name).ToArray(),
                current.BestOf,
                current.BracketSize));
            if (reset.Payload is null) return Results.BadRequest(new { error = reset.Error ?? "Could not reset bracket." });

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE public.public_tool_bracket_versions
                   SET graph_json = @graph::jsonb
                 WHERE bracket_id = @id AND status = 'active';
                UPDATE public.public_tool_brackets
                   SET updated_at = now()
                 WHERE id = @id;
                """,
                new { id, graph = JsonSerializer.Serialize(reset.Payload, JsonOptions) });
            return Results.Ok(reset.Payload);
        }).RequireAuthorization("Authenticated");

        app.MapPatch("/api/tools/brackets/{id:guid}/sharing", async (
            Guid id,
            PublicBracketSharingRequest req,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var shareToken = req.Enabled ? GenerateToken() : null;
            using var conn = db.CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE public.public_tool_brackets
                   SET visibility = @visibility,
                       share_token = CASE WHEN @enabled THEN COALESCE(share_token, @shareToken) ELSE NULL END,
                       updated_at = now()
                 WHERE id = @id AND owner_user_id = @owner
                """,
                new
                {
                    id,
                    owner = userCtx.UserIdGuid,
                    enabled = req.Enabled,
                    visibility = req.Enabled ? "unlisted" : "private",
                    shareToken
                });
            if (affected == 0) return Results.NotFound();
            var item = await LoadBracketAsync(db, id, userCtx.UserIdGuid);
            return Results.Ok(item is null ? null : ToBracketResponse(item));
        }).RequireAuthorization("Authenticated");

        app.MapDelete("/api/tools/brackets/{id:guid}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                DELETE FROM public.public_tool_brackets
                 WHERE id = @id AND owner_user_id = @owner
                """,
                new { id, owner = userCtx.UserIdGuid });
            return affected == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization("Authenticated");
    }

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
            IDbConnectionFactory db) =>
            await ApplyPublicVetoActionAsync(db, token, "ban", req.MapId, null)).AllowAnonymous();

        app.MapPost("/api/tools/map-veto/team/{token}/pick", async (
            string token,
            PublicVetoActionRequest req,
            IDbConnectionFactory db) =>
            await ApplyPublicVetoActionAsync(db, token, "pick", req.MapId, null)).AllowAnonymous();

        app.MapPost("/api/tools/map-veto/team/{token}/pick-side", async (
            string token,
            PublicVetoPickSideRequest req,
            IDbConnectionFactory db) =>
            await ApplyPublicVetoActionAsync(db, token, "pick_side", req.MapId, req.Side)).AllowAnonymous();

        app.MapPost("/api/tools/map-veto/host/{token}/reset", async (
            string token,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM public.public_veto_sessions WHERE host_token = @token AND expires_at > now()",
                new { token });
            if (row is null) return Results.NotFound();
            var firstStep = VetoSequences.GetStep((int)row.best_of, 1, (string)row.game, ((string[])row.selected_map_pool).Length);
            if (firstStep is null) return Results.BadRequest(new { error = "Invalid veto sequence." });
            var currentTeamId = firstStep.Team == "T1" ? (Guid)row.team1_id : (Guid)row.team2_id;
            await conn.ExecuteAsync(
                """
                UPDATE public.public_veto_sessions
                   SET status = 'in_progress',
                       current_team_id = @currentTeamId,
                       current_action = @action,
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
                new { id = (Guid)row.id, currentTeamId, action = firstStep.Action });
            return Results.Ok(await LoadPublicVetoByTokenAsync(db, token, "host"));
        }).AllowAnonymous();
    }

    private static ToolResult<PublicBracketPayload> BuildToolBracketPayload(PublicBracketRequest req)
    {
        var format = (req.Format ?? "single_elimination").Trim().ToLowerInvariant();
        if (format is not ("single_elimination" or "double_elimination"))
            return ToolResult<PublicBracketPayload>.Fail("Only single and double elimination are supported.");

        var bestOf = req.BestOf is 1 or 3 or 5 ? req.BestOf : 1;
        var names = (req.Teams ?? [])
            .Select(t => (t ?? "").Trim())
            .Where(t => t.Length > 0)
            .ToList();
        if (names.Count == 1) return ToolResult<PublicBracketPayload>.Fail("Add at least two team names, or leave the list empty for an empty bracket.");
        if (names.Count != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            return ToolResult<PublicBracketPayload>.Fail("Team names must be unique.");

        var size = Math.Max(2, req.BracketSize ?? Math.Max(names.Count, 8));
        var teams = names.Select((name, index) => new PublicToolTeam(Guid.NewGuid(), name, index + 1)).ToList();
        IBracketGenerator generator = format == "double_elimination"
            ? new DoubleEliminationGenerator()
            : new SingleEliminationGenerator();
        var graph = generator.Generate(teams.Select(t => (t.Id, t.Name)).ToList(), Guid.Empty, null, bestOf, size);
        var errors = GraphValidator.Validate(graph);
        if (errors.Count > 0) return ToolResult<PublicBracketPayload>.Fail(string.Join("; ", errors));
        return ToolResult<PublicBracketPayload>.Ok(new PublicBracketPayload(
            string.IsNullOrWhiteSpace(req.Title) ? "Untitled bracket" : req.Title.Trim(),
            format,
            bestOf,
            size,
            teams,
            graph));
    }

    private static ToolResult<PublicBracketPayload> UpdateToolMatch(PublicBracketPayload payload, Guid matchId, PublicBracketMatchUpdateRequest req)
    {
        var nodes = payload.Graph.Nodes.ToList();
        var idx = nodes.FindIndex(n => n.Id == matchId);
        if (idx < 0) return ToolResult<PublicBracketPayload>.Fail("Match was not found.");
        var node = nodes[idx];
        var winnerId = req.WinnerId;
        if (winnerId is not null && winnerId != node.Team1Id && winnerId != node.Team2Id)
            return ToolResult<PublicBracketPayload>.Fail("Winner must be one of the match teams.");
        var loserId = winnerId == node.Team1Id ? node.Team2Id : winnerId == node.Team2Id ? node.Team1Id : null;
        nodes[idx] = node with
        {
            Team1Score = req.Team1Score,
            Team2Score = req.Team2Score,
            WinnerId = winnerId,
            LoserId = loserId,
            Status = winnerId is null ? "pending" : "completed"
        };

        if (winnerId is not null)
        {
            foreach (var edge in payload.Graph.Edges.Where(e => e.SourceMatchId == matchId))
            {
                var targetIdx = nodes.FindIndex(n => n.Id == edge.TargetMatchId);
                if (targetIdx < 0) continue;
                var target = nodes[targetIdx];
                var advancingId = edge.Type == "loser" ? loserId : winnerId;
                if (advancingId is null) continue;
                nodes[targetIdx] = edge.TargetSlot == 1
                    ? target with { Team1Id = advancingId }
                    : target with { Team2Id = advancingId };
            }
        }

        var graph = payload.Graph with { Nodes = nodes };
        return ToolResult<PublicBracketPayload>.Ok(payload with { Graph = graph });
    }

    private static async Task<dynamic?> LoadBracketAsync(IDbConnectionFactory db, Guid id, Guid owner)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT b.id, b.title, b.format, b.best_of, b.status, b.visibility, b.share_token,
                   b.created_at, b.updated_at, v.graph_json
            FROM public.public_tool_brackets b
            JOIN LATERAL (
                SELECT graph_json
                FROM public.public_tool_bracket_versions
                WHERE bracket_id = b.id AND status = 'active'
                ORDER BY version_number DESC
                LIMIT 1
            ) v ON TRUE
            WHERE b.id = @id AND b.owner_user_id = @owner
            """,
            new { id, owner });
    }

    private static object ToBracketResponse(dynamic row)
    {
        var payload = JsonSerializer.Deserialize<PublicBracketPayload>((string)row.graph_json, JsonOptions);
        return new
        {
            id = row.id,
            title = row.title,
            format = row.format,
            bestOf = row.best_of,
            status = row.status,
            visibility = row.visibility,
            shareToken = row.share_token,
            createdAt = row.created_at,
            updatedAt = row.updated_at,
            payload
        };
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
        var firstStep = VetoSequences.GetStep(bestOf, 1, game, mapPool.Length);
        if (firstStep is null) return ToolResult<object>.Fail("No veto sequence exists for those settings.");

        var id = Guid.NewGuid();
        var team1Id = Guid.NewGuid();
        var team2Id = Guid.NewGuid();
        var currentTeamId = firstStep.Team == "T1" ? team1Id : team2Id;
        var hostToken = GenerateToken();
        var team1Token = GenerateToken();
        var team2Token = GenerateToken();

        await conn.ExecuteAsync(
            """
            INSERT INTO public.public_veto_sessions
                (id, game, best_of, team1_name, team2_name, team1_id, team2_id,
                 current_team_id, current_action, current_action_number,
                 selected_map_pool, host_token, team1_token, team2_token)
            VALUES
                (@id, @game, @bestOf, @team1, @team2, @team1Id, @team2Id,
                 @currentTeamId, @action, 1,
                 @mapPool, @hostToken, @team1Token, @team2Token)
            """,
            new { id, game, bestOf, team1, team2, team1Id, team2Id, currentTeamId, action = firstStep.Action, mapPool, hostToken, team1Token, team2Token });

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

    private static async Task<IResult> ApplyPublicVetoActionAsync(IDbConnectionFactory db, string token, string action, string mapId, string? side)
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
        if ((string)row.status != "in_progress") return Results.BadRequest(new { error = "This veto is already completed." });

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
            {
                t1Picked[t1Picked.IndexOf(t1Existing)] = t1Existing with { Side = side };
            }
            else if (t2Existing is not null)
            {
                t2Picked[t2Picked.IndexOf(t2Existing)] = t2Existing with { Side = side };
            }
        }

        Guid? nextTeamId = null;
        if (next is not null) nextTeamId = next.Team == "T1" ? (Guid)row.team1_id : (Guid)row.team2_id;
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
        return Results.Ok(await LoadPublicVetoByTokenAsync(db, token, "team"));
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
        var role = row.host_token == token ? "host" : row.team1_token == token ? "team1" : row.team2_token == token ? "team2" : "viewer";
        return new
        {
            id = row.id,
            game = row.game,
            bestOf = row.best_of,
            team1Name = row.team1_name,
            team2Name = row.team2_name,
            team1Id = row.team1_id,
            team2Id = row.team2_id,
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
            completedAt = row.completed_at
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

    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

    private sealed record ToolResult<T>(T? Payload, string? Error)
    {
        public static ToolResult<T> Ok(T payload) => new(payload, null);
        public static ToolResult<T> Fail(string error) => new(default, error);
    }
}

public sealed record PublicBracketRequest(string? Title, string? Format, string[]? Teams, int BestOf = 1, int? BracketSize = null);
public sealed record PublicToolTeam(Guid Id, string Name, int Seed);
public sealed record PublicBracketPayload(string Title, string Format, int BestOf, int BracketSize, IReadOnlyList<PublicToolTeam> Teams, BracketGraph Graph);
public sealed record PublicBracketMatchUpdateRequest(int? Team1Score, int? Team2Score, Guid? WinnerId);
public sealed record PublicBracketSharingRequest(bool Enabled);

public sealed record PublicVetoCreateRequest(string? Game, int BestOf, string? Team1Name, string? Team2Name, string[]? MapIds);
public sealed record PublicVetoActionRequest(string MapId);
public sealed record PublicVetoPickSideRequest(string MapId, string Side);
public sealed record PickedMapDto(string MapId, string? Side);
