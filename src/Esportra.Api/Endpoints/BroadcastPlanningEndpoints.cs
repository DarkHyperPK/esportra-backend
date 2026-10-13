using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Core.Broadcast;
using Esportra.Api.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastPlanningEndpoints
{
    public static void MapBroadcastPlanningEndpoints(this WebApplication app)
    {
        app.MapPatch("/api/broadcast/shows/{showId:guid}", UpdateShow)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
        app.MapGet("/api/broadcast/home", GetHome).RequireAuthorization("Authenticated");
    }

    internal static Task<bool> IsTournamentInOrganizationAsync(IDbConnection conn, Guid tournamentId, Guid orgId, IDbTransaction? tx = null) =>
        conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @tournamentId AND organization_id = @orgId AND deleted_at IS NULL)", new { tournamentId, orgId }, tx);

    private static async Task<IResult> UpdateShow(Guid showId, [FromBody] JsonElement patch, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (patch.ValueKind != JsonValueKind.Object || !patch.EnumerateObject().Any() || patch.EnumerateObject().Any(field => field.Name is not ("title" or "tournamentId" or "scheduledAt")))
            return Results.BadRequest(new { error = "Provide title, tournamentId or scheduledAt." });
        var hasTitle = patch.TryGetProperty("title", out var titleJson);
        if (hasTitle && titleJson.ValueKind != JsonValueKind.String) return Results.BadRequest(new { error = "title must be a string" });
        var title = hasTitle ? titleJson.GetString()?.Trim() : null;
        var hasTournament = patch.TryGetProperty("tournamentId", out var tournamentJson);
        Guid? tournamentId = null;
        if (hasTournament && tournamentJson.ValueKind != JsonValueKind.Null)
        {
            if (tournamentJson.ValueKind != JsonValueKind.String || !Guid.TryParse(tournamentJson.GetString(), out var id) || id == Guid.Empty)
                return Results.BadRequest(new { error = "tournamentId must be a UUID or null" });
            tournamentId = id;
        }
        var hasSchedule = patch.TryGetProperty("scheduledAt", out var scheduledJson);
        DateTime? scheduledAt = null;
        if (hasSchedule && scheduledJson.ValueKind != JsonValueKind.Null)
        {
            if (scheduledJson.ValueKind != JsonValueKind.String || !scheduledJson.TryGetDateTimeOffset(out var parsed))
                return Results.BadRequest(new { error = "scheduledAt must be an ISO date or null" });
            scheduledAt = parsed.UtcDateTime;
        }
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var show = await conn.QuerySingleOrDefaultAsync<EditableShow>("SELECT id AS Id, org_id AS OrgId, title AS Title, status AS Status FROM broadcast_shows WHERE id = @showId FOR UPDATE", new { showId }, tx);
        if (show is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid, tx)) return Results.Json(new { error = "forbidden" }, statusCode: 403);
        if (hasTitle && BroadcastShowRules.ValidateTitleChange(show.Status, show.Title, title) is { } error)
            return Results.Json(new { error }, statusCode: error == "show_live_title_locked" ? 409 : 400);
        if (hasSchedule && !BroadcastShowRules.CanEditPlan(show.Status))
            return Results.Conflict(new { error = "The schedule is locked for live or closed shows." });
        if (tournamentId.HasValue && !await IsTournamentInOrganizationAsync(conn, tournamentId.Value, show.OrgId, tx))
            return Results.BadRequest(new { error = "Tournament must belong to this organization." });
        if (tournamentId.HasValue && await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM broadcast_show_matches sm JOIN brkt_matches m ON m.id = sm.match_id JOIN brkt_versions v ON v.id = m.version_id WHERE sm.show_id = @showId AND v.tournament_id <> @tournamentId)", new { showId, tournamentId }, tx))
            return Results.Conflict(new { error = "Remove incompatible match slots before changing tournaments." });
        await conn.ExecuteAsync("""
            UPDATE broadcast_shows SET title = CASE WHEN @hasTitle THEN @title ELSE title END,
              tournament_id = CASE WHEN @hasTournament THEN @tournamentId ELSE tournament_id END,
              scheduled_at = CASE WHEN @hasSchedule THEN @scheduledAt ELSE scheduled_at END,
              updated_at = NOW() WHERE id = @showId
            """, new { showId, hasTitle, title, hasTournament, tournamentId, hasSchedule, scheduledAt }, tx);
        tx.Commit();
        return Results.Ok(new { ok = true, showId });
    }

    private static async Task<IResult> GetHome([FromQuery] Guid? orgId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (!orgId.HasValue || orgId == Guid.Empty) return Results.BadRequest(new { error = "orgId is required" });
        using var conn = db.CreateConnection();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, orgId.Value, user.UserIdGuid)) return Results.Json(new { error = "forbidden" }, statusCode: 403);
        using var result = await conn.QueryMultipleAsync("""
            SELECT s.id AS Id, s.title AS Title, s.status AS Status, s.scheduled_at AS ScheduledAt,
                   s.tournament_id AS TournamentId, t.name AS TournamentName
            FROM broadcast_shows s LEFT JOIN tournaments t ON t.id = s.tournament_id AND t.organization_id = s.org_id AND t.deleted_at IS NULL
            WHERE s.org_id = @orgId ORDER BY s.created_at DESC LIMIT 100;
            SELECT id AS Id, name AS Name, last_seen_at AS LastSeenAt, is_active AS IsActive
            FROM broadcast_nodes WHERE org_id = @orgId AND is_active = true ORDER BY last_seen_at DESC NULLS LAST LIMIT 100;
            SELECT COUNT(DISTINCT c.user_id) FROM broadcast_crew c JOIN broadcast_shows s ON s.id = c.show_id
            WHERE s.org_id = @orgId AND c.is_active = true;
            """, new { orgId });
        var shows = (await result.ReadAsync<HomeShow>()).ToArray();
        var nodes = (await result.ReadAsync<HomeNode>()).ToArray();
        var crewCount = await result.ReadSingleAsync<long>();
        return Results.Ok(new { shows, nodes, crewCount, alerts = Array.Empty<object>() });
    }

    private sealed record EditableShow(Guid Id, Guid OrgId, string Title, string Status);
    private sealed record HomeShow(Guid Id, string Title, string Status, DateTime? ScheduledAt, Guid? TournamentId, string? TournamentName);
    private sealed record HomeNode(Guid Id, string Name, DateTime? LastSeenAt, bool IsActive);
}
