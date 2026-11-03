namespace Esportra.Api.Services;

/// <summary>
/// Configuration for the scrim veto integration.
/// Bind from appsettings section "Scrims".
/// Changing the VetoEnabledGames list does NOT require a code change — hot-reloadable.
/// </summary>
public sealed class ScrimVetoOptions
{
    public const string SectionName = "Scrims";

    /// <summary>
    /// Game slugs for which a veto session is automatically created when both teams check in.
    /// Default: valorant, cs2.
    /// </summary>
    public string[] VetoEnabledGames { get; set; } = ["valorant", "cs2"];

    /// <summary>
    /// Minutes a veto may sit with no action (toss, ban, pick, side, host reset) before the scrim is cancelled as abandoned.
    /// Every action restarts the clock, so a long Bo3/Bo5 veto is never cancelled while teams are still acting.
    /// </summary>
    public int VetoInactivityMinutes { get; set; } = 20;
}
