namespace Esportra.Api.Auth;

/// <summary>
/// Defines the required authorization level for a route.
/// </summary>
public enum AuthLevel
{
    /// <summary>No auth required (health, public reads, webhooks).</summary>
    Public,

    /// <summary>Valid JWT required, no specific role.</summary>
    Authenticated,

    /// <summary>Authenticated + specific platform role in user_roles (optionally verified).</summary>
    VerifiedRole,

    /// <summary>Any admin role in admin_user_roles.</summary>
    AdminAny,

    /// <summary>Specific admin permission(s) from admin_role_permissions.</summary>
    AdminPermission,
}

/// <summary>
/// A single authorization rule mapping a route pattern to its required access level.
/// </summary>
public sealed record RouteAuthRule
{
    /// <summary>HTTP method: "GET", "POST", "PUT", "DELETE", "PATCH", or "*" for any.</summary>
    public required string Method { get; init; }

    /// <summary>
    /// Route pattern using minimal wildcard syntax:
    ///   /api/exact/path      — exact match
    ///   /api/prefix/{*}      — prefix match (any path after prefix)
    ///   /api/resource/{id}   — matches any single segment for {id}
    /// </summary>
    public required string Pattern { get; init; }

    /// <summary>Required authorization level.</summary>
    public required AuthLevel Level { get; init; }

    /// <summary>For VerifiedRole: which platform role is required (e.g. "organizer").</summary>
    public string? RequiredRole { get; init; }

    /// <summary>
    /// For AdminPermission: user needs at least ONE of these permissions.
    /// SuperAdmin bypasses this check.
    /// </summary>
    public string[]? RequiredPermissions { get; init; }

    /// <summary>For VerifiedRole: must also have approved entry in verified_roles.</summary>
    public bool RequiresVerification { get; init; }

    /// <summary>For VerifiedRole: must also own an organization.</summary>
    public bool RequiresOrganization { get; init; }
}
