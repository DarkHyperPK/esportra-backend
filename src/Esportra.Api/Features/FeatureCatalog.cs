namespace Esportra.Api.Features;

/// <summary>
/// Curated registry of platform-wide features that can be toggled from the Admin Centre.
/// Each entry defines its off-behavior contract: hide entry points, block APIs (409),
/// preserve all data. Re-enabling restores everything — disabling never deletes.
/// </summary>
public static class FeatureCatalog
{
    public sealed record FeatureMeta(
        string Key,
        string Name,
        string Description,
        string Category,
        string[] NavHrefs);

    public static readonly FeatureMeta BattleRoyale = new(
        "battle-royale",
        "Battle Royale Mode",
        "BR lobbies, join flow, evidence and results. Off hides BR surfaces and blocks /api/br routes. Existing BR data is preserved.",
        "Tournaments",
        ["/admin/content/tournaments"]);

    public static readonly FeatureMeta TournamentRegistration = new(
        "tournament-registration",
        "Tournament Registration",
        "New registrations for tournaments. Off hides register buttons and blocks the registration endpoint. Existing participants are unaffected.",
        "Tournaments",
        ["/admin/content/tournaments"]);

    public static readonly FeatureMeta TeamInvites = new(
        "team-invites",
        "Team Invites",
        "Creating and accepting team invites. Off blocks invite creation/redemption; teams, rosters and members stay fully manageable.",
        "Community",
        ["/admin/content/teams"]);

    public static readonly FeatureMeta WalletPos = new(
        "wallet-pos",
        "Wallet & POS",
        "Venue wallets, top-ups and POS orders. Off hides venue commerce surfaces and blocks wallet/POS endpoints. Balances and orders are preserved.",
        "Commerce",
        []);

    public static readonly FeatureMeta SponsorAds = new(
        "sponsor-ads",
        "Sponsor Ads & Ticker",
        "Homepage ticker, partner showcase placements and impression/click beacons. Off stops rendering and tracking; sponsor records and stats stay intact.",
        "Partners",
        ["/admin/partners/sponsors"]);

    public static readonly FeatureMeta Broadcasts = new(
        "broadcasts",
        "Broadcasts & Notifications",
        "User broadcast inbox and broadcast sending. Off pauses delivery and hides the inbox; drafts, templates and history are preserved.",
        "Operations",
        ["/admin/operations/broadcasts"]);

    public static readonly FeatureMeta GhostMode = new(
        "ghost-mode",
        "Ghost Mode",
        "Admin impersonation sessions. Off blocks starting new ghost sessions and approval requests. Audit trails remain queryable while disabled.",
        "Security",
        ["/admin/security/ghost"]);

    public static readonly IReadOnlyList<FeatureMeta> All =
    [
        BattleRoyale,
        TournamentRegistration,
        TeamInvites,
        WalletPos,
        SponsorAds,
        Broadcasts,
        GhostMode,
    ];

    public static FeatureMeta? Find(string key) =>
        All.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
