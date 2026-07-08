using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;
using Esportra.Core.Audit;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class GhostModeEndpoints
{
    private const int DefaultSessionDurationMinutes = 30;

    private static int GetSessionDurationMinutes(IConfiguration config)
    {
        var configured = config.GetValue<int?>("GhostMode:SessionDurationMinutes");
        return configured.HasValue ? Math.Clamp(configured.Value, 5, 120) : DefaultSessionDurationMinutes;
    }

    public static void MapGhostModeEndpoints(this WebApplication app)
    {
        // ── POST /api/admin/ghost/request ──────────────────────────────────────
        // Non-super_admin requests approval to ghost a user
        app.MapPost("/api/admin/ghost/request", async (
            [FromBody] GhostApprovalRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("users:impersonate"))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.BadRequest(new { error = "Reason is required for ghost mode requests" });

            using var conn = db.CreateConnection();

            // Check target user exists
            var targetExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM profiles WHERE id = @targetUserId)",
                new { targetUserId = req.TargetUserId });
            if (!targetExists)
                return Results.NotFound(new { error = "Target user not found" });

            // Check for existing pending request
            var existingRequest = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT id FROM ghost_approvals
                WHERE requester_id = @requesterId AND target_user_id = @targetUserId
                  AND status = 'pending'
                """,
                new { requesterId = userCtx.UserIdGuid, targetUserId = req.TargetUserId });

            if (existingRequest.HasValue)
                return Results.BadRequest(new { error = "You already have a pending request for this user" });

            var id = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO ghost_approvals (requester_id, target_user_id, reason, status)
                VALUES (@requesterId, @targetUserId, @reason, 'pending')
                RETURNING id
                """,
                new
                {
                    requesterId = userCtx.UserIdGuid,
                    targetUserId = req.TargetUserId,
                    reason = req.Reason
                });

            return Results.Ok(new { id, success = true, message = "Request submitted for approval" });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/ghost/approvals/pending ─────────────────────────────
        // Super admin views pending approval requests
        app.MapGet("/api/admin/ghost/approvals/pending", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("users:impersonate:approve"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var requests = await conn.QueryAsync<dynamic>(
                """
                SELECT ga.id, ga.requester_id, ga.target_user_id, ga.reason, ga.created_at,
                       req.username AS requester_name, req.avatar_url AS requester_avatar,
                       tgt.username AS target_name, tgt.avatar_url AS target_avatar
                FROM ghost_approvals ga
                JOIN profiles req ON req.id = ga.requester_id
                JOIN profiles tgt ON tgt.id = ga.target_user_id
                WHERE ga.status = 'pending'
                ORDER BY ga.created_at ASC
                """);
            return Results.Ok(requests);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/ghost/approvals/mine ────────────────────────────────
        // Current user's approved ghost requests (ready to use)
        app.MapGet("/api/admin/ghost/approvals/mine", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("users:impersonate"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var approvals = await conn.QueryAsync<dynamic>(
                """
                SELECT ga.id, ga.target_user_id, ga.reason, ga.status,
                       ga.approved_at, ga.expires_at, ga.created_at,
                       tgt.username AS target_user_name, tgt.avatar_url AS target_avatar,
                       approver.username AS approver_name
                FROM ghost_approvals ga
                JOIN profiles tgt ON tgt.id = ga.target_user_id
                LEFT JOIN profiles approver ON approver.id = ga.approved_by
                WHERE ga.requester_id = @requesterId
                  AND ga.status = 'approved'
                  AND (ga.expires_at IS NULL OR ga.expires_at > NOW())
                ORDER BY ga.approved_at DESC
                """,
                new { requesterId = userCtx.UserIdGuid });
            return Results.Ok(approvals);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/ghost/approvals/{id}/approve ───────────────────────
        app.MapPost("/api/admin/ghost/approvals/{id}/approve", async (
            Guid id,
            [FromBody] ApproveGhostRequest? req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("users:impersonate:approve"))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            // Clamp approval validity to 1-168 hours (max 7 days)
            var hours = Math.Clamp(req?.ValidForHours ?? 24, 1, 168);
            var expiresAt = DateTime.UtcNow.AddHours(hours);

            var affected = await conn.ExecuteAsync(
                """
                UPDATE ghost_approvals
                SET status = 'approved',
                    approved_by = @approvedBy,
                    approved_at = NOW(),
                    expires_at = @expiresAt
                WHERE id = @id AND status = 'pending'
                """,
                new { id, approvedBy = userCtx.UserIdGuid, expiresAt });

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Request not found or already processed" });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/ghost/approvals/{id}/deny ──────────────────────────
        app.MapPost("/api/admin/ghost/approvals/{id}/deny", async (
            Guid id,
            [FromBody] DenyGhostRequest? req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("users:impersonate:approve"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE ghost_approvals
                SET status = 'denied',
                    approved_by = @deniedBy,
                    approved_at = NOW(),
                    denial_reason = @denialReason
                WHERE id = @id AND status = 'pending'
                """,
                new { id, deniedBy = userCtx.UserIdGuid, denialReason = req?.Reason });

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Request not found or already processed" });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/ghost/{userId} ─────────────────────────────────────
        // Start a ghost session (super_admin direct, others need approval)
        app.MapPost("/api/admin/ghost/{userId}", async (
            Guid userId,
            [FromBody] StartGhostRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IConfiguration config,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("users:impersonate"))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.BadRequest(new { error = "Reason is required" });

            using var conn = db.CreateConnection();

            // Check target user exists
            var targetUser = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, username FROM profiles WHERE id = @userId",
                new { userId });
            if (targetUser is null)
                return Results.NotFound(new { error = "Target user not found" });

            // Check if super_admin or has approval
            Guid? approvalId = null;
            var isSuperAdmin = userCtx.AdminRoles.Contains("super_admin", StringComparer.OrdinalIgnoreCase);

            if (!isSuperAdmin)
            {
                // Need an approved request
                var approval = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id, expires_at FROM ghost_approvals
                    WHERE requester_id = @requesterId AND target_user_id = @targetUserId
                      AND status = 'approved' AND (expires_at IS NULL OR expires_at > NOW())
                    """,
                    new { requesterId = userCtx.UserIdGuid, targetUserId = userId });

                if (approval is null)
                    return Results.Forbid();

                approvalId = (Guid)approval.id;
            }

            // Generate token
            var token = GenerateGhostToken();
            var tokenHash = HashToken(token);

            var sessionDuration = GetSessionDurationMinutes(config);
            var expiresAt = DateTime.UtcNow.AddMinutes(sessionDuration);

            var sessionId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO ghost_sessions
                    (admin_id, target_user_id, approval_id, token_hash, reason,
                     admin_ip, admin_user_agent, expires_at)
                VALUES
                    (@adminId, @targetUserId, @approvalId, @tokenHash, @reason,
                     @adminIp, @adminUserAgent, @expiresAt)
                RETURNING id
                """,
                new
                {
                    adminId = userCtx.UserIdGuid,
                    targetUserId = userId,
                    approvalId,
                    tokenHash,
                    reason = req.Reason,
                    adminIp = ctx.Connection.RemoteIpAddress?.ToString(),
                    adminUserAgent = ctx.Request.Headers.UserAgent.ToString(),
                    expiresAt
                });

            // Audit log
            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Login, TargetType.User,
                userId, (string)targetUser.username,
                details: $"Ghost session started: {req.Reason}",
                ct: ct);

            return Results.Ok(new
            {
                session_id = sessionId,
                ghost_token = token,
                expires_at = expiresAt,
                target_user = new { id = userId, username = (string)targetUser.username }
            });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/ghost/end ──────────────────────────────────────────
        // End a ghost session (token in body for easier client integration)
        app.MapPost("/api/admin/ghost/end", async (
            [FromBody] EndGhostRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Token))
                return Results.BadRequest(new { error = "Ghost token required" });

            var tokenHash = HashToken(req.Token);

            using var conn = db.CreateConnection();

            var session = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT gs.id, gs.admin_id, gs.target_user_id, p.username AS target_username
                FROM ghost_sessions gs
                JOIN profiles p ON p.id = gs.target_user_id
                WHERE gs.token_hash = @tokenHash AND gs.ended_at IS NULL
                """,
                new { tokenHash });

            if (session is null)
                return Results.NotFound(new { error = "Active ghost session not found" });

            await conn.ExecuteAsync(
                "UPDATE ghost_sessions SET ended_at = NOW() WHERE id = @id",
                new { id = (Guid)session.id });

            var userCtx = ctx.Items["UserContext"] as UserContext;
            await audit.LogAsync(
                (Guid)session.admin_id, userCtx?.Email ?? "unknown",
                ActionType.Logout, TargetType.User,
                (Guid)session.target_user_id, (string)session.target_username,
                details: "Ghost session ended",
                ct: ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/ghost ────────────────────────────────────────────
        // End the current ghost session (legacy, token in header)
        app.MapDelete("/api/admin/ghost", async (
            [FromHeader(Name = "X-Ghost-Token")] string? ghostToken,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(ghostToken))
                return Results.BadRequest(new { error = "Ghost token required" });

            var tokenHash = HashToken(ghostToken);

            using var conn = db.CreateConnection();

            var session = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT gs.id, gs.admin_id, gs.target_user_id, p.username AS target_username
                FROM ghost_sessions gs
                JOIN profiles p ON p.id = gs.target_user_id
                WHERE gs.token_hash = @tokenHash AND gs.ended_at IS NULL
                """,
                new { tokenHash });

            if (session is null)
                return Results.NotFound(new { error = "Active ghost session not found" });

            await conn.ExecuteAsync(
                "UPDATE ghost_sessions SET ended_at = NOW() WHERE id = @id",
                new { id = (Guid)session.id });

            // Audit log
            var userCtx = ctx.Items["UserContext"] as UserContext;
            await audit.LogAsync(
                (Guid)session.admin_id, userCtx?.Email ?? "unknown",
                ActionType.Logout, TargetType.User,
                (Guid)session.target_user_id, (string)session.target_username,
                details: "Ghost session ended",
                ct: ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/ghost/sessions ──────────────────────────────────────
        // Audit: view all ghost sessions
        app.MapGet("/api/admin/ghost/sessions", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct,
            [FromQuery] string? admin_id = null,
            [FromQuery] string? target_id = null,
            [FromQuery] int limit = 50) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("ghost:audit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            var p = new DynamicParameters();
            p.Add("limit", Math.Clamp(limit, 1, 200));

            if (Guid.TryParse(admin_id, out var adminGuid))
            {
                conditions.Add("gs.admin_id = @adminId");
                p.Add("adminId", adminGuid);
            }
            if (Guid.TryParse(target_id, out var targetGuid))
            {
                conditions.Add("gs.target_user_id = @targetId");
                p.Add("targetId", targetGuid);
            }

            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

            var sessions = await conn.QueryAsync<dynamic>(
                $"""
                SELECT gs.id, gs.admin_id, gs.target_user_id, gs.reason,
                       gs.admin_ip, gs.started_at, gs.ended_at, gs.expires_at,
                       gs.pages_viewed, gs.fields_unmasked,
                       admin.username AS admin_name,
                       target.username AS target_name
                FROM ghost_sessions gs
                JOIN profiles admin ON admin.id = gs.admin_id
                JOIN profiles target ON target.id = gs.target_user_id
                {where}
                ORDER BY gs.started_at DESC
                LIMIT @limit
                """, p);

            return Results.Ok(sessions);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/ghost/audit ─────────────────────────────────────────
        // All recent ghost data access logs across all sessions
        app.MapGet("/api/admin/ghost/audit", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct,
            [FromQuery] int limit = 100) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("ghost:audit"))
                return Results.Forbid();

            limit = Math.Clamp(limit, 1, 500);

            using var conn = db.CreateConnection();
            var logs = await conn.QueryAsync<dynamic>(
                """
                SELECT l.id, l.session_id, l.field_path AS resource_type,
                       CASE WHEN l.was_unmasked THEN 'unmask' ELSE 'view' END AS access_type,
                       split_part(l.field_path, '.', 2) AS field_name,
                       gs.target_user_id AS resource_id,
                       l.accessed_at
                FROM ghost_data_access_log l
                JOIN ghost_sessions gs ON gs.id = l.session_id
                ORDER BY l.accessed_at DESC
                LIMIT @limit
                """,
                new { limit });

            return Results.Ok(logs);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/ghost/sessions/{id}/access-log ──────────────────────
        // Detailed data access log for a session
        app.MapGet("/api/admin/ghost/sessions/{id}/access-log", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("ghost:audit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var logs = await conn.QueryAsync<dynamic>(
                """
                SELECT id, field_path, was_unmasked, unmasked_reason, accessed_at
                FROM ghost_data_access_log
                WHERE session_id = @sessionId
                ORDER BY accessed_at DESC
                """,
                new { sessionId = id });

            return Results.Ok(logs);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/ghost/log-page-view ────────────────────────────────
        // Called by frontend to log pages viewed during ghost session
        app.MapPost("/api/admin/ghost/log-page-view", async (
            [FromHeader(Name = "X-Ghost-Token")] string? ghostToken,
            [FromBody] LogPageViewRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(ghostToken))
                return Results.BadRequest(new { error = "Ghost token required" });

            var tokenHash = HashToken(ghostToken);

            using var conn = db.CreateConnection();

            // Update pages_viewed array
            await conn.ExecuteAsync(
                """
                UPDATE ghost_sessions
                SET pages_viewed = pages_viewed || @pageEntry::jsonb,
                    last_activity_at = NOW()
                WHERE token_hash = @tokenHash AND ended_at IS NULL
                """,
                new
                {
                    tokenHash,
                    pageEntry = JsonSerializer.Serialize(new { path = req.Path, timestamp = DateTime.UtcNow })
                });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/ghost/log-field-access ─────────────────────────────
        // Log access to sensitive fields
        app.MapPost("/api/admin/ghost/log-field-access", async (
            [FromHeader(Name = "X-Ghost-Token")] string? ghostToken,
            [FromBody] LogFieldAccessRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(ghostToken))
                return Results.BadRequest(new { error = "Ghost token required" });

            var tokenHash = HashToken(ghostToken);

            using var conn = db.CreateConnection();

            var sessionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM ghost_sessions WHERE token_hash = @tokenHash AND ended_at IS NULL",
                new { tokenHash });

            if (!sessionId.HasValue)
                return Results.NotFound(new { error = "Active ghost session not found" });

            await conn.ExecuteAsync(
                """
                INSERT INTO ghost_data_access_log (session_id, field_path, was_unmasked, unmasked_reason)
                VALUES (@sessionId, @fieldPath, @wasUnmasked, @unmaskedReason)
                """,
                new
                {
                    sessionId = sessionId.Value,
                    fieldPath = req.FieldPath,
                    wasUnmasked = req.WasUnmasked,
                    unmaskedReason = req.UnmaskedReason
                });

            // Also update session fields_unmasked if unmasked
            if (req.WasUnmasked)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE ghost_sessions
                    SET fields_unmasked = fields_unmasked || @entry::jsonb
                    WHERE id = @sessionId
                    """,
                    new
                    {
                        sessionId = sessionId.Value,
                        entry = JsonSerializer.Serialize(new
                        {
                            field = req.FieldPath,
                            timestamp = DateTime.UtcNow,
                            reason = req.UnmaskedReason
                        })
                    });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");
    }

    private static string GenerateGhostToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record GhostApprovalRequest(
    [property: JsonPropertyName("target_user_id")] Guid TargetUserId,
    string Reason);

public sealed record ApproveGhostRequest(
    [property: JsonPropertyName("valid_for_hours")] int? ValidForHours = null);

public sealed record DenyGhostRequest(string? Reason = null);

public sealed record StartGhostRequest(string Reason);

public sealed record EndGhostRequest(string Token);

public sealed record LogPageViewRequest(string Path);

public sealed record LogFieldAccessRequest(
    [property: JsonPropertyName("field_path")] string FieldPath,
    [property: JsonPropertyName("was_unmasked")] bool WasUnmasked = false,
    [property: JsonPropertyName("unmasked_reason")] string? UnmaskedReason = null);
