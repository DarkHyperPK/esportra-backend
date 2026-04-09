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

    /// <summary>
    /// Authenticator Assurance Level from the Supabase JWT.
    /// "aal1" = password only, "aal2" = password + TOTP verified.
    /// </summary>
    public string Aal { get; init; } = "aal1";

    /// <summary>
    /// Whether this user's admin role(s) require MFA per the 2FA enforcement settings.
    /// When true and Aal != "aal2", admin endpoints will deny access with a specific error.
    /// </summary>
    public bool MfaRequired { get; init; } = false;

    /// <summary>
    /// UserId parsed as Guid — use this when passing to Dapper so Npgsql
    /// sends the parameter as uuid type (not text), avoiding "operator does not exist: uuid = text".
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid UserIdGuid => Guid.TryParse(UserId, out var g) ? g : Guid.Empty;
}
