using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
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
            int                  limit  = 50,
            int                  offset = 0,
            HttpContext          ctx    = null!,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Fetch notifications
            var notifications = await conn.QueryAsync<dynamic>(
                """
                SELECT id, user_id, type, title, message, link, team_id,
                       is_read, data, created_at
                FROM notifications
                WHERE user_id = @userId
                ORDER BY created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { userId = userCtx.UserIdGuid, limit, offset });

            // Fetch pending team invites (synthetic notifications)
            var invites = await conn.QueryAsync<dynamic>(
                """
                SELECT id, team_id, created_at, message
                FROM team_invitations
                WHERE invited_user_id = @userId AND status = 'pending'
                ORDER BY created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(new { notifications, invites });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/notifications/{id}/read ────────────────────────────────
        app.MapPut("/api/notifications/{id}/read", async (
            string               id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var idGuid = Guid.Parse(id);

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE notifications SET is_read = TRUE WHERE id = @id AND user_id = @userId",
                new { id = idGuid, userId = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/notifications/read-all ─────────────────────────────────
        app.MapPut("/api/notifications/read-all", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            string               id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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

        // ── POST /api/notifications/bulk-delete ─────────────────────────────
        app.MapPost("/api/notifications/bulk-delete", async (
            [FromBody] BulkDeleteNotificationsRequest req,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var syntheticIds = req.Ids.Where(id => id.StartsWith("invite-")).Select(id => Guid.Parse(id["invite-".Length..])).ToArray();
            var regularIds   = req.Ids.Where(id => !id.StartsWith("invite-")).Select(id => Guid.Parse(id)).ToArray();

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
            HttpContext                    ctx,
            IDbConnectionFactory          db,
            IHubContext<NotificationHub>  notifHub,
            CancellationToken             ct) =>
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
            var notifData = JsonSerializer.Serialize(new { team_id = req.TeamId });
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, team_id, data, is_read)
                VALUES (@userId, 'team_invite_response', 'Team Invite Accepted',
                        'An invited player accepted your team invite.',
                        @teamId, @data::jsonb, FALSE)
                """,
                new { userId = invite.invited_by_user_id, teamId = teamIdGuid, data = notifData });

            // Push via SignalR
            await notifHub.Clients
                .Group(NotificationHub.UserGroup(invite.invited_by_user_id.ToString()))
                .SendAsync(NotificationHubEvents.NewNotification,
                    new { type = "team_invite_response", title = "Team Invite Accepted" }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/notifications/reject-invite ───────────────────────────
        app.MapPost("/api/notifications/reject-invite", async (
            [FromBody] InviteActionRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory          db,
            IHubContext<NotificationHub>  notifHub,
            CancellationToken             ct) =>
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
            var notifData = JsonSerializer.Serialize(new { team_id = req.TeamId });
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, team_id, data, is_read)
                VALUES (@userId, 'team_invite_response', 'Team Invite Rejected',
                        'An invited player rejected your team invite.',
                        @teamId, @data::jsonb, FALSE)
                """,
                new { userId = invite.invited_by_user_id, teamId = teamIdGuid, data = notifData });

            await notifHub.Clients
                .Group(NotificationHub.UserGroup(invite.invited_by_user_id.ToString()))
                .SendAsync(NotificationHubEvents.NewNotification,
                    new { type = "team_invite_response", title = "Team Invite Rejected" }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }
}

// ── Request records ──────────────────────────────────────────────────────────

public sealed record BulkDeleteNotificationsRequest(List<string> Ids);

public sealed record InviteActionRequest(
    string  TeamId,
    string? NotificationId = null);
