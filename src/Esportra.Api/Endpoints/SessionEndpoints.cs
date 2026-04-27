using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        // ── Active sessions ─────────────────────────────────────
        app.MapGet("/api/venues/{id}/sessions/active", async (
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
            var sessions = await conn.QueryAsync<dynamic>(
                """
                SELECT s.id, s.station_id, s.session_type, s.started_at, s.expires_at,
                       s.rate_per_hour, s.total_charged, s.display_name, s.zone,
                       s.member_id, s.staff_id, s.package_id, s.notes,
                       EXTRACT(EPOCH FROM (NOW() - s.started_at)) / 60 AS elapsed_minutes,
                       vs.label AS station_label,
                       z.name AS zone_name, z.color AS zone_color, z.hourly_rate AS zone_rate
                FROM venue_sessions s
                LEFT JOIN venue_stations vs ON vs.venue_id = s.venue_id AND vs.station_id = s.station_id
                LEFT JOIN zones z ON z.id = vs.zone_id
                WHERE s.venue_id = @VenueId AND s.ended_at IS NULL
                ORDER BY s.started_at DESC
                """,
                new { VenueId = id });

            return Results.Ok(sessions);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Sessions");

        // ── Start session ───────────────────────────────────────
        app.MapPost("/api/venues/{id}/sessions/start", async (
            Guid id,
            StartSessionRequest req,
            IDbConnectionFactory db,
            BillingService billing,
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

            // Check station exists and is active
            var station = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT station_id, zone, zone_id, status
                FROM venue_stations
                WHERE venue_id = @VenueId AND station_id = @StationId
                """,
                new { VenueId = id, req.StationId });

            if (station is null)
                return Results.NotFound(new { error = "Station not found" });

            if ((string?)station.status == "maintenance")
                return Results.BadRequest(new { error = "Station is in maintenance mode" });

            // Check no active session on this station
            var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT id FROM venue_sessions
                WHERE venue_id = @VenueId AND station_id = @StationId AND ended_at IS NULL
                """,
                new { VenueId = id, req.StationId });

            if (existing is not null)
                return Results.Conflict(new { error = "Station already has an active session" });

            // Resolve rate from zone if not provided
            decimal? ratePerHour = req.RatePerHour;
            if (ratePerHour is null && station.zone_id is not null)
            {
                ratePerHour = await conn.QuerySingleOrDefaultAsync<decimal?>(
                    "SELECT hourly_rate FROM zones WHERE id = @ZoneId",
                    new { ZoneId = (Guid)station.zone_id });
            }

            var sessionType = req.SessionType ?? "hourly";
            var expiresAt = req.DurationMinutes > 0
                ? DateTimeOffset.UtcNow.AddMinutes(req.DurationMinutes)
                : DateTimeOffset.UtcNow.AddHours(24); // default 24h max

            var sessionId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO venue_sessions
                    (venue_id, station_id, session_type, started_at, expires_at,
                     rate_per_hour, display_name, zone, member_id, staff_id,
                     package_id, notes)
                VALUES
                    (@VenueId, @StationId, @SessionType, NOW(), @ExpiresAt,
                     @RatePerHour, @DisplayName, @Zone, @MemberId, @StaffId,
                     @PackageId, @Notes)
                RETURNING id
                """,
                new
                {
                    VenueId = id,
                    req.StationId,
                    SessionType = sessionType,
                    ExpiresAt = expiresAt,
                    RatePerHour = ratePerHour,
                    DisplayName = req.DisplayName,
                    Zone = (string?)station.zone,
                    MemberId = req.MemberId,
                    StaffId = user.UserIdGuid,
                    PackageId = req.PackageId,
                    Notes = req.Notes,
                });

            // Log session event
            await conn.ExecuteAsync(
                """
                INSERT INTO venue_session_events (session_id, event_type, metadata)
                VALUES (@SessionId, 'started', @Metadata::jsonb)
                """,
                new
                {
                    SessionId = sessionId,
                    Metadata = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        started_by = user.UserIdGuid,
                        session_type = sessionType,
                        station_id = req.StationId,
                    }),
                });

            return Results.Ok(new
            {
                session_id = sessionId,
                station_id = req.StationId,
                session_type = sessionType,
                started_at = DateTimeOffset.UtcNow,
                expires_at = expiresAt,
                rate_per_hour = ratePerHour,
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Sessions");

        // ── End session ─────────────────────────────────────────
        app.MapPost("/api/venues/{id}/sessions/{sessionId}/end", async (
            Guid id,
            Guid sessionId,
            EndSessionRequest? req,
            IDbConnectionFactory db,
            BillingService billing,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            // Verify session exists and is active
            var session = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, station_id, started_at, ended_at
                FROM venue_sessions
                WHERE id = @SessionId AND venue_id = @VenueId
                """,
                new { SessionId = sessionId, VenueId = id });

            if (session is null)
                return Results.NotFound(new { error = "Session not found" });

            if (session.ended_at is not null)
                return Results.BadRequest(new { error = "Session already ended" });

            // Calculate bill
            var bill = await billing.CalculateSessionBillAsync(id, sessionId, ct);

            // End the session
            await conn.ExecuteAsync(
                """
                UPDATE venue_sessions
                SET ended_at = NOW(),
                    total_charged = @Total,
                    ended_by = @EndedBy,
                    duration_minutes = EXTRACT(EPOCH FROM (NOW() - started_at)) / 60
                WHERE id = @SessionId
                """,
                new { SessionId = sessionId, Total = bill.Total, EndedBy = user.UserIdGuid });

            // Create invoice
            var invoiceId = await billing.CreateSessionInvoiceAsync(
                id, sessionId, bill,
                req?.PaymentMethod, user.UserIdGuid, req?.Notes, ct);

            // Log session event
            await conn.ExecuteAsync(
                """
                INSERT INTO venue_session_events (session_id, event_type, metadata)
                VALUES (@SessionId, 'ended', @Metadata::jsonb)
                """,
                new
                {
                    SessionId = sessionId,
                    Metadata = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        ended_by = user.UserIdGuid,
                        total = bill.Total,
                        duration_minutes = bill.DurationMinutes,
                        billing_type = bill.BillingType,
                        invoice_id = invoiceId,
                    }),
                });

            return Results.Ok(new
            {
                session_id = sessionId,
                station_id = (string)session.station_id,
                total = bill.Total,
                duration_minutes = bill.DurationMinutes,
                billing_type = bill.BillingType,
                description = bill.Description,
                invoice_id = invoiceId,
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Sessions");

        // ── Extend session ──────────────────────────────────────
        app.MapPost("/api/venues/{id}/sessions/{sessionId}/extend", async (
            Guid id,
            Guid sessionId,
            ExtendSessionRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            if (req.AdditionalMinutes is <= 0 or > 1440)
                return Results.BadRequest(new { error = "Additional minutes must be 1-1440" });

            using var conn = db.CreateConnection();

            var session = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, expires_at, ended_at FROM venue_sessions WHERE id = @Id AND venue_id = @VenueId",
                new { Id = sessionId, VenueId = id });

            if (session is null)
                return Results.NotFound(new { error = "Session not found" });

            if (session.ended_at is not null)
                return Results.BadRequest(new { error = "Session already ended" });

            var currentExpiry = (DateTimeOffset)session.expires_at;
            var baseTime = currentExpiry > DateTimeOffset.UtcNow ? currentExpiry : DateTimeOffset.UtcNow;
            var newExpiry = baseTime.AddMinutes(req.AdditionalMinutes);

            await conn.ExecuteAsync(
                "UPDATE venue_sessions SET expires_at = @NewExpiry WHERE id = @Id",
                new { Id = sessionId, NewExpiry = newExpiry });

            await conn.ExecuteAsync(
                """
                INSERT INTO venue_session_events (session_id, event_type, metadata)
                VALUES (@SessionId, 'extended', @Metadata::jsonb)
                """,
                new
                {
                    SessionId = sessionId,
                    Metadata = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        extended_by = user.UserIdGuid,
                        additional_minutes = req.AdditionalMinutes,
                        new_expires_at = newExpiry,
                    }),
                });

            return Results.Ok(new
            {
                session_id = sessionId,
                new_expires_at = newExpiry,
                additional_minutes = req.AdditionalMinutes,
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Sessions");

        // ── Session detail ──────────────────────────────────────
        app.MapGet("/api/venues/{id}/sessions/{sessionId}", async (
            Guid id,
            Guid sessionId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var session = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT s.*, vs.label AS station_label,
                       z.name AS zone_name, z.color AS zone_color, z.hourly_rate AS zone_rate,
                       EXTRACT(EPOCH FROM (COALESCE(s.ended_at, NOW()) - s.started_at)) / 60 AS elapsed_minutes
                FROM venue_sessions s
                LEFT JOIN venue_stations vs ON vs.venue_id = s.venue_id AND vs.station_id = s.station_id
                LEFT JOIN zones z ON z.id = vs.zone_id
                WHERE s.id = @SessionId AND s.venue_id = @VenueId
                """,
                new { SessionId = sessionId, VenueId = id });

            if (session is null)
                return Results.NotFound(new { error = "Session not found" });

            var events = await conn.QueryAsync<dynamic>(
                """
                SELECT id, event_type, metadata, created_at
                FROM venue_session_events
                WHERE session_id = @SessionId
                ORDER BY created_at ASC
                """,
                new { SessionId = sessionId });

            var invoice = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, subtotal, tax, discount, total, payment_method, status, paid_at, line_items
                FROM session_invoices
                WHERE session_id = @SessionId
                """,
                new { SessionId = sessionId });

            return Results.Ok(new { session, events, invoice });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Sessions");

        // ── Session history ─────────────────────────────────────
        app.MapGet("/api/venues/{id}/sessions/history", async (
            Guid id,
            string? from,
            string? to,
            string? station_id,
            string? session_type,
            int page = 1,
            int pageSize = 20,
            IDbConnectionFactory db = null!,
            HttpContext ctx = null!,
            CancellationToken ct = default) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var offset = (page - 1) * pageSize;

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT s.id, s.station_id, s.session_type, s.started_at, s.ended_at,
                       s.total_charged, s.display_name, s.zone, s.duration_minutes,
                       vs.label AS station_label,
                       z.name AS zone_name
                FROM venue_sessions s
                LEFT JOIN venue_stations vs ON vs.venue_id = s.venue_id AND vs.station_id = s.station_id
                LEFT JOIN zones z ON z.id = vs.zone_id
                WHERE s.venue_id = @VenueId
                  AND s.ended_at IS NOT NULL
                  AND (@From IS NULL OR s.started_at >= @From::timestamptz)
                  AND (@To IS NULL OR s.started_at < @To::timestamptz)
                  AND (@StationId IS NULL OR s.station_id = @StationId)
                  AND (@SessionType IS NULL OR s.session_type = @SessionType)
                ORDER BY s.started_at DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new { VenueId = id, From = from, To = to, StationId = station_id, SessionType = session_type, Limit = pageSize, Offset = offset });

            var total = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)
                FROM venue_sessions
                WHERE venue_id = @VenueId
                  AND ended_at IS NOT NULL
                  AND (@From IS NULL OR started_at >= @From::timestamptz)
                  AND (@To IS NULL OR started_at < @To::timestamptz)
                  AND (@StationId IS NULL OR station_id = @StationId)
                  AND (@SessionType IS NULL OR session_type = @SessionType)
                """,
                new { VenueId = id, From = from, To = to, StationId = station_id, SessionType = session_type });

            return Results.Ok(new { data = rows, total, page, page_size = pageSize });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Sessions");
    }

    // ── Helper: check venue owner or active staff ──────────────
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
public record StartSessionRequest(
    string StationId,
    string? SessionType = "hourly",
    int DurationMinutes = 0,
    decimal? RatePerHour = null,
    string? DisplayName = null,
    Guid? MemberId = null,
    string? PackageId = null,
    string? Notes = null);

public record EndSessionRequest(
    string? PaymentMethod = "cash",
    string? Notes = null);

public record ExtendSessionRequest(
    int AdditionalMinutes = 30);
