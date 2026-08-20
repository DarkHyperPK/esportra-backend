using Dapper;
using Esportra.Contracts.Database;
using Esportra.Contracts.Responses;

namespace Esportra.Api.SponsorAnalytics;

public sealed class SponsorPerformanceReportService(
    IDbConnectionFactory connectionFactory,
    TimeProvider timeProvider)
{
    public async Task<SponsorAnalyticsSummaryResponse> GetSummaryAsync(
        Guid sponsorId,
        int days,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var (startDate, endExclusiveDate) = SponsorAnalyticsPolicy.CreateWindow(days, now);
        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusive = endExclusiveDate.ToDateTime(TimeOnly.MinValue);
        var previousStart = start.AddDays(-days);
        var window = new SponsorAnalyticsWindowDto(startDate, endExclusiveDate, now);

        using var connection = connectionFactory.CreateConnection();

        var current = await connection.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            """
            SELECT COALESCE(SUM(impressions), 0) AS Impressions,
                   COALESCE(SUM(clicks), 0) AS Clicks
            FROM public.sponsor_daily_totals
            WHERE sponsor_id = @sponsorId AND stat_date >= @start AND stat_date < @endExclusive
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken));

        var previous = await connection.QuerySingleAsync<TotalsRow>(new CommandDefinition(
            """
            SELECT COALESCE(SUM(impressions), 0) AS Impressions,
                   COALESCE(SUM(clicks), 0) AS Clicks
            FROM public.sponsor_daily_totals
            WHERE sponsor_id = @sponsorId AND stat_date >= @previousStart AND stat_date < @start
            """,
            new { sponsorId, previousStart, start },
            cancellationToken: cancellationToken));

        var uniqueAudience = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(DISTINCT audience_id)
            FROM public.sponsor_audience_daily_facts
            WHERE sponsor_id = @sponsorId
              AND event_type = 'impression'
              AND fact_date >= @start AND fact_date < @endExclusive
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken));

        var devices = (await connection.QueryAsync<DeviceRow>(new CommandDefinition(
            """
            SELECT device_class AS DeviceClass,
                   COALESCE(SUM(impressions), 0) AS Impressions,
                   COALESCE(SUM(clicks), 0) AS Clicks
            FROM public.sponsor_device_daily_stats
            WHERE sponsor_id = @sponsorId AND stat_date >= @start AND stat_date < @endExclusive
            GROUP BY device_class
            ORDER BY Impressions DESC
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        var currentCtr = ComputeCtr(current.Clicks, current.Impressions);
        var previousCtr = ComputeCtr(previous.Clicks, previous.Impressions);

        var trend = new SponsorAnalyticsTrendDto(
            ComputeChangePercent(current.Impressions, previous.Impressions),
            ComputeChangePercent(current.Clicks, previous.Clicks),
            previous.Impressions > 0 ? Math.Round(currentCtr - previousCtr, 2) : 0m,
            previous.Impressions,
            previous.Clicks);

        return new SponsorAnalyticsSummaryResponse(
            1,
            window,
            current.Impressions,
            current.Clicks,
            currentCtr,
            uniqueAudience,
            trend,
            devices.Select(d => new SponsorDeviceStatsDto(
                d.DeviceClass, d.Impressions, d.Clicks,
                ComputeCtr(d.Clicks, d.Impressions))).ToList());
    }

    public async Task<SponsorAnalyticsPerformanceResponse> GetPerformanceAsync(
        Guid sponsorId,
        int days,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var (startDate, endExclusiveDate) = SponsorAnalyticsPolicy.CreateWindow(days, now);
        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusive = endExclusiveDate.ToDateTime(TimeOnly.MinValue);
        var window = new SponsorAnalyticsWindowDto(startDate, endExclusiveDate, now);

        using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<DailyRow>(new CommandDefinition(
            """
            SELECT stat_date AS Date, impressions AS Impressions, clicks AS Clicks
            FROM public.sponsor_daily_totals
            WHERE sponsor_id = @sponsorId AND stat_date >= @start AND stat_date < @endExclusive
            ORDER BY stat_date ASC
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        return new SponsorAnalyticsPerformanceResponse(
            1,
            window,
            rows.Select(r => new SponsorDailyPerformanceDto(
                r.Date, r.Impressions, r.Clicks,
                ComputeCtr(r.Clicks, r.Impressions))).ToList());
    }

    public async Task<SponsorAnalyticsPlacementsResponse> GetPlacementsAsync(
        Guid sponsorId,
        int days,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var (startDate, endExclusiveDate) = SponsorAnalyticsPolicy.CreateWindow(days, now);
        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusive = endExclusiveDate.ToDateTime(TimeOnly.MinValue);
        var window = new SponsorAnalyticsWindowDto(startDate, endExclusiveDate, now);

        using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<PlacementRow>(new CommandDefinition(
            """
            SELECT placement AS Placement,
                   COALESCE(SUM(impressions), 0) AS Impressions,
                   COALESCE(SUM(clicks), 0) AS Clicks
            FROM public.sponsor_placement_daily_stats
            WHERE sponsor_id = @sponsorId AND stat_date >= @start AND stat_date < @endExclusive
            GROUP BY placement
            ORDER BY Impressions DESC
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        return new SponsorAnalyticsPlacementsResponse(
            1,
            window,
            rows.Select(r => new SponsorPlacementStatsDto(
                r.Placement, r.Impressions, r.Clicks,
                ComputeCtr(r.Clicks, r.Impressions))).ToList());
    }

    public async Task<SponsorAnalyticsContentResponse> GetContentAsync(
        Guid sponsorId,
        int days,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var (startDate, endExclusiveDate) = SponsorAnalyticsPolicy.CreateWindow(days, now);
        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusive = endExclusiveDate.ToDateTime(TimeOnly.MinValue);
        var window = new SponsorAnalyticsWindowDto(startDate, endExclusiveDate, now);

        using var connection = connectionFactory.CreateConnection();

        var tournaments = (await connection.QueryAsync<TournamentRow>(new CommandDefinition(
            """
            SELECT cs.tournament_id AS TournamentId,
                   t.name AS TournamentName,
                   COALESCE(SUM(cs.impressions), 0) AS Impressions,
                   COALESCE(SUM(cs.clicks), 0) AS Clicks
            FROM public.sponsor_content_daily_stats cs
            LEFT JOIN public.tournaments t ON t.id = cs.tournament_id
            WHERE cs.sponsor_id = @sponsorId
              AND cs.stat_date >= @start AND cs.stat_date < @endExclusive
              AND cs.tournament_id IS NOT NULL
            GROUP BY cs.tournament_id, t.name
            ORDER BY Impressions DESC
            LIMIT 10
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        var pages = (await connection.QueryAsync<PageRow>(new CommandDefinition(
            """
            SELECT page_path AS PagePath,
                   COALESCE(SUM(impressions), 0) AS Impressions,
                   COALESCE(SUM(clicks), 0) AS Clicks
            FROM public.sponsor_content_daily_stats
            WHERE sponsor_id = @sponsorId
              AND stat_date >= @start AND stat_date < @endExclusive
              AND page_path IS NOT NULL
            GROUP BY page_path
            ORDER BY Impressions DESC
            LIMIT 10
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        return new SponsorAnalyticsContentResponse(
            1,
            window,
            tournaments.Select(r => new SponsorTournamentStatsDto(
                r.TournamentId, r.TournamentName, r.Impressions, r.Clicks,
                ComputeCtr(r.Clicks, r.Impressions))).ToList(),
            pages.Select(r => new SponsorPageStatsDto(
                r.PagePath, r.Impressions, r.Clicks,
                ComputeCtr(r.Clicks, r.Impressions))).ToList());
    }

    public async Task<SponsorAnalyticsDevicesResponse> GetDevicesAsync(
        Guid sponsorId,
        int days,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var (startDate, endExclusiveDate) = SponsorAnalyticsPolicy.CreateWindow(days, now);
        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusive = endExclusiveDate.ToDateTime(TimeOnly.MinValue);
        var window = new SponsorAnalyticsWindowDto(startDate, endExclusiveDate, now);

        using var connection = connectionFactory.CreateConnection();

        var totals = (await connection.QueryAsync<DeviceRow>(new CommandDefinition(
            """
            SELECT device_class AS DeviceClass,
                   COALESCE(SUM(impressions), 0) AS Impressions,
                   COALESCE(SUM(clicks), 0) AS Clicks
            FROM public.sponsor_device_daily_stats
            WHERE sponsor_id = @sponsorId AND stat_date >= @start AND stat_date < @endExclusive
            GROUP BY device_class
            ORDER BY Impressions DESC
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        var daily = (await connection.QueryAsync<DailyDeviceRow>(new CommandDefinition(
            """
            SELECT stat_date AS Date,
                   device_class AS DeviceClass,
                   impressions AS Impressions,
                   clicks AS Clicks
            FROM public.sponsor_device_daily_stats
            WHERE sponsor_id = @sponsorId AND stat_date >= @start AND stat_date < @endExclusive
            ORDER BY stat_date ASC, device_class ASC
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        return new SponsorAnalyticsDevicesResponse(
            1,
            window,
            totals.Select(d => new SponsorDeviceStatsDto(
                d.DeviceClass, d.Impressions, d.Clicks,
                ComputeCtr(d.Clicks, d.Impressions))).ToList(),
            daily.Select(d => new SponsorDailyDeviceDto(
                d.Date, d.DeviceClass, d.Impressions, d.Clicks,
                ComputeCtr(d.Clicks, d.Impressions))).ToList());
    }

    public async Task<SponsorAnalyticsSlotsResponse> GetSlotsAsync(
        Guid sponsorId,
        int days,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var (startDate, endExclusiveDate) = SponsorAnalyticsPolicy.CreateWindow(days, now);
        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusive = endExclusiveDate.ToDateTime(TimeOnly.MinValue);
        var window = new SponsorAnalyticsWindowDto(startDate, endExclusiveDate, now);

        using var connection = connectionFactory.CreateConnection();

        var tournamentRows = (await connection.QueryAsync<SlotRow>(new CommandDefinition(
            """
            SELECT ss.tournament_id AS TournamentId,
                   t.name AS TournamentName,
                   ss.placement_zone AS PlacementZone,
                   COALESCE(SUM(ss.impressions), 0) AS Impressions,
                   COALESCE(SUM(ss.clicks), 0) AS Clicks
            FROM public.sponsor_slot_daily_stats ss
            LEFT JOIN public.tournaments t ON t.id = ss.tournament_id
            WHERE ss.sponsor_id = @sponsorId
              AND ss.stat_date >= @start AND ss.stat_date < @endExclusive
            GROUP BY ss.tournament_id, t.name, ss.placement_zone
            ORDER BY Impressions DESC
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        // Global placements (partner_showcase, homepage_ticker) have no tournament — pull from
        // sponsor_placement_daily_stats for zones that are known to be global.
        var globalRows = (await connection.QueryAsync<GlobalSlotRow>(new CommandDefinition(
            """
            SELECT placement AS PlacementZone,
                   COALESCE(SUM(impressions), 0) AS Impressions,
                   COALESCE(SUM(clicks), 0) AS Clicks
            FROM public.sponsor_placement_daily_stats
            WHERE sponsor_id = @sponsorId
              AND stat_date >= @start AND stat_date < @endExclusive
              AND placement IN ('partner_showcase', 'homepage_ticker')
            GROUP BY placement
            ORDER BY Impressions DESC
            """,
            new { sponsorId, start, endExclusive },
            cancellationToken: cancellationToken))).AsList();

        var slots = tournamentRows
            .Select(r => new SponsorSlotStatsDto(
                r.TournamentId, r.TournamentName, r.PlacementZone,
                r.Impressions, r.Clicks, ComputeCtr(r.Clicks, r.Impressions)))
            .Concat(globalRows.Select(r => new SponsorSlotStatsDto(
                null, null, r.PlacementZone,
                r.Impressions, r.Clicks, ComputeCtr(r.Clicks, r.Impressions))))
            .ToList();

        return new SponsorAnalyticsSlotsResponse(1, window, slots);
    }

    internal static decimal ComputeCtr(long clicks, long impressions) =>
        impressions > 0 ? Math.Round((decimal)clicks / impressions * 100m, 2) : 0m;

    internal static decimal ComputeChangePercent(long current, long previous) =>
        previous > 0 ? Math.Round((current - previous) / (decimal)previous * 100m, 1) : 0m;

    private sealed record TotalsRow(long Impressions, long Clicks);
    private sealed record DailyRow(DateOnly Date, long Impressions, long Clicks);
    private sealed record PlacementRow
    {
        public string Placement { get; init; } = string.Empty;
        public long Impressions { get; init; }
        public long Clicks { get; init; }
    }
    private sealed record DeviceRow(string DeviceClass, long Impressions, long Clicks);
    private sealed record DailyDeviceRow(DateOnly Date, string DeviceClass, long Impressions, long Clicks);
    private sealed record TournamentRow(Guid TournamentId, string? TournamentName, long Impressions, long Clicks);
    private sealed record PageRow(string PagePath, long Impressions, long Clicks);
    private sealed record SlotRow
    {
        public Guid TournamentId { get; init; }
        public string? TournamentName { get; init; }
        public string PlacementZone { get; init; } = string.Empty;
        public long Impressions { get; init; }
        public long Clicks { get; init; }
    }

    private sealed record GlobalSlotRow
    {
        public string PlacementZone { get; init; } = string.Empty;
        public long Impressions { get; init; }
        public long Clicks { get; init; }
    }
}
