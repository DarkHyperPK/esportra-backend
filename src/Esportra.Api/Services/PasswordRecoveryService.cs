using Esportra.Api.Auth;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Supabase;
using Microsoft.Extensions.Options;

namespace Esportra.Api.Services;

public sealed class PasswordRecoveryService(
    ISupabasePublicAuthClient publicAuthClient,
    ISupabaseAdminClient adminClient,
    IEmailService emailService,
    IOptions<RecoveryOptions> recoveryOptions)
{
    public const string GenericMessage = "If an account exists for this address, a recovery link will arrive shortly.";

    public async Task RequestAsync(string email, RecoveryPortal portal, CancellationToken cancellationToken)
    {
        var redirectUrl = portal == RecoveryPortal.Main
            ? recoveryOptions.Value.MainRedirectUrl
            : recoveryOptions.Value.PartnerRedirectUrl;

        try
        {
            var link = await adminClient.GenerateRecoveryLinkAsync(email, redirectUrl, cancellationToken);
            await emailService.SendAsync(email, EmailType.PasswordReset, new { resetUrl = link.ActionLink }, cancellationToken);
            return;
        }
        catch
        {
            await publicAuthClient.RequestPasswordRecoveryAsync(email, portal, cancellationToken);
        }
    }
}