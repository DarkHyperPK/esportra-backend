using Esportra.Api.Auth;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Supabase;

namespace Esportra.Api.Services;

public sealed class PasswordRecoveryService(
    ISupabasePublicAuthClient publicAuthClient,
    ISupabaseAdminClient adminClient,
    IEmailService emailService)
{
    public const string GenericMessage = "If an account exists for this address, a recovery link will arrive shortly.";

    public async Task RequestAsync(string email, RecoveryPortal portal, CancellationToken cancellationToken)
    {
        // Prefer generating the recovery link via the admin API so the backend can send the branded template.
        // Fall back to the public auth client if admin generation or sending fails.
        try
        {
            var link = await adminClient.GenerateRecoveryLinkAsync(email, cancellationToken);
            // Send using backend email service (Resend or SMTP based on DI). Template expects 'resetUrl'.
            await emailService.SendAsync(email, Esportra.Infrastructure.Email.EmailType.PasswordReset, new { resetUrl = link.ActionLink }, cancellationToken);
            return;
        }
        catch
        {
            // If anything goes wrong (no admin access, network), fall back to public recover endpoint.
            await publicAuthClient.RequestPasswordRecoveryAsync(email, portal, cancellationToken);
        }
    }
}