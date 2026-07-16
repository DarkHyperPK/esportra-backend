using System.Security.Cryptography;
using System.Text;
using Dapper;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Supabase;

namespace Esportra.Api.Services;

public sealed record PartnerInvitationDelivery(
    Guid InvitationId,
    bool RequiresPasswordSetup,
    bool WasDelivered);
public sealed record PartnerInvitationClaim(Guid SponsorId, string Role);
public sealed record PartnerInvitationPreview(bool AccountExists, bool RequiresPasswordSetup);
public sealed record PartnerInvitationSummary(
    Guid Id,
    Guid SponsorId,
    string SponsorName,
    string Email,
    string Role,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? AcceptedAt);

public sealed class PartnerSponsorOnboardingService(
    IDbConnectionFactory connectionFactory,
    ISupabaseAdminClient supabase,
    IEmailService emailService,
    IConfiguration configuration,
    ILogger<PartnerSponsorOnboardingService> logger)
{
    public async Task<PartnerInvitationDelivery?> CreateInvitationAsync(
        Guid sponsorId,
        string email,
        string role,
        Guid invitedBy,
        CancellationToken cancellationToken)
    {
        if (!PartnerInvitationPolicy.IsValidEmail(email) || !PartnerInvitationPolicy.IsSupportedRole(role)) return null;
        var normalizedEmail = PartnerInvitationPolicy.NormalizeEmail(email);
        var existingUser = await supabase.GetUserByEmailAsync(normalizedEmail, cancellationToken);
        var accountExists = existingUser is not null;
        var requiresPasswordSetup = !accountExists;

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var sponsorName = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM public.sponsors WHERE id = @sponsorId",
            new { sponsorId }, transaction);
        if (sponsorName is null) return null;

        await connection.ExecuteAsync(
            """
            UPDATE public.partner_sponsor_invitations
                        SET status = 'revoked', revoked_at = NOW(), updated_at = NOW()
                        WHERE sponsor_id = @sponsorId
                            AND LOWER(email) = @normalizedEmail
                            AND status = 'pending'
            """,
                        new { sponsorId, normalizedEmail }, transaction);

        var token = CreateToken();
        var invitationId = await connection.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.partner_sponsor_invitations
                (sponsor_id, email, role, requires_password_setup, token_hash, invited_by, expires_at)
            VALUES
                (@sponsorId, @normalizedEmail, @role, @requiresPasswordSetup, @tokenHash, @invitedBy, @expiresAt)
            RETURNING id
            """,
            new
            {
                sponsorId,
                normalizedEmail,
                role,
                requiresPasswordSetup,
                tokenHash = HashToken(token),
                invitedBy,
                expiresAt = DateTimeOffset.UtcNow.Add(PartnerInvitationPolicy.Lifetime),
            }, transaction);

        transaction.Commit();

        var invitationUrl = BuildInvitationUrl(token);
        var wasDelivered = true;
        try
        {
            if (!accountExists)
            {
                var link = await supabase.GenerateInviteLinkAsync(normalizedEmail, invitationUrl, cancellationToken);
                invitationUrl = $"{invitationUrl}&auth_token_hash={Uri.EscapeDataString(link.TokenHash)}&auth_type=invite";
            }

            await emailService.SendAsync(normalizedEmail, EmailType.PartnerInvite, new
            {
                sponsorName,
                invitationUrl,
                accountExists = accountExists ? "true" : "false",
                requiresPasswordSetup = requiresPasswordSetup ? "true" : "false",
            }, cancellationToken);
            await MarkDeliveredAsync(invitationId);
        }
        catch (Exception exception)
        {
            wasDelivered = false;
            logger.LogWarning(exception, "Partner invitation delivery failed for invitation {InvitationId}", invitationId);
            await MarkDeliveryFailureAsync(invitationId, CancellationToken.None);
        }

        return new PartnerInvitationDelivery(invitationId, requiresPasswordSetup, wasDelivered);
    }

    public async Task<PartnerInvitationPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        if (!PartnerInvitationPolicy.IsValidToken(token)) return null;

        using var connection = connectionFactory.CreateConnection();
        var invitation = await connection.QuerySingleOrDefaultAsync<(string Email, bool RequiresPasswordSetup)>(
            """
            SELECT email AS Email, requires_password_setup AS RequiresPasswordSetup
            FROM public.partner_sponsor_invitations
            WHERE token_hash = @tokenHash
                            AND status IN ('pending', 'accepted')
              AND expires_at > NOW()
            """,
            new { tokenHash = HashToken(token) });
        if (invitation == default) return null;

        var accountExists = await supabase.GetUserByEmailAsync(invitation.Email, cancellationToken) is not null;
        return new PartnerInvitationPreview(accountExists, !accountExists);
    }

    public async Task<IReadOnlyList<PartnerInvitationSummary>> ListAsync(
        Guid? sponsorId,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        var invitations = await connection.QueryAsync<PartnerInvitationSummary>(new CommandDefinition(
            """
            SELECT invitation.id AS Id,
                   invitation.sponsor_id AS SponsorId,
                   sponsor.name AS SponsorName,
                   invitation.email AS Email,
                   invitation.role AS Role,
                   CASE
                       WHEN invitation.status = 'pending' AND invitation.expires_at <= NOW() THEN 'expired'
                       ELSE invitation.status
                   END AS Status,
                   invitation.created_at AS CreatedAt,
                   invitation.expires_at AS ExpiresAt,
                   invitation.delivered_at AS DeliveredAt,
                   invitation.accepted_at AS AcceptedAt
            FROM public.partner_sponsor_invitations AS invitation
            JOIN public.sponsors AS sponsor ON sponsor.id = invitation.sponsor_id
            WHERE (@sponsorId IS NULL OR invitation.sponsor_id = @sponsorId)
            ORDER BY invitation.created_at DESC
            LIMIT 200
            """,
            new { sponsorId },
            cancellationToken: cancellationToken));
        return invitations.AsList();
    }

    public async Task<bool> RevokeAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE public.partner_sponsor_invitations
            SET status = 'revoked', revoked_at = NOW(), updated_at = NOW()
            WHERE id = @invitationId AND status IN ('pending', 'delivery_failed')
            """,
            new { invitationId },
            cancellationToken: cancellationToken));
        return affectedRows == 1;
    }

    public async Task<PartnerInvitationDelivery?> ResendAsync(
        Guid invitationId,
        Guid invitedBy,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        var invitation = await connection.QuerySingleOrDefaultAsync<(Guid SponsorId, string Email, string Role)>(
            new CommandDefinition(
                """
                SELECT sponsor_id AS SponsorId, email AS Email, role AS Role
                FROM public.partner_sponsor_invitations
                WHERE id = @invitationId AND status IN ('pending', 'delivery_failed', 'expired', 'revoked')
                """,
                new { invitationId },
                cancellationToken: cancellationToken));
        if (invitation == default) return null;

        await RevokeAsync(invitationId, cancellationToken);
        return await CreateInvitationAsync(
            invitation.SponsorId,
            invitation.Email,
            invitation.Role,
            invitedBy,
            cancellationToken);
    }

    public async Task<PartnerInvitationClaim?> ClaimAsync(
        string token,
        Guid userId,
        string email,
        CancellationToken cancellationToken)
    {
        if (!PartnerInvitationPolicy.IsValidToken(token)) return null;
        var tokenHash = HashToken(token);
        var normalizedEmail = PartnerInvitationPolicy.NormalizeEmail(email);
        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext(@userId::text))",
            new { userId }, transaction);

        var invitation = await connection.QuerySingleOrDefaultAsync<InvitationRow>(
            """
            SELECT id AS Id,
                   sponsor_id AS SponsorId,
                   role AS Role,
                   email AS Email,
                   status AS Status,
                   accepted_by_user_id AS AcceptedByUserId
            FROM public.partner_sponsor_invitations
            WHERE token_hash = @tokenHash
              AND status IN ('pending', 'accepted')
              AND expires_at > NOW()
            FOR UPDATE
            """,
            new { tokenHash }, transaction);
        if (invitation is null || !string.Equals(invitation.Email, normalizedEmail, StringComparison.Ordinal))
            return null;

        if (invitation.Status == "accepted")
        {
            if (invitation.AcceptedByUserId != userId) return null;
            transaction.Commit();
            return new PartnerInvitationClaim(invitation.SponsorId, invitation.Role);
        }

        var existingSponsorId = await connection.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT sponsor_id
            FROM public.sponsor_accounts
            WHERE user_id = @userId AND status = 'active'
            """,
            new { userId }, transaction);
        if (existingSponsorId is not null && existingSponsorId != invitation.SponsorId)
            return null;

        if (existingSponsorId is null)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO public.sponsor_accounts
                    (user_id, sponsor_id, role, status, invitation_id, accepted_at, onboarding_meta)
                VALUES
                    (@userId, @sponsorId, @role, 'active', @invitationId, NOW(),
                     '{"completed":false,"current_step":0,"steps":{}}'::jsonb)
                """,
                new
                {
                    userId,
                    sponsorId = invitation.SponsorId,
                    role = invitation.Role,
                    invitationId = invitation.Id,
                }, transaction);
        }

        await connection.ExecuteAsync(
            """
            UPDATE public.partner_sponsor_invitations
            SET status = 'accepted',
                accepted_at = NOW(),
                accepted_by_user_id = @userId,
                updated_at = NOW()
            WHERE id = @invitationId
            """,
            new { invitationId = invitation.Id, userId }, transaction);
        transaction.Commit();

        return new PartnerInvitationClaim(invitation.SponsorId, invitation.Role);
    }

    private async Task MarkDeliveryFailureAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            """
            UPDATE public.partner_sponsor_invitations
            SET status = 'delivery_failed',
                delivery_attempts = delivery_attempts + 1,
                delivery_error_code = 'provider_error',
                updated_at = NOW()
            WHERE id = @invitationId
            """,
            new { invitationId });
    }

    private async Task MarkDeliveredAsync(Guid invitationId)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            """
            UPDATE public.partner_sponsor_invitations
            SET delivered_at = NOW(),
                delivery_attempts = delivery_attempts + 1,
                delivery_error_code = NULL,
                updated_at = NOW()
            WHERE id = @invitationId
            """,
            new { invitationId });
    }

    private string BuildInvitationUrl(string token)
    {
        var partnerUrl = configuration["PartnerUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("PartnerUrl is required.");
        return $"{partnerUrl}/invite/accept?token={Uri.EscapeDataString(token)}";
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(PartnerInvitationPolicy.NormalizeToken(token))));

    private sealed record InvitationRow(
        Guid Id,
        Guid SponsorId,
        string Role,
        string Email,
        string Status,
        Guid? AcceptedByUserId);
}