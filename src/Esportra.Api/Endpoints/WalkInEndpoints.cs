using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

public static class WalkInEndpoints
{
    public static void MapWalkInEndpoints(this WebApplication app)
    {
        // ── POST /api/venues/{id}/walk-ins — add to walk-in queue ───────────
        app.MapPost("/api/venues/{id}/walk-ins", async (
            Guid id,
            AddWalkInRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            // Must have either customer_name or member_id
            if (string.IsNullOrWhiteSpace(req.CustomerName) && req.MemberId is null)
                return Results.BadRequest(new { error = "Either customer_name or member_id is required" });

            if (req.DurationMinutes is <= 0 or > 1440)
                return Results.BadRequest(new { error = "duration_minutes must be between 1 and 1440" });

            using var conn = db.CreateConnection();

            // If member_id provided, verify it belongs to this venue
            string? resolvedName = req.CustomerName;
            if (req.MemberId is not null)
            {
                var member = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT id, display_name FROM members WHERE id = @MemberId AND venue_id = @VenueId",
                    new { req.MemberId, VenueId = id });

                if (member is null)
                    return Results.BadRequest(new { error = "Member not found in this venue" });

                resolvedName ??= (string?)member.display_name;
            }

            // If zone_id provided, verify it belongs to this venue
            if (req.ZoneId is not null)
            {
                var zoneExists = await conn.QuerySingleOrDefaultAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM zones WHERE id = @ZoneId AND venue_id = @VenueId)",
                    new { req.ZoneId, VenueId = id });

                if (!zoneExists)
                    return Results.BadRequest(new { error = "Zone not found in this venue" });
            }

            var walkIn = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO walk_in_queue
                    (venue_id, member_id, customer_name, zone_id,
                     station_preference, requested_duration_minutes, position)
                VALUES
                    (@VenueId, @MemberId, @CustomerName, @ZoneId,
                     @StationPreference, @DurationMinutes,
                     (SELECT COALESCE(MAX(position), 0) + 1
                      FROM walk_in_queue
                      WHERE venue_id = @VenueId AND status = 'waiting'))
                RETURNING id, venue_id, member_id, customer_name, zone_id,
                          station_preference, requested_duration_minutes,
                          status, position, created_at
                """,
                new
                {
                    VenueId = id,
                    req.MemberId,
                    CustomerName = resolvedName,
                    req.ZoneId,
                    req.StationPreference,
                    DurationMinutes = req.DurationMinutes ?? 60,
                });

            return Results.Ok(walkIn);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("WalkIns");

        // ── GET /api/venues/{id}/walk-ins — get current queue ───────────────
        app.MapGet("/api/venues/{id}/walk-ins", async (
            Guid id,
            string? status,
            int limit = 50,
            int offset = 0,
            IDbConnectionFactory db = null!,
            HttpContext ctx = null!,
            CancellationToken ct = default) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            var filterStatus = status ?? "waiting";

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT wq.id, wq.venue_id, wq.member_id, wq.customer_name,
                       wq.zone_id, wq.station_preference, wq.requested_duration_minutes,
                       wq.status, wq.assigned_station_id, wq.assigned_at,
                       wq.position, wq.created_at,
                       z.name AS zone_name, z.color AS zone_color,
                       m.display_name AS member_display_name, m.phone AS member_phone
                FROM walk_in_queue wq
                LEFT JOIN zones z ON z.id = wq.zone_id
                LEFT JOIN members m ON m.id = wq.member_id
                WHERE wq.venue_id = @VenueId
                  AND wq.status = @Status
                ORDER BY wq.position ASC, wq.created_at ASC
                LIMIT @Limit OFFSET @Offset
                """,
                new { VenueId = id, Status = filterStatus, Limit = limit, Offset = offset });

            var total = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)
                FROM walk_in_queue
                WHERE venue_id = @VenueId AND status = @Status
                """,
                new { VenueId = id, Status = filterStatus });

            return Results.Ok(new { data = rows, total, status = filterStatus });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("WalkIns");

        // ── POST /api/venues/{id}/walk-ins/{queueId}/assign — assign station ─
        app.MapPost("/api/venues/{id}/walk-ins/{queueId}/assign", async (
            Guid id,
            Guid queueId,
            AssignWalkInRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.StationId))
                return Results.BadRequest(new { error = "station_id is required" });

            using var conn = db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                // Verify walk-in is waiting
                var walkIn = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id, member_id, customer_name, zone_id, requested_duration_minutes
                    FROM walk_in_queue
                    WHERE id = @QueueId AND venue_id = @VenueId AND status = 'waiting'
                    """,
                    new { QueueId = queueId, VenueId = id }, tx);

                if (walkIn is null)
                    return Results.NotFound(new { error = "Walk-in not found or not in waiting status" });

                // Verify station exists and is available
                var station = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT station_id, zone, zone_id, status
                    FROM venue_stations
                    WHERE venue_id = @VenueId AND station_id = @StationId
                    """,
                    new { VenueId = id, req.StationId }, tx);

                if (station is null)
                    return Results.NotFound(new { error = "Station not found" });

                if ((string?)station.status == "maintenance")
                    return Results.BadRequest(new { error = "Station is in maintenance mode" });

                // Check no active session on this station
                var activeSession = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    SELECT id FROM venue_sessions
                    WHERE venue_id = @VenueId AND station_id = @StationId AND ended_at IS NULL
                    """,
                    new { VenueId = id, req.StationId }, tx);

                if (activeSession is not null)
                    return Results.Conflict(new { error = "Station already has an active session" });

                // Resolve rate from zone
                decimal? ratePerHour = null;
                if (station.zone_id is not null)
                {
                    ratePerHour = await conn.QuerySingleOrDefaultAsync<decimal?>(
                        "SELECT hourly_rate FROM zones WHERE id = @ZoneId",
                        new { ZoneId = (Guid)station.zone_id }, tx);
                }

                int durationMinutes = (int?)walkIn.requested_duration_minutes ?? 60;
                var expiresAt = DateTimeOffset.UtcNow.AddMinutes(durationMinutes);
                string displayName = (string?)walkIn.customer_name ?? "Walk-in";

                // Start session
                var sessionId = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO venue_sessions
                        (venue_id, station_id, session_type, started_at, expires_at,
                         rate_per_hour, display_name, zone, member_id, staff_id, notes)
                    VALUES
                        (@VenueId, @StationId, 'walk_in', NOW(), @ExpiresAt,
                         @RatePerHour, @DisplayName, @Zone, @MemberId, @StaffId, @Notes)
                    RETURNING id
                    """,
                    new
                    {
                        VenueId = id,
                        req.StationId,
                        ExpiresAt = expiresAt,
                        RatePerHour = ratePerHour,
                        DisplayName = displayName,
                        Zone = (string?)station.zone,
                        MemberId = (Guid?)walkIn.member_id,
                        StaffId = user.UserIdGuid,
                        Notes = $"Walk-in queue #{queueId}",
                    }, tx);

                // Update walk-in status
                await conn.ExecuteAsync(
                    """
                    UPDATE walk_in_queue
                    SET status = 'assigned',
                        assigned_station_id = @StationId,
                        assigned_at = NOW()
                    WHERE id = @QueueId
                    """,
                    new { QueueId = queueId, req.StationId }, tx);

                tx.Commit();

                return Results.Ok(new
                {
                    queue_id = queueId,
                    session_id = sessionId,
                    station_id = req.StationId,
                    display_name = displayName,
                    expires_at = expiresAt,
                    rate_per_hour = ratePerHour,
                    status = "assigned",
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        })
        .RequireAuthorization("Authenticated")
        .WithTags("WalkIns");

        // ── POST /api/venues/{id}/walk-ins/{queueId}/cancel — remove from queue ─
        app.MapPost("/api/venues/{id}/walk-ins/{queueId}/cancel", async (
            Guid id,
            Guid queueId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE walk_in_queue
                SET status = 'cancelled'
                WHERE id = @QueueId AND venue_id = @VenueId AND status = 'waiting'
                """,
                new { QueueId = queueId, VenueId = id });

            return affected > 0
                ? Results.Ok(new { cancelled = true, queue_id = queueId })
                : Results.NotFound(new { error = "Walk-in not found or not in waiting status" });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("WalkIns");

        // ── POST /api/venues/{id}/walk-ins/auto-assign — auto-assign next in queue ─
        app.MapPost("/api/venues/{id}/walk-ins/auto-assign", async (
            Guid id,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                // Find next waiting walk-in (lowest position)
                var walkIn = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id, member_id, customer_name, zone_id, requested_duration_minutes
                    FROM walk_in_queue
                    WHERE venue_id = @VenueId AND status = 'waiting'
                    ORDER BY position ASC, created_at ASC
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """,
                    new { VenueId = id }, tx);

                if (walkIn is null)
                    return Results.NotFound(new { error = "No walk-ins waiting in queue" });

                // Find best available station:
                // 1. Prefer matching zone if walk-in has zone preference
                // 2. Station must be 'active' and have no active session
                var bestStation = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT vs.station_id, vs.zone, vs.zone_id
                    FROM venue_stations vs
                    WHERE vs.venue_id = @VenueId
                      AND vs.status = 'active'
                      AND NOT EXISTS (
                          SELECT 1 FROM venue_sessions s
                          WHERE s.venue_id = vs.venue_id
                            AND s.station_id = vs.station_id
                            AND s.ended_at IS NULL
                      )
                    ORDER BY
                        CASE WHEN @ZoneId IS NOT NULL AND vs.zone_id = @ZoneId THEN 0 ELSE 1 END,
                        vs.station_id
                    LIMIT 1
                    """,
                    new { VenueId = id, ZoneId = (Guid?)walkIn.zone_id }, tx);

                if (bestStation is null)
                    return Results.Conflict(new { error = "No available stations" });

                string stationId = (string)bestStation.station_id;

                // Resolve rate from zone
                decimal? ratePerHour = null;
                if (bestStation.zone_id is not null)
                {
                    ratePerHour = await conn.QuerySingleOrDefaultAsync<decimal?>(
                        "SELECT hourly_rate FROM zones WHERE id = @ZoneId",
                        new { ZoneId = (Guid)bestStation.zone_id }, tx);
                }

                int durationMinutes = (int?)walkIn.requested_duration_minutes ?? 60;
                var expiresAt = DateTimeOffset.UtcNow.AddMinutes(durationMinutes);
                string displayName = (string?)walkIn.customer_name ?? "Walk-in";
                Guid queueId = (Guid)walkIn.id;

                // Start session
                var sessionId = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO venue_sessions
                        (venue_id, station_id, session_type, started_at, expires_at,
                         rate_per_hour, display_name, zone, member_id, staff_id, notes)
                    VALUES
                        (@VenueId, @StationId, 'walk_in', NOW(), @ExpiresAt,
                         @RatePerHour, @DisplayName, @Zone, @MemberId, @StaffId, @Notes)
                    RETURNING id
                    """,
                    new
                    {
                        VenueId = id,
                        StationId = stationId,
                        ExpiresAt = expiresAt,
                        RatePerHour = ratePerHour,
                        DisplayName = displayName,
                        Zone = (string?)bestStation.zone,
                        MemberId = (Guid?)walkIn.member_id,
                        StaffId = user.UserIdGuid,
                        Notes = $"Auto-assigned from walk-in queue #{queueId}",
                    }, tx);

                // Update walk-in status
                await conn.ExecuteAsync(
                    """
                    UPDATE walk_in_queue
                    SET status = 'assigned',
                        assigned_station_id = @StationId,
                        assigned_at = NOW()
                    WHERE id = @QueueId
                    """,
                    new { QueueId = queueId, StationId = stationId }, tx);

                tx.Commit();

                return Results.Ok(new
                {
                    queue_id = queueId,
                    session_id = sessionId,
                    station_id = stationId,
                    display_name = displayName,
                    expires_at = expiresAt,
                    rate_per_hour = ratePerHour,
                    status = "assigned",
                    auto_assigned = true,
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        })
        .RequireAuthorization("Authenticated")
        .WithTags("WalkIns");
    }

    private static async Task<bool> IsOwnerOrStaff(IDbConnectionFactory db, Guid? userId, Guid venueId)
    {
        if (userId is null) return false;
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM venues WHERE id = @VenueId AND owner_id = @UserId
                UNION ALL
                SELECT 1 FROM venue_staff WHERE venue_id = @VenueId AND user_id = @UserId AND status = 'active'
            )
            """,
            new { VenueId = venueId, UserId = userId });
    }
}

// ── Request DTOs ────────────────────────────────────────────────
public record AddWalkInRequest(
    string? CustomerName = null,
    Guid? MemberId = null,
    Guid? ZoneId = null,
    string? StationPreference = null,
    int? DurationMinutes = 60);

public record AssignWalkInRequest(
    string StationId);
