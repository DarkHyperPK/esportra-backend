using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain 8: Venues & Bookings
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
        // ── GET /api/venues — list / search —————————————————————————————————
        // Supports: ?q=, ?city=, ?country=, ?owner_id=, ?owned=true, ?limit=, ?offset=
        app.MapGet("/api/venues", async (
            string?              q,
            string?              city,
            string?              country,
            Guid?                owner_id,
            bool?                owned,
            int                  limit  = 20,
            int                  offset = 0,
            IDbConnectionFactory db     = null!,
            HttpContext          ctx    = null!,
            CancellationToken    ct     = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            // If owned=true, resolve owner from JWT
            Guid? effectiveOwnerId = owner_id;
            if (owned == true && effectiveOwnerId is null)
            {
                var userCtx = ctx.Items["UserContext"] as UserContext;
                effectiveOwnerId = userCtx?.UserIdGuid;
            }

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, name, description, address, city, state, country, postal_code,
                       slug, venue_id, status, stations, hours, games,
                       price_per_hour, currency, images, card_image, amenities, pc_specs,
                       owner_id, latitude, longitude, subscription_tier,
                       rejection_reason, submitted_at, published_at, created_at
                FROM venues
                WHERE deleted_at IS NULL
                  AND (@ownerId IS NULL OR owner_id = @ownerId)
                  AND (@ownerId IS NOT NULL OR status = 'published')
                  AND (@q IS NULL OR name ILIKE '%' || @q || '%' OR description ILIKE '%' || @q || '%')
                  AND (@city IS NULL OR city ILIKE '%' || @city || '%')
                  AND (@country IS NULL OR country ILIKE '%' || @country || '%')
                ORDER BY created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { ownerId = effectiveOwnerId, q, city, country, limit, offset });
            return Results.Ok(rows);
        });

        // ── GET /api/venues/filters — distinct cities/countries with content ─
        app.MapGet("/api/venues/filters", async (
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            var result = await cache.GetOrCreateAsync("venue-filters", async _ =>
            {
                using var conn = db.CreateConnection();
                var cities = (await conn.QueryAsync<string>(
                    """
                    SELECT DISTINCT city FROM venues
                    WHERE status = 'published' AND deleted_at IS NULL
                      AND city IS NOT NULL AND city <> ''
                    ORDER BY city
                    """)).AsList();
                var countries = (await conn.QueryAsync<string>(
                    """
                    SELECT DISTINCT country FROM venues
                    WHERE status = 'published' AND deleted_at IS NULL
                      AND country IS NOT NULL AND country <> ''
                    ORDER BY country
                    """)).AsList();
                return new { cities, countries };
            }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) }, cancellationToken: ct);
            return Results.Ok(result);
        });

        // ── GET /api/venues/nearby — Haversine RPC ————————————————————————
        app.MapGet("/api/venues/nearby", async (
            double               lat,
            double               lng,
            double               radiusKm   = 50,
            IDbConnectionFactory db         = null!,
            CancellationToken    ct         = default) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT v.*,
                       ROUND((6371 * acos(
                         LEAST(1.0,
                           cos(radians(@lat)) * cos(radians(v.latitude)) *
                           cos(radians(v.longitude) - radians(@lng)) +
                           sin(radians(@lat)) * sin(radians(v.latitude))
                         )
                       ))::numeric, 1) AS distance_km
                FROM public.venues v
                WHERE v.status = 'published'
                  AND v.deleted_at IS NULL
                  AND v.latitude IS NOT NULL
                  AND v.longitude IS NOT NULL
                  AND (6371 * acos(
                    LEAST(1.0,
                      cos(radians(@lat)) * cos(radians(v.latitude)) *
                      cos(radians(v.longitude) - radians(@lng)) +
                      sin(radians(@lat)) * sin(radians(v.latitude))
                    )
                  )) <= @radiusKm
                ORDER BY distance_km ASC
                """,
                new { lat, lng, radiusKm });
            return Results.Ok(rows);
        });

        // ── GET /api/venues/{slugOrId} — single venue by slug or UUID ——————
        app.MapGet("/api/venues/{slugOrId}", async (
            string               slugOrId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            dynamic? row;
            if (Guid.TryParse(slugOrId, out var guidId))
            {
                row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT * FROM venues WHERE id = @id AND deleted_at IS NULL",
                    new { id = guidId });
            }
            else
            {
                row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT * FROM venues WHERE slug = @slug AND deleted_at IS NULL",
                    new { slug = slugOrId });
            }

            return row is null ? Results.NotFound() : Results.Ok(row);
        });

        // ── POST /api/venues — create ——————————————————————————————————————
        app.MapPost("/api/venues", async (
            [FromBody] CreateVenueRequest req,
            HttpContext                   ctx,
            IDbConnectionFactory          db,
            CancellationToken             ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Generate a unique venue_id (VN-XXXXX format)
            var venueIdStr = $"VN-{Guid.NewGuid().ToString("N")[..6].ToUpper()}";

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO venues
                    (name, description, address, city, state, country, postal_code,
                     stations, hours, games, amenities, images, card_image, pc_specs,
                     slug, venue_id, owner_id, contact_email, contact_phone,
                     price_per_hour, currency, latitude, longitude, status, submitted_at)
                VALUES
                    (@name, @description, @address, @city, @state, @country, @postalCode,
                     @stations, @hours, @games, @amenities, @images, @cardImage, @pcSpecs::jsonb,
                     @slug, @venueId, @ownerId, @contactEmail, @contactPhone,
                     @pricePerHour, @currency, @latitude, @longitude, @status, @submittedAt::timestamptz)
                RETURNING id, name, description, address, city, state, country, postal_code,
                         stations, hours, games, amenities, images, card_image, pc_specs,
                         slug, venue_id, owner_id, contact_email, contact_phone,
                         price_per_hour, currency, latitude, longitude, status, submitted_at, created_at
                """,
                new
                {
                    name         = req.Name,
                    description  = req.Description,
                    address      = req.Address,
                    city         = req.City,
                    state        = req.State,
                    country      = req.Country,
                    postalCode   = req.PostalCode,
                    stations     = req.Stations,
                    hours        = req.Hours,
                    games        = req.Games,
                    amenities    = req.Amenities ?? Array.Empty<string>(),
                    images       = req.Images ?? Array.Empty<string>(),
                    cardImage    = req.CardImage,
                    pcSpecs      = req.PcSpecs is not null
                                       ? JsonSerializer.Serialize(req.PcSpecs)
                                       : "{}",
                    slug         = req.Slug,
                    venueId      = venueIdStr,
                    ownerId      = userCtx.UserIdGuid,
                    contactEmail = req.ContactEmail,
                    contactPhone = req.ContactPhone,
                    pricePerHour = req.PricePerHour,
                    currency     = req.Currency ?? "USD",
                    latitude     = req.Latitude,
                    longitude    = req.Longitude,
                    status       = req.Status ?? "draft",
                    submittedAt  = req.SubmittedAt,
                });

            return Results.Created($"/api/venues/{row.slug}", row);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/venues/{id} — owner update ————————————————————————————
        app.MapPut("/api/venues/{id}", async (
            Guid                          id,
            [FromBody] UpdateVenueRequest req,
            HttpContext                   ctx,
            IDbConnectionFactory          db,
            CancellationToken             ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify ownership
            var ownerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT owner_id FROM venues WHERE id = @id AND deleted_at IS NULL",
                new { id });

            if (ownerId is null)
                return Results.NotFound();
            if (ownerId != userCtx.UserIdGuid)
                return Results.Forbid();

            // Build dynamic SET clauses
            var setClauses = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("id", id);

            if (req.Name is not null)         { setClauses.Add("name = @name");                   parameters.Add("name", req.Name); }
            if (req.Description is not null)  { setClauses.Add("description = @description");     parameters.Add("description", req.Description); }
            if (req.Address is not null)      { setClauses.Add("address = @address");             parameters.Add("address", req.Address); }
            if (req.City is not null)         { setClauses.Add("city = @city");                   parameters.Add("city", req.City); }
            if (req.State is not null)        { setClauses.Add("state = @state");                 parameters.Add("state", req.State); }
            if (req.Country is not null)      { setClauses.Add("country = @country");             parameters.Add("country", req.Country); }
            if (req.Stations.HasValue)        { setClauses.Add("stations = @stations");           parameters.Add("stations", req.Stations.Value); }
            if (req.Hours is not null)        { setClauses.Add("hours = @hours");                 parameters.Add("hours", req.Hours); }
            if (req.Games is not null)        { setClauses.Add("games = @games");                 parameters.Add("games", req.Games); }
            if (req.ContactEmail is not null) { setClauses.Add("contact_email = @contactEmail");  parameters.Add("contactEmail", req.ContactEmail); }
            if (req.ContactPhone is not null) { setClauses.Add("contact_phone = @contactPhone");  parameters.Add("contactPhone", req.ContactPhone); }
            if (req.Images is not null)       { setClauses.Add("images = @images");               parameters.Add("images", req.Images); }
            if (req.CardImage is not null)    { setClauses.Add("card_image = @cardImage");        parameters.Add("cardImage", req.CardImage); }
            if (req.PricePerHour.HasValue)    { setClauses.Add("price_per_hour = @pricePerHour"); parameters.Add("pricePerHour", req.PricePerHour.Value); }
            if (req.Currency is not null)     { setClauses.Add("currency = @currency");           parameters.Add("currency", req.Currency); }
            if (req.Amenities is not null)    { setClauses.Add("amenities = @amenities");         parameters.Add("amenities", req.Amenities); }
            if (req.PcSpecs is not null)      { setClauses.Add("pc_specs = @pcSpecs::jsonb");     parameters.Add("pcSpecs", JsonSerializer.Serialize(req.PcSpecs)); }
            if (req.PostalCode is not null)   { setClauses.Add("postal_code = @postalCode");      parameters.Add("postalCode", req.PostalCode); }
            if (req.Latitude.HasValue)        { setClauses.Add("latitude = @latitude");           parameters.Add("latitude", req.Latitude.Value); }
            if (req.Longitude.HasValue)       { setClauses.Add("longitude = @longitude");         parameters.Add("longitude", req.Longitude.Value); }

            if (req.Status is not null)
            {
                // Owner can only set draft → pending_review or pending_review → draft
                if (req.Status == "pending_review" || req.Status == "draft")
                {
                    setClauses.Add("status = @status");
                    parameters.Add("status", req.Status);
                    if (req.Status == "pending_review")
                    {
                        setClauses.Add("submitted_at = NOW()");
                    }
                }
            }

            if (setClauses.Count == 0)
                return Results.BadRequest(new { error = "No fields to update." });

            setClauses.Add("updated_at = NOW()");

            var sql = $"UPDATE venues SET {string.Join(", ", setClauses)} WHERE id = @id RETURNING id, name, description, address, city, state, country, postal_code, stations, hours, games, amenities, images, card_image, pc_specs, slug, venue_id, owner_id, contact_email, contact_phone, price_per_hour, currency, latitude, longitude, status, created_at, updated_at";
            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, parameters);

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/admin/venues — admin list with status filter ————————
        app.MapGet("/api/admin/venues", async (
            string?              status,
            int                  limit  = 50,
            int                  offset = 0,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT v.*, p.full_name AS owner_name, p.email AS owner_email
                FROM venues v
                LEFT JOIN profiles p ON p.id = v.owner_id
                WHERE v.deleted_at IS NULL
                  AND (@status IS NULL OR v.status = @status)
                ORDER BY v.submitted_at DESC NULLS LAST, v.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { status, limit, offset });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/venues/{id} — admin update (no ownership check) —
        app.MapPut("/api/admin/venues/{id}", async (
            Guid                          id,
            [FromBody] UpdateVenueRequest req,
            IDbConnectionFactory          db,
            CancellationToken             ct) =>
        {
            using var conn = db.CreateConnection();

            var setClauses = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("id", id);

            if (req.Name is not null)         { setClauses.Add("name = @name");                   parameters.Add("name", req.Name); }
            if (req.Description is not null)  { setClauses.Add("description = @description");     parameters.Add("description", req.Description); }
            if (req.Address is not null)      { setClauses.Add("address = @address");             parameters.Add("address", req.Address); }
            if (req.City is not null)         { setClauses.Add("city = @city");                   parameters.Add("city", req.City); }
            if (req.Stations.HasValue)        { setClauses.Add("stations = @stations");           parameters.Add("stations", req.Stations.Value); }
            if (req.Hours is not null)        { setClauses.Add("hours = @hours");                 parameters.Add("hours", req.Hours); }
            if (req.Games is not null)        { setClauses.Add("games = @games");                 parameters.Add("games", req.Games); }
            if (req.ContactEmail is not null) { setClauses.Add("contact_email = @contactEmail");  parameters.Add("contactEmail", req.ContactEmail); }
            if (req.ContactPhone is not null) { setClauses.Add("contact_phone = @contactPhone");  parameters.Add("contactPhone", req.ContactPhone); }
            if (req.Images is not null)       { setClauses.Add("images = @images");               parameters.Add("images", req.Images); }
            if (req.PricePerHour.HasValue)    { setClauses.Add("price_per_hour = @pricePerHour"); parameters.Add("pricePerHour", req.PricePerHour.Value); }
            if (req.Status is not null)          { setClauses.Add("status = @status");                     parameters.Add("status", req.Status); }
            if (req.RejectionReason is not null)  { setClauses.Add("rejection_reason = @rejectionReason");   parameters.Add("rejectionReason", req.RejectionReason); }
            if (req.ReviewedBy is not null)       { setClauses.Add("reviewed_by = @reviewedBy");            parameters.Add("reviewedBy", Guid.Parse(req.ReviewedBy)); }
            if (req.ReviewedAt is not null)       { setClauses.Add("reviewed_at = @reviewedAt::timestamptz"); parameters.Add("reviewedAt", req.ReviewedAt); }
            if (req.PublishedAt is not null)      { setClauses.Add("published_at = @publishedAt::timestamptz"); parameters.Add("publishedAt", req.PublishedAt); }

            if (setClauses.Count == 0)
                return Results.BadRequest(new { error = "No fields to update." });

            setClauses.Add("updated_at = NOW()");

            var sql = $"UPDATE venues SET {string.Join(", ", setClauses)} WHERE id = @id AND deleted_at IS NULL RETURNING id, name, description, address, city, state, country, postal_code, stations, hours, games, amenities, images, card_image, pc_specs, slug, venue_id, owner_id, contact_email, contact_phone, price_per_hour, status, created_at, updated_at";
            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, parameters);

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization("Admin");

        // ── DELETE /api/venues/{id} — soft delete (owner only) —————————————
        app.MapDelete("/api/venues/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.ExecuteAsync(
                "UPDATE venues SET deleted_at = NOW(), status = 'archived' WHERE id = @id AND owner_id = @ownerId AND deleted_at IS NULL",
                new { id, ownerId = userCtx.UserIdGuid });

            return rows == 0 ? Results.NotFound() : Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{id}/live-status ————————————————————————————————
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

        // ── GET /api/venues/{id}/availability ——————————————————————————————
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
                    RETURNING id, venue_id, user_id, booking_date, start_time, end_time,
                             duration_hours, stations_booked, total_amount, status,
                             special_requests, contact_phone, contact_email, created_at
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

        // ── DELETE /api/venues/bookings/{bookingId} — cancel —————————————————
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

        // ── POST /api/venues/{id}/impressions — fire-and-forget ————————————
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

        // ── GET /api/venues/{id}/impressions — daily aggregated ————————————
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

        // ── GET /api/venues/{id}/impressions/totals ————————————————————————
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

        // ── GET /api/bookings/{id} ────────────────────────────────────────────
        app.MapGet("/api/bookings/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var booking = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT vb.*, v.name AS venue_name, v.address AS venue_address,
                       p.username AS booker_name
                FROM venue_bookings vb
                LEFT JOIN venues v ON v.id = vb.venue_id
                LEFT JOIN profiles p ON p.id = vb.user_id
                WHERE vb.id = @id
                  AND (vb.user_id = @userId OR v.owner_id = @userId)
                """, new { id, userId = userCtx.UserIdGuid });
            return booking is null ? Results.NotFound() : Results.Ok(booking);
        }).RequireAuthorization("Authenticated");
    }
}

// ── Request records ————————————————————————————————————————————————————————————

public sealed record CreateVenueRequest(
    string    Name,
    string    Address,
    string    City,
    string    Country,
    string?   Description     = null,
    string?   State           = null,
    string?   PostalCode      = null,
    int       Stations        = 0,
    string?   Hours           = null,
    string?   Games           = null,
    string[]? Amenities       = null,
    string[]? Images          = null,
    string?   CardImage       = null,
    object?   PcSpecs         = null,
    string?   Slug            = null,
    string?   ContactEmail    = null,
    string?   ContactPhone    = null,
    decimal   PricePerHour    = 0,
    string?   Currency        = null,
    double?   Latitude        = null,
    double?   Longitude       = null,
    string?   Status          = null,
    string?   SubmittedAt     = null);

public sealed record UpdateVenueRequest(
    string?   Name            = null,
    string?   Description     = null,
    string?   Address         = null,
    string?   City            = null,
    string?   State           = null,
    string?   Country         = null,
    string?   PostalCode      = null,
    int?      Stations        = null,
    string?   Hours           = null,
    string?   Games           = null,
    string?   ContactEmail    = null,
    string?   ContactPhone    = null,
    string[]? Images          = null,
    string?   CardImage       = null,
    decimal?  PricePerHour    = null,
    string?   Currency        = null,
    string[]? Amenities       = null,
    object?   PcSpecs         = null,
    double?   Latitude        = null,
    double?   Longitude       = null,
    string?   Status          = null,
    string?   RejectionReason = null,
    string?   PriceRange      = null,
    string?   ReviewedBy      = null,
    string?   ReviewedAt      = null,
    string?   PublishedAt     = null);

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
