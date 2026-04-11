using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain: Venue Announcements — promos, events, alerts, and general info
/// displayed to gamers visiting a venue.
/// </summary>
public static class AnnouncementEndpoints
{
    public static void MapAnnouncementEndpoints(this WebApplication app)
    {
        // ── GET /api/venues/{venueId}/announcements — announcements ──────────
        // ?staff=true returns ALL announcements (including inactive/expired) for management UI.
        // Default returns only active, non-expired announcements (public/gamer view).
        app.MapGet("/api/venues/{venueId}/announcements", async (
            Guid                 venueId,
            [FromQuery] bool     staff = false,
            HttpContext          ctx = null!,
            IDbConnectionFactory db  = null!,
            CancellationToken    ct  = default) =>
        {
            using var conn = db.CreateConnection();

            if (staff)
            {
                // Staff view: return all announcements for management
                var userCtx = ctx.Items["UserContext"] as UserContext;
                if (userCtx is null) return Results.Unauthorized();

                var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                    "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                    new { userId = userCtx.UserIdGuid, venueId });
                if (staffCheck == 0) return Results.Unauthorized();

                var all = await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, venue_id, title, body, type, priority,
                           is_active, starts_at, expires_at, created_by,
                           created_at, updated_at
                    FROM venue_announcements
                    WHERE venue_id = @venueId
                    ORDER BY priority DESC, created_at DESC
                    LIMIT 100
                    """,
                    new { venueId });
                return Results.Ok(all);
            }

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, venue_id, title, body, type, priority,
                       is_active, starts_at, expires_at, created_by,
                       created_at, updated_at
                FROM venue_announcements
                WHERE venue_id  = @venueId
                  AND is_active = true
                  AND (starts_at  IS NULL OR starts_at  <= now())
                  AND (expires_at IS NULL OR expires_at >  now())
                ORDER BY priority DESC, created_at DESC
                LIMIT 20
                """,
                new { venueId });

            return Results.Ok(rows);
        });

        // ── POST /api/venues/{venueId}/announcements — create announcement ──
        app.MapPost("/api/venues/{venueId}/announcements", async (
            Guid                                    venueId,
            [FromBody] CreateAnnouncementRequest     req,
            HttpContext                              ctx,
            IDbConnectionFactory                    db,
            CancellationToken                       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required." });
            if (string.IsNullOrWhiteSpace(req.Type))
                return Results.BadRequest(new { error = "Type is required." });
            if (req.Priority is not null && (req.Priority < 0 || req.Priority > 100))
                return Results.BadRequest(new { error = "Priority must be between 0 and 100." });

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO venue_announcements
                    (venue_id, title, body, type, priority, starts_at, expires_at, created_by)
                VALUES
                    (@venueId, @title, @body, @type, @priority, @startsAt, @expiresAt, @createdBy)
                RETURNING id, venue_id, title, body, type, priority,
                          is_active, starts_at, expires_at, created_by,
                          created_at, updated_at
                """,
                new
                {
                    venueId,
                    title     = req.Title.Trim(),
                    body      = req.Body?.Trim() ?? "",
                    type      = req.Type.Trim(),
                    priority  = Math.Clamp(req.Priority ?? 0, 0, 100),
                    startsAt  = req.StartsAt,
                    expiresAt = req.ExpiresAt,
                    createdBy = userCtx.UserIdGuid
                });

            return Results.Created($"/api/venues/{venueId}/announcements/{row.id}", row);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/venues/{venueId}/announcements/{id} — update ───────────
        app.MapPut("/api/venues/{venueId}/announcements/{id}", async (
            Guid                                    venueId,
            Guid                                    id,
            [FromBody] UpdateAnnouncementRequest     req,
            HttpContext                              ctx,
            IDbConnectionFactory                    db,
            CancellationToken                       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required." });
            if (string.IsNullOrWhiteSpace(req.Type))
                return Results.BadRequest(new { error = "Type is required." });
            if (req.Priority is not null && (req.Priority < 0 || req.Priority > 100))
                return Results.BadRequest(new { error = "Priority must be between 0 and 100." });

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE venue_announcements
                SET title      = @title,
                    body       = @body,
                    type       = @type,
                    priority   = @priority,
                    starts_at  = @startsAt,
                    expires_at = @expiresAt
                WHERE id = @id AND venue_id = @venueId
                RETURNING id, venue_id, title, body, type, priority,
                          is_active, starts_at, expires_at, created_by,
                          created_at, updated_at
                """,
                new
                {
                    id,
                    venueId,
                    title     = req.Title.Trim(),
                    body      = req.Body?.Trim() ?? "",
                    type      = req.Type.Trim(),
                    priority  = Math.Clamp(req.Priority ?? 0, 0, 100),
                    startsAt  = req.StartsAt,
                    expiresAt = req.ExpiresAt
                });

            return row is not null ? Results.Ok(row) : Results.NotFound();
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/venues/{venueId}/announcements/{id} — owner only ────
        app.MapDelete("/api/venues/{venueId}/announcements/{id}", async (
            Guid                 venueId,
            Guid                 id,
            HttpContext          ctx = null!,
            IDbConnectionFactory db  = null!,
            CancellationToken    ct  = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Venue owner gate (role = 'owner')
            var ownerCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND role = 'owner' AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (ownerCheck == 0) return Results.Unauthorized();

            var deleted = await conn.ExecuteAsync(
                "DELETE FROM venue_announcements WHERE id = @id AND venue_id = @venueId",
                new { id, venueId });

            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/venues/{venueId}/announcements/{id}/toggle — toggle active
        app.MapPatch("/api/venues/{venueId}/announcements/{id}/toggle", async (
            Guid                 venueId,
            Guid                 id,
            HttpContext          ctx = null!,
            IDbConnectionFactory db  = null!,
            CancellationToken    ct  = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Venue staff gate
            var staffCheck = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM venue_staff WHERE user_id = @userId AND venue_id = @venueId AND accepted_at IS NOT NULL LIMIT 1",
                new { userId = userCtx.UserIdGuid, venueId });
            if (staffCheck == 0) return Results.Unauthorized();

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE venue_announcements
                SET is_active = NOT is_active
                WHERE id = @id AND venue_id = @venueId
                RETURNING id, venue_id, title, body, type, priority,
                          is_active, starts_at, expires_at, created_by,
                          created_at, updated_at
                """,
                new { id, venueId });

            return row is not null ? Results.Ok(row) : Results.NotFound();
        }).RequireAuthorization("Authenticated");
    }

    // ── Request DTOs ────────────────────────────────────────────────────────────

    private sealed record CreateAnnouncementRequest(
        string  Title,
        string? Body,
        string  Type,
        int?    Priority,
        string? StartsAt,
        string? ExpiresAt);

    private sealed record UpdateAnnouncementRequest(
        string  Title,
        string? Body,
        string  Type,
        int?    Priority,
        string? StartsAt,
        string? ExpiresAt);
}
