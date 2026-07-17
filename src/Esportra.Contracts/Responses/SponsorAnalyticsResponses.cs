namespace Esportra.Contracts.Responses;

public sealed record SponsorAnalyticsWindowDto(
    DateOnly StartsOn,
    DateOnly EndsOnExclusive,
    DateTimeOffset GeneratedAt);

public sealed record SponsorAnalyticsPrivacyDto(
    int MinimumAudience,
    string MethodologyVersion);

public sealed record SponsorAudienceSegmentDto(
    string Key,
    long Audience,
    decimal PercentageOfKnown);

public sealed record SponsorAudienceDimensionDto(
    string Status,
    long? KnownAudience,
    long? UnknownAudience,
    decimal? CoveragePercent,
    int SuppressedSegmentCount,
    IReadOnlyList<SponsorAudienceSegmentDto> Segments);

public sealed record SponsorAudienceReportResponse(
    int SchemaVersion,
    string Status,
    int PeriodDays,
    SponsorAnalyticsWindowDto Window,
    SponsorAnalyticsPrivacyDto Privacy,
    long? EstimatedUniqueAudience,
    SponsorAudienceDimensionDto Country,
    SponsorAudienceDimensionDto Age);
