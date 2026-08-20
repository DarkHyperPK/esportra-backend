namespace Esportra.Contracts.Requests;

public sealed record CreatePlacementRequest(
    Guid SponsorId,
    Guid? TournamentId,
    string PlacementZone,
    int SlotNumber,
    string? BannerUrl = null,
    Guid? BannerAssetId = null,
    string? LogoUrl = null,
    Guid? LogoAssetId = null,
    string? Headline = null,
    string? Description = null,
    string? CtaText = null,
    string? CtaUrl = null,
    int Priority = 0,
    bool IsActive = true,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null);

public sealed record UpdatePlacementRequest(
    string? BannerUrl,
    Guid? BannerAssetId,
    string? LogoUrl,
    Guid? LogoAssetId,
    string? Headline,
    string? Description,
    string? CtaText,
    string? CtaUrl,
    int? Priority,
    bool? IsActive,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt);

public sealed record ResolvePlacementReviewRequest(
    Guid? TournamentId,
    string PlacementZone,
    int SlotNumber);

public sealed record ReplaceCreativeRequest(Guid AssetId);

public sealed record UpdatePlacementMetadataRequest(
    string? Headline,
    string? CtaText,
    string? CtaUrl,
    int? Priority,
    bool? IsActive,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt);
