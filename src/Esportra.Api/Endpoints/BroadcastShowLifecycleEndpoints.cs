using System.Data;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Core.Broadcast;

namespace Esportra.Api.Endpoints;

public static class BroadcastShowLifecycleEndpoints
{
    public static void MapBroadcastShowLifecycleEndpoints(this WebApplication app)
    {
        app.MapPost("/api/broadcast/shows/{showId:guid}/duplicate", Duplicate)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
        app.MapDelete("/api/broadcast/shows/{showId:guid}", Delete)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
    }

    private static async Task<IResult> Duplicate(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction(IsolationLevel.RepeatableRead);
        var show = await conn.QuerySingleOrDefaultAsync<SourceShow>("""
            SELECT org_id AS OrgId, title AS Title, status AS Status, started_at AS StartedAt
            FROM broadcast_shows WHERE id = @showId FOR UPDATE
            """, new { showId }, tx);
        if (show is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid, tx)) return Results.StatusCode(403);
        var newId = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO broadcast_shows (org_id, tournament_id, title, status, created_by)
            SELECT org_id, tournament_id, @title, 'draft', @userId FROM broadcast_shows WHERE id = @showId
            RETURNING id
            """, new { showId, title = BroadcastShowRules.DuplicateTitle(show.Title), userId = user.UserIdGuid }, tx);
        // Copy planning only, using fresh IDs and reset progression. Crew credentials,
        // schedule, sync timestamps, runtime events/results and proof are not copied.
        await conn.ExecuteAsync("""
            INSERT INTO broadcast_show_matches (show_id, match_id, display_order, match_code)
            SELECT @newId, match_id, display_order, match_code FROM broadcast_show_matches WHERE show_id = @showId;
            INSERT INTO rundown_segments (show_id, display_order, type, title, duration_seconds, config)
            SELECT @newId, display_order, type, title, duration_seconds, config FROM rundown_segments WHERE show_id = @showId;
            INSERT INTO broadcast_talent (show_id, name, handle, role, bio, avatar_url, display_order)
            SELECT @newId, name, handle, role, bio, avatar_url, display_order FROM broadcast_talent WHERE show_id = @showId;
            INSERT INTO broadcast_show_sponsors (show_id, name, logo_url, display_slot, display_order, is_active)
            SELECT @newId, name, logo_url, display_slot, display_order, is_active FROM broadcast_show_sponsors WHERE show_id = @showId;
            """, new { showId, newId }, tx);
        tx.Commit();
        return Results.Ok(new { showId = newId });
    }

    private static async Task<IResult> Delete(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var show = await conn.QuerySingleOrDefaultAsync<SourceShow>("""
            SELECT org_id AS OrgId, title AS Title, status AS Status, started_at AS StartedAt
            FROM broadcast_shows WHERE id = @showId FOR UPDATE
            """, new { showId }, tx);
        if (show is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid, tx)) return Results.StatusCode(403);
        var hasHistory = show.StartedAt is not null || await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM broadcast_events WHERE show_id = @showId)
              OR EXISTS(SELECT 1 FROM broadcast_corrections WHERE show_id = @showId)
              OR EXISTS(SELECT 1 FROM broadcast_sponsor_impressions WHERE show_id = @showId)
              OR EXISTS(SELECT 1 FROM rundown_segments WHERE show_id = @showId AND status <> 'pending')
              OR EXISTS(SELECT 1 FROM broadcast_show_matches WHERE show_id = @showId AND status <> 'pending')
            """, new { showId }, tx);
        if (!BroadcastShowRules.CanDelete(show.Status, hasHistory))
            return Results.Conflict(new { error = "Only unstarted shows without broadcast history can be deleted. Keep this show for reports." });
        await conn.ExecuteAsync("DELETE FROM broadcast_shows WHERE id = @showId", new { showId }, tx);
        tx.Commit();
        return Results.Ok(new { success = true });
    }

    private sealed record SourceShow(Guid OrgId, string Title, string Status, DateTime? StartedAt);
}
