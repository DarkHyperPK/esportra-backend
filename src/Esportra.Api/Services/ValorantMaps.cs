namespace Esportra.Api.Services;

/// <summary>
/// The one table of VALORANT maps: Riot's internal code names (the last segment of /Game/Maps/Triad/Triad) and what players call them.
/// Everything that shows or compares a map name goes through here, so an internal name never reaches a screen.
/// </summary>
public static class ValorantMaps
{
    public const string Unknown = "Unknown map";

    private sealed record Entry(string Code, string Name, bool Competitive);

    private static readonly Entry[] Entries =
    [
        new("Ascent", "Ascent", true), new("Duality", "Bind", true), new("Triad", "Haven", true), new("Bonsai", "Split", true),
        new("Port", "Icebox", true), new("Foxtrot", "Breeze", true), new("Canyon", "Fracture", true), new("Pitt", "Pearl", true),
        new("Jam", "Lotus", true), new("Juliett", "Sunset", true), new("Infinity", "Abyss", true), new("Rook", "Corrode", true),
        new("HURM_Alley", "District", false), new("HURM_Bowl", "Kasbah", false), new("HURM_Yard", "Piazza", false),
    ];

    private static readonly Dictionary<string, string> NameByCode =
        Entries.ToDictionary(e => e.Code, e => e.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Names = Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Display names of the maps in the competitive pool that this project can identify, in a stable order.</summary>
    public static IReadOnlyList<string> CompetitiveNames { get; } = Entries.Where(e => e.Competitive).Select(e => e.Name).ToArray();

    /// <summary>The player-facing name for a Riot map path or bare code ("/Game/Maps/Triad/Triad", "Triad"); null when not known.</summary>
    public static string? DisplayName(string? riotMapIdOrCode)
    {
        var code = riotMapIdOrCode?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return code is not null && NameByCode.TryGetValue(code, out var name) ? name : null;
    }

    /// <summary>True when the text is already a known display name ("Haven"), not a code.</summary>
    public static bool IsDisplayName(string? text) => !string.IsNullOrWhiteSpace(text) && Names.Contains(text.Trim());

    /// <summary>A name that is safe to show: the display name for a Riot path or code; free text (a typed or catalogue name) unchanged; an unrecognised Riot path becomes "Unknown map". Empty stays empty.</summary>
    public static string Resolve(string? riotMapIdOrCode)
    {
        if (string.IsNullOrWhiteSpace(riotMapIdOrCode)) return string.Empty;
        return DisplayName(riotMapIdOrCode) ?? (riotMapIdOrCode.Contains('/') ? Unknown : riotMapIdOrCode.Trim());
    }
}
