namespace Esportra.Api.Services;

public static class PartnerInvitationPolicy
{
    public static TimeSpan Lifetime { get; } = TimeSpan.FromHours(24);

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static string NormalizeToken(string token) => token.Trim().ToUpperInvariant();

    public static bool IsValidEmail(string email) =>
        email.Length is > 0 and <= 254
        && System.Net.Mail.MailAddress.TryCreate(email.Trim(), out _);

    public static bool IsSupportedRole(string role) => role == "owner";

    public static bool IsValidToken(string token) =>
        token.Length == 64 && token.All(char.IsAsciiHexDigit);
}
