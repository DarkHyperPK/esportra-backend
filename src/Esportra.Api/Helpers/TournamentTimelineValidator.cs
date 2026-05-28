using System.Data;

namespace Esportra.Api.Helpers;

public static class TournamentTimelineValidator
{
    public static string? ValidateDateOrder(DateTimeOffset? startDate, DateTimeOffset? endDate)
    {
        if (startDate is null || endDate is null)
            return null;

        return endDate < startDate
            ? "Tournament end date must be on or after the start date."
            : null;
    }

    public static string? ValidateRegistrationDeadline(DateTimeOffset? registrationDeadline, DateTimeOffset? startDate)
    {
        if (registrationDeadline is null || startDate is null)
            return null;

        return registrationDeadline > startDate
            ? "Registration deadline must be on or before the tournament start date."
            : null;
    }

    public static string FormatWindow(DateTimeOffset? startDate, DateTimeOffset? endDate)
    {
        if (startDate is null && endDate is null)
            return "the tournament schedule";

        if (startDate is not null && endDate is not null)
            return $"{startDate.Value:MMM d, yyyy h:mm tt} – {endDate.Value:MMM d, yyyy h:mm tt}";

        if (startDate is not null)
            return $"on or after {startDate.Value:MMM d, yyyy h:mm tt}";

        return $"on or before {endDate!.Value:MMM d, yyyy h:mm tt}";
    }

    public static string? ValidateTimestampWithinWindow(
        DateTimeOffset? timestamp,
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        string actionLabel)
    {
        if (timestamp is null)
            return null;

        if (startDate is not null && timestamp < startDate)
        {
            return $"{actionLabel} must be within the tournament window ({FormatWindow(startDate, endDate)}).";
        }

        if (endDate is not null && timestamp > endDate)
        {
            return $"{actionLabel} must be within the tournament window ({FormatWindow(startDate, endDate)}).";
        }

        return null;
    }

    public static string? ValidateTournamentIsOngoing(string? tournamentStatus)
    {
        if (string.Equals(tournamentStatus, "ongoing", StringComparison.OrdinalIgnoreCase))
            return null;

        return "The tournament must be marked as ongoing before starting a live round.";
    }
}
