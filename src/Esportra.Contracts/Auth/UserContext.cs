namespace Esportra.Contracts.Auth;

/// <summary>
/// Enriched user context — populated by RoleEnrichmentMiddleware from the Supabase JWT
/// and cached in Redis under user-ctx:{userId}.
/// </summary>
public sealed record UserContext
{
    public string UserId       { get; init; } = string.Empty;
    public string Email        { get; init; } = string.Empty;
    public string[] Roles      { get; init; } = [];   // e.g. ["casual","organizer"]
    public string[] AdminRoles { get; init; } = [];   // e.g. ["ops_admin","moderator"]
    public string[] Permissions { get; init; } = [];  // e.g. ["users:ban","disputes:resolve"]
}
