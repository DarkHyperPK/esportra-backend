namespace Esportra.Core.Tournaments;

/// <summary>
/// Maps stored team/participant rows to catalog-friendly display metadata.
/// </summary>
public static class ParticipantEntryMetadata
{
    public static string ResolveTeamKind(bool? isMock, string? teamKind, bool? isSolo, string? tag)
    {
        if (isMock == true) return "mock";
        if (teamKind is "team" or "solo" or "mock") return teamKind;
        if (isSolo == true) return "solo";
        var normalizedTag = tag ?? string.Empty;
        if (normalizedTag.StartsWith("mock-", StringComparison.OrdinalIgnoreCase)) return "mock";
        if (normalizedTag.StartsWith("solo-", StringComparison.OrdinalIgnoreCase)) return "solo";
        return "team";
    }

    public static string ResolveEntryKind(bool? isMock, string teamKind, string? participantType)
    {
        if (isMock == true || teamKind == "mock") return "mock";
        if (teamKind == "solo" || string.Equals(participantType, "solo", StringComparison.OrdinalIgnoreCase))
            return "solo_player";
        return "real_team";
    }

    public static string? ResolveDisplayName(
        string entryKind,
        string? teamName,
        string? soloUsername,
        string? soloFullName)
    {
        if (entryKind == "solo_player")
            return soloUsername ?? soloFullName ?? teamName;
        return teamName;
    }

    public static string? ResolveDisplayLogoUrl(
        string entryKind,
        string? teamLogoUrl,
        string? soloAvatarUrl)
    {
        if (entryKind == "solo_player")
            return soloAvatarUrl ?? teamLogoUrl;
        return teamLogoUrl;
    }
}
