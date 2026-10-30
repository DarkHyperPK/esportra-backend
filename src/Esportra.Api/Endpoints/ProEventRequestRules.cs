using System.Net.Mail;
using Esportra.Contracts.Licensing;

namespace Esportra.Api.Endpoints;

internal static class ProEventRequestRules
{
    internal static string? Validate(ProEventRequest request, DateOnly today)
    {
        if (request.RequestId == Guid.Empty) return "A request identifier is required.";
        if (!ProEventProofEndpoints.ValidToken(request.AccessToken)) return "A secure request token is required.";
        if (!CleanText(request.EventName, 3, 120)) return "Enter an event name between 3 and 120 characters.";
        if (!CleanText(request.GameTitle, 2, 80)) return "Enter a game title between 2 and 80 characters.";
        if (!CleanText(request.Email, 3, 254) || !MailAddress.TryCreate(request.Email.Trim(), out var email)
            || !string.Equals(email.Address, request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            return "Enter a valid reply email.";
        if (request.StartsOn < today || request.StartsOn > today.AddYears(3))
            return "Choose a future event date within the next three years.";
        if (request.EndsOn < request.StartsOn || request.EndsOn > request.StartsOn.AddYears(1))
            return "Choose an end date on or after the start, within one year.";
        if (request.TeamCount is < 2 or > 10000) return "Enter between 2 and 10,000 teams.";
        if (!string.IsNullOrEmpty(request.Website)) return "Unable to accept this request.";
        return null;
    }

    internal static ProEventRequest Normalize(ProEventRequest request) => request with
    {
        EventName = request.EventName.Trim(),
        GameTitle = request.GameTitle.Trim(),
        Email = request.Email.Trim().ToLowerInvariant(),
        Website = "",
    };

    private static bool CleanText(string? value, int min, int max) =>
        value is not null && value.Trim().Length >= min && value.Length <= max && !value.Any(char.IsControl);
}
