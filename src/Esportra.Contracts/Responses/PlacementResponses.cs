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
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("sponsor_id")] string SponsorId,
    [property: System.Text.Json.Serialization.JsonPropertyName("sponsor_type")] string SponsorType,
    [property: System.Text.Json.Serialization.JsonPropertyName("placement_zones")] IReadOnlyList<string> PlacementZones,
    [property: System.Text.Json.Serialization.JsonPropertyName("media_overrides")] Dictionary<string, string>? MediaOverrides,
    [property: System.Text.Json.Serialization.JsonPropertyName("priority")] int Priority,
    [property: System.Text.Json.Serialization.JsonPropertyName("sponsor")] TournamentSponsorDto Sponsor);

public sealed record TournamentSponsorDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("tagline")] string? Tagline,
    [property: System.Text.Json.Serialization.JsonPropertyName("logo_url")] string? LogoUrl,
    [property: System.Text.Json.Serialization.JsonPropertyName("banner_image_url")] string? BannerImageUrl,
    [property: System.Text.Json.Serialization.JsonPropertyName("accent_color")] string AccentColor,
    [property: System.Text.Json.Serialization.JsonPropertyName("tier")] string Tier,
    [property: System.Text.Json.Serialization.JsonPropertyName("cta_text")] string? CtaText,
    [property: System.Text.Json.Serialization.JsonPropertyName("website_url")] string? WebsiteUrl,
    [property: System.Text.Json.Serialization.JsonPropertyName("gallery_images")] IReadOnlyList<string>? GalleryImages);

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
