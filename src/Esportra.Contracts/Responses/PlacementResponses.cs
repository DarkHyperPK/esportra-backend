namespace Esportra.Contracts.Responses;

public sealed record PlacementDto
{
    public Guid Id { get; init; }
    public Guid SponsorId { get; init; }
    public Guid? TournamentId { get; init; }
    public string PlacementZone { get; init; } = "";
    public int? SlotNumber { get; init; }
    public string? BannerUrl { get; init; }
    public Guid? BannerAssetId { get; init; }
    public string? LogoUrl { get; init; }
    public Guid? LogoAssetId { get; init; }
    public string? Headline { get; init; }
    public string? CtaText { get; init; }
    public string? CtaUrl { get; init; }
    public int Priority { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string Lifecycle { get; init; } = "";
    public string? ReviewReason { get; init; }
    public string SponsorName { get; init; } = "";
    public string? SponsorTier { get; init; }
    public string? SponsorLogoUrl { get; init; }
    public string? SponsorWebsiteUrl { get; init; }
    public string? TournamentName { get; init; }
}

public sealed record PlacementPageDto(
    IReadOnlyList<PlacementDto> Items,
    int Page,
    int PageSize,
    long Total,
    IReadOnlyDictionary<string, int> StatusCounts);

public sealed record TournamentSponsorLinkDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("sponsor_id")] string SponsorId,
    [property: System.Text.Json.Serialization.JsonPropertyName("sponsor_type")] string SponsorType,
    [property: System.Text.Json.Serialization.JsonPropertyName("placement_zones")] IReadOnlyList<string> PlacementZones,
    [property: System.Text.Json.Serialization.JsonPropertyName("media_overrides")] Dictionary<string, string>? MediaOverrides,
    [property: System.Text.Json.Serialization.JsonPropertyName("priority")] int Priority,
    [property: System.Text.Json.Serialization.JsonPropertyName("slot_number")] int SlotNumber,
    [property: System.Text.Json.Serialization.JsonPropertyName("headline")] string? Headline,
    [property: System.Text.Json.Serialization.JsonPropertyName("cta_text")] string? CtaText,
    [property: System.Text.Json.Serialization.JsonPropertyName("cta_url")] string? CtaUrl,
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
    int SlotNumber,
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
