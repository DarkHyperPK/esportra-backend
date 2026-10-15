using System.Data;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Api.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastRundownEndpoints
{
    public static void MapBroadcastRundownEndpoints(this WebApplication app) =>
        app.MapPost("/api/broadcast/shows/{showId:guid}/segments/reorder", Reorder)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));

    private static async Task<IResult> Reorder(Guid showId, [FromBody] ReorderBroadcastSegmentsRequest request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        var ids = request.SegmentIds;
        if (ids is null || ids.Length > 200 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
            return Results.BadRequest(new { error = "Provide up to 200 unique segment IDs." });
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var show = await conn.QuerySingleOrDefaultAsync<ShowScope>("SELECT org_id AS OrgId, status AS Status FROM broadcast_shows WHERE id = @showId FOR UPDATE", new { showId }, tx);
        if (show is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid, tx)) return Results.Json(new { error = "forbidden" }, statusCode: 403);
        if (!Esportra.Core.Broadcast.BroadcastShowRules.CanEditPlan(show.Status)) return Results.Conflict(new { error = "Show flow is locked during and after a live show." });
        var counts = await conn.QuerySingleAsync<SegmentCounts>("SELECT COUNT(*) AS Total, COUNT(*) FILTER (WHERE id = ANY(@ids)) AS Selected FROM rundown_segments WHERE show_id = @showId", new { showId, ids }, tx);
        if (counts.Total != ids.Length || counts.Selected != ids.Length) return Results.Conflict(new { error = "Rundown changed. Reload before reordering." });
        await conn.ExecuteAsync("""
            UPDATE rundown_segments s SET display_order = ordering.position - 1, updated_at = NOW()
            FROM (SELECT id, position::integer AS position FROM unnest(@ids::uuid[]) WITH ORDINALITY AS ordered_ids(id, position)) ordering
            WHERE s.id = ordering.id AND s.show_id = @showId
            """, new { showId, ids }, tx);
        tx.Commit();
        return Results.Ok(new { ok = true, count = ids.Length });
    }
    private sealed record SegmentCounts(long Total, long Selected);
    private sealed record ShowScope(Guid OrgId, string Status);
}

public sealed record ReorderBroadcastSegmentsRequest(Guid[]? SegmentIds);
