namespace Esportra.Contracts.Responses;

public sealed record PlacementDto(
    Guid Id,
    Guid SponsorId,
    Guid? TournamentId,
    string PlacementZone,
    string? BannerUrl,
    string? LogoUrl,
    string? Headline,
    string? CtaText,
    string? CtaUrl,
    int Priority,
    bool IsActive,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset CreatedAt,
    string SponsorName,
    string? SponsorTier,
    string? SponsorLogoUrl,
    string? SponsorWebsiteUrl,
    string? TournamentName);

public sealed record TournamentSponsorLinkDto(
    string Id,
    string SponsorId,
    string SponsorType,
    IReadOnlyList<string> PlacementZones,
    Dictionary<string, string>? MediaOverrides,
    int Priority,
    TournamentSponsorDto Sponsor);

public sealed record TournamentSponsorDto(
    string Id,
    string Name,
    string? Tagline,
    string? LogoUrl,
    string? BannerImageUrl,
    string AccentColor,
    string Tier,
    string? CtaText,
    string? WebsiteUrl,
    IReadOnlyList<string>? GalleryImages);

public sealed record GlobalPlacementDto(
    Guid Id,
    Guid SponsorId,
    string PlacementZone,
    string? BannerUrl,
    string? LogoUrl,
    string? Headline,
    string? CtaText,
    string? CtaUrl,
    int Priority,
    string SponsorName,
    string? SponsorTier,
    string? SponsorLogoUrl,
    string? SponsorBannerUrl,
    string? SponsorWebsiteUrl);
