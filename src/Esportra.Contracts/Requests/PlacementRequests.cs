namespace Esportra.Contracts.Requests;

public sealed record CreatePlacementRequest(
    Guid SponsorId,
    Guid? TournamentId,
    string PlacementZone,
    string? BannerUrl = null,
    string? LogoUrl = null,
    string? Headline = null,
    string? CtaText = null,
    string? CtaUrl = null,
    int Priority = 0,
    bool IsActive = true,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null);

public sealed record UpdatePlacementRequest(
    string? BannerUrl,
    string? LogoUrl,
    string? Headline,
    string? CtaText,
    string? CtaUrl,
    int? Priority,
    bool? IsActive,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt);
