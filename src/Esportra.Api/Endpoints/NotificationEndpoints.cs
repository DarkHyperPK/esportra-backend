using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain 7: Notifications REST API
/// GET    /api/notifications                  — list user's notifications
/// PUT    /api/notifications/{id}/read        — mark one as read
/// PUT    /api/notifications/read-all         — mark all as read
/// DELETE /api/notifications/{id}             — delete one
/// POST   /api/notifications/bulk-delete      — delete many
/// POST   /api/notifications/accept-invite    — accept team invite from notification
/// POST   /api/notifications/reject-invite    — reject team invite from notification
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this WebApplication app)
    {
        // ── GET /api/notifications ──────────────────────────────────────────
        app.MapGet("/api/notifications", async (
            int limit = 50,
            int offset = 0,
            HttpContext ctx = null!,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Fetch notifications
            var notifications = await conn.QueryAsync<dynamic>(
                """
                SELECT id, user_id, type, title, message, link,
                       is_read, data, created_at
                FROM notifications
                WHERE user_id = @userId
                ORDER BY created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { userId = userCtx.UserIdGuid, limit, offset });

            // Dapper returns jsonb as string — parse to proper objects for JSON serialization
            var parsed = notifications.Select(n =>
            {
                if (n is IDictionary<string, object?> dict
                    && dict.TryGetValue("data", out var val) && val is string s && s.Length > 0)
                {
                    try { dict["data"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(s); }
                    catch { /* leave as-is */ }
                }
                return n;
            }).ToList();

            // Fetch pending team invites (synthetic notifications)
            var invites = await conn.QueryAsync<dynamic>(
                """
                SELECT id, team_id, created_at, message
                FROM team_invitations
                WHERE invited_user_id = @userId AND status = 'pending'
                ORDER BY created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(new { notifications = parsed, invites });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/notifications/{id}/read ────────────────────────────────
        app.MapPut("/api/notifications/{id}/read", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE notifications SET is_read = TRUE WHERE id = @id AND user_id = @userId",
                new { id, userId = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/notifications/read-all ─────────────────────────────────
        app.MapPut("/api/notifications/read-all", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var count = await conn.ExecuteAsync(
                "UPDATE notifications SET is_read = TRUE WHERE user_id = @userId AND is_read = FALSE",
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(new { success = true, updated = count });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/notifications/{id} ──────────────────────────────────
        app.MapDelete("/api/notifications/{id}", async (
            string id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Handle synthetic invite IDs (prefixed with "invite-")
            if (id.StartsWith("invite-"))
            {
                var inviteId = id["invite-".Length..];
                await conn.ExecuteAsync(
                    "DELETE FROM team_invitations WHERE id = @inviteId AND invited_user_id = @userId",
                    new { inviteId = Guid.Parse(inviteId), userId = userCtx.UserIdGuid });
            }
            else
            {
                await conn.ExecuteAsync(
                    "DELETE FROM notifications WHERE id = @id AND user_id = @userId",
                    new { id = Guid.Parse(id), userId = userCtx.UserIdGuid });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/notifications ─────────────────────────────────────────
        // Create a notification for a specific user (organizer/admin only)
        app.MapPost("/api/notifications", async (
            [FromBody] CreateNotificationRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> notifHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!Guid.TryParse(req.UserId, out var targetUserId))
                return Results.BadRequest(new { error = "Invalid userId" });

            using var conn = db.CreateConnection();

            // Only admins or organizers can send notifications to other users
            var callerRole = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT role::text FROM profiles WHERE id = @id",
                new { id = userCtx.UserIdGuid });
            var isAdmin = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM admin_user_roles WHERE user_id = @id)",
                new { id = userCtx.UserIdGuid });

            if (!isAdmin && callerRole != "organizer")
                return Results.Json(new { error = "Only organizers or admins can send notifications." }, statusCode: 403);

            var dataJson = req.Data is not null
                ? JsonSerializer.Serialize(req.Data)
                : null;

            try
            {
                var id = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                    VALUES (@userId, @type, @title, @message, @link, @data::jsonb, FALSE)
                    RETURNING id
                    """,
                    new
                    {
                        userId = targetUserId,
                        type = req.Type ?? "general",
                        title = req.Title ?? "",
                        message = req.Message ?? "",
                        link = req.Link,
                        data = dataJson
                    });

                // Push real-time notification via SignalR
                await notifHub.Clients
                    .Group(NotificationHub.UserGroup(targetUserId.ToString()))
                    .SendAsync(NotificationHubEvents.NewNotification,
                        new { id, type = req.Type, title = req.Title, message = req.Message, link = req.Link }, ct);

                return Results.Ok(new { id, success = true });
            }
            catch (Exception)
            {
                return Results.Json(new { error = "We couldn't send the notification. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/notifications/bulk-delete─────────────────────────────
        app.MapPost("/api/notifications/bulk-delete", async (
            [FromBody] BulkDeleteNotificationsRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var syntheticIds = req.Ids.Where(id => id.StartsWith("invite-")).Select(id => Guid.Parse(id["invite-".Length..])).ToArray();
            var regularIds = req.Ids.Where(id => !id.StartsWith("invite-")).Select(id => Guid.Parse(id)).ToArray();

            if (syntheticIds.Length > 0)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM team_invitations WHERE id = ANY(@ids) AND invited_user_id = @userId",
                    new { ids = syntheticIds, userId = userCtx.UserIdGuid });
            }

            if (regularIds.Length > 0)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM notifications WHERE id = ANY(@ids) AND user_id = @userId",
                    new { ids = regularIds, userId = userCtx.UserIdGuid });
            }

            return Results.Ok(new { success = true, deleted = req.Ids.Count });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/notifications/accept-invite ───────────────────────────
        app.MapPost("/api/notifications/accept-invite", async (
            [FromBody] InviteActionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> notifHub,
            DiscordNotificationService discord,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Check admin status
            var isAdmin = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT COALESCE(is_admin, FALSE) FROM profiles WHERE id = @userId",
                new { userId = userCtx.UserIdGuid });
            if (isAdmin) return Results.BadRequest(new { error = "Admins cannot join teams." });

            var teamIdGuid = Guid.Parse(req.TeamId);

            // Find the pending invite
            var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, team_id, invited_user_id, invited_by_user_id
                FROM team_invitations
                WHERE invited_user_id = @userId
                  AND team_id = @teamId
                  AND status = 'pending'
                LIMIT 1
                """,
                new { userId = userCtx.UserIdGuid, teamId = teamIdGuid });

            if (invite is null)
                return Results.NotFound(new { error = "Invite not found or expired." });

            // Add to team
            await conn.ExecuteAsync(
                """
                INSERT INTO team_members (team_id, user_id, role, is_active, joined_at)
                VALUES (@teamId, @userId, 'member', TRUE, NOW())
                ON CONFLICT (team_id, user_id) DO UPDATE SET is_active = TRUE, joined_at = NOW()
                """,
                new { teamId = teamIdGuid, userId = userCtx.UserIdGuid });

            // Accept invite
            await conn.ExecuteAsync(
                "UPDATE team_invitations SET status = 'accepted', responded_at = NOW() WHERE id = @id",
                new { id = invite.id });

            // Mark notification as read if provided
            if (req.NotificationId is not null && !req.NotificationId.StartsWith("invite-"))
            {
                await conn.ExecuteAsync(
                    "UPDATE notifications SET is_read = TRUE WHERE id = @id AND user_id = @userId",
                    new { id = Guid.Parse(req.NotificationId!), userId = userCtx.UserIdGuid });
            }

            // Notify inviter
            var acceptedPlayerName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT COALESCE(full_name, username, 'A player') FROM profiles WHERE id = @id",
                new { id = userCtx.UserIdGuid });
            var acceptedTeamName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM teams WHERE id = @teamId",
                new { teamId = Guid.Parse(req.TeamId) });
            var notifData = JsonSerializer.Serialize(new { team_id = req.TeamId });
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, data, is_read)
                VALUES (@userId, 'team_invite_response', @title,
                        @msg,
                        @data::jsonb, FALSE)
                """,
                new
                {
                    userId = invite.invited_by_user_id,
                    title = $"✅ {acceptedPlayerName} Joined {acceptedTeamName ?? "Your Team"}!",
                    msg = $"{acceptedPlayerName} accepted your invite and is now part of {acceptedTeamName ?? "the team"}. Your roster just got stronger!",
                    data = notifData
                });

            // Push via SignalR
            await notifHub.Clients
                .Group(NotificationHub.UserGroup(invite.invited_by_user_id.ToString()))
                .SendAsync(NotificationHubEvents.NewNotification,
                    new { type = "team_invite_response", title = $"✅ {acceptedPlayerName} Joined {acceptedTeamName ?? "Your Team"}!" }, ct);

            await discord.TrySendDmAsync(
                (Guid)invite.invited_by_user_id,
                "team_invite_response",
                "Invite Response",
                $"{acceptedPlayerName ?? "A player"} accepted your team invitation.");

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/notifications/reject-invite ───────────────────────────
        app.MapPost("/api/notifications/reject-invite", async (
            [FromBody] InviteActionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> notifHub,
            DiscordNotificationService discord,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var teamIdGuid = Guid.Parse(req.TeamId);

            using var conn = db.CreateConnection();

            var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, invited_by_user_id
                FROM team_invitations
                WHERE invited_user_id = @userId AND team_id = @teamId AND status = 'pending'
                LIMIT 1
                """,
                new { userId = userCtx.UserIdGuid, teamId = teamIdGuid });

            if (invite is null)
                return Results.NotFound(new { error = "Invite not found." });

            await conn.ExecuteAsync(
                "UPDATE team_invitations SET status = 'rejected', responded_at = NOW() WHERE id = @id",
                new { id = invite.id });

            if (req.NotificationId is not null && !req.NotificationId.StartsWith("invite-"))
            {
                await conn.ExecuteAsync(
                    "UPDATE notifications SET is_read = TRUE WHERE id = @id AND user_id = @userId",
                    new { id = Guid.Parse(req.NotificationId!), userId = userCtx.UserIdGuid });
            }

            // Notify inviter
            var rejectedPlayerName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT COALESCE(full_name, username, 'A player') FROM profiles WHERE id = @id",
                new { id = userCtx.UserIdGuid });
            var rejectedTeamName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM teams WHERE id = @teamId",
                new { teamId = Guid.Parse(req.TeamId) });
            var notifData = JsonSerializer.Serialize(new { team_id = req.TeamId });
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, data, is_read)
                VALUES (@userId, 'team_invite_response', @title,
                        @msg,
                        @data::jsonb, FALSE)
                """,
                new
                {
                    userId = invite.invited_by_user_id,
                    title = $"❌ Invite Declined — {rejectedTeamName ?? "Your Team"}",
                    msg = $"{rejectedPlayerName} declined your invite to join {rejectedTeamName ?? "the team"}.",
                    data = notifData
                });

            await notifHub.Clients
                .Group(NotificationHub.UserGroup(invite.invited_by_user_id.ToString()))
                .SendAsync(NotificationHubEvents.NewNotification,
                    new { type = "team_invite_response", title = $"❌ Invite Declined — {rejectedTeamName ?? "Your Team"}" }, ct);

            await discord.TrySendDmAsync(
                (Guid)invite.invited_by_user_id,
                "team_invite_response",
                "Invite Response",
                $"{rejectedPlayerName ?? "A player"} declined your team invitation.");

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }
}

// ── Request records ──────────────────────────────────────────────────────────

public sealed record BulkDeleteNotificationsRequest(List<string> Ids);

public sealed record CreateNotificationRequest(
    string UserId,
    string Type,
    string Title,
    string Message,
    string? Link = null,
    object? Data = null,
    bool IsRead = false);

public sealed record InviteActionRequest(
    string TeamId,
    string? NotificationId = null);
