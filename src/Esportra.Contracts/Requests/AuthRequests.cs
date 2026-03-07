namespace Esportra.Contracts.Requests;

public sealed record SendRecoveryEmailRequest(string Email, string? RedirectBase = null);
public sealed record SetPasswordRequest(string Password, string? TokenHash = null, string? Type = null);
