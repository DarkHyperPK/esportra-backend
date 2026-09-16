namespace Esportra.Contracts.Auth;

/// <summary>
/// Scope constants for developer API key access control.
/// </summary>
public static class ApiKeyScopes
{
    public const string TournamentsRead = "tournaments:read";
    public const string TournamentsWrite = "tournaments:write";
    public const string BracketsRead = "brackets:read";
    public const string BracketsWrite = "brackets:write";
    public const string MatchesRead = "matches:read";
    public const string MatchesWrite = "matches:write";
    public const string VetoRead = "veto:read";
    public const string VetoWrite = "veto:write";

    public static bool HasScope(string[] granted, string required)
        => granted.Contains(required, StringComparer.OrdinalIgnoreCase);
}
