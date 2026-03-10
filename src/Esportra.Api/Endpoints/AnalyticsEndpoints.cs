using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Analytics event tracking — replaces supabase.from('analytics_events').insert()
/// in useAnalytics.ts. Also provides admin read endpoints.
/// </summary>
public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        // ── POST /api/analytics/events ───────────────────────────────────────
        app.MapPost("/api/analytics/events", async (
            [FromBody] TrackEventRequest req,
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                """
                INSERT INTO analytics_events (user_id, event_type, event_data, session_id, user_agent, ip_address)
                VALUES (@userId, @eventType, @eventData::jsonb, @sessionId, @userAgent, @ipAddress)
                """,
                new
                {
                    userId    = userCtx?.UserId,
                    eventType = req.EventType,
                    eventData = req.EventData ?? "{}",
                    sessionId = req.SessionId,
                    userAgent = req.UserAgent,
                    ipAddress = ctx.Connection.RemoteIpAddress?.ToString(),
                });

            return Results.Ok(new { success = true });
        });

        // ── GET /api/analytics/events (admin) ────────────────────────────────
        app.MapGet("/api/analytics/events", async (
            IDbConnectionFactory db,
            [FromQuery] string startDate,
            [FromQuery] string endDate,
            [FromQuery] string? eventType = null,
            [FromQuery] int limit = 500,
            CancellationToken ct = default) =>
        {
            using var conn = db.CreateConnection();

            var filter = eventType is not null ? "AND event_type = @eventType" : "";

            var data = await conn.QueryAsync<dynamic>(
                $"""
                SELECT * FROM analytics_events
                WHERE created_at >= @startDate::timestamptz
                  AND created_at <= @endDate::timestamptz
                  {filter}
                ORDER BY created_at DESC
                LIMIT @limit
                """,
                new { startDate, endDate, eventType, limit });

            return Results.Ok(data);
        }).RequireAuthorization("Admin");

        // ── GET /api/analytics/user/{userId} (admin) ─────────────────────────
        app.MapGet("/api/analytics/user/{userId}", async (
            string userId,
            IDbConnectionFactory db,
            [FromQuery] string startDate,
            [FromQuery] string endDate,
            CancellationToken ct = default) =>
        {
            using var conn = db.CreateConnection();

            var data = await conn.QueryAsync<dynamic>(
                """
                SELECT event_type, event_data, created_at FROM analytics_events
                WHERE user_id = @userId
                  AND created_at >= @startDate::timestamptz
                  AND created_at <= @endDate::timestamptz
                ORDER BY created_at DESC
                """,
                new { userId, startDate, endDate });

            var events = data.AsList();
            var summary = new
            {
                total_events       = events.Count,
                page_views         = events.Count(e => (string)e.event_type == "page_view"),
                user_actions       = events.Count(e => (string)e.event_type == "user_action"),
                tournament_events  = events.Count(e => (string)e.event_type == "tournament_event"),
                team_events        = events.Count(e => (string)e.event_type == "team_event"),
                venue_events       = events.Count(e => (string)e.event_type == "venue_event"),
                payment_events     = events.Count(e => (string)e.event_type == "payment_event"),
                search_events      = events.Count(e => (string)e.event_type == "search"),
                error_events       = events.Count(e => (string)e.event_type == "error"),
            };

            return Results.Ok(new { data = events, summary });
        }).RequireAuthorization("Admin");

        // ── GET /api/me/roles ────────────────────────────────────────────────
        // Returns active user_roles + verified_roles for the current user.
        // Replaces RoleContext's Supabase queries.
        app.MapGet("/api/me/roles", async (
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var userRoles = await conn.QueryAsync<dynamic>(
                "SELECT role, is_active, assigned_at FROM user_roles WHERE user_id = @userId AND is_active = TRUE ORDER BY assigned_at DESC",
                new { userId = userCtx.UserId });

            var verifiedRoles = await conn.QueryAsync<dynamic>(
                "SELECT role, status, is_active FROM verified_roles WHERE user_id = @userId",
                new { userId = userCtx.UserId });

            return Results.Ok(new { userRoles, verifiedRoles });
        }).RequireAuthorization("Authenticated");
    }
}
