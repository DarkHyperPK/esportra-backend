using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

public static class BroadcastTimelineEndpoints
{
    public static void MapBroadcastTimelineEndpoints(this WebApplication app) => app.MapGet("/api/broadcast/shows/{showId:guid}/timeline", List).RequireAuthorization("Authenticated");

    private static async Task<IResult> List(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT org_id FROM broadcast_shows WHERE id = @showId", new { showId });
        if (orgId is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, orgId.Value, user.UserIdGuid)) return Results.StatusCode(403);
        // Typed/allowlisted metadata only. No raw payload, credential or correction reason.
        var rows = (await conn.QueryAsync<TimelineRow>("""
            SELECT e.id AS Id, e.event_type AS EventType, e.created_at AS At, n.name AS NodeName, e.node_id AS NodeId,
              CASE WHEN e.payload->>'actorKind' IN ('operator','automation','observer','game-feed','system') THEN e.payload->>'actorKind' ELSE 'recorded_node' END AS ActorKind,
              CASE WHEN e.payload->>'action' IN ('hold','skip','take_requested','manual_take','resume','technical_pause','timeout_started','timeout_ended','data_fixed','data_fix_queued') THEN e.payload->>'action' END AS Action,
              CASE WHEN e.payload->>'cause' IN ('manual','trigger','hold_expired','connection','missing_events') THEN e.payload->>'cause' END AS Cause,
              CASE WHEN e.payload->>'kind' IN ('obs_drop','stale_feed','output_drop') THEN e.payload->>'kind' END AS Kind,
              CASE WHEN e.payload->>'status' IN ('opened','resolved') THEN e.payload->>'status' END AS Status,
              CASE WHEN e.event_type = 'SegmentTake' THEN LEFT(e.payload->>'segmentName',200) END AS SegmentName
            FROM broadcast_events e LEFT JOIN broadcast_nodes n ON n.id = e.node_id
            WHERE e.show_id = @showId AND e.created_at >= NOW() - INTERVAL '60 minutes'
              AND e.created_at <= NOW() + INTERVAL '5 seconds'
              AND e.event_type IN ('SegmentTake','ProductionAction','ProductionIncident')
            ORDER BY e.created_at DESC, e.id DESC LIMIT 5001
            """, new { showId })).ToArray();
        return Results.Ok(new { items = rows.Take(5000).Reverse(), since = DateTimeOffset.UtcNow.AddHours(-1), until = DateTimeOffset.UtcNow, truncated = rows.Length > 5000 });
    }

    private sealed record TimelineRow(Guid Id, string EventType, DateTime At, string? NodeName, Guid? NodeId, string ActorKind, string? Action, string? Cause, string? Kind, string? Status, string? SegmentName);
}
