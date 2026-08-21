namespace Esportra.Core.Tournaments;

// Reward types. "cash" is platform-tracked; all others are organizer-managed.
public static class RewardType
{
    public const string Cash = "cash";
    public const string InGameCurrency = "in_game_currency";
    public const string PhysicalProduct = "physical_product";
    public const string DigitalProduct = "digital_product";
    public const string Trophy = "trophy";
    public const string Other = "other";

    public static bool IsOrganizerManaged(string type) =>
        !string.Equals(type, Cash, StringComparison.OrdinalIgnoreCase);
}

// Fulfillment responsibility is always "organizer" for non-cash rewards.
// The platform is not responsible for delivery, availability, or disputes
// relating to organizer-managed rewards.
public sealed record PrizeReward(
    string Type,                       // RewardType constant
    string Title,                      // e.g. "Gaming PC", "5000 V-Bucks"
    string? Description = null,       // optional extra detail
    decimal? EstimatedValue = null,    // optional USD estimate for display
    int Quantity = 1,
    string? FulfillmentNotes = null);  // e.g. "DM organizer on Discord to claim"

public static class PrizeRewardExtensions
{
    public static bool IsOrganizerManaged(this PrizeReward r) =>
        RewardType.IsOrganizerManaged(r.Type);
}

public sealed record PrizeDistributionConfig(
    string Mode,
    List<PrizeDistributionEntry> Placements,
    // Shown publicly. Defaults to the platform disclaimer when organizer-managed rewards exist.
    string? Disclaimer = null);

public sealed record PrizeDistributionEntry(
    int Position,
    string Label,
    decimal Percentage,
    int SharedCount = 1,
    List<PrizeReward>? Rewards = null);

public sealed record ResolvedPlacement(
    Guid TeamId,
    string TeamName,
    int Placement,
    string PlacementLabel,
    decimal PrizeAmount,
    List<PrizeReward> Rewards,
    bool IsTied);

public sealed record PrizeDistributionValidation(
    bool IsValid,
    string? Error,
    decimal TotalPercentage);

public sealed record PrizeDistributionTemplate(
    string Name,
    string Description,
    PrizeDistributionConfig Config);

// Fulfillment record surfaced in the reward-distributions API.
public sealed record RewardDistributionStatus(
    Guid TeamId,
    string TeamName,
    int Placement,
    string PlacementLabel,
    int RewardIndex,
    string RewardTitle,
    string RewardType,
    string Status,           // pending | distributed | claimed | cancelled
    string? Notes,
    Guid? DistributedBy,
    DateTime? DistributedAt);

public static class PlatformDisclaimer
{
    public const string OrganizerManagedRewards =
        "Non-monetary prizes (physical products, in-game currencies, digital goods, trophies, etc.) " +
        "are awarded and distributed directly by the tournament organizer. " +
        "The platform is not responsible for the fulfillment, delivery, availability, or any disputes " +
        "relating to organizer-managed rewards.";
}
