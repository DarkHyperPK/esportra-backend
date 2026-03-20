namespace Esportra.Contracts.Responses;

public sealed record SponsorProfileResponse(
    SponsorDto        Sponsor,
    SponsorAccountDto Account,
    SponsorStatsDto   Stats,
    List<DailyStatDto> History);

public sealed record SponsorDto(
    string    Id,
    string    Name,
    string?   Tagline,
    string?   Description,
    string    WebsiteUrl,
    string?   LogoUrl,
    string?   BannerImageUrl,
    string    AccentColor,
    string    Tier,
    string[]  Placement,
    string    CtaText,
    string?   DiscountText,
    bool      IsActive,
    int       Priority,
    string[]  GalleryImages,
    string    CreatedAt);

public sealed record SponsorAccountDto(
    string  SponsorId,
    string  Role,
    object? OnboardingMeta);

public sealed record SponsorStatsDto(
    long   Impressions,
    long   UniqueImpressions,
    long   Clicks,
    double Ctr);

public sealed record DailyStatDto(
    string StatDate,
    long   Impressions,
    long   UniqueImpressions,
    long   Clicks);

public sealed record SponsorDemographicsResponse(
    List<CountryStatDto>  Countries,
    List<AgeGroupStatDto> AgeGroups);

public sealed record CountryStatDto(string Name, int Count);
public sealed record AgeGroupStatDto(string Group, int Count);

public sealed record OnboardingResponse(
    string  SponsorId,
    object? Meta);

public sealed record BrandingResponse(
    string LogoUrl,
    string IconUrl);

public sealed record SponsorListItemDto(
    string    Id,
    string    Name,
    string?   Tagline,
    string?   Description,
    string    WebsiteUrl,
    string?   LogoUrl,
    string?   BannerImageUrl,
    string    AccentColor,
    string    Tier,
    string[]  Placement,
    string    CtaText,
    string?   DiscountText,
    bool      IsActive,
    int       Priority,
    string[]  GalleryImages,
    string?   StartDate,
    string?   EndDate,
    string    CreatedAt);

public sealed record SponsorStatsSummaryDto(
    long   Impressions,
    long   Clicks,
    string Ctr);
