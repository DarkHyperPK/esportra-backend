using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;
using Hangfire;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastEndpoints
{
    public static void MapBroadcastEndpoints(this WebApplication app)
    {
        // ── GET /api/admin/broadcasts ──────────────────────────────────────────
        app.MapGet("/api/admin/broadcasts", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct,
            [FromQuery] string? status = null,
            [FromQuery] int limit = 50,
            [FromQuery] int offset = 0) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("broadcasts:view"))
                return Results.Forbid();

            limit = Math.Clamp(limit, 1, 100);

            using var conn = db.CreateConnection();
            var where = status is not null ? "WHERE b.status = @status" : "";

            var broadcasts = await conn.QueryAsync<dynamic>(
                $"""
                SELECT b.id, b.title, b.content, b.broadcast_type, b.priority,
                       b.target_type, b.target_segment, b.channels, b.status,
                       b.scheduled_at, b.sent_at, b.total_recipients,
                       b.delivered_count, b.read_count, b.created_at,
                       p.username AS created_by_name
                FROM broadcasts b
                LEFT JOIN profiles p ON p.id = b.created_by
                {where}
                ORDER BY b.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { status, limit, offset });

            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM broadcasts b {where}", new { status });

            return Results.Ok(new { items = broadcasts, total });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/broadcasts ─────────────────────────────────────────
        app.MapPost("/api/admin/broadcasts", async (
            [FromBody] CreateBroadcastRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("broadcasts:create"))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Content))
                return Results.BadRequest(new { error = "Title and Content are required" });

            using var conn = db.CreateConnection();

            var id = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO broadcasts (title, content, content_html, broadcast_type, priority,
                                        target_type, target_segment, target_user_ids, channels,
                                        status, scheduled_at, created_by)
                VALUES (@title, @content, @contentHtml, @broadcastType, @priority,
                        @targetType, @targetSegment::jsonb, @targetUserIds, @channels,
                        @status, @scheduledAt, @createdBy)
                RETURNING id
                """,
                new
                {
                    title = req.Title,
                    content = req.Content,
                    contentHtml = req.ContentHtml,
                    broadcastType = req.BroadcastType ?? "announcement",
                    priority = req.Priority ?? "normal",
                    targetType = req.TargetType ?? "all",
                    targetSegment = req.TargetSegment is not null
                        ? JsonSerializer.Serialize(req.TargetSegment)
                        : null,
                    targetUserIds = req.TargetUserIds,
                    channels = req.Channels ?? new[] { "in_app" },
                    status = req.ScheduledAt.HasValue ? "scheduled" : "draft",
                    scheduledAt = req.ScheduledAt,
                    createdBy = userCtx.UserIdGuid
                });

            return Results.Ok(new { id, success = true });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/broadcasts/{id} ─────────────────────────────────────
        app.MapPut("/api/admin/broadcasts/{id}", async (
            Guid id,
            [FromBody] UpdateBroadcastRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("broadcasts:edit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            // Can only edit drafts
            var currentStatus = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT status FROM broadcasts WHERE id = @id", new { id });
            if (currentStatus is null)
                return Results.NotFound(new { error = "Broadcast not found" });
            if (currentStatus != "draft" && currentStatus != "scheduled")
                return Results.BadRequest(new { error = "Can only edit draft or scheduled broadcasts" });

            var sets = new List<string> { "updated_at = NOW()" };
            var p = new DynamicParameters();
            p.Add("id", id);

            if (req.Title is not null) { sets.Add("title = @title"); p.Add("title", req.Title); }
            if (req.Content is not null) { sets.Add("content = @content"); p.Add("content", req.Content); }
            if (req.ContentHtml is not null) { sets.Add("content_html = @contentHtml"); p.Add("contentHtml", req.ContentHtml); }
            if (req.BroadcastType is not null) { sets.Add("broadcast_type = @broadcastType"); p.Add("broadcastType", req.BroadcastType); }
            if (req.Priority is not null) { sets.Add("priority = @priority"); p.Add("priority", req.Priority); }
            if (req.TargetType is not null) { sets.Add("target_type = @targetType"); p.Add("targetType", req.TargetType); }
            if (req.TargetSegment is not null)
            {
                sets.Add("target_segment = @targetSegment::jsonb");
                p.Add("targetSegment", JsonSerializer.Serialize(req.TargetSegment));
            }
            if (req.TargetUserIds is not null) { sets.Add("target_user_ids = @targetUserIds"); p.Add("targetUserIds", req.TargetUserIds); }
            if (req.Channels is not null) { sets.Add("channels = @channels"); p.Add("channels", req.Channels); }
            if (req.ScheduledAt.HasValue)
            {
                sets.Add("scheduled_at = @scheduledAt");
                sets.Add("status = 'scheduled'");
                p.Add("scheduledAt", req.ScheduledAt.Value);
            }

            await conn.ExecuteAsync(
                $"UPDATE broadcasts SET {string.Join(", ", sets)} WHERE id = @id", p);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/broadcasts/{id} ──────────────────────────────────
        app.MapDelete("/api/admin/broadcasts/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("broadcasts:delete"))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            // Can only delete drafts
            var affected = await conn.ExecuteAsync(
                "DELETE FROM broadcasts WHERE id = @id AND status IN ('draft', 'scheduled')",
                new { id });

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.BadRequest(new { error = "Can only delete draft or scheduled broadcasts" });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/broadcasts/{id}/send ───────────────────────────────
        app.MapPost("/api/admin/broadcasts/{id}/send", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            Hangfire.IBackgroundJobClient jobClient,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("broadcasts:send"))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var broadcast = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, status, target_type, target_segment, target_user_ids FROM broadcasts WHERE id = @id",
                new { id });
            if (broadcast is null)
                return Results.NotFound(new { error = "Broadcast not found" });
            if (broadcast.status == "sent")
                return Results.BadRequest(new { error = "Broadcast already sent" });
            if (broadcast.status == "sending")
                return Results.BadRequest(new { error = "Broadcast is already being sent" });

            // Update status to sending
            await conn.ExecuteAsync(
                "UPDATE broadcasts SET status = 'sending', updated_at = NOW() WHERE id = @id",
                new { id });

            // Enqueue background job for batch processing
            jobClient.Enqueue<Esportra.Api.ScheduledJobs.BroadcastSendJob>(
                job => job.ExecuteAsync(id, CancellationToken.None));

            return Results.Ok(new { success = true, message = "Broadcast queued for delivery" });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/broadcasts/{id}/cancel ─────────────────────────────
        app.MapPost("/api/admin/broadcasts/{id}/cancel", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("broadcasts:edit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE broadcasts
                SET status = 'cancelled', updated_at = NOW()
                WHERE id = @id AND status = 'scheduled'
                """,
                new { id });

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.BadRequest(new { error = "Can only cancel scheduled broadcasts" });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/broadcasts/{id}/stats ───────────────────────────────
        app.MapGet("/api/admin/broadcasts/{id}/stats", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("broadcasts:view"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var stats = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT b.total_recipients, b.delivered_count, b.read_count,
                       (SELECT COUNT(*) FROM broadcast_deliveries bd WHERE bd.broadcast_id = @id AND bd.status = 'failed') AS failed_count,
                       CASE WHEN b.total_recipients > 0
                            THEN ROUND(100.0 * b.read_count / b.total_recipients, 1)
                            ELSE 0 END AS read_rate
                FROM broadcasts b
                WHERE b.id = @id
                """, new { id });

            return stats is not null
                ? Results.Ok(stats)
                : Results.NotFound(new { error = "Broadcast not found" });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/broadcast-templates ─────────────────────────────────
        app.MapGet("/api/admin/broadcast-templates", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("broadcasts:view"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var templates = await conn.QueryAsync<dynamic>(
                """
                SELECT id, name, title, content, content_html, broadcast_type, created_at
                FROM broadcast_templates
                ORDER BY name
                """);
            return Results.Ok(templates);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/broadcast-templates ────────────────────────────────
        app.MapPost("/api/admin/broadcast-templates", async (
            [FromBody] CreateBroadcastTemplateRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("broadcasts:create"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var id = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO broadcast_templates (name, title, content, content_html, broadcast_type, created_by)
                VALUES (@name, @title, @content, @contentHtml, @broadcastType, @createdBy)
                RETURNING id
                """,
                new
                {
                    name = req.Name,
                    title = req.Title,
                    content = req.Content,
                    contentHtml = req.ContentHtml,
                    broadcastType = req.BroadcastType ?? "announcement",
                    createdBy = userCtx.UserIdGuid
                });

            return Results.Ok(new { id, success = true });
        }).RequireAuthorization("Admin");

        // ── User endpoints ─────────────────────────────────────────────────────

        // GET /api/notifications/broadcasts - Get user's broadcast notifications
        app.MapGet("/api/notifications/broadcasts", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct,
            [FromQuery] bool? unread_only = null,
            [FromQuery] int limit = 20) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var where = unread_only == true ? "AND bd.status != 'read'" : "";

            var notifications = await conn.QueryAsync<dynamic>(
                $"""
                SELECT bd.id, bd.broadcast_id, bd.status, bd.delivered_at, bd.read_at,
                       b.title, b.content, b.broadcast_type, b.priority
                FROM broadcast_deliveries bd
                JOIN broadcasts b ON b.id = bd.broadcast_id
                WHERE bd.user_id = @userId AND bd.channel = 'in_app' {where}
                ORDER BY bd.delivered_at DESC
                LIMIT @limit
                """,
                new { userId = userCtx.UserIdGuid, limit });

            return Results.Ok(notifications);
        }).RequireAuthorization("Authenticated");

        // PUT /api/notifications/broadcasts/{id}/read - Mark as read
        app.MapPut("/api/notifications/broadcasts/{id}/read", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE broadcast_deliveries
                SET status = 'read', read_at = NOW()
                WHERE broadcast_id = @broadcastId AND user_id = @userId AND status != 'read'
                """,
                new { broadcastId = id, userId = userCtx.UserIdGuid });

            if (affected > 0)
            {
                // Update broadcast read count
                await conn.ExecuteAsync(
                    "UPDATE broadcasts SET read_count = read_count + 1 WHERE id = @id",
                    new { id });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }
}

public sealed record CreateBroadcastRequest(
    string Title,
    string Content,
    [property: JsonPropertyName("content_html")] string? ContentHtml = null,
    [property: JsonPropertyName("broadcast_type")] string? BroadcastType = null,
    string? Priority = null,
    [property: JsonPropertyName("target_type")] string? TargetType = null,
    [property: JsonPropertyName("target_segment")] JsonElement? TargetSegment = null,
    [property: JsonPropertyName("target_user_ids")] Guid[]? TargetUserIds = null,
    string[]? Channels = null,
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset? ScheduledAt = null);

public sealed record UpdateBroadcastRequest(
    string? Title = null,
    string? Content = null,
    [property: JsonPropertyName("content_html")] string? ContentHtml = null,
    [property: JsonPropertyName("broadcast_type")] string? BroadcastType = null,
    string? Priority = null,
    [property: JsonPropertyName("target_type")] string? TargetType = null,
    [property: JsonPropertyName("target_segment")] JsonElement? TargetSegment = null,
    [property: JsonPropertyName("target_user_ids")] Guid[]? TargetUserIds = null,
    string[]? Channels = null,
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset? ScheduledAt = null);

public sealed record CreateBroadcastTemplateRequest(
    string Name,
    string Title,
    string Content,
    [property: JsonPropertyName("content_html")] string? ContentHtml = null,
    [property: JsonPropertyName("broadcast_type")] string? BroadcastType = null);
