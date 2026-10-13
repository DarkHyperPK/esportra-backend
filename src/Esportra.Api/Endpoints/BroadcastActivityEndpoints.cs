using Dapper;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastActivityEndpoints
{
    public static void MapBroadcastActivityEndpoints(this WebApplication app) =>
        app.MapGet("/api/broadcast/shows/{showId:guid}/activity", List).RequireAuthorization("Authenticated");

    private static async Task<IResult> List(Guid showId, HttpContext ctx, IDbConnectionFactory db,
        [FromQuery] DateTimeOffset? beforeAt = null, [FromQuery] Guid? beforeId = null, [FromQuery] int limit = 50)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (limit is < 1 or > 100 || beforeAt.HasValue != beforeId.HasValue || beforeId == Guid.Empty)
            return Results.BadRequest(new { error = "Use limit 1–100 and both beforeAt/beforeId for pagination." });
        using var conn = db.CreateConnection();
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT org_id FROM broadcast_shows WHERE id = @showId", new { showId });
        if (orgId is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, orgId.Value, user.UserIdGuid)) return Results.StatusCode(403);
        // Metadata only: never return raw game payloads, credentials or correction reasons.
        var rows = (await conn.QueryAsync<ActivityRow>("""
            SELECT e.id AS Id, e.event_type AS EventType, e.source AS Source,
                   e.created_at AS CreatedAt, n.name AS NodeName
            FROM broadcast_events e LEFT JOIN broadcast_nodes n ON n.id = e.node_id
            WHERE e.show_id = @showId
              AND (@beforeAt::timestamptz IS NULL OR (e.created_at, e.id) < (@beforeAt::timestamptz, @beforeId::uuid))
            ORDER BY e.created_at DESC, e.id DESC LIMIT @take
            """, new { showId, beforeAt = beforeAt?.UtcDateTime, beforeId, take = limit + 1 })).ToArray();
        var hasMore = rows.Length > limit;
        var items = rows.Take(limit).ToArray();
        var last = items.LastOrDefault();
        return Results.Ok(new { items, nextCursor = hasMore && last is not null ? new Cursor(last.CreatedAt, last.Id) : null });
    }

    private sealed record ActivityRow(Guid Id, string EventType, string Source, DateTime CreatedAt, string? NodeName);
    private sealed record Cursor(DateTime BeforeAt, Guid BeforeId);
}
