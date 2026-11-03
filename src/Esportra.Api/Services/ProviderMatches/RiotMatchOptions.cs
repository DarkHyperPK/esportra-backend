namespace Esportra.Api.Services.ProviderMatches;

/// <summary>Riot match handling switches. Off by default; staging turns it on with Riot__AllowNonCustomMatches=true.</summary>
public sealed class RiotMatchOptions
{
    public const string SectionName = "Riot";

    /// <summary>
    /// Lets ranked, unrated and other matchmade games be attached to scrim maps and verified. Such a game
    /// can only be partially verified: the opposing scrim team is not in it, so only the reporting side and the score are checked.
    /// </summary>
    public bool AllowNonCustomMatches { get; set; }
}
