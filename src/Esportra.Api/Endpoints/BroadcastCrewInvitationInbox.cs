using System.Data;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Email;

namespace Esportra.Api.Endpoints;

public static partial class BroadcastCrewEndpoints
{
    private static void MapBroadcastCrewInvitationInbox(this WebApplication app)
    {
        app.MapGet("/api/broadcast/crew/invitations", ListReceivedInvites).RequireAuthorization("Authenticated");
        app.MapPost("/api/broadcast/crew/invitations/{inviteId:guid}/accept", AcceptReceivedInvite).RequireAuthorization("Authenticated");
    }

    private static async Task<IssuedInvite> IssueTargetedInvite(IDbConnection conn, Guid showId, Guid orgId, Guid creator, string role, string? recipientEmail)
    {
        var rawToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashString(rawToken); var expiresAt = DateTimeOffset.UtcNow.AddHours(72);
        var recipientId = recipientEmail is null ? null : await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM auth.users WHERE LOWER(email) = @recipientEmail AND email_confirmed_at IS NOT NULL ORDER BY created_at LIMIT 1", new { recipientEmail });
        var title = await conn.QuerySingleAsync<string>("SELECT title FROM broadcast_shows WHERE id = @showId", new { showId });
        using var tx = conn.BeginTransaction();
        var inviteId = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO broadcast_crew_invites (show_id, org_id, role, token_hash, created_by, expires_at, recipient_email, recipient_user_id)
            VALUES (@showId, @orgId, @role, @tokenHash, @creator, @expiresAt, @recipientEmail, @recipientId) RETURNING id
            """, new { showId, orgId, role, tokenHash, creator, expiresAt, recipientEmail, recipientId }, tx);
        if (recipientId.HasValue)
            await conn.ExecuteAsync("""
                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@recipientId, 'broadcast'::notification_type, 'Broadcast crew invitation', @message,
                        '/broadcast/invitations', jsonb_build_object('broadcast_invite_id', @inviteId::uuid), FALSE)
                """, new { recipientId, inviteId, message = $"You have been invited to {title} as {role.Replace('_', ' ')}." }, tx);
        tx.Commit();
        return new IssuedInvite(inviteId, rawToken, expiresAt, title, recipientId);
    }

    private sealed record IssuedInvite(Guid Id, string Token, DateTimeOffset ExpiresAt, string Title, Guid? RecipientId);

    private static async Task<string> SendCrewInviteEmail(IEmailService email, IConfiguration config, string recipient, string title, string role, string rawToken, CancellationToken ct)
    {
        try
        {
            var origin = new Uri(config["FrontendUrl"] ?? "https://esportra.com", UriKind.Absolute);
            if (origin.Scheme != Uri.UriSchemeHttps && !(origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback)) return "failed";
            var acceptUrl = new Uri(origin, "/broadcast/join#token=" + Uri.EscapeDataString(rawToken)).AbsoluteUri;
            await email.SendAsync(recipient, EmailType.BroadcastCrewInvite, new { showTitle = title, role, acceptUrl }, ct);
            return "submitted";
        }
        catch { return "failed"; }
    }

    private static async Task<bool> InviteRecipientMatches(IDbConnection conn, IDbTransaction tx, InviteRow invite, Guid userId)
    {
        if (invite.RecipientEmail is null) return true;
        if (invite.RecipientUserId.HasValue && invite.RecipientUserId.Value != userId) return false;
        return await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM auth.users WHERE id = @userId AND email_confirmed_at IS NOT NULL AND (@recipientId::uuid IS NOT NULL OR LOWER(email) = @email))", new { userId, recipientId = invite.RecipientUserId, email = invite.RecipientEmail }, tx);
    }

    private static async Task<IResult> ListReceivedInvites(HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<ReceivedInvite>("""
            SELECT i.id AS Id, i.show_id AS ShowId, s.title AS ShowTitle, i.role AS Role, i.expires_at AS ExpiresAt
            FROM broadcast_crew_invites i JOIN broadcast_shows s ON s.id = i.show_id
            WHERE i.claimed_at IS NULL AND i.expires_at > NOW()
              AND EXISTS(SELECT 1 FROM auth.users WHERE id = @userId AND email_confirmed_at IS NOT NULL)
              AND (i.recipient_user_id = @userId OR (i.recipient_user_id IS NULL AND i.recipient_email IS NOT NULL
                AND i.recipient_email = (SELECT LOWER(email) FROM auth.users WHERE id = @userId)))
            ORDER BY i.created_at DESC LIMIT 100
            """, new { userId = user.UserIdGuid });
        return Results.Ok(rows);
    }

    private static async Task<IResult> AcceptReceivedInvite(Guid inviteId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        var invite = await conn.QuerySingleOrDefaultAsync<InviteRow>("""
            SELECT id AS Id, show_id AS ShowId, role AS Role, recipient_email AS RecipientEmail, recipient_user_id AS RecipientUserId
            FROM broadcast_crew_invites WHERE id = @inviteId AND claimed_at IS NULL AND expires_at > NOW() FOR UPDATE
            """, new { inviteId }, tx);
        if (invite is null || invite.RecipientEmail is null || !await InviteRecipientMatches(conn, tx, invite, user.UserIdGuid)) return Results.NotFound(new { error = "invitation_unavailable" });
        await conn.ExecuteAsync("""
            UPDATE broadcast_crew_invites SET claimed_at = NOW(), claimed_by = @userId WHERE id = @inviteId;
            INSERT INTO broadcast_crew (show_id, user_id, role, joined_at) VALUES (@showId, @userId, @role, NOW())
            ON CONFLICT (show_id, user_id) DO UPDATE SET role = @role, is_active = true, joined_at = NOW();
            """, new { inviteId, userId = user.UserIdGuid, showId = invite.ShowId, role = invite.Role }, tx);
        tx.Commit();
        return Results.Ok(new { showId = invite.ShowId, role = invite.Role });
    }

    private sealed record ReceivedInvite(Guid Id, Guid ShowId, string ShowTitle, string Role, DateTime ExpiresAt);
}
