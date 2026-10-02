using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Bracket;
using Esportra.Core.Match;

namespace Esportra.Api.Endpoints;

internal static partial class PublicToolEndpoints
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
                LIMIT 200
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
        if (req.BracketSize is int requestedSize && names.Count > requestedSize)
            return ToolResult<PublicBracketPayload>.Fail(
                $"You have {names.Count} teams but the bracket size is set to {requestedSize}. Increase the bracket size or remove extra teams.");

        var size = Math.Max(2, req.BracketSize ?? Math.Max(names.Count, 8));
        var teams = names.Select((name, index) => new PublicToolTeam(Guid.NewGuid(), name, index + 1)).ToList();
        IBracketGenerator generator = format == "double_elimination"
            ? new DoubleEliminationGenerator()
            : new SingleEliminationGenerator();
        var roundConfig = StageRoundConfiguration.PerStage(format, bestOf);
        var graph = generator.Generate(teams.Select(t => (t.Id, t.Name)).ToList(), Guid.Empty, null, roundConfig, size);
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
