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
