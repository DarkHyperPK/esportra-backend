namespace Esportra.Core.Tournaments;

public enum SponsorCreativeRole { Banner, Logo }

public sealed record PlacementZonePolicy(
    string Zone,
    bool IsGlobal,
    int Capacity,
    SponsorCreativeRole RequiredRole,
    int MinimumWidth,
    int MinimumHeight,
    double MinimumAspectRatio,
    double MaximumAspectRatio,
    IReadOnlySet<string> AllowedTiers);

public static class SponsorPlacementPolicy
{
    private static readonly IReadOnlyDictionary<string, PlacementZonePolicy> Policies =
        new Dictionary<string, PlacementZonePolicy>(StringComparer.Ordinal)
        {
            ["homepage_ticker"] = Create("homepage_ticker", true, 10, SponsorCreativeRole.Logo, 240, 80, 0.5, 6, "partner", "standard", "diamond", "ascendant", "radiant"),
            ["partner_showcase"] = Create("partner_showcase", true, 6, SponsorCreativeRole.Banner, 1200, 600, 1.5, 2, "radiant"),
            ["sidebar_partner"] = Create("sidebar_partner", false, 2, SponsorCreativeRole.Banner, 300, 400, 0.3, 0.85, "ascendant", "radiant"),
            ["wide_partner"] = Create("wide_partner", false, 4, SponsorCreativeRole.Banner, 600, 250, 1.5, 3.0, "ascendant", "radiant"),
            ["card_badge"] = Create("card_badge", false, 1, SponsorCreativeRole.Logo, 160, 48, 0.5, 6, "ascendant", "radiant"),
            ["partner_logo"] = Create("partner_logo", false, 4, SponsorCreativeRole.Logo, 240, 80, 0.5, 6, "radiant"),
        };

    public static IReadOnlyCollection<PlacementZonePolicy> All => Policies.Values.ToArray();
    public static bool TryGet(string zone, out PlacementZonePolicy policy) => Policies.TryGetValue(zone, out policy!);
    public static bool IsScopeValid(PlacementZonePolicy policy, Guid? tournamentId) => policy.IsGlobal ? tournamentId is null : tournamentId.HasValue;
    public static bool IsSlotValid(PlacementZonePolicy policy, int slotNumber) => slotNumber >= 1 && slotNumber <= policy.Capacity;
    public static bool IsTierAllowed(PlacementZonePolicy policy, string? tier) => tier is not null && policy.AllowedTiers.Contains(tier);
    public static bool IsAspectRatioValid(PlacementZonePolicy policy, int width, int height)
    {
        if (width < policy.MinimumWidth || height < policy.MinimumHeight || height <= 0) return false;
        var ratio = width / (double)height;
        return ratio >= policy.MinimumAspectRatio && ratio <= policy.MaximumAspectRatio;
    }

    private static PlacementZonePolicy Create(string zone, bool isGlobal, int capacity, SponsorCreativeRole role, int width, int height, double minRatio, double maxRatio, params string[] tiers) =>
        new(zone, isGlobal, capacity, role, width, height, minRatio, maxRatio, tiers.ToHashSet(StringComparer.OrdinalIgnoreCase));
}
