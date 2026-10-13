using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Api.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastTalentEndpoints
{
    public static void MapBroadcastTalentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/broadcast/shows/{showId:guid}/talent", List).RequireAuthorization("Authenticated");
        app.MapPost("/api/broadcast/shows/{showId:guid}/talent", Add).RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
        app.MapDelete("/api/broadcast/shows/{showId:guid}/talent/{talentId:guid}", Remove).RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
    }
    private static async Task<IResult> List(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await OwnsShow(conn, showId, user.UserIdGuid)) return Results.Json(new { error = "forbidden" }, statusCode: 403);
        var rows = await conn.QueryAsync<Talent>("SELECT id AS Id, name AS Name, handle AS Handle, role AS Role, display_order AS DisplayOrder FROM broadcast_talent WHERE show_id = @showId ORDER BY display_order LIMIT 100", new { showId });
        return Results.Ok(rows);
    }
    private static async Task<IResult> Add(Guid showId, [FromBody] AddBroadcastTalentRequest request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || request.Handle?.Length > 100 || request.Role is not ("caster" or "analyst" or "host"))
            return Results.BadRequest(new { error = "Use a name up to 100 characters and caster, analyst or host role." });
        using var conn = db.CreateConnection();
        if (!await OwnsShow(conn, showId, user.UserIdGuid)) return Results.Json(new { error = "forbidden" }, statusCode: 403);
        var id = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO broadcast_talent (show_id, name, handle, role, display_order)
            VALUES (@showId, @name, @handle, @role, (SELECT COUNT(*)::integer FROM broadcast_talent WHERE show_id = @showId)) RETURNING id
            """, new { showId, name = request.Name.Trim(), handle = request.Handle?.Trim(), role = request.Role });
        return Results.Ok(new { id });
    }
    private static async Task<IResult> Remove(Guid showId, Guid talentId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await OwnsShow(conn, showId, user.UserIdGuid)) return Results.Json(new { error = "forbidden" }, statusCode: 403);
        var count = await conn.ExecuteAsync("DELETE FROM broadcast_talent WHERE id = @talentId AND show_id = @showId", new { showId, talentId });
        return count == 1 ? Results.Ok(new { ok = true }) : Results.NotFound();
    }
    private static Task<bool> OwnsShow(System.Data.IDbConnection conn, Guid showId, Guid userId) => conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM broadcast_shows s JOIN organizations o ON o.id = s.org_id WHERE s.id = @showId AND o.owner_id = @userId)", new { showId, userId });
    private sealed record Talent(Guid Id, string Name, string? Handle, string Role, int DisplayOrder);
}
public sealed record AddBroadcastTalentRequest(string Name, string? Handle, string Role);
