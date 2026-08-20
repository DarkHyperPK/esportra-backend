using Dapper;
using Esportra.Contracts.Database;
using Esportra.Contracts.Responses;

namespace Esportra.Api.SponsorAnalytics;

public sealed class SponsorAudienceReportService(
    IDbConnectionFactory connectionFactory,
    TimeProvider timeProvider)
{
    public async Task<SponsorAudienceReportResponse> GetAsync(
        Guid sponsorId,
        int days,
        CancellationToken cancellationToken)
    {
        if (!SponsorAnalyticsPolicy.IsSupportedPeriod(days))
            throw new ArgumentOutOfRangeException(nameof(days));

        var now = timeProvider.GetUtcNow();
        var (startDate, endExclusiveDate) = SponsorAnalyticsPolicy.CreateWindow(days, now);
        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusive = endExclusiveDate.ToDateTime(TimeOnly.MinValue);
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
        var window = new SponsorAnalyticsWindowDto(startDate, endExclusiveDate, now);
        var disclosure = new SponsorAnalyticsDisclosureDto("exact-aggregates-v2", false);
        if (total == 0)
            return Empty(days, window, disclosure);

        var countryGroups = audience.Where(row => row.CountryCode is not null)
            .GroupBy(row => row.CountryCode!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal);
        var ageGroups = audience.Where(row => row.AgeBand is not null)
            .GroupBy(row => row.AgeBand!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal);

        return new SponsorAudienceReportResponse(
            2,
            "available",
            days,
            window,
            disclosure,
            total,
            SponsorAnalyticsPolicy.CreateDimension(countryGroups, total, countryGroups.Values.Sum()),
            SponsorAnalyticsPolicy.CreateDimension(ageGroups, total, ageGroups.Values.Sum()));
    }

    private static SponsorAudienceReportResponse Empty(
        int days,
        SponsorAnalyticsWindowDto window,
        SponsorAnalyticsDisclosureDto disclosure) => new(
            2, "empty", days, window, disclosure, 0,
            new SponsorAudienceDimensionDto("unavailable", 0, 0, null, []),
            new SponsorAudienceDimensionDto("unavailable", 0, 0, null, []));

    private sealed record AudienceRow
    {
        public Guid AudienceId { get; init; }
        public string? CountryCode { get; init; }
        public string? AgeBand { get; init; }
    }
}
