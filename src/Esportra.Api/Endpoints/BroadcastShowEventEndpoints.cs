using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastShowEventEndpoints
{
    public static void MapBroadcastShowEventEndpoints(this WebApplication app)
    {
        app.MapPost("/api/broadcast/shows/{showId:guid}/events/drain", Drain)
            .WithMetadata(new RateLimitPolicyMetadata("strict"));
    }

    // Historical events retain the original route-bound show. The server verifies
    // ownership from the node credential before using that ID. No payload show_id.
    private static async Task<IResult> Drain(Guid showId, [FromBody] DrainEventsRequest request,
        HttpContext ctx, IDbConnectionFactory db)
    {
        var node = await BroadcastHelpers.ValidateNodeBearerAsync(ctx, db);
        if (node is null) return Results.Unauthorized();
        if (request.Events is null || request.Events.Count == 0) return Results.Ok(new { accepted = 0, rejected = 0 });
        if (request.Events.Count > 100) return Results.BadRequest(new { error = "Maximum 100 events per batch." });
        using var conn = db.CreateConnection();
        if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM broadcast_shows WHERE id = @showId AND org_id = @orgId)", new { showId, orgId = node.OrgId })) return Results.StatusCode(403);
        var rows = new List<object>();
        foreach (var item in request.Events)
        {
            if (item is null || !BroadcastEventGuard.IsValidEventId(item.EventId) ||
                !BroadcastEventGuard.IsAllowedEventType(item.EventType) ||
                !BroadcastEventGuard.IsDataWithinSizeLimit(item.Payload, out var json)) continue;
            rows.Add(new { event_id = Guid.Parse(item.EventId).ToString(), event_type = item.EventType, payload = JsonSerializer.Deserialize<JsonElement>(json), created_at = item.Ts?.UtcDateTime ?? DateTime.UtcNow });
        }
        if (rows.Count > 0)
        {
            await conn.ExecuteAsync("""
                INSERT INTO broadcast_events (show_id, node_id, event_id, event_type, payload, source, created_at)
                SELECT @showId, @nodeId, batch.event_id, batch.event_type, batch.payload, 'node', batch.created_at
                FROM jsonb_to_recordset(@records::jsonb) AS batch(event_id text, event_type text, payload jsonb, created_at timestamptz)
                ON CONFLICT (event_id) DO NOTHING
                """, new { showId, nodeId = node.NodeId, records = JsonSerializer.Serialize(rows) });
        }
        return Results.Ok(new { accepted = rows.Count, rejected = request.Events.Count - rows.Count });
    }
}
