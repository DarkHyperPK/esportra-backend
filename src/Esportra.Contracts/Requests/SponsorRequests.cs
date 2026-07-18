using System.Text.Json.Serialization;

namespace Esportra.Contracts.Requests;

public sealed record SponsorUpdateRequest(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("tagline")] string? Tagline = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("website_url")] string? WebsiteUrl = null,
    [property: JsonPropertyName("cta_text")] string? CtaText = null,
    [property: JsonPropertyName("discount_text")] string? DiscountText = null,
    [property: JsonPropertyName("logo_url")] string? LogoUrl = null,
    [property: JsonPropertyName("banner_image_url")] string? BannerImageUrl = null,
    [property: JsonPropertyName("gallery_images")] string[]? GalleryImages = null,
    [property: JsonPropertyName("detail_deck_url")] string? DetailDeckUrl = null);

public sealed record OnboardingStepRequest(
    string StepName,
    Dictionary<string, object?> StepData,
    int NextStep);

public sealed record OnboardingCompleteRequest(bool AcceptLegalTerms, string TermsVersion);
