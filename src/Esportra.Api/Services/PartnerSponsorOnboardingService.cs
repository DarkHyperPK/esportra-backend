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
public sealed record PartnerInvitationPreview(bool RequiresPasswordSetup);

public sealed class PartnerSponsorOnboardingService(
    IDbConnectionFactory connectionFactory,
    ISupabaseAdminClient supabase,
    IEmailService emailService,
    IConfiguration configuration,
    ILogger<PartnerSponsorOnboardingService> logger)
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public async Task<PartnerInvitationDelivery?> CreateInvitationAsync(
        Guid sponsorId,
        string email,
        string role,
        Guid invitedBy,
        CancellationToken cancellationToken)
    {
        if (!IsValidEmail(email) || !IsSupportedRole(role)) return null;
        var normalizedEmail = NormalizeEmail(email);
        var existingUser = await supabase.GetUserByEmailAsync(normalizedEmail, cancellationToken);
        var requiresPasswordSetup = existingUser is null;

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var sponsorName = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM public.sponsors WHERE id = @sponsorId",
            new { sponsorId }, transaction);
        if (sponsorName is null) return null;

        await connection.ExecuteAsync(
            """
            UPDATE public.partner_sponsor_invitations
            SET status = 'revoked', revoked_at = NOW()
            WHERE LOWER(email) = @normalizedEmail AND status = 'pending'
            """,
            new { normalizedEmail }, transaction);

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
                expiresAt = DateTimeOffset.UtcNow.Add(InvitationLifetime),
            }, transaction);
        transaction.Commit();

        var invitationUrl = BuildInvitationUrl(token);
        var wasDelivered = true;
        try
        {
            if (requiresPasswordSetup)
            {
                var link = await supabase.GenerateInviteLinkAsync(normalizedEmail, invitationUrl, cancellationToken);
                invitationUrl = link.ActionLink;
            }

            await emailService.SendAsync(normalizedEmail, EmailType.PartnerInvite, new
            {
                sponsorName,
                invitationUrl,
                isNewUser = requiresPasswordSetup ? "true" : "false",
            }, cancellationToken);
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
        if (!IsValidToken(token)) return null;

        using var connection = connectionFactory.CreateConnection();
        return await connection.QuerySingleOrDefaultAsync<PartnerInvitationPreview>(
            """
            SELECT requires_password_setup AS RequiresPasswordSetup
            FROM public.partner_sponsor_invitations
            WHERE token_hash = @tokenHash
              AND status = 'pending'
              AND expires_at > NOW()
            """,
            new { tokenHash = HashToken(token) });
    }

    public async Task<PartnerInvitationClaim?> ClaimAsync(
        string token,
        Guid userId,
        string email,
        CancellationToken cancellationToken)
    {
        if (!IsValidToken(token)) return null;
        var tokenHash = HashToken(token);
        var normalizedEmail = NormalizeEmail(email);
        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext(@userId::text))",
            new { userId }, transaction);

        var invitation = await connection.QuerySingleOrDefaultAsync<InvitationRow>(
            """
            SELECT id AS Id, sponsor_id AS SponsorId, role AS Role, email AS Email
            FROM public.partner_sponsor_invitations
            WHERE token_hash = @tokenHash
              AND status = 'pending'
              AND expires_at > NOW()
            FOR UPDATE
            """,
            new { tokenHash }, transaction);
        if (invitation is null || !string.Equals(invitation.Email, normalizedEmail, StringComparison.Ordinal))
            return null;

        var hasMembership = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM public.sponsor_accounts WHERE user_id = @userId)",
            new { userId }, transaction);
        if (hasMembership) return null;

        await connection.ExecuteAsync(
            """
            INSERT INTO public.sponsor_accounts (user_id, sponsor_id, role, onboarding_meta)
            VALUES (@userId, @sponsorId, @role, '{"completed":false,"current_step":0,"steps":{}}'::jsonb)
            """,
            new { userId, invitation.SponsorId, invitation.Role }, transaction);
        await connection.ExecuteAsync(
            """
            UPDATE public.partner_sponsor_invitations
            SET status = 'accepted', accepted_at = NOW()
            WHERE id = @invitationId
            """,
            new { invitationId = invitation.Id }, transaction);
        transaction.Commit();

        return new PartnerInvitationClaim(invitation.SponsorId, invitation.Role);
    }

    private async Task MarkDeliveryFailureAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE public.partner_sponsor_invitations SET status = 'delivery_failed' WHERE id = @invitationId",
            new { invitationId });
    }

    private string BuildInvitationUrl(string token)
    {
        var partnerUrl = configuration["PartnerUrl"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("PartnerUrl is required.");
        return $"{partnerUrl}/invite/accept?token={Uri.EscapeDataString(token)}";
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static bool IsValidEmail(string email) =>
        email.Length is > 0 and <= 254
        && System.Net.Mail.MailAddress.TryCreate(email.Trim(), out _);

    private static bool IsSupportedRole(string role) => role is "owner" or "viewer";

    private static bool IsValidToken(string token) =>
        token.Length == 64 && token.All(character => char.IsAsciiHexDigit(character));

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record InvitationRow(Guid Id, Guid SponsorId, string Role, string Email);
}