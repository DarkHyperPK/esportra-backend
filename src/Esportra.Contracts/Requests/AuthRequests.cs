namespace Esportra.Contracts.Requests;

public sealed record SetPasswordRequest(string Password, string? TokenHash = null, string? Type = null);

public sealed record DesktopLoginRequest(string Email, string Password);
