using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Dapper;
using System.Net.Mail;
using Esportra.Api.Hubs;
using Esportra.Infrastructure.Email;
using Microsoft.AspNetCore.SignalR;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static partial class BroadcastCrewEndpoints
{
    private static readonly HashSet<string> ValidCrewRoles =
        ["producer", "observer", "monitor", "technical_director", "replay_operator"];

    public static void MapBroadcastCrewEndpoints(this WebApplication app)
    {
        app.MapBroadcastCrewWorkspaceEndpoints();
        app.MapBroadcastCrewInvitationInbox();
        app.MapPost("/api/broadcast/shows/{showId}/crew/invites", CreateCrewInvite)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("strict"));

        app.MapDelete("/api/broadcast/shows/{showId}/crew/invites/{inviteId}", RevokeCrewInvite)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/broadcast/crew/invites/accept", AcceptCrewInvite)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/broadcast/shows/{showId}/crew", ListCrew)
            .RequireAuthorization("Authenticated");

        app.MapDelete("/api/broadcast/shows/{showId}/crew/{userId}", RemoveCrewMember)
            .RequireAuthorization("Authenticated");
    }

    // ── POST /api/broadcast/shows/{showId}/crew/invites ───────────────────────

    private static async Task<IResult> CreateCrewInvite(
        Guid showId,
        [FromBody] CreateCrewInviteRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        IEmailService email,
        IHubContext<NotificationHub> notifications,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Role) || !ValidCrewRoles.Contains(req.Role))
            return Results.BadRequest(new
            {
                error = "role must be one of: producer, observer, monitor, technical_director, replay_operator",
            });
        string? recipientEmail = null;
        if (!string.IsNullOrWhiteSpace(req.Email))
        {
            if (req.Email.Length > 254 || !MailAddress.TryCreate(req.Email.Trim(), out var address) || address.Address != req.Email.Trim())
                return Results.BadRequest(new { error = "Enter a valid email address." });
            recipientEmail = address.Address.ToLowerInvariant();
        }

        using var conn = db.CreateConnection();
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT org_id FROM broadcast_shows WHERE id = @showId", new { showId });

        if (orgId is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, orgId.Value, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var invite = await IssueTargetedInvite(conn, showId, orgId.Value, userCtx.UserIdGuid, req.Role, recipientEmail);
        var inviteId = invite.Id; var rawToken = invite.Token; var expiresAt = invite.ExpiresAt;
        var recipientId = invite.RecipientId; var title = invite.Title;

        var emailStatus = recipientEmail is null ? "not_requested" : await SendCrewInviteEmail(email, config, recipientEmail, title, req.Role, rawToken, ct);
        await conn.ExecuteAsync("UPDATE broadcast_crew_invites SET email_status = @emailStatus WHERE id = @inviteId", new { emailStatus, inviteId });
        if (recipientId.HasValue)
        {
            try { await notifications.Clients.Group(NotificationHub.UserGroup(recipientId.Value.ToString())).SendAsync(NotificationHubEvents.NewNotification, new { type = "broadcast", title = "Broadcast crew invitation" }, ct); }
            catch { loggerFactory.CreateLogger("BroadcastCrewInvites").LogWarning("Crew invitation notification push failed; inbox remains available."); }
        }

        return Results.Ok(new { inviteId, token = rawToken, expiresAt, emailStatus, recipientBound = recipientEmail is not null });
    }

    // ── DELETE /api/broadcast/shows/{showId}/crew/invites/{inviteId} ──────────

    private static async Task<IResult> RevokeCrewInvite(
        Guid showId,
        Guid inviteId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT org_id FROM broadcast_shows WHERE id = @showId", new { showId });

        if (orgId is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, orgId.Value, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        // Delete only unclaimed invites — claimed invites are audit records and cannot be revoked.
        var affected = await conn.ExecuteAsync(
            """
            DELETE FROM broadcast_crew_invites
            WHERE id = @inviteId
              AND show_id = @showId
              AND claimed_at IS NULL
            """,
            new { inviteId, showId });

        return affected > 0
            ? Results.Ok(new { success = true })
            : Results.NotFound(new { error = "invite_not_found_or_already_claimed" });
    }

    // ── POST /api/broadcast/crew/invites/accept ───────────────────────────────

    private static async Task<IResult> AcceptCrewInvite(
        [FromBody] AcceptCrewInviteRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Token))
            return Results.BadRequest(new { error = "token is required" });

        var tokenHash = HashString(req.Token);

        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // FOR UPDATE prevents concurrent claims of the same invite token.
        var invite = await conn.QuerySingleOrDefaultAsync<InviteRow>(
            """
            SELECT id AS Id, show_id AS ShowId, role AS Role, recipient_email AS RecipientEmail, recipient_user_id AS RecipientUserId
            FROM broadcast_crew_invites
            WHERE token_hash = @tokenHash
              AND expires_at > NOW()
              AND claimed_at IS NULL
            FOR UPDATE
            """,
            new { tokenHash }, tx);

        if (invite is null)
        {
            tx.Rollback();
            return Results.NotFound(new { error = "invite_not_found_or_expired" });
        }
        if (!await InviteRecipientMatches(conn, tx, invite, userCtx.UserIdGuid)) return Results.Json(new { error = "Sign in with the invited account to accept this invitation." }, statusCode: 403);

        await conn.ExecuteAsync(
            "UPDATE broadcast_crew_invites SET claimed_at = NOW(), claimed_by = @userId WHERE id = @id",
            new { userId = userCtx.UserIdGuid, id = invite.Id }, tx);

        // ON CONFLICT handles re-invites: update role and re-activate.
        await conn.ExecuteAsync(
            """
            INSERT INTO broadcast_crew (show_id, user_id, role, joined_at)
            VALUES (@showId, @userId, @role, NOW())
            ON CONFLICT (show_id, user_id) DO UPDATE
                SET role = @role, is_active = true, joined_at = NOW()
            """,
            new { showId = invite.ShowId, userId = userCtx.UserIdGuid, role = invite.Role }, tx);

        tx.Commit();
        return Results.Ok(new { showId = invite.ShowId, role = invite.Role });
    }

    // ── GET /api/broadcast/shows/{showId}/crew ────────────────────────────────

    private static async Task<IResult> ListCrew(
        Guid showId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT org_id FROM broadcast_shows WHERE id = @showId", new { showId });

        if (orgId is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, orgId.Value, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var crew = await conn.QueryAsync<CrewListRow>(
            """
            SELECT c.id AS Id, c.user_id AS UserId, c.role AS Role,
                   c.joined_at AS JoinedAt, c.is_active AS IsActive,
                   p.username AS DisplayName, p.avatar_url AS AvatarUrl
            FROM broadcast_crew c
            LEFT JOIN profiles p ON p.id = c.user_id
            WHERE c.show_id = @showId AND c.is_active = true
            ORDER BY c.joined_at
            """,
            new { showId });

        var pendingInvites = await conn.QueryAsync<PendingInviteRow>(
            """
            SELECT id AS Id, show_id AS ShowId, role AS Role,
                   expires_at AS ExpiresAt, created_at AS CreatedAt, recipient_email AS RecipientEmail, email_status AS EmailStatus
            FROM broadcast_crew_invites
            WHERE show_id = @showId
              AND claimed_at IS NULL
              AND expires_at > NOW()
            ORDER BY created_at DESC
            """,
            new { showId });

        return Results.Ok(new { crew, pendingInvites });
    }

    // ── DELETE /api/broadcast/shows/{showId}/crew/{userId} ────────────────────

    private static async Task<IResult> RemoveCrewMember(
        Guid showId,
        Guid userId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        // Authorization is embedded: org owner check via correlated subquery.
        var affected = await conn.ExecuteAsync(
            """
            UPDATE broadcast_crew
            SET is_active = false
            WHERE show_id = @showId
              AND user_id = @userId
              AND EXISTS (
                SELECT 1 FROM broadcast_shows bs
                JOIN organizations o ON o.id = bs.org_id
                WHERE bs.id = @showId AND o.owner_id = @callerId
              )
            """,
            new { showId, userId, callerId = userCtx.UserIdGuid });

        return affected > 0
            ? Results.Ok(new { success = true })
            : Results.NotFound(new { error = "crew_member_not_found" });
    }

    // ── Private helpers (delegates to BroadcastHelpers) ──────────────────────

    private static Task<bool> IsOrgOwnerAsync(
        System.Data.IDbConnection conn, Guid orgId, Guid userId) =>
        BroadcastHelpers.IsOrgOwnerAsync(conn, orgId, userId);

    private static string HashString(string value) =>
        BroadcastHelpers.HashString(value);

    // ── Private row types ─────────────────────────────────────────────────────

    private sealed record InviteRow(Guid Id, Guid ShowId, string Role, string? RecipientEmail, Guid? RecipientUserId);
    private sealed record CrewListRow(Guid Id, Guid UserId, string Role,
        DateTime? JoinedAt, bool IsActive, string? DisplayName, string? AvatarUrl);
    private sealed record PendingInviteRow(Guid Id, Guid ShowId, string Role,
        DateTime ExpiresAt, DateTime CreatedAt, string? RecipientEmail, string? EmailStatus);
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public sealed record CreateCrewInviteRequest(string Role, string? Email = null);

public sealed record AcceptCrewInviteRequest(string Token);
