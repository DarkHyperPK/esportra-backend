using Esportra.Api.Auth;

namespace Esportra.Api.Services;

public sealed class PasswordRecoveryService(ISupabasePublicAuthClient publicAuthClient)
{
    public const string GenericMessage = "If an account exists for this address, a recovery link will arrive shortly.";

    public Task RequestAsync(string email, RecoveryPortal portal, CancellationToken cancellationToken)
        => publicAuthClient.RequestPasswordRecoveryAsync(email, portal, cancellationToken);
}