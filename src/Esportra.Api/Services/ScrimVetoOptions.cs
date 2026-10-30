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
}
