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

    /// <summary>What Riot's published map list adds to the built-in table (new maps appear here without a release). Replaced as a whole, so readers never see a half-built list.</summary>
    private static volatile IReadOnlyDictionary<string, (string Name, bool Competitive)> _learned =
        new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

    private static volatile bool _unknownSeen;

    /// <summary>True when a Riot map path could not be named since the list was last refreshed, so the refresher knows a retry is worthwhile.</summary>
    public static bool UnknownSeen => _unknownSeen;

    /// <summary>Teaches the table the maps in Riot's published list. Built-in names always win; an empty list changes nothing.</summary>
    public static void Learn(IEnumerable<(string Code, string Name, bool Competitive)> maps)
    {
        var next = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, name, competitive) in maps)
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(name)) next[code.Trim()] = (name.Trim(), competitive);
        if (next.Count == 0) return;
        _learned = next;
        _unknownSeen = false;
    }

    /// <summary>Forgets everything learned (tests, and a clean start).</summary>
    public static void ForgetLearned()
    {
        _learned = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        _unknownSeen = false;
    }

    /// <summary>Display names of the maps in the competitive pool that this project can identify, built-in first, then learned ones in name order.</summary>
    public static IReadOnlyList<string> CompetitiveNames =>
        Entries.Where(e => e.Competitive).Select(e => e.Name)
            .Concat(_learned.Values.Where(v => v.Competitive && !Names.Contains(v.Name)).Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal))
            .ToArray();

    /// <summary>The player-facing name for a Riot map path or bare code ("/Game/Maps/Triad/Triad", "Triad"); null when not known.</summary>
    public static string? DisplayName(string? riotMapIdOrCode)
    {
        var code = riotMapIdOrCode?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (code is null) return null;
        if (NameByCode.TryGetValue(code, out var name)) return name;
        return _learned.TryGetValue(code, out var learned) ? learned.Name : null;
    }

    /// <summary>True when the text is already a known display name ("Haven"), not a code.</summary>
    public static bool IsDisplayName(string? text) =>
        !string.IsNullOrWhiteSpace(text) && (Names.Contains(text.Trim()) || _learned.Values.Any(v => string.Equals(v.Name, text.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>A name that is safe to show: the display name for a Riot path or code; free text (a typed or catalogue name) unchanged; an unrecognised Riot path becomes "Unknown map". Empty stays empty.</summary>
    public static string Resolve(string? riotMapIdOrCode)
    {
        if (string.IsNullOrWhiteSpace(riotMapIdOrCode)) return string.Empty;
        if (DisplayName(riotMapIdOrCode) is { } known) return known;
        if (!riotMapIdOrCode.Contains('/')) return riotMapIdOrCode.Trim();
        _unknownSeen = true;
        return Unknown;
    }
}
