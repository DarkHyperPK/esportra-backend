namespace Esportra.Core.Tournaments;

public static class RosterPoolModeRules
{
    public static bool UsesRosterPoolSelection(
        string participantMode,
        string modeKey,
        string? modeGroup = null,
        string? mapPoolFilter = null) =>
        string.Equals(participantMode, "team", StringComparison.OrdinalIgnoreCase)
        && (
            string.Equals(modeGroup, "Skirmish", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mapPoolFilter, "skirmish", StringComparison.OrdinalIgnoreCase)
            || modeKey.Contains("skirmish", StringComparison.OrdinalIgnoreCase));

    public static bool RosterMatchesPoolSource(string poolModeKey, string? rosterFormat, IReadOnlyList<string>? poolAliases = null)
    {
        if (string.IsNullOrWhiteSpace(rosterFormat)) return true;

        if (string.Equals(poolModeKey, rosterFormat, StringComparison.OrdinalIgnoreCase))
            return true;

        return poolAliases?.Any(alias => string.Equals(alias, rosterFormat, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
