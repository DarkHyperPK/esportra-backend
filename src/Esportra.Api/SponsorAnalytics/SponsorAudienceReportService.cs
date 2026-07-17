using Dapper;
using Esportra.Contracts.Database;
using Esportra.Contracts.Responses;
using Microsoft.Extensions.Options;

namespace Esportra.Api.SponsorAnalytics;

public sealed class SponsorAudienceReportService(
    IDbConnectionFactory connectionFactory,
    IOptions<SponsorAnalyticsOptions> options,
    TimeProvider timeProvider)
{
    private readonly int _threshold = options.Value.MinimumAudience;

    public async Task<SponsorAudienceReportResponse> GetAsync(
        Guid sponsorId,
        int days,
        CancellationToken cancellationToken)
    {
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            throw new ArgumentOutOfRangeException(nameof(days));

        var now = timeProvider.GetUtcNow();
        var (start, endExclusive) = SponsorAnalyticsPolicy.CreateWindow(days, now);
        using var connection = connectionFactory.CreateConnection();
        var audience = (await connection.QueryAsync<AudienceRow>(new CommandDefinition(
            """
            WITH ranked AS (
                SELECT audience_id AS AudienceId,
                       country_code AS CountryCode,
                       age_band AS AgeBand,
                       ROW_NUMBER() OVER (
                           PARTITION BY audience_id
                           ORDER BY fact_date DESC, last_event_sequence DESC
                       ) AS row_number
                FROM public.sponsor_audience_daily_facts
                WHERE sponsor_id = @sponsorId
                  AND event_type = 'impression'
                  AND fact_date >= @start
                  AND fact_date < @endExclusive
            )
            SELECT AudienceId, CountryCode, AgeBand
            FROM ranked
            WHERE row_number = 1
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        var total = audience.LongCount();
        var window = new SponsorAnalyticsWindowDto(start, endExclusive, now);
        var privacy = new SponsorAnalyticsPrivacyDto(_threshold, "k10-complementary-v1");
        if (total == 0)
            return Empty(days, window, privacy);
        if (total < _threshold)
            return Suppressed(days, window, privacy);

        var countryGroups = audience.Where(row => row.CountryCode is not null)
            .GroupBy(row => row.CountryCode!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal);
        var ageGroups = audience.Where(row => row.AgeBand is not null)
            .GroupBy(row => row.AgeBand!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal);

        return new SponsorAudienceReportResponse(
            1,
            "available",
            days,
            window,
            privacy,
            total,
            SponsorAnalyticsPolicy.Suppress(countryGroups, total, countryGroups.Values.Sum(), _threshold),
            SponsorAnalyticsPolicy.Suppress(ageGroups, total, ageGroups.Values.Sum(), _threshold));
    }

    private static SponsorAudienceReportResponse Empty(
        int days,
        SponsorAnalyticsWindowDto window,
        SponsorAnalyticsPrivacyDto privacy) => new(
            1, "empty", days, window, privacy, 0,
            new SponsorAudienceDimensionDto("unavailable", 0, 0, 0, 0, []),
            new SponsorAudienceDimensionDto("unavailable", 0, 0, 0, 0, []));

    private static SponsorAudienceReportResponse Suppressed(
        int days,
        SponsorAnalyticsWindowDto window,
        SponsorAnalyticsPrivacyDto privacy) => new(
            1, "suppressed", days, window, privacy, null,
            SponsorAnalyticsPolicy.SuppressedDimension(),
            SponsorAnalyticsPolicy.SuppressedDimension());

    private sealed record AudienceRow
    {
        public Guid AudienceId { get; init; }
        public string? CountryCode { get; init; }
        public string? AgeBand { get; init; }
    }
}
