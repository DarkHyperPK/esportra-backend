using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

public static class BroadcastCrewWorkspaceEndpoints
{
    public static void MapBroadcastCrewWorkspaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/broadcast/crew/shows", ListAssignments).RequireAuthorization("Authenticated");
        app.MapGet("/api/broadcast/crew/shows/{showId:guid}", GetWorkspace).RequireAuthorization("Authenticated");
    }

    private static async Task<IResult> ListAssignments(HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<Assignment>("""
            SELECT s.id AS Id, s.title AS Title, s.status AS Status, c.role AS Role
            FROM broadcast_crew c JOIN broadcast_shows s ON s.id = c.show_id
            WHERE c.user_id = @userId AND c.is_active = true ORDER BY s.created_at DESC LIMIT 100
            """, new { userId = user.UserIdGuid });
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetWorkspace(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<Assignment>("""
            SELECT s.id AS Id, s.title AS Title, s.status AS Status,
              CASE WHEN o.owner_id = @userId THEN 'owner' ELSE c.role END AS Role
            FROM broadcast_shows s JOIN organizations o ON o.id = s.org_id
            LEFT JOIN broadcast_crew c ON c.show_id = s.id AND c.user_id = @userId AND c.is_active = true
            WHERE s.id = @showId AND (o.owner_id = @userId OR c.id IS NOT NULL)
            """, new { showId, userId = user.UserIdGuid });
        if (show is null) return Results.StatusCode(403);
        var matches = await conn.QueryAsync<MatchCode>("SELECT match_code AS Code FROM broadcast_show_matches WHERE show_id = @showId ORDER BY display_order LIMIT 200", new { showId });
        var rundown = await conn.QueryAsync<PlannedSegment>("SELECT id AS Id, title AS Title, type AS Type, duration_seconds AS DurationSeconds, status AS Status FROM rundown_segments WHERE show_id = @showId ORDER BY display_order LIMIT 200", new { showId });
        return Results.Ok(new { show, matches, rundown });
    }

    private sealed record Assignment(Guid Id, string Title, string Status, string Role);
    private sealed record MatchCode(string Code);
    private sealed record PlannedSegment(Guid Id, string Title, string Type, int? DurationSeconds, string Status);
}
