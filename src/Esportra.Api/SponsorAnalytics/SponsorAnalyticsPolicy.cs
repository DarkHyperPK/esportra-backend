using System.Globalization;
using Esportra.Contracts.Responses;

namespace Esportra.Api.SponsorAnalytics;

public static class SponsorAnalyticsPolicy
{
    public static readonly int[] SupportedPeriods = [7, 30, 90];

    public static bool IsSupportedPeriod(int days) => SupportedPeriods.Contains(days);

    public static (DateOnly Start, DateOnly EndExclusive) CreateWindow(int days, DateTimeOffset now)
    {
        var endExclusive = DateOnly.FromDateTime(now.UtcDateTime.Date).AddDays(1);
        return (endExclusive.AddDays(-days), endExclusive);
    }

    public static string? NormalizeCountry(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return null;
        var normalized = countryCode.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsAsciiLetter) ? normalized : null;
    }

    public static string? CalculateAgeBand(DateOnly? dateOfBirth, DateOnly today)
    {
        if (dateOfBirth is null) return null;
        var dob = dateOfBirth.Value;
        var age = today.Year - dob.Year;
        if (dob > today.AddYears(-age)) age--;
        return age switch
        {
            >= 13 and < 18 => "13_17",
            >= 18 and < 25 => "18_24",
            >= 25 and < 35 => "25_34",
            >= 35 and < 45 => "35_44",
            >= 45 and < 55 => "45_54",
            >= 55 and <= 120 => "55_plus",
            _ => null,
        };
    }

    public static SponsorAudienceDimensionDto CreateDimension(
        IReadOnlyDictionary<string, long> candidateSegments,
        long totalAudience,
        long knownAudience)
    {
        var unknownAudience = totalAudience - knownAudience;
        var segments = candidateSegments
            .OrderByDescending(segment => segment.Value)
            .ThenBy(segment => segment.Key, StringComparer.Ordinal)
            .Select(segment => new SponsorAudienceSegmentDto(
            segment.Key,
            segment.Value,
            knownAudience > 0
                ? Math.Round((decimal)segment.Value / knownAudience * 100m, 1)
                : 0m)).ToList();

        return new SponsorAudienceDimensionDto(
            knownAudience > 0 ? "available" : "unavailable",
            knownAudience,
            unknownAudience,
            totalAudience > 0 ? Math.Round((decimal)knownAudience / totalAudience * 100m, 1) : null,
            segments);
    }
}
