using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

public static partial class BroadcastShowEndpoints
{
    private static async Task<long?> PublishRundown(IDbConnection conn, Guid showId, ShowConfigPayload payload)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var snapshot = JsonSerializer.Serialize(payload.Rundown ?? [], options);
        var configuration = JsonSerializer.Serialize(payload, options);
        // Atomic allocation prevents duplicate or decreasing publication versions.
        return await conn.QuerySingleOrDefaultAsync<long?>("""
            UPDATE broadcast_shows
            SET rundown_version = rundown_version + 1, published_rundown = @snapshot::jsonb,
                published_configuration = jsonb_set(@configuration::jsonb, '{rundownVersion}', to_jsonb(rundown_version + 1)),
                rundown_published_at = NOW(), last_synced_at = NOW(), updated_at = NOW()
            WHERE id = @showId AND status NOT IN ('ended', 'cancelled')
            RETURNING rundown_version
            """, new { showId, snapshot, configuration });
    }

    private static async Task<IResult> GetNodePublication(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        var node = await BroadcastHelpers.ValidateNodeBearerAsync(ctx, db);
        if (node is null) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var snapshot = await conn.QuerySingleOrDefaultAsync<string>("""
            SELECT published_configuration::text FROM broadcast_shows
            WHERE id = @showId AND org_id = @orgId AND status NOT IN ('ended', 'cancelled')
            """, new { showId, orgId = node.OrgId });
        return snapshot is null ? Results.NotFound() : Results.Json(JsonSerializer.Deserialize<JsonElement>(snapshot));
    }

    private static async Task<IResult> GetPublication(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var scope = await conn.QuerySingleOrDefaultAsync<PublicationScope>("""
            SELECT org_id AS OrgId, rundown_version AS Version, rundown_published_at AS PublishedAt,
                   published_rundown::text AS Rundown FROM broadcast_shows WHERE id = @showId
            """, new { showId });
        if (scope is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, scope.OrgId, user.UserIdGuid)) return Results.StatusCode(403);
        var nodes = await conn.QueryAsync<PublicationNode>("""
            SELECT id AS Id, name AS Name, runtime_seen_at AS SeenAt,
                   (runtime_status->'plan')::text AS Plan
            FROM broadcast_nodes WHERE org_id = @orgId AND is_active = true
              AND runtime_status->'plan'->>'showId' = @show LIMIT 100
            """, new { orgId = scope.OrgId, show = showId.ToString() });
        return Results.Ok(new
        {
            version = scope.Version,
            publishedAt = scope.PublishedAt,
            rundown = scope.Rundown is null ? Array.Empty<ShowRundownSlot>() : JsonSerializer.Deserialize<ShowRundownSlot[]>(scope.Rundown, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            nodes
        });
    }

    private sealed record PublicationScope(Guid OrgId, long Version, DateTime? PublishedAt, string? Rundown);
    private sealed record PublicationNode(Guid Id, string Name, DateTime? SeenAt, string? Plan);
}
