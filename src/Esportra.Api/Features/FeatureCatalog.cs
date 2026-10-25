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

    public static readonly FeatureMeta NavV2 = new(
        "nav_v2",
        "Sidebar Navigation",
        "New persistent sidebar navigation shell (nav_v2). Replaces the top bar on desktop. Off falls back to the legacy top bar. Default on.",
        "Platform",
        []);

    public static readonly FeatureMeta CompetitiveProfiles = new(
        "competitive_profiles",
        "Competitive Profiles",
        "Per-game competitive identity: rank, roles, stats, bio. Off hides profile setup and blocks /api/competitive/profile routes.",
        "Competitive",
        []);

    public static readonly FeatureMeta Lfg = new(
        "lfg",
        "Looking for Group",
        "LFG listing board — post and browse looking-for-group cards by game. Off hides the board and blocks /api/competitive/lfg routes.",
        "Competitive",
        []);

    public static readonly FeatureMeta Recruitment = new(
        "recruitment",
        "Team Recruitment",
        "Team recruitment openings and applications. Off hides recruitment surfaces and blocks /api/recruitment routes.",
        "Competitive",
        []);

    public static readonly FeatureMeta Tryouts = new(
        "tryouts",
        "Team Tryouts",
        "Structured tryout sessions with invitations and evaluations. Off blocks /api/competitive/tryouts routes.",
        "Competitive",
        []);

    public static readonly FeatureMeta Scrims = new(
        "scrims",
        "Scrim Finder",
        "Team-vs-team scrim listings and match scheduling. Off hides the scrim board and blocks /api/competitive/scrims routes.",
        "Competitive",
        []);

    public static readonly FeatureMeta Scouting = new(
        "scouting",
        "Scouting & Shortlists",
        "Player scouting shortlists and notes for team managers. Off blocks /api/competitive/scouting routes.",
        "Competitive",
        []);

    public static readonly IReadOnlyList<FeatureMeta> All =
    [
        NavV2,
        BattleRoyale,
        TournamentRegistration,
        TeamInvites,
        WalletPos,
        SponsorAds,
        Broadcasts,
        GhostMode,
        CompetitiveProfiles,
        Lfg,
        Recruitment,
        Tryouts,
        Scrims,
        Scouting,
    ];

    public static FeatureMeta? Find(string key) =>
        All.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
