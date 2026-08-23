using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
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
            string? q,
            string? city,
            string? country,
            Guid? owner_id,
            bool? owned,
            int limit = 20,
            int offset = 0,
            IDbConnectionFactory db = null!,
            HttpContext ctx = null!,
            CancellationToken ct = default) =>
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
            HybridCache cache,
            CancellationToken ct) =>
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
            double lat,
            double lng,
            double radiusKm = 50,
            int limit = 20,
            int offset = 0,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            if (limit > 100) limit = 100;
            if (limit < 1) limit = 1;
            if (offset < 0) offset = 0;

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
                LIMIT @limit OFFSET @offset
                """,
                new { lat, lng, radiusKm, limit, offset });
            return Results.Ok(rows);
        });

        // ── GET /api/venues/{slugOrId} — single venue by slug or UUID ——————
        app.MapGet("/api/venues/{slugOrId}", async (
            string slugOrId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Generate a unique venue_id with collision retry
            string venueIdStr;
            for (int attempt = 0; ; attempt++)
            {
                venueIdStr = $"VN-{Guid.NewGuid().ToString("N")[..8].ToUpper()}";
                var exists = await conn.QuerySingleOrDefaultAsync<int>(
                    "SELECT 1 FROM venues WHERE venue_id = @v LIMIT 1", new { v = venueIdStr });
                if (exists == 0) break;
                if (attempt >= 5) return Results.Problem("Unable to generate unique venue ID. Please retry.");
            }

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
                    name = req.Name,
                    description = req.Description,
                    address = req.Address,
                    city = req.City,
                    state = req.State,
                    country = req.Country,
                    postalCode = req.PostalCode,
                    stations = req.Stations,
                    hours = req.Hours,
                    games = req.Games,
                    amenities = req.Amenities ?? Array.Empty<string>(),
                    images = req.Images ?? Array.Empty<string>(),
                    cardImage = req.CardImage,
                    pcSpecs = req.PcSpecs is not null
                                       ? JsonSerializer.Serialize(req.PcSpecs)
                                       : "{}",
                    slug = req.Slug,
                    venueId = venueIdStr,
                    ownerId = userCtx.UserIdGuid,
                    contactEmail = req.ContactEmail,
                    contactPhone = req.ContactPhone,
                    pricePerHour = req.PricePerHour,
                    currency = req.Currency ?? "USD",
                    latitude = req.Latitude,
                    longitude = req.Longitude,
                    status = "draft",
                    submittedAt = req.SubmittedAt,
                });

            return Results.Created($"/api/venues/{row.slug}", row);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/venues/{id} — owner update ————————————————————————————
        app.MapPut("/api/venues/{id}", async (
            Guid id,
            [FromBody] UpdateVenueRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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

            if (req.Name is not null) { setClauses.Add("name = @name"); parameters.Add("name", req.Name); }
            if (req.Description is not null) { setClauses.Add("description = @description"); parameters.Add("description", req.Description); }
            if (req.Address is not null) { setClauses.Add("address = @address"); parameters.Add("address", req.Address); }
            if (req.City is not null) { setClauses.Add("city = @city"); parameters.Add("city", req.City); }
            if (req.State is not null) { setClauses.Add("state = @state"); parameters.Add("state", req.State); }
            if (req.Country is not null) { setClauses.Add("country = @country"); parameters.Add("country", req.Country); }
            if (req.Stations.HasValue) { setClauses.Add("stations = @stations"); parameters.Add("stations", req.Stations.Value); }
            if (req.Hours is not null) { setClauses.Add("hours = @hours"); parameters.Add("hours", req.Hours); }
            if (req.Games is not null) { setClauses.Add("games = @games"); parameters.Add("games", req.Games); }
            if (req.ContactEmail is not null) { setClauses.Add("contact_email = @contactEmail"); parameters.Add("contactEmail", req.ContactEmail); }
            if (req.ContactPhone is not null) { setClauses.Add("contact_phone = @contactPhone"); parameters.Add("contactPhone", req.ContactPhone); }
            if (req.Images is not null) { setClauses.Add("images = @images"); parameters.Add("images", req.Images); }
            if (req.CardImage is not null) { setClauses.Add("card_image = @cardImage"); parameters.Add("cardImage", req.CardImage); }
            if (req.PricePerHour.HasValue) { setClauses.Add("price_per_hour = @pricePerHour"); parameters.Add("pricePerHour", req.PricePerHour.Value); }
            if (req.Currency is not null) { setClauses.Add("currency = @currency"); parameters.Add("currency", req.Currency); }
            if (req.Amenities is not null) { setClauses.Add("amenities = @amenities"); parameters.Add("amenities", req.Amenities); }
            if (req.PcSpecs is not null) { setClauses.Add("pc_specs = @pcSpecs::jsonb"); parameters.Add("pcSpecs", JsonSerializer.Serialize(req.PcSpecs)); }
            if (req.PostalCode is not null) { setClauses.Add("postal_code = @postalCode"); parameters.Add("postalCode", req.PostalCode); }
            if (req.Latitude.HasValue) { setClauses.Add("latitude = @latitude"); parameters.Add("latitude", req.Latitude.Value); }
            if (req.Longitude.HasValue) { setClauses.Add("longitude = @longitude"); parameters.Add("longitude", req.Longitude.Value); }

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
            string? status,
            int limit = 50,
            int offset = 0,
            HttpContext ctx = default!,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.VenuesView)) return Results.Forbid();

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
            Guid id,
            [FromBody] UpdateVenueRequest req,
            HttpContext ctx = default!,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.VenuesEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var setClauses = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("id", id);

            if (req.Name is not null) { setClauses.Add("name = @name"); parameters.Add("name", req.Name); }
            if (req.Description is not null) { setClauses.Add("description = @description"); parameters.Add("description", req.Description); }
            if (req.Address is not null) { setClauses.Add("address = @address"); parameters.Add("address", req.Address); }
            if (req.City is not null) { setClauses.Add("city = @city"); parameters.Add("city", req.City); }
            if (req.Stations.HasValue) { setClauses.Add("stations = @stations"); parameters.Add("stations", req.Stations.Value); }
            if (req.Hours is not null) { setClauses.Add("hours = @hours"); parameters.Add("hours", req.Hours); }
            if (req.Games is not null) { setClauses.Add("games = @games"); parameters.Add("games", req.Games); }
            if (req.ContactEmail is not null) { setClauses.Add("contact_email = @contactEmail"); parameters.Add("contactEmail", req.ContactEmail); }
            if (req.ContactPhone is not null) { setClauses.Add("contact_phone = @contactPhone"); parameters.Add("contactPhone", req.ContactPhone); }
            if (req.Images is not null) { setClauses.Add("images = @images"); parameters.Add("images", req.Images); }
            if (req.PricePerHour.HasValue) { setClauses.Add("price_per_hour = @pricePerHour"); parameters.Add("pricePerHour", req.PricePerHour.Value); }
            if (req.Status is not null) { setClauses.Add("status = @status"); parameters.Add("status", req.Status); }
            if (req.RejectionReason is not null) { setClauses.Add("rejection_reason = @rejectionReason"); parameters.Add("rejectionReason", req.RejectionReason); }
            if (req.ReviewedBy is not null) { setClauses.Add("reviewed_by = @reviewedBy"); parameters.Add("reviewedBy", Guid.Parse(req.ReviewedBy)); }
            if (req.ReviewedAt is not null) { setClauses.Add("reviewed_at = @reviewedAt::timestamptz"); parameters.Add("reviewedAt", req.ReviewedAt); }
            if (req.PublishedAt is not null) { setClauses.Add("published_at = @publishedAt::timestamptz"); parameters.Add("publishedAt", req.PublishedAt); }

            if (setClauses.Count == 0)
                return Results.BadRequest(new { error = "No fields to update." });

            setClauses.Add("updated_at = NOW()");

            var sql = $"UPDATE venues SET {string.Join(", ", setClauses)} WHERE id = @id AND deleted_at IS NULL RETURNING id, name, description, address, city, state, country, postal_code, stations, hours, games, amenities, images, card_image, pc_specs, slug, venue_id, owner_id, contact_email, contact_phone, price_per_hour, status, created_at, updated_at";
            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, parameters);

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization("Admin");

        // ── DELETE /api/venues/{id} — soft delete (owner only) —————————————
        app.MapDelete("/api/venues/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT seats_total, seats_occupied, is_open, updated_at
                FROM venue_live_status
                WHERE venue_id = @id
                """,
                new { id });

            if (row is not null)
                return Results.Ok(row);

            // No live-status row (Desktop Agent not connected yet) — derive
            // seats_total from venue_stations so the dashboard shows the real count.
            var stationCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM venue_stations WHERE venue_id = @id",
                new { id });

            return Results.Ok(new
            {
                seats_total = stationCount,
                seats_occupied = 0,
                is_open = false,
                updated_at = (DateTime?)null,
            });
        });

        // ── GET /api/venues/{id}/stations——————————————————————————————————
        app.MapGet("/api/venues/{id}/stations", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is venue owner or staff member
            var isOwnerOrStaff = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS (
                    SELECT 1 FROM venues
                    WHERE id = @id AND deleted_at IS NULL AND owner_id = @userId
                    UNION ALL
                    SELECT 1 FROM venue_staff
                    WHERE venue_id = @id AND user_id = @userId
                )
                """,
                new { id, userId = userCtx.UserIdGuid });

            if (!isOwnerOrStaff)
                return Results.Forbid();

            var stations = await conn.QueryAsync<dynamic>(
                """
                SELECT id,
                       station_id,
                       COALESCE(NULLIF(label, ''), station_id) AS name,
                       ROW_NUMBER() OVER (ORDER BY label, station_id) AS station_number,
                       CASE
                           WHEN status = 'maintenance' THEN 'maintenance'
                           WHEN status = 'decommissioned' THEN 'offline'
                           WHEN status = 'offline' THEN 'offline'
                           WHEN status = 'active' THEN 'available'
                           ELSE 'available'
                       END AS status,
                       zone,
                       pos_x, pos_y, width, height, rotation,
                       NULL::text AS current_session,
                       NULL::text AS hardware,
                       NULL::text AS last_seen,
                       created_at
                FROM venue_stations
                WHERE venue_id = @id
                ORDER BY label, station_id
                """,
                new { id });

            return Results.Ok(stations);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{id}/availability ——————————————————————————————
        app.MapGet("/api/venues/{id}/availability", async (
            Guid id,
            string? date,
            string? time,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid id,
            [FromBody] CreateBookingRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            Esportra.Api.Services.VenueHubService venueHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Input validation
            if (req.Hours <= 0 || req.Hours > 24)
                return Results.BadRequest(new { error = "Hours must be between 1 and 24." });
            if (req.Stations <= 0)
                return Results.BadRequest(new { error = "Stations must be at least 1." });
            if (!TimeOnly.TryParse(req.StartTime, out var start))
                return Results.BadRequest(new { error = "Invalid start time format." });

            using var conn = db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();
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
                else
                {
                    // No availability record — check against venue's total station count
                    var venueStations = await conn.QuerySingleOrDefaultAsync<int?>(
                        "SELECT total_stations FROM venues WHERE id = @id", new { id }, tx);
                    if (venueStations is null)
                        return Results.NotFound(new { error = "Venue not found." });
                    if (req.Stations > venueStations.Value)
                        return Results.BadRequest(new { error = $"Only {venueStations.Value} stations available." });
                }

                // 2. Calculate end time and generate booking code
                var end = start.AddHours(req.Hours);
                var endStr = end.ToString("HH:mm");
                var total = effectivePrice * req.Hours * req.Stations;
                var bookingCode = GenerateBookingCode();

                // 3. Insert booking with code
                var booking = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO venue_bookings
                        (venue_id, user_id, booking_date, start_time, end_time,
                         duration_hours, stations_booked, total_amount, status,
                         booking_code, special_requests, contact_phone, contact_email)
                    VALUES
                        (@venueId, @userId, @date::date, @startTime::time, @endTime::time,
                         @hours, @stations, @total, 'confirmed',
                         @bookingCode, @specialRequests, @contactPhone, @contactEmail)
                    RETURNING id, venue_id, user_id, booking_date, start_time, end_time,
                             duration_hours, stations_booked, total_amount, status,
                             booking_code, special_requests, contact_phone, contact_email, created_at
                    """,
                    new
                    {
                        venueId = id,
                        userId = userCtx.UserIdGuid,
                        date = req.Date,
                        startTime = req.StartTime,
                        endTime = endStr,
                        hours = req.Hours,
                        stations = req.Stations,
                        total,
                        bookingCode,
                        specialRequests = req.SpecialRequests,
                        contactPhone = req.ContactPhone,
                        contactEmail = req.ContactEmail,
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

                // 5. Route to venue-hub (fire-and-forget, non-blocking)
                if (venueHub.IsConfigured)
                {
                    var bookingId = ((dynamic)booking).id.ToString();
                    var durationMinutes = req.Hours * 60;

                    // Fetch display name from profile instead of exposing email
                    var displayName = await conn.QuerySingleOrDefaultAsync<string?>(
                        "SELECT username FROM profiles WHERE id = @userId",
                        new { userId = userCtx.UserIdGuid }) ?? "Online Booking";

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await venueHub.RouteBookingAsync(
                                bookingId, id.ToString(), null,
                                userCtx.UserId, displayName,
                                durationMinutes, bookingCode, req.StartTime);
                        }
                        catch (Exception ex)
                        {
                            // Best-effort — local hub will pick it up via booking code sync
                            Console.WriteLine($"[VenueBooking] Route to venue-hub failed for {bookingId}: {ex.Message}");
                        }
                    });
                }

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
            Guid bookingId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid id,
            [FromBody] TrackImpressionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid id,
            int days = 30,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
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
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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

        // ── GET /api/venues/{id}/online — venue hub online status ————————————
        app.MapGet("/api/venues/{id}/online", async (
            Guid id,
            Esportra.Api.Services.VenueHubService venueHub) =>
        {
            var isOnline = await venueHub.IsVenueOnlineAsync(id.ToString());
            return Results.Ok(new { venueId = id, isOnline });
        });

        // ── GET /api/venues/{id}/seats — real-time seat availability —————————
        app.MapGet("/api/venues/{id}/seats", async (
            Guid id,
            Esportra.Api.Services.VenueHubService venueHub) =>
        {
            var seats = await venueHub.GetVenueSeatsAsync(id.ToString());
            if (seats is null)
                return Results.Ok(new { venueId = id, isOnline = false, stations = Array.Empty<object>() });
            return Results.Ok(seats);
        });

        // ── POST /api/venues/{id}/hub-key — generate / rotate hub API key ———
        app.MapPost("/api/venues/{id}/hub-key", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Verify caller owns the venue
            var venue = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, owner_id, hub_key_issued_at FROM venues WHERE id = @id AND deleted_at IS NULL",
                new { id });

            if (venue is null) return Results.NotFound();
            if ((Guid)venue.owner_id != userCtx.UserIdGuid) return Results.Forbid();

            // 2. Generate a random 32-char hex key
            var plainKey = Guid.NewGuid().ToString("N");

            // 3. Hash with bcrypt
            var hash = BCrypt.Net.BCrypt.HashPassword(plainKey);

            // 4. Persist hash + timestamps
            bool hadPreviousKey = venue.hub_key_issued_at is not null;
            await conn.ExecuteAsync(
                """
                UPDATE venues
                SET hub_api_key_hash  = @hash,
                    hub_key_issued_at = NOW(),
                    hub_key_rotated_at = CASE WHEN hub_key_issued_at IS NOT NULL THEN NOW() ELSE NULL END
                WHERE id = @id AND owner_id = @userId
                """,
                new { hash, id, userId = userCtx.UserIdGuid });

            // 5. Fetch updated timestamps
            var updated = await conn.QueryFirstAsync<dynamic>(
                "SELECT hub_key_issued_at, hub_key_rotated_at FROM venues WHERE id = @id",
                new { id });

            return Results.Ok(new
            {
                key = plainKey,
                issuedAt = (DateTimeOffset?)updated.hub_key_issued_at,
                rotated = hadPreviousKey
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{id}/hub-config — hub connection config for desktop app
        app.MapGet("/api/venues/{id}/hub-config", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            IConfiguration config,
            VenueConnectionTracker tracker,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var venue = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, owner_id, hub_api_key_hash, hub_key_issued_at, hub_key_rotated_at,
                       hub_lan_url, hub_version, hub_last_heartbeat
                FROM venues
                WHERE id = @id AND deleted_at IS NULL
                """,
                new { id });

            if (venue is null) return Results.NotFound();
            if ((Guid)venue.owner_id != userCtx.UserIdGuid) return Results.Forbid();

            var hubUrl = config["VenueHub:Url"]?.TrimEnd('/') ?? "";
            var connInfo = tracker.GetConnectionInfo(id.ToString());

            return Results.Ok(new
            {
                venueId = id,
                hubUrl,
                hubLanUrl = (string?)venue.hub_lan_url,
                hubVersion = (string?)venue.hub_version,
                hubLastHeartbeat = (DateTimeOffset?)venue.hub_last_heartbeat,
                hasKey = venue.hub_api_key_hash is not null,
                keyIssuedAt = (DateTimeOffset?)venue.hub_key_issued_at,
                keyRotatedAt = (DateTimeOffset?)venue.hub_key_rotated_at,
                hubConnected = connInfo is not null,
                hubConnectedAt = connInfo?.ConnectedAt
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/venues/{id}/hub-heartbeat — local hub reports its LAN URL & version
        app.MapPost("/api/venues/{id}/hub-heartbeat", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            // Authenticate via X-Hub-ApiKey header (same key the hub uses for VenueSyncHub)
            var apiKey = ctx.Request.Headers["X-Hub-ApiKey"].ToString();
            if (string.IsNullOrEmpty(apiKey))
                return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var hash = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT hub_api_key_hash FROM venues WHERE id = @id AND deleted_at IS NULL",
                new { id });

            if (hash is null || !BCrypt.Net.BCrypt.Verify(apiKey, hash))
                return Results.Unauthorized();

            // Parse body
            var body = await ctx.Request.ReadFromJsonAsync<HeartbeatPayload>(ct);
            if (body is null)
                return Results.BadRequest(new { error = "Invalid payload" });

            // Update venue with hub info
            await conn.ExecuteAsync(
                """
                UPDATE venues
                SET hub_lan_url = @lanUrl,
                    hub_version = @version,
                    hub_last_heartbeat = NOW()
                WHERE id = @id
                """,
                new { id, lanUrl = body.LanUrl, version = body.Version });

            return Results.Ok(new { status = "ok" });
        });

        // ── GET /api/venues/{id}/availability/slots — time-slot availability ─
        app.MapGet("/api/venues/{id}/availability/slots", async (
            Guid id,
            string? date,
            Guid? zone_id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(date) || !DateOnly.TryParse(date, out var bookingDate))
                return Results.BadRequest(new { error = "date query parameter is required (YYYY-MM-DD)" });

            using var conn = db.CreateConnection();

            // Get venue hours (JSON string like "09:00-23:00") or default 08:00-00:00
            var venueHours = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT hours FROM venues WHERE id = @Id AND deleted_at IS NULL",
                new { Id = id });

            if (venueHours is null)
                return Results.NotFound(new { error = "Venue not found" });

            // Parse venue hours — expect "HH:mm-HH:mm" format
            TimeOnly openTime = new(8, 0), closeTime = new(0, 0);
            if (!string.IsNullOrWhiteSpace(venueHours))
            {
                var parts = venueHours.Split('-', 2);
                if (parts.Length == 2)
                {
                    TimeOnly.TryParse(parts[0].Trim(), out openTime);
                    TimeOnly.TryParse(parts[1].Trim(), out closeTime);
                }
            }
            // If close == 00:00, treat as midnight (end of day)
            bool closesAtMidnight = closeTime == new TimeOnly(0, 0);

            // Count total stations (optionally filtered by zone)
            var totalStations = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)
                FROM venue_stations
                WHERE venue_id = @VenueId
                  AND status = 'active'
                  AND (@ZoneId IS NULL OR zone_id = @ZoneId)
                """,
                new { VenueId = id, ZoneId = zone_id });

            // Get all bookings for the date (optionally filtered by zone)
            var bookings = (await conn.QueryAsync<dynamic>(
                """
                SELECT start_time, end_time, stations_booked
                FROM venue_bookings
                WHERE venue_id = @VenueId
                  AND booking_date = @Date
                  AND status NOT IN ('cancelled')
                  AND (@ZoneId IS NULL OR zone_id = @ZoneId)
                """,
                new { VenueId = id, Date = bookingDate.ToString("yyyy-MM-dd"), ZoneId = zone_id })).ToList();

            // Generate 30-minute slots from opening to closing
            var slots = new List<object>();
            var current = openTime;
            var endLimit = closesAtMidnight ? new TimeOnly(23, 30) : closeTime.AddMinutes(-30);

            while (current <= endLimit)
            {
                var slotEnd = current.AddMinutes(30);
                if (closesAtMidnight && current == new TimeOnly(23, 30))
                    slotEnd = new TimeOnly(0, 0);

                // Count how many stations are booked during this slot
                int stationsBooked = 0;
                foreach (var b in bookings)
                {
                    var bStart = (TimeSpan)b.start_time;
                    var bEnd = (TimeSpan)b.end_time;
                    var slotStartSpan = current.ToTimeSpan();
                    // Slot overlaps booking if slot_start < booking_end AND slot_end > booking_start
                    bool overlaps;
                    if (bEnd <= bStart) // crosses midnight
                        overlaps = slotStartSpan < bEnd || current.ToTimeSpan() >= bStart;
                    else
                        overlaps = slotStartSpan < bEnd && slotEnd.ToTimeSpan() > bStart;

                    if (overlaps)
                        stationsBooked += (int)b.stations_booked;
                }

                var available = Math.Max(0, totalStations - stationsBooked);
                slots.Add(new
                {
                    start_time = current.ToString("HH:mm"),
                    end_time = slotEnd.ToString("HH:mm"),
                    available_stations = available,
                    total_stations = totalStations,
                });

                current = current.AddMinutes(30);
                // Break before looping past midnight
                if (current == new TimeOnly(0, 0)) break;
            }

            return Results.Ok(new
            {
                date = bookingDate.ToString("yyyy-MM-dd"),
                zone_id,
                slots,
            });
        })
        .WithTags("Venues");

        // ── POST /api/venues/{id}/bookings/{bookingId}/start-session — convert booking to session ─
        app.MapPost("/api/venues/{id}/bookings/{bookingId}/start-session", async (
            Guid id,
            Guid bookingId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, userCtx.UserIdGuid, id))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            conn.Open();
            using var tx = conn.BeginTransaction();

            try
            {
                // Verify booking exists, belongs to venue, and is checked in
                var booking = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id, venue_id, station_id, station_preference, zone_id,
                           duration_hours, checked_in_at, session_id, status,
                           member_id, user_id
                    FROM venue_bookings
                    WHERE id = @BookingId AND venue_id = @VenueId
                    """,
                    new { BookingId = bookingId, VenueId = id }, tx);

                if (booking is null)
                    return Results.NotFound(new { error = "Booking not found" });

                if (booking.checked_in_at is null)
                    return Results.BadRequest(new { error = "Booking must be checked in before starting a session" });

                if (booking.session_id is not null)
                    return Results.Conflict(new { error = "Booking already has an active session" });

                // Resolve station — use assigned station_id or station_preference
                string? stationId = (string?)booking.station_id ?? (string?)booking.station_preference;
                if (string.IsNullOrWhiteSpace(stationId))
                    return Results.BadRequest(new { error = "No station assigned to this booking" });

                // Verify station exists
                var station = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT station_id, zone, zone_id, status
                    FROM venue_stations
                    WHERE venue_id = @VenueId AND station_id = @StationId
                    """,
                    new { VenueId = id, StationId = stationId }, tx);

                if (station is null)
                    return Results.NotFound(new { error = "Assigned station not found" });

                if ((string?)station.status == "maintenance")
                    return Results.BadRequest(new { error = "Station is in maintenance mode" });

                // Check no active session on this station
                var existingSession = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    SELECT id FROM venue_sessions
                    WHERE venue_id = @VenueId AND station_id = @StationId AND ended_at IS NULL
                    """,
                    new { VenueId = id, StationId = stationId }, tx);

                if (existingSession is not null)
                    return Results.Conflict(new { error = "Station already has an active session" });

                // Resolve rate from zone
                decimal? ratePerHour = null;
                if (station.zone_id is not null)
                {
                    ratePerHour = await conn.QuerySingleOrDefaultAsync<decimal?>(
                        "SELECT hourly_rate FROM zones WHERE id = @ZoneId",
                        new { ZoneId = (Guid)station.zone_id }, tx);
                }

                int durationHours = (int?)booking.duration_hours ?? 1;
                var expiresAt = DateTimeOffset.UtcNow.AddHours(durationHours);

                // Get display name for the session
                var displayName = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT username FROM profiles WHERE id = @UserId",
                    new { UserId = (Guid?)booking.user_id }, tx) ?? "Booking";

                // Start session
                var sessionId = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO venue_sessions
                        (venue_id, station_id, session_type, started_at, expires_at,
                         rate_per_hour, display_name, zone, member_id, staff_id,
                         booking_id, notes)
                    VALUES
                        (@VenueId, @StationId, 'booking', NOW(), @ExpiresAt,
                         @RatePerHour, @DisplayName, @Zone, @MemberId, @StaffId,
                         @BookingId, @Notes)
                    RETURNING id
                    """,
                    new
                    {
                        VenueId = id,
                        StationId = stationId,
                        ExpiresAt = expiresAt,
                        RatePerHour = ratePerHour,
                        DisplayName = displayName,
                        Zone = (string?)station.zone,
                        MemberId = (Guid?)booking.member_id,
                        StaffId = userCtx.UserIdGuid,
                        BookingId = bookingId,
                        Notes = $"Started from booking #{bookingId}",
                    }, tx);

                // Update booking with session_id
                await conn.ExecuteAsync(
                    "UPDATE venue_bookings SET session_id = @SessionId WHERE id = @BookingId",
                    new { SessionId = sessionId, BookingId = bookingId }, tx);

                tx.Commit();

                return Results.Ok(new
                {
                    session_id = sessionId,
                    booking_id = bookingId,
                    station_id = stationId,
                    display_name = displayName,
                    session_type = "booking",
                    started_at = DateTimeOffset.UtcNow,
                    expires_at = expiresAt,
                    rate_per_hour = ratePerHour,
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Venues");
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

    private static string GenerateBookingCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I/O/0/1 to avoid confusion
        var bytes = new byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var code = new char[8];
        for (var i = 0; i < 8; i++)
            code[i] = chars[bytes[i] % chars.Length];
        return new string(code);
    }
}

// ── Request records ————————————————————————————————————————————————————————————

public sealed record CreateVenueRequest(
    string Name,
    string Address,
    string City,
    string Country,
    string? Description = null,
    string? State = null,
    string? PostalCode = null,
    int Stations = 0,
    string? Hours = null,
    string? Games = null,
    string[]? Amenities = null,
    string[]? Images = null,
    string? CardImage = null,
    object? PcSpecs = null,
    string? Slug = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    decimal PricePerHour = 0,
    string? Currency = null,
    double? Latitude = null,
    double? Longitude = null,
    string? Status = null,
    string? SubmittedAt = null);

public sealed record UpdateVenueRequest(
    string? Name = null,
    string? Description = null,
    string? Address = null,
    string? City = null,
    string? State = null,
    string? Country = null,
    string? PostalCode = null,
    int? Stations = null,
    string? Hours = null,
    string? Games = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string[]? Images = null,
    string? CardImage = null,
    decimal? PricePerHour = null,
    string? Currency = null,
    string[]? Amenities = null,
    object? PcSpecs = null,
    double? Latitude = null,
    double? Longitude = null,
    string? Status = null,
    string? RejectionReason = null,
    string? PriceRange = null,
    string? ReviewedBy = null,
    string? ReviewedAt = null,
    string? PublishedAt = null);

public sealed record CreateBookingRequest(
    string Date,
    string StartTime,
    int Hours,
    int Stations,
    decimal PricePerHour,
    string? SpecialRequests = null,
    string? ContactPhone = null,
    string? ContactEmail = null);

public sealed record TrackImpressionRequest(string EventType);

public sealed record HeartbeatPayload(string? LanUrl, string? Version);
