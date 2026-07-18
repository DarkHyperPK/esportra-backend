namespace Esportra.Contracts.Responses;

public sealed record SponsorAnalyticsWindowDto(
    DateOnly StartsOn,
    DateOnly EndsOnExclusive,
    DateTimeOffset GeneratedAt);

public sealed record SponsorAnalyticsDisclosureDto(
    string MethodologyVersion,
    bool SmallSegmentsSuppressed);

public sealed record SponsorAudienceSegmentDto(
    string Key,
    long Audience,
    decimal PercentageOfKnown);

public sealed record SponsorAudienceDimensionDto(
    string Status,
    long? KnownAudience,
    long? UnknownAudience,
    decimal? CoveragePercent,
    IReadOnlyList<SponsorAudienceSegmentDto> Segments);

public sealed record SponsorAudienceReportResponse(
    int SchemaVersion,
    string Status,
    int PeriodDays,
    SponsorAnalyticsWindowDto Window,
    SponsorAnalyticsDisclosureDto Disclosure,
    long EstimatedUniqueAudience,
    SponsorAudienceDimensionDto Country,
    SponsorAudienceDimensionDto Age);

// --- Performance analytics ---

public sealed record SponsorAnalyticsSummaryResponse(
    int SchemaVersion,
    SponsorAnalyticsWindowDto Window,
    long TotalImpressions,
    long TotalClicks,
    decimal Ctr,
    long UniqueAudience,
    SponsorAnalyticsTrendDto Trend,
    IReadOnlyList<SponsorDeviceStatsDto> Devices);

public sealed record SponsorAnalyticsTrendDto(
    decimal ImpressionsChangePercent,
    decimal ClicksChangePercent,
    decimal CtrChangePercent,
    long PreviousPeriodImpressions,
    long PreviousPeriodClicks);

public sealed record SponsorAnalyticsPerformanceResponse(
    int SchemaVersion,
    SponsorAnalyticsWindowDto Window,
    IReadOnlyList<SponsorDailyPerformanceDto> Days);

public sealed record SponsorDailyPerformanceDto(
    DateOnly Date,
    long Impressions,
    long Clicks,
    decimal Ctr);

public sealed record SponsorAnalyticsPlacementsResponse(
    int SchemaVersion,
    SponsorAnalyticsWindowDto Window,
    IReadOnlyList<SponsorPlacementStatsDto> Placements);

public sealed record SponsorPlacementStatsDto(
    string Placement,
    long Impressions,
    long Clicks,
    decimal Ctr);

public sealed record SponsorAnalyticsContentResponse(
    int SchemaVersion,
    SponsorAnalyticsWindowDto Window,
    IReadOnlyList<SponsorTournamentStatsDto> Tournaments,
    IReadOnlyList<SponsorPageStatsDto> Pages);

public sealed record SponsorTournamentStatsDto(
    Guid TournamentId,
    string? TournamentName,
    long Impressions,
    long Clicks,
    decimal Ctr);

public sealed record SponsorPageStatsDto(
    string PagePath,
    long Impressions,
    long Clicks,
    decimal Ctr);

public sealed record SponsorAnalyticsDevicesResponse(
    int SchemaVersion,
    SponsorAnalyticsWindowDto Window,
    IReadOnlyList<SponsorDeviceStatsDto> Devices,
    IReadOnlyList<SponsorDailyDeviceDto> DailyBreakdown);

public sealed record SponsorDeviceStatsDto(
    string DeviceClass,
    long Impressions,
    long Clicks,
    decimal Ctr);

public sealed record SponsorDailyDeviceDto(
    DateOnly Date,
    string DeviceClass,
    long Impressions,
    long Clicks,
    decimal Ctr);

// --- Export ---

public sealed record SponsorAnalyticsExportResponse(
    Guid ExportId,
    string Status,
    string ReportType,
    int PeriodDays,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    string? DownloadUrl,
    DateTimeOffset? UrlExpiresAt);
