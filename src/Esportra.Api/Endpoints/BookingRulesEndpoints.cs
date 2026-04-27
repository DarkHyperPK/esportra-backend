using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

public static class BookingRulesEndpoints
{
    public static void MapBookingRulesEndpoints(this WebApplication app)
    {
        // ── GET /api/venues/{id}/booking-rules — get rules (auto-create default if none) ──
        app.MapGet("/api/venues/{id}/booking-rules", async (
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

            var rules = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, venue_id, min_duration_minutes, max_duration_minutes,
                       advance_booking_days, cancellation_minutes, no_show_cancel_minutes,
                       buffer_minutes, max_stations_per_booking, allow_walk_ins,
                       require_payment_upfront, peak_hours, off_peak_discount_percent,
                       created_at, updated_at
                FROM booking_rules
                WHERE venue_id = @VenueId
                """,
                new { VenueId = id });

            if (rules is not null)
                return Results.Ok(rules);

            // Auto-create default booking rules for this venue
            rules = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO booking_rules (venue_id)
                VALUES (@VenueId)
                ON CONFLICT (venue_id) DO NOTHING
                RETURNING id, venue_id, min_duration_minutes, max_duration_minutes,
                          advance_booking_days, cancellation_minutes, no_show_cancel_minutes,
                          buffer_minutes, max_stations_per_booking, allow_walk_ins,
                          require_payment_upfront, peak_hours, off_peak_discount_percent,
                          created_at, updated_at
                """,
                new { VenueId = id });

            // Edge case: concurrent insert — re-fetch if RETURNING is empty
            if (rules is null)
            {
                rules = await conn.QuerySingleAsync<dynamic>(
                    "SELECT * FROM booking_rules WHERE venue_id = @VenueId",
                    new { VenueId = id });
            }

            return Results.Ok(rules);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("BookingRules");

        // ── PUT /api/venues/{id}/booking-rules — update rules (owner/staff only) ──
        app.MapPut("/api/venues/{id}/booking-rules", async (
            Guid id,
            UpdateBookingRulesRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, id))
                return Results.Forbid();

            // Validation
            if (req.MinDurationMinutes is < 5 or > 1440)
                return Results.BadRequest(new { error = "min_duration_minutes must be between 5 and 1440" });
            if (req.MaxDurationMinutes is < 5 or > 1440)
                return Results.BadRequest(new { error = "max_duration_minutes must be between 5 and 1440" });
            if (req.MinDurationMinutes is not null && req.MaxDurationMinutes is not null
                && req.MinDurationMinutes > req.MaxDurationMinutes)
                return Results.BadRequest(new { error = "min_duration_minutes cannot exceed max_duration_minutes" });
            if (req.AdvanceBookingDays is < 0 or > 365)
                return Results.BadRequest(new { error = "advance_booking_days must be between 0 and 365" });
            if (req.CancellationMinutes is < 0)
                return Results.BadRequest(new { error = "cancellation_minutes must be non-negative" });
            if (req.BufferMinutes is < 0 or > 120)
                return Results.BadRequest(new { error = "buffer_minutes must be between 0 and 120" });
            if (req.MaxStationsPerBooking is < 1 or > 100)
                return Results.BadRequest(new { error = "max_stations_per_booking must be between 1 and 100" });
            if (req.OffPeakDiscountPercent is < 0 or > 100)
                return Results.BadRequest(new { error = "off_peak_discount_percent must be between 0 and 100" });

            using var conn = db.CreateConnection();

            // Upsert: update existing or insert default + update
            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                INSERT INTO booking_rules (venue_id)
                VALUES (@VenueId)
                ON CONFLICT (venue_id) DO NOTHING;

                UPDATE booking_rules
                SET min_duration_minutes     = COALESCE(@MinDurationMinutes, min_duration_minutes),
                    max_duration_minutes     = COALESCE(@MaxDurationMinutes, max_duration_minutes),
                    advance_booking_days     = COALESCE(@AdvanceBookingDays, advance_booking_days),
                    cancellation_minutes     = COALESCE(@CancellationMinutes, cancellation_minutes),
                    no_show_cancel_minutes   = COALESCE(@NoShowCancelMinutes, no_show_cancel_minutes),
                    buffer_minutes           = COALESCE(@BufferMinutes, buffer_minutes),
                    max_stations_per_booking = COALESCE(@MaxStationsPerBooking, max_stations_per_booking),
                    allow_walk_ins           = COALESCE(@AllowWalkIns, allow_walk_ins),
                    require_payment_upfront  = COALESCE(@RequirePaymentUpfront, require_payment_upfront),
                    peak_hours               = COALESCE(@PeakHours::jsonb, peak_hours),
                    off_peak_discount_percent = COALESCE(@OffPeakDiscountPercent, off_peak_discount_percent),
                    updated_at               = NOW()
                WHERE venue_id = @VenueId
                RETURNING id, venue_id, min_duration_minutes, max_duration_minutes,
                          advance_booking_days, cancellation_minutes, no_show_cancel_minutes,
                          buffer_minutes, max_stations_per_booking, allow_walk_ins,
                          require_payment_upfront, peak_hours, off_peak_discount_percent,
                          created_at, updated_at
                """,
                new
                {
                    VenueId = id,
                    req.MinDurationMinutes,
                    req.MaxDurationMinutes,
                    req.AdvanceBookingDays,
                    req.CancellationMinutes,
                    req.NoShowCancelMinutes,
                    req.BufferMinutes,
                    req.MaxStationsPerBooking,
                    req.AllowWalkIns,
                    req.RequirePaymentUpfront,
                    PeakHours = req.PeakHours is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.PeakHours)
                        : null,
                    req.OffPeakDiscountPercent,
                });

            return updated is not null
                ? Results.Ok(updated)
                : Results.NotFound(new { error = "Failed to update booking rules" });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("BookingRules");
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
public record UpdateBookingRulesRequest(
    int? MinDurationMinutes = null,
    int? MaxDurationMinutes = null,
    int? AdvanceBookingDays = null,
    int? CancellationMinutes = null,
    int? NoShowCancelMinutes = null,
    int? BufferMinutes = null,
    int? MaxStationsPerBooking = null,
    bool? AllowWalkIns = null,
    bool? RequirePaymentUpfront = null,
    object[]? PeakHours = null,
    decimal? OffPeakDiscountPercent = null);
