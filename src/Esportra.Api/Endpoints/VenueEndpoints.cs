using System.Data;
using Dapper;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain 8: Venues &amp; Bookings
///
/// Key perf fix:
///   useVenueBooking had 3 separate round-trips (availability check → insert booking → decrement stations).
///   Now a single transaction: check + insert + decrement.
///
///   useVenueImpressions aggregated client-side after fetching all rows.
///   Now server-side GROUP BY aggregation.
/// </summary>
public static class VenueEndpoints
{
    public static void MapVenueEndpoints(this WebApplication app)
    {
        // ── GET /api/venues — text/city search ────────────────────────────────
        app.MapGet("/api/venues", async (
            string?              q,
            string?              city,
            bool?                includeOwned,
            int                  limit  = 20,
            int                  offset = 0,
            IDbConnectionFactory db     = null!,
            HybridCache          cache  = null!,
            HttpContext          ctx    = null!,
            CancellationToken    ct     = default) =>
        {
            var cacheKey = $"venues:{q}:{city}:{includeOwned}:{limit}:{offset}";
            return await cache.GetOrCreateAsync(cacheKey, async (_) =>
            {
                using var conn = db.CreateConnection();
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, name, description, address, city, country,
                           status, price_per_hour, price_range, games,
                           image_url, logo_url, latitude, longitude,
                           total_stations, open_now, created_at
                    FROM venues
                    WHERE (@includeOwned = TRUE OR status = 'published')
                      AND (@q IS NULL OR name ILIKE '%' || @q || '%' OR description ILIKE '%' || @q || '%')
                      AND (@city IS NULL OR city ILIKE '%' || @city || '%')
                    ORDER BY name ASC
                    LIMIT @limit OFFSET @offset
                    """,
                    new { q, city, includeOwned = includeOwned ?? false, limit, offset });
                return Results.Ok(rows);
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) },
            cancellationToken: ct);
        });

        // ── GET /api/venues/nearby — Haversine RPC ────────────────────────────
        // Delegates to existing find_nearby_venues DB function.
        app.MapGet("/api/venues/nearby", async (
            double               lat,
            double               lng,
            double               radiusKm   = 50,
            IDbConnectionFactory db         = null!,
            CancellationToken    ct         = default) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT * FROM find_nearby_venues(@lat, @lng, @radiusKm)",
                new { lat, lng, radiusKm });
            return Results.Ok(rows);
        });

        // ── GET /api/venues/{id}/live-status ──────────────────────────────────
        // Initial state fetch for useVenueLiveStatus (real-time updates via LiveHub).
        app.MapGet("/api/venues/{id}/live-status", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT seats_total, seats_occupied, is_open, updated_at
                FROM venue_live_status
                WHERE venue_id = @id
                """,
                new { id });
            return row is null ? Results.NotFound() : Results.Ok(row);
        });

        // ── GET /api/venues/{id}/availability ─────────────────────────────────
        app.MapGet("/api/venues/{id}/availability", async (
            Guid                 id,
            string?              date,
            string?              time,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, available_stations, price_per_hour, is_available
                FROM venue_availability
                WHERE venue_id = @id
                  AND (@date IS NULL OR date = @date::date)
                  AND (@time IS NULL OR start_time = @time::time)
                """,
                new { id, date, time });
            return Results.Ok(row);
        });

        // ── POST /api/venues/{id}/bookings — atomic check + insert + decrement
        app.MapPost("/api/venues/{id}/bookings", async (
            Guid                           id,
            [FromBody] CreateBookingRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx   = conn.BeginTransaction();
            try
            {
                // 1. Check availability
                var avail = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id, available_stations, price_per_hour, is_available
                    FROM venue_availability
                    WHERE venue_id = @id AND date = @date::date AND start_time = @startTime::time
                    """,
                    new { id, date = req.Date, startTime = req.StartTime }, tx);

                decimal effectivePrice = req.PricePerHour;

                if (avail is not null)
                {
                    if (!(bool)avail.is_available || (int)avail.available_stations < req.Stations)
                        return Results.BadRequest(new { error = "Slot not available or insufficient stations." });
                    effectivePrice = (decimal)avail.price_per_hour;
                }
                else if (req.Stations > req.AvailableStations)
                {
                    return Results.BadRequest(new { error = $"Only {req.AvailableStations} stations available." });
                }

                // 2. Calculate end time
                var start   = TimeOnly.Parse(req.StartTime);
                var end     = start.AddHours(req.Hours);
                var endStr  = end.ToString("HH:mm");
                var total   = effectivePrice * req.Hours * req.Stations;

                // 3. Insert booking
                var booking = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO venue_bookings
                        (venue_id, user_id, booking_date, start_time, end_time,
                         duration_hours, stations_booked, total_amount, status,
                         special_requests, contact_phone, contact_email)
                    VALUES
                        (@venueId, @userId, @date::date, @startTime::time, @endTime::time,
                         @hours, @stations, @total, 'pending',
                         @specialRequests, @contactPhone, @contactEmail)
                    RETURNING *
                    """,
                    new
                    {
                        venueId        = id,
                        userId         = userCtx.UserIdGuid,
                        date           = req.Date,
                        startTime      = req.StartTime,
                        endTime        = endStr,
                        hours          = req.Hours,
                        stations       = req.Stations,
                        total,
                        specialRequests = req.SpecialRequests,
                        contactPhone   = req.ContactPhone,
                        contactEmail   = req.ContactEmail,
                    }, tx);

                // 4. Decrement stations if availability record exists
                if (avail is not null)
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE venue_availability
                        SET available_stations = available_stations - @stations
                        WHERE venue_id = @id AND date = @date::date AND start_time = @startTime::time
                        """,
                        new { id, date = req.Date, startTime = req.StartTime, stations = req.Stations }, tx);
                }

                tx.Commit();
                return Results.Ok(booking);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/venues/bookings/{bookingId} — cancel ──────────────────
        app.MapDelete("/api/venues/bookings/{bookingId}", async (
            Guid                 bookingId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.ExecuteAsync(
                "UPDATE venue_bookings SET status = 'cancelled' WHERE id = @bookingId AND user_id = @userId",
                new { bookingId, userId = userCtx.UserIdGuid });

            return rows == 0 ? Results.NotFound() : Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/venues/{id}/impressions — fire-and-forget ───────────────
        app.MapPost("/api/venues/{id}/impressions", async (
            Guid                              id,
            [FromBody] TrackImpressionRequest req,
            HttpContext                       ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "INSERT INTO venue_impressions (venue_id, event_type, user_id) VALUES (@id, @eventType, @userId)",
                new { id, eventType = req.EventType, userId = userCtx?.UserIdGuid });
            return Results.Ok();
        });

        // ── GET /api/venues/{id}/impressions — daily aggregated ───────────────
        // Server-side GROUP BY: eliminates client-side loop over raw rows.
        app.MapGet("/api/venues/{id}/impressions", async (
            Guid                 id,
            int                  days  = 30,
            IDbConnectionFactory db    = null!,
            CancellationToken    ct    = default) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT DATE(created_at) AS date,
                       COUNT(*) FILTER (WHERE event_type = 'view')          AS views,
                       COUNT(*) FILTER (WHERE event_type = 'booking_click') AS booking_clicks
                FROM venue_impressions
                WHERE venue_id = @id
                  AND created_at >= NOW() - (@days || ' days')::interval
                  AND event_type IN ('view', 'booking_click')
                GROUP BY DATE(created_at)
                ORDER BY DATE(created_at) ASC
                """,
                new { id, days });
            return Results.Ok(rows);
        });

        // ── GET /api/venues/{id}/impressions/totals ───────────────────────────
        app.MapGet("/api/venues/{id}/impressions/totals", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    COUNT(*) FILTER (WHERE event_type = 'view')          AS views,
                    COUNT(*) FILTER (WHERE event_type = 'card_view')     AS card_views,
                    COUNT(*) FILTER (WHERE event_type = 'booking_click') AS booking_clicks,
                    COUNT(*) FILTER (WHERE event_type = 'contact_click') AS contact_clicks
                FROM venue_impressions
                WHERE venue_id = @id
                """,
                new { id });
            return Results.Ok(row);
        });
    }
}

// ── Request records ────────────────────────────────────────────────────────────

public sealed record CreateBookingRequest(
    string   Date,
    string   StartTime,
    int      Hours,
    int      Stations,
    decimal  PricePerHour,
    int      AvailableStations,
    string?  SpecialRequests = null,
    string?  ContactPhone    = null,
    string?  ContactEmail    = null);

public sealed record TrackImpressionRequest(string EventType);
