namespace Esportra.Api.Auth;

public enum RecoveryPortal
{
    Main,
    Partner,
}

public sealed record PasswordRecoveryRequest(string? Email, string? Portal);

public sealed record PasswordRecoveryResponse(string Message);