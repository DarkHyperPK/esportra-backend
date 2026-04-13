using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace Esportra.Api.Endpoints;

public static class VenueAnalyticsEndpoints
{
    public static void MapVenueAnalyticsEndpoints(this WebApplication app)
    {
        // ── 1. Real-time dashboard ──────────────────────────────────────────
        app.MapGet("/api/venues/{venueId}/analytics/dashboard", async (
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

            var dashboard = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT
                  (SELECT COUNT(*) FROM venue_sessions
                   WHERE venue_id = @VenueId AND ended_at IS NULL) AS active_sessions,

                  (SELECT COALESCE(SUM(total_charged), 0) FROM venue_sessions
                   WHERE venue_id = @VenueId
                     AND started_at >= CURRENT_DATE
                     AND started_at < CURRENT_DATE + INTERVAL '1 day') AS session_revenue_today,

                  (SELECT COALESCE(SUM(total), 0) FROM pos_orders
                   WHERE venue_id = @VenueId
                     AND created_at >= CURRENT_DATE
                     AND created_at < CURRENT_DATE + INTERVAL '1 day'
                     AND status != 'cancelled') AS pos_revenue_today,

                  (SELECT COUNT(*) FROM pos_orders
                   WHERE venue_id = @VenueId
                     AND created_at >= CURRENT_DATE
                     AND created_at < CURRENT_DATE + INTERVAL '1 day'
                     AND status != 'cancelled') AS orders_today,

                  (SELECT COUNT(DISTINCT member_id) FROM venue_sessions
                   WHERE venue_id = @VenueId
                     AND started_at >= CURRENT_DATE
                     AND started_at < CURRENT_DATE + INTERVAL '1 day'
                     AND member_id IS NOT NULL) AS members_today,

                  (SELECT COUNT(*) FROM venue_sessions
                   WHERE venue_id = @VenueId
                     AND started_at >= CURRENT_DATE
                     AND started_at < CURRENT_DATE + INTERVAL '1 day') AS sessions_today,

                  (SELECT COUNT(*) FROM venue_stations
                   WHERE venue_id = @VenueId) AS total_stations
                """,
                new { VenueId = venueId });

            // Calculate utilization: active sessions / total stations
            var totalStations = (long)(dashboard?.total_stations ?? 0L);
            var activeSessions = (long)(dashboard?.active_sessions ?? 0L);
            var utilization = totalStations > 0
                ? Math.Round((double)activeSessions / totalStations * 100, 1)
                : 0.0;

            return Results.Ok(new
            {
                active_sessions = activeSessions,
                revenue_today = (decimal)(dashboard?.session_revenue_today ?? 0m) + (decimal)(dashboard?.pos_revenue_today ?? 0m),
                session_revenue_today = (decimal)(dashboard?.session_revenue_today ?? 0m),
                pos_revenue_today = (decimal)(dashboard?.pos_revenue_today ?? 0m),
                utilization_percent = utilization,
                orders_today = (long)(dashboard?.orders_today ?? 0L),
                members_today = (long)(dashboard?.members_today ?? 0L),
                sessions_today = (long)(dashboard?.sessions_today ?? 0L),
                total_stations = totalStations,
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Venue Analytics");

        // ── 2. Revenue time series ──────────────────────────────────────────
        app.MapGet("/api/venues/{venueId}/analytics/revenue", async (
            Guid venueId,
            [FromQuery] string from,
            [FromQuery] string to,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (!DateOnly.TryParse(from, out var fromDate) || !DateOnly.TryParse(to, out var toDate))
                return Results.BadRequest(new { error = "Invalid date range. Use YYYY-MM-DD." });

            if (toDate < fromDate)
                return Results.BadRequest(new { error = "'to' must be >= 'from'." });

            // Cap range to 365 days
            if (toDate.DayNumber - fromDate.DayNumber > 365)
                return Results.BadRequest(new { error = "Date range cannot exceed 365 days." });

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT d.date,
                       COALESCE(ds.total_sessions, 0) AS total_sessions,
                       COALESCE(ds.total_hours, 0) AS total_hours,
                       COALESCE(ds.session_revenue, 0) AS session_revenue,
                       COALESCE(ds.pos_revenue, 0) AS pos_revenue,
                       COALESCE(ds.package_revenue, 0) AS package_revenue,
                       COALESCE(ds.total_revenue, 0) AS total_revenue,
                       COALESCE(ds.total_orders, 0) AS total_orders
                FROM generate_series(@From::date, @To::date, '1 day'::interval) d(date)
                LEFT JOIN daily_stats ds
                  ON ds.venue_id = @VenueId AND ds.date = d.date::date
                ORDER BY d.date
                """,
                new { VenueId = venueId, From = from, To = to });

            var data = rows.AsList();

            // Summary totals
            var totalSessionRevenue = data.Sum(r => (decimal)(r.session_revenue ?? 0m));
            var totalPosRevenue = data.Sum(r => (decimal)(r.pos_revenue ?? 0m));
            var totalPackageRevenue = data.Sum(r => (decimal)(r.package_revenue ?? 0m));

            return Results.Ok(new
            {
                data,
                summary = new
                {
                    session_revenue = totalSessionRevenue,
                    pos_revenue = totalPosRevenue,
                    package_revenue = totalPackageRevenue,
                    total_revenue = totalSessionRevenue + totalPosRevenue + totalPackageRevenue,
                    days = data.Count,
                },
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Venue Analytics");

        // ── 3. Utilization data ─────────────────────────────────────────────
        app.MapGet("/api/venues/{venueId}/analytics/utilization", async (
            Guid venueId,
            [FromQuery] string from,
            [FromQuery] string to,
            [FromQuery] Guid? zone_id,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (!DateOnly.TryParse(from, out var fromDate) || !DateOnly.TryParse(to, out var toDate))
                return Results.BadRequest(new { error = "Invalid date range. Use YYYY-MM-DD." });

            if (toDate < fromDate)
                return Results.BadRequest(new { error = "'to' must be >= 'from'." });

            if (toDate.DayNumber - fromDate.DayNumber > 365)
                return Results.BadRequest(new { error = "Date range cannot exceed 365 days." });

            using var conn = db.CreateConnection();

            // Daily utilization from daily_stats
            var daily = await conn.QueryAsync<dynamic>(
                """
                SELECT d.date,
                       COALESCE(ds.avg_utilization, 0) AS utilization,
                       COALESCE(ds.total_sessions, 0) AS total_sessions,
                       COALESCE(ds.total_hours, 0) AS total_hours,
                       ds.peak_hour
                FROM generate_series(@From::date, @To::date, '1 day'::interval) d(date)
                LEFT JOIN daily_stats ds
                  ON ds.venue_id = @VenueId AND ds.date = d.date::date
                ORDER BY d.date
                """,
                new { VenueId = venueId, From = from, To = to });

            // Hourly breakdown — aggregate across the range
            // Optionally filter by zone via station join
            var hourly = await conn.QueryAsync<dynamic>(
                """
                SELECT EXTRACT(HOUR FROM s.started_at)::int AS hour,
                       COUNT(*) AS session_count,
                       COALESCE(SUM(
                           EXTRACT(EPOCH FROM (COALESCE(s.ended_at, NOW()) - s.started_at)) / 3600
                       ), 0) AS total_hours
                FROM venue_sessions s
                LEFT JOIN venue_stations vs
                  ON vs.venue_id = s.venue_id AND vs.station_id = s.station_id
                WHERE s.venue_id = @VenueId
                  AND s.started_at >= @From::date
                  AND s.started_at < (@To::date + INTERVAL '1 day')
                  AND (@ZoneId::uuid IS NULL OR vs.zone_id = @ZoneId)
                GROUP BY EXTRACT(HOUR FROM s.started_at)
                ORDER BY hour
                """,
                new { VenueId = venueId, From = from, To = to, ZoneId = zone_id });

            return Results.Ok(new
            {
                daily,
                hourly_breakdown = hourly,
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Venue Analytics");

        // ── 4. Member metrics ───────────────────────────────────────────────
        app.MapGet("/api/venues/{venueId}/analytics/members", async (
            Guid venueId,
            [FromQuery] string from,
            [FromQuery] string to,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (!DateOnly.TryParse(from, out var fromDate) || !DateOnly.TryParse(to, out var toDate))
                return Results.BadRequest(new { error = "Invalid date range. Use YYYY-MM-DD." });

            if (toDate < fromDate)
                return Results.BadRequest(new { error = "'to' must be >= 'from'." });

            using var conn = db.CreateConnection();

            // New members in period
            var newMembers = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*) FROM members
                WHERE venue_id = @VenueId
                  AND created_at >= @From::date
                  AND created_at < (@To::date + INTERVAL '1 day')
                """,
                new { VenueId = venueId, From = from, To = to });

            // Active members (had sessions in period)
            var activeMembers = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(DISTINCT member_id) FROM venue_sessions
                WHERE venue_id = @VenueId
                  AND started_at >= @From::date
                  AND started_at < (@To::date + INTERVAL '1 day')
                  AND member_id IS NOT NULL
                """,
                new { VenueId = venueId, From = from, To = to });

            // Total members
            var totalMembers = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM members WHERE venue_id = @VenueId",
                new { VenueId = venueId });

            // Top spenders in period (top 10)
            var topSpenders = await conn.QueryAsync<dynamic>(
                """
                SELECT m.id, m.display_name, m.email, m.avatar_url,
                       COALESCE(SUM(s.total_charged), 0) AS total_spent,
                       COUNT(s.id) AS session_count
                FROM members m
                LEFT JOIN venue_sessions s
                  ON s.member_id = m.id
                  AND s.venue_id = m.venue_id
                  AND s.started_at >= @From::date
                  AND s.started_at < (@To::date + INTERVAL '1 day')
                WHERE m.venue_id = @VenueId
                GROUP BY m.id, m.display_name, m.email, m.avatar_url
                HAVING COALESCE(SUM(s.total_charged), 0) > 0
                ORDER BY total_spent DESC
                LIMIT 10
                """,
                new { VenueId = venueId, From = from, To = to });

            // Retention: members active in period who also had sessions before
            var returningMembers = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(DISTINCT s.member_id)
                FROM venue_sessions s
                WHERE s.venue_id = @VenueId
                  AND s.started_at >= @From::date
                  AND s.started_at < (@To::date + INTERVAL '1 day')
                  AND s.member_id IS NOT NULL
                  AND EXISTS (
                    SELECT 1 FROM venue_sessions prev
                    WHERE prev.venue_id = @VenueId
                      AND prev.member_id = s.member_id
                      AND prev.started_at < @From::date
                  )
                """,
                new { VenueId = venueId, From = from, To = to });

            var retentionPercent = activeMembers > 0
                ? Math.Round((double)returningMembers / activeMembers * 100, 1)
                : 0.0;

            return Results.Ok(new
            {
                new_members = newMembers,
                active_members = activeMembers,
                total_members = totalMembers,
                returning_members = returningMembers,
                retention_percent = retentionPercent,
                top_spenders = topSpenders,
            });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Venue Analytics");

        // ── 5. Export CSV ───────────────────────────────────────────────────
        app.MapGet("/api/venues/{venueId}/analytics/export", async (
            Guid venueId,
            [FromQuery] string format,
            [FromQuery] string type,
            [FromQuery] string from,
            [FromQuery] string to,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (format != "csv")
                return Results.BadRequest(new { error = "Only 'csv' format is supported." });

            if (type is not "revenue" and not "sessions" and not "members")
                return Results.BadRequest(new { error = "Type must be 'revenue', 'sessions', or 'members'." });

            if (!DateOnly.TryParse(from, out var fromDate) || !DateOnly.TryParse(to, out var toDate))
                return Results.BadRequest(new { error = "Invalid date range. Use YYYY-MM-DD." });

            if (toDate < fromDate)
                return Results.BadRequest(new { error = "'to' must be >= 'from'." });

            if (toDate.DayNumber - fromDate.DayNumber > 365)
                return Results.BadRequest(new { error = "Date range cannot exceed 365 days." });

            using var conn = db.CreateConnection();
            var csv = new StringBuilder();

            switch (type)
            {
                case "revenue":
                {
                    csv.AppendLine("Date,Total Sessions,Session Revenue,POS Revenue,Package Revenue,Total Revenue,Total Orders");
                    var rows = await conn.QueryAsync<dynamic>(
                        """
                        SELECT ds.date, ds.total_sessions, ds.session_revenue,
                               ds.pos_revenue, ds.package_revenue, ds.total_revenue, ds.total_orders
                        FROM daily_stats ds
                        WHERE ds.venue_id = @VenueId
                          AND ds.date >= @From::date AND ds.date <= @To::date
                        ORDER BY ds.date
                        """,
                        new { VenueId = venueId, From = from, To = to });

                    foreach (var r in rows)
                        csv.AppendLine($"{r.date:yyyy-MM-dd},{r.total_sessions},{r.session_revenue},{r.pos_revenue},{r.package_revenue},{r.total_revenue},{r.total_orders}");
                    break;
                }

                case "sessions":
                {
                    csv.AppendLine("Session ID,Station,Type,Started At,Ended At,Duration (min),Total Charged,Member ID,Display Name");
                    var rows = await conn.QueryAsync<dynamic>(
                        """
                        SELECT s.id, s.station_id, s.session_type, s.started_at, s.ended_at,
                               s.duration_minutes, s.total_charged, s.member_id, s.display_name
                        FROM venue_sessions s
                        WHERE s.venue_id = @VenueId
                          AND s.started_at >= @From::date
                          AND s.started_at < (@To::date + INTERVAL '1 day')
                        ORDER BY s.started_at
                        """,
                        new { VenueId = venueId, From = from, To = to });

                    foreach (var r in rows)
                    {
                        var endedAt = r.ended_at is not null ? ((DateTimeOffset)r.ended_at).ToString("yyyy-MM-dd HH:mm:ss") : "";
                        var duration = r.duration_minutes is not null ? $"{r.duration_minutes:F1}" : "";
                        var displayName = EscapeCsv((string?)r.display_name);
                        csv.AppendLine($"{r.id},{r.station_id},{r.session_type},{r.started_at:yyyy-MM-dd HH:mm:ss},{endedAt},{duration},{r.total_charged},{r.member_id},{displayName}");
                    }
                    break;
                }

                case "members":
                {
                    csv.AppendLine("Member ID,Display Name,Email,Total Sessions,Total Spent,Total Hours,Loyalty Tier,Created At");
                    var rows = await conn.QueryAsync<dynamic>(
                        """
                        SELECT m.id, m.display_name, m.email, m.total_sessions,
                               m.total_spent, m.total_hours, m.loyalty_tier, m.created_at
                        FROM members m
                        WHERE m.venue_id = @VenueId
                          AND m.created_at >= @From::date
                          AND m.created_at < (@To::date + INTERVAL '1 day')
                        ORDER BY m.created_at
                        """,
                        new { VenueId = venueId, From = from, To = to });

                    foreach (var r in rows)
                    {
                        var displayName = EscapeCsv((string?)r.display_name);
                        var email = EscapeCsv((string?)r.email);
                        csv.AppendLine($"{r.id},{displayName},{email},{r.total_sessions},{r.total_spent},{r.total_hours},{r.loyalty_tier},{r.created_at:yyyy-MM-dd HH:mm:ss}");
                    }
                    break;
                }
            }

            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return Results.File(bytes, "text/csv", $"{type}_{from}_{to}.csv");
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Venue Analytics");

        // ── 6. Activity log ─────────────────────────────────────────────────
        app.MapGet("/api/venues/{venueId}/activity-log", async (
            Guid venueId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? action = null,
            [FromQuery] Guid? actor_id = null,
            IDbConnectionFactory db = null!,
            HttpContext ctx = null!,
            CancellationToken ct = default) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var offset = (page - 1) * pageSize;

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, actor_id, actor_name, action, target_type, target_id, details, created_at
                FROM activity_log
                WHERE venue_id = @VenueId
                  AND (@Action IS NULL OR action = @Action)
                  AND (@ActorId IS NULL OR actor_id = @ActorId)
                ORDER BY created_at DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new { VenueId = venueId, Action = action, ActorId = actor_id, Limit = pageSize, Offset = offset });

            var total = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)
                FROM activity_log
                WHERE venue_id = @VenueId
                  AND (@Action IS NULL OR action = @Action)
                  AND (@ActorId IS NULL OR actor_id = @ActorId)
                """,
                new { VenueId = venueId, Action = action, ActorId = actor_id });

            return Results.Ok(new { data = rows, total, page, page_size = pageSize });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Venue Analytics");

        // ── 7. Refresh daily stats ──────────────────────────────────────────
        app.MapPost("/api/venues/{venueId}/analytics/refresh-daily-stats", async (
            Guid venueId,
            [FromBody] RefreshDailyStatsRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var user = ctx.Items["UserContext"] as UserContext;
            if (user is null) return Results.Unauthorized();

            if (!await IsOwnerOrStaff(db, user.UserIdGuid, venueId))
                return Results.Forbid();

            if (!DateOnly.TryParse(req.Date, out var date))
                return Results.BadRequest(new { error = "Invalid date. Use YYYY-MM-DD." });

            // Don't allow refreshing future dates
            if (date > DateOnly.FromDateTime(DateTime.UtcNow))
                return Results.BadRequest(new { error = "Cannot refresh stats for future dates." });

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                "SELECT refresh_daily_stats(@VenueId, @Date::date)",
                new { VenueId = venueId, Date = req.Date });

            return Results.Ok(new { success = true, venue_id = venueId, date = req.Date });
        })
        .RequireAuthorization("Authenticated")
        .WithTags("Venue Analytics");
    }

    // ── Helper: check venue owner or active staff ──────────────────────
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

    // ── Helper: escape CSV field ───────────────────────────────────────
    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

// ── Request DTO ────────────────────────────────────────────────────────────
public record RefreshDailyStatsRequest(string Date);
