using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Endpoints;

public static class NotificationPreferenceEndpoints
{
    public static void MapNotificationPreferenceEndpoints(this WebApplication app)
    {
        // ── GET preferences (upsert default row if none) ────────
        app.MapGet("/api/venues/{venueId}/notification-preferences", async (
            Guid venueId,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var prefs = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, venue_id, user_id,
                       station_offline, low_balance, new_booking,
                       order_ready, session_ending, staff_clock,
                       tamper_alert, walk_in_queue, daily_summary,
                       created_at, updated_at
                FROM notification_preferences
                WHERE venue_id = @VenueId AND user_id = @UserId
                """,
                new { VenueId = venueId, UserId = user.UserIdGuid });

            if (prefs is not null)
                return Results.Ok(prefs);

            // Create default row on first access
            prefs = await conn.QueryFirstAsync<dynamic>(
                """
                INSERT INTO notification_preferences (venue_id, user_id)
                VALUES (@VenueId, @UserId)
                ON CONFLICT (venue_id, user_id) DO NOTHING
                RETURNING id, venue_id, user_id,
                          station_offline, low_balance, new_booking,
                          order_ready, session_ending, staff_clock,
                          tamper_alert, walk_in_queue, daily_summary,
                          created_at, updated_at
                """,
                new { VenueId = venueId, UserId = user.UserIdGuid });

            return Results.Ok(prefs);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Notification Preferences");

        // ── PUT update preferences ──────────────────────────────
        app.MapPut("/api/venues/{venueId}/notification-preferences", async (
            Guid venueId,
            UpdateNotificationPreferencesRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var updated = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                INSERT INTO notification_preferences
                    (venue_id, user_id, station_offline, low_balance, new_booking,
                     order_ready, session_ending, staff_clock,
                     tamper_alert, walk_in_queue, daily_summary)
                VALUES
                    (@VenueId, @UserId, @StationOffline, @LowBalance, @NewBooking,
                     @OrderReady, @SessionEnding, @StaffClock,
                     @TamperAlert, @WalkInQueue, @DailySummary)
                ON CONFLICT (venue_id, user_id) DO UPDATE SET
                    station_offline = EXCLUDED.station_offline,
                    low_balance     = EXCLUDED.low_balance,
                    new_booking     = EXCLUDED.new_booking,
                    order_ready     = EXCLUDED.order_ready,
                    session_ending  = EXCLUDED.session_ending,
                    staff_clock     = EXCLUDED.staff_clock,
                    tamper_alert    = EXCLUDED.tamper_alert,
                    walk_in_queue   = EXCLUDED.walk_in_queue,
                    daily_summary   = EXCLUDED.daily_summary,
                    updated_at      = now()
                RETURNING id, venue_id, user_id,
                          station_offline, low_balance, new_booking,
                          order_ready, session_ending, staff_clock,
                          tamper_alert, walk_in_queue, daily_summary,
                          created_at, updated_at
                """,
                new
                {
                    VenueId = venueId,
                    UserId = user.UserIdGuid,
                    req.StationOffline,
                    req.LowBalance,
                    req.NewBooking,
                    req.OrderReady,
                    req.SessionEnding,
                    req.StaffClock,
                    req.TamperAlert,
                    req.WalkInQueue,
                    req.DailySummary,
                });

            return Results.Ok(updated);
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Notification Preferences");
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

// ── Request DTO ─────────────────────────────────────────────────
public record UpdateNotificationPreferencesRequest(
    bool StationOffline = true,
    bool LowBalance = true,
    bool NewBooking = true,
    bool OrderReady = true,
    bool SessionEnding = true,
    bool StaffClock = false,
    bool TamperAlert = true,
    bool WalkInQueue = true,
    bool DailySummary = false);
