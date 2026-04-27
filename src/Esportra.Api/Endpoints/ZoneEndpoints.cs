using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

public static class ZoneEndpoints
{
    public static void MapZoneEndpoints(this WebApplication app)
    {
        // ── List zones ──────────────────────────────────────────
        app.MapGet("/api/venues/{id}/zones", async (
            Guid id,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var zones = await conn.QueryAsync<dynamic>(
                """
                SELECT z.id, z.name, z.color, z.hourly_rate, z.sort_order, z.is_active,
                       z.created_at, z.updated_at,
                       COUNT(vs.id) AS station_count
                FROM zones z
                LEFT JOIN venue_stations vs ON vs.zone_id = z.id
                WHERE z.venue_id = @VenueId
                GROUP BY z.id
                ORDER BY z.sort_order, z.name
                """,
                new { VenueId = id });

            return Results.Ok(zones);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Zones");

        // ── Create zone ─────────────────────────────────────────
        app.MapPost("/api/venues/{id}/zones", async (
            Guid id,
            CreateZoneRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsVenueOwner(db, user.UserIdGuid, id))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Zone name is required" });

            using var conn = db.CreateConnection();

            // Check duplicate name
            var exists = await conn.QuerySingleAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM zones WHERE venue_id = @VenueId AND LOWER(name) = LOWER(@Name))",
                new { VenueId = id, req.Name });

            if (exists)
                return Results.Conflict(new { error = "Zone with this name already exists" });

            var zoneId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO zones (venue_id, name, color, hourly_rate, sort_order)
                VALUES (@VenueId, @Name, @Color, @HourlyRate, @SortOrder)
                RETURNING id
                """,
                new
                {
                    VenueId = id,
                    req.Name,
                    Color = req.Color ?? "#3b82f6",
                    HourlyRate = req.HourlyRate ?? 0m,
                    SortOrder = req.SortOrder ?? 0,
                });

            return Results.Ok(new { id = zoneId, name = req.Name });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Zones");

        // ── Update zone ─────────────────────────────────────────
        app.MapPut("/api/venues/{id}/zones/{zoneId}", async (
            Guid id,
            Guid zoneId,
            UpdateZoneRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsVenueOwner(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE zones
                SET name        = COALESCE(@Name, name),
                    color       = COALESCE(@Color, color),
                    hourly_rate = COALESCE(@HourlyRate, hourly_rate),
                    sort_order  = COALESCE(@SortOrder, sort_order),
                    is_active   = COALESCE(@IsActive, is_active),
                    updated_at  = NOW()
                WHERE id = @ZoneId AND venue_id = @VenueId
                """,
                new
                {
                    ZoneId = zoneId,
                    VenueId = id,
                    req.Name,
                    req.Color,
                    HourlyRate = req.HourlyRate,
                    SortOrder = req.SortOrder,
                    IsActive = req.IsActive,
                });

            return affected > 0
                ? Results.Ok(new { updated = true })
                : Results.NotFound(new { error = "Zone not found" });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Zones");

        // ── Delete zone ─────────────────────────────────────────
        app.MapDelete("/api/venues/{id}/zones/{zoneId}", async (
            Guid id,
            Guid zoneId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsVenueOwner(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            // Unlink stations from this zone first
            await conn.ExecuteAsync(
                "UPDATE venue_stations SET zone_id = NULL WHERE zone_id = @ZoneId AND venue_id = @VenueId",
                new { ZoneId = zoneId, VenueId = id });

            var affected = await conn.ExecuteAsync(
                "DELETE FROM zones WHERE id = @ZoneId AND venue_id = @VenueId",
                new { ZoneId = zoneId, VenueId = id });

            return affected > 0
                ? Results.Ok(new { deleted = true })
                : Results.NotFound(new { error = "Zone not found" });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Zones");

        // ── Assign stations to zone ─────────────────────────────
        app.MapPost("/api/venues/{id}/zones/{zoneId}/assign-stations", async (
            Guid id,
            Guid zoneId,
            AssignStationsRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsVenueOwner(db, user.UserIdGuid, id))
                return Results.Forbid();

            if (req.StationIds is null || req.StationIds.Length == 0)
                return Results.BadRequest(new { error = "station_ids required" });

            using var conn = db.CreateConnection();

            // Verify zone exists
            var zoneExists = await conn.QuerySingleAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM zones WHERE id = @ZoneId AND venue_id = @VenueId)",
                new { ZoneId = zoneId, VenueId = id });

            if (!zoneExists)
                return Results.NotFound(new { error = "Zone not found" });

            // Also update the text zone column for backward compat
            var zoneName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM zones WHERE id = @ZoneId",
                new { ZoneId = zoneId });

            var affected = await conn.ExecuteAsync(
                """
                UPDATE venue_stations
                SET zone_id = @ZoneId, zone = @ZoneName
                WHERE venue_id = @VenueId AND station_id = ANY(@StationIds)
                """,
                new { ZoneId = zoneId, ZoneName = zoneName, VenueId = id, StationIds = req.StationIds });

            return Results.Ok(new { assigned = affected });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Zones");
    }

    private static async Task<bool> IsVenueOwner(IDbConnectionFactory db, Guid? userId, Guid venueId)
    {
        if (userId is null) return false;
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM venues WHERE id = @VenueId AND owner_id = @UserId
                UNION ALL
                SELECT 1 FROM venue_staff WHERE venue_id = @VenueId AND user_id = @UserId
                    AND status = 'active' AND role IN ('owner', 'manager')
            )
            """,
            new { VenueId = venueId, UserId = userId });
    }
}

// ── Request DTOs ────────────────────────────────────────────────
public record CreateZoneRequest(
    string Name,
    string? Color = null,
    decimal? HourlyRate = null,
    int? SortOrder = null);

public record UpdateZoneRequest(
    string? Name = null,
    string? Color = null,
    decimal? HourlyRate = null,
    int? SortOrder = null,
    bool? IsActive = null);

public record AssignStationsRequest(string[] StationIds);
