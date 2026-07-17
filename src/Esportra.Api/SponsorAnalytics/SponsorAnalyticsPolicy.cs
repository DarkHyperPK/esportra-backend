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

    public static string? CalculateAgeBand(DateTime? dateOfBirth, DateOnly today)
    {
        if (dateOfBirth is null) return null;
        var dob = DateOnly.FromDateTime(dateOfBirth.Value);
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

    public static SponsorAudienceDimensionDto Suppress(
        IReadOnlyDictionary<string, long> candidateSegments,
        long totalAudience,
        long knownAudience,
        int threshold)
    {
        if (totalAudience < threshold)
            return SuppressedDimension();

        var published = candidateSegments
            .Where(segment => segment.Value >= threshold)
            .OrderByDescending(segment => segment.Value)
            .ToList();
        var suppressedCount = candidateSegments.Count - published.Count;

        // Complementary suppression: do not expose a sole published cell when hidden cells exist.
        if (suppressedCount > 0 && published.Count == 1)
        {
            suppressedCount += published.Count;
            published.Clear();
        }

        var unknownAudience = totalAudience - knownAudience;
        var canPublishKnown = knownAudience == 0 || knownAudience >= threshold;
        var canPublishUnknown = unknownAudience == 0 || unknownAudience >= threshold;
        var canPublishCoverage = canPublishKnown && canPublishUnknown;

        var segments = published.Select(segment => new SponsorAudienceSegmentDto(
            segment.Key,
            segment.Value,
            knownAudience > 0
                ? Math.Round((decimal)segment.Value / knownAudience * 100m, 1)
                : 0m)).ToList();

        return new SponsorAudienceDimensionDto(
            segments.Count > 0 && suppressedCount == 0
                ? "available"
                : knownAudience == 0 ? "unavailable" : "suppressed",
            canPublishCoverage ? knownAudience : null,
            canPublishCoverage ? unknownAudience : null,
            canPublishCoverage && totalAudience > 0
                ? Math.Round((decimal)knownAudience / totalAudience * 100m, 1)
                : null,
            suppressedCount,
            segments);
    }

    public static SponsorAudienceDimensionDto SuppressedDimension() => new(
        "suppressed", null, null, null, 0, []);
}
