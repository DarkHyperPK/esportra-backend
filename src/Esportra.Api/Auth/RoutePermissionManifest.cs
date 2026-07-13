using Esportra.Contracts.Auth;

namespace Esportra.Api.Auth;

/// <summary>
/// Declarative route-to-permission map. The single source of truth for
/// which routes require which authorization level.
///
/// Rules are evaluated in order — first match wins.
/// More specific patterns must come before broader wildcard patterns.
/// </summary>
public static class RoutePermissionManifest
{
    private static readonly RouteAuthRule[] _rules =
    [
        // ══════════════════════════════════════════════════════════════════════
        // PUBLIC — no auth required
        // ══════════════════════════════════════════════════════════════════════

        // Health / probes
        P("GET",  "/health"),
        P("GET",  "/health/live"),
        P("GET",  "/health/ready"),

        // Auth recovery initiation and legacy compatibility tombstone
        P("POST", "/api/auth/recovery"),
        P("POST", "/api/auth/set-password"),

        // Public reads — tournaments
        P("GET",  "/api/tournaments"),
        P("GET",  "/api/tournaments/{id}"),
        P("GET",  "/api/tournaments/{id}/stages"),
        P("GET",  "/api/tournaments/{id}/stages/{*}"),
        P("GET",  "/api/tournaments/{id}/brackets/{*}"),
        P("GET",  "/api/tournaments/{id}/participants"),
        P("GET",  "/api/tournaments/{id}/participants/{*}"),
        P("GET",  "/api/tournaments/{id}/disputes/{*}"),
        P("GET",  "/api/tournaments/filters"),
        P("GET",  "/api/tournaments/slug/{*}"),

        // Public reads — teams, profiles
        P("GET",  "/api/teams"),
        P("GET",  "/api/teams/{id}"),
        P("GET",  "/api/teams/batch"),
        P("GET",  "/api/profiles/{id}"),
        P("GET",  "/api/profiles/search"),
        P("GET",  "/api/profiles/resolve-players"),

        // Public tools
        P("*",    "/api/veto/public/{*}"),
        P("*",    "/api/veto/token/{*}"),
        P("GET",  "/api/tools/{*}"),
        P("POST", "/api/tools/{*}"),

        // Public bracket share
        P("GET",  "/api/tools/brackets/share/{*}"),

        // Anonymous analytics + metrics
        P("POST", "/api/analytics/events"),
        P("POST", "/api/metrics"),
        P("GET",  "/api/metrics/{*}"),

        // MatchZy M2M endpoints (secret-based, not JWT)
        P("GET",  "/api/matchzy/{*}"),
        P("POST", "/api/matchzy/{*}"),

        // Public game catalog reads
        P("GET",  "/api/games/{*}"),

        // Organizations public reads
        P("GET",  "/api/organizations/{id}"),
        P("GET",  "/api/organizations/{id}/tournaments"),
        P("GET",  "/api/organizations/{id}/media"),
        P("GET",  "/api/organizations/{id}/media/{*}"),
        P("GET",  "/api/organizations/{id}/albums"),
        P("GET",  "/api/organizations/{id}/albums/{*}"),

        // Venue public reads
        P("GET",  "/api/venues"),
        P("GET",  "/api/venues/{id}"),
        P("GET",  "/api/venues/{id}/{*}"),

        // Integrations callbacks (OAuth)
        P("GET",  "/api/integrations/{*}"),

        // ══════════════════════════════════════════════════════════════════════
        // AUTHENTICATED — valid JWT required, no specific role
        // ══════════════════════════════════════════════════════════════════════

        // User profile management
        A("POST", "/api/auth/password-reset-completed"),
        A("GET",  "/api/profiles/me"),
        A("PUT",  "/api/profiles/{*}"),
        A("GET",  "/api/me/roles"),

        // Notifications
        A("GET",  "/api/notifications"),
        A("GET",  "/api/notifications/{*}"),
        A("PUT",  "/api/notifications/{*}"),
        A("POST", "/api/notifications/{*}"),

        // Teams (CRUD for authenticated users)
        A("POST", "/api/teams"),
        A("POST", "/api/teams/{*}"),
        A("PUT",  "/api/teams/{*}"),
        A("DELETE","/api/teams/{*}"),

        // Tournament registration / interaction
        A("POST", "/api/tournaments/{id}/register"),
        A("POST", "/api/tournaments/{id}/upload-receipt"),
        A("POST", "/api/tournaments/{id}/disputes/{*}"),
        A("PUT",  "/api/tournaments/{id}/disputes/{*}"),

        // Matches / bracket interaction
        A("*",    "/api/matches/{*}"),
        A("*",    "/api/brackets/{*}"),

        // Storage (upload, delete)
        A("*",    "/api/storage/{*}"),

        // Veto (authenticated actions)
        A("*",    "/api/veto/{*}"),

        // Integrations (link accounts)
        A("POST", "/api/integrations/{*}"),
        A("DELETE","/api/integrations/{*}"),

        // Staff PIN login (specific venue auth)
        A("POST", "/api/auth/staff-pin-login"),

        // Verification requests
        A("POST", "/api/profiles/verification-request"),

        // Invitations
        A("*",    "/api/invitations/{*}"),
        A("*",    "/api/tournaments/{id}/invitations/{*}"),

        // Lobbies / BR interaction
        A("*",    "/api/lobbies/{*}"),
        A("*",    "/api/br/{*}"),

        // Wallets
        A("GET",  "/api/wallets/{*}"),

        // Loyalty
        A("GET",  "/api/loyalty/{*}"),
        A("POST", "/api/loyalty/{*}"),

        // Bookings
        A("*",    "/api/bookings/{*}"),

        // Sessions (venue)
        A("*",    "/api/sessions/{*}"),

        // POS
        A("*",    "/api/pos/{*}"),

        // Sponsors reads
        A("GET",  "/api/sponsors/{*}"),

        // Conversations / messaging
        A("GET",  "/api/conversations"),
        A("GET",  "/api/conversations/{*}"),
        A("POST", "/api/conversations"),
        A("POST", "/api/conversations/{*}"),
        A("PUT",  "/api/conversations/{*}"),
        A("PUT",  "/api/messages/{*}"),
        A("DELETE","/api/messages/{*}"),

        // Reviews
        A("GET",  "/api/reviews/{*}"),
        A("POST", "/api/reviews"),
        A("PUT",  "/api/reviews/{*}"),
        A("DELETE","/api/reviews/{*}"),

        // Steam / linked accounts
        A("GET",  "/api/accounts/{*}"),
        A("DELETE","/api/accounts/{*}"),

        // Content reporting
        A("POST", "/api/report-content"),

        // GDPR / consent
        A("POST", "/api/gdpr/{*}"),
        A("POST", "/api/consent"),

        // Partners
        A("POST", "/api/partners/{*}"),

        // Operations (authenticated impersonation end)
        A("POST", "/api/operations/{*}"),

        // Emails (admin sends)
        A("POST", "/api/emails"),

        // User role lookup
        A("GET",  "/api/users/{*}"),

        // ══════════════════════════════════════════════════════════════════════
        // VERIFIED ROLE — requires specific verified platform role
        // ══════════════════════════════════════════════════════════════════════

        // Tournament creation (verified organizer + org)
        new()
        {
            Method = "POST", Pattern = "/api/tournaments",
            Level = AuthLevel.VerifiedRole, RequiredRole = "organizer",
            RequiresVerification = true, RequiresOrganization = true,
        },

        // Organization creation (verified organizer)
        new()
        {
            Method = "POST", Pattern = "/api/organizations",
            Level = AuthLevel.VerifiedRole, RequiredRole = "organizer",
            RequiresVerification = true,
        },

        // Tournament management (ownership checked in handler via TournamentAuthorizationService)
        A("PUT",  "/api/tournaments/{id}"),
        A("DELETE","/api/tournaments/{id}"),
        A("POST", "/api/tournaments/{id}/stages/{*}"),
        A("PUT",  "/api/tournaments/{id}/stages/{*}"),
        A("DELETE","/api/tournaments/{id}/stages/{*}"),
        A("POST", "/api/tournaments/{id}/participants/{*}"),
        A("PUT",  "/api/tournaments/{id}/participants/{*}"),

        // Organization management (ownership checked in handler)
        A("PUT",  "/api/organizations/{*}"),
        A("POST", "/api/organizations/{*}"),
        A("DELETE","/api/organizations/{*}"),

        // Venue management (ownership checked in handler)
        A("POST", "/api/venues"),
        A("PUT",  "/api/venues/{*}"),
        A("DELETE","/api/venues/{*}"),

        // Staff permission endpoints
        A("*",    "/api/staff/{*}"),

        // ══════════════════════════════════════════════════════════════════════
        // ADMIN — specific permission groups
        // ══════════════════════════════════════════════════════════════════════

        // Admin user management
        Adm("*", "/api/admin/users/{*}",
            [Permissions.UsersView, Permissions.UsersBan, Permissions.UsersEdit]),

        // Admin tournament management
        Adm("*", "/api/admin/tournaments/{*}",
            [Permissions.TournamentsView, Permissions.TournamentsEdit, Permissions.TournamentsCancel]),

        // Admin venue management
        Adm("*", "/api/admin/venues/{*}",
            [Permissions.VenuesView, Permissions.VenuesApprove]),

        // Admin game catalog
        Adm("*", "/api/admin/games/{*}",
            [Permissions.GamesView, Permissions.GamesManage]),

        // Admin operations / system
        Adm("*", "/api/admin/operations/{*}",
            [Permissions.SystemSettings]),

        // Admin security
        Adm("*", "/api/admin/security/{*}",
            [Permissions.SecurityView, Permissions.SecurityManageIpAllowlist]),

        // Admin analytics
        Adm("GET", "/api/analytics/events",
            [Permissions.AnalyticsView]),
        Adm("GET", "/api/analytics/user/{*}",
            [Permissions.AnalyticsView]),

        // Admin reports
        Adm("*", "/api/admin/reports/{*}",
            [Permissions.ReportsView]),

        // Admin sponsors
        Adm("POST",  "/api/sponsors/{*}", [Permissions.SponsorsCreate]),
        Adm("PUT",   "/api/sponsors/{*}", [Permissions.SponsorsEdit]),
        Adm("DELETE","/api/sponsors/{*}", [Permissions.SponsorsDelete]),

        // Admin RBAC management
        Adm("GET",  "/api/admin/roles", [Permissions.RbacView]),
        Adm("GET",  "/api/admin/roles/{*}", [Permissions.RbacView]),
        Adm("GET",  "/api/admin/permissions", [Permissions.RbacView]),
        Adm("POST", "/api/admin/roles", [Permissions.RbacCreateRole]),
        Adm("PUT",  "/api/admin/roles/{*}", [Permissions.RbacEditRole]),
        Adm("DELETE","/api/admin/roles/{*}", [Permissions.RbacDeleteRole]),

        // Admin user roles (assign/revoke)
        Adm("GET",  "/api/admin/admin-user-roles", [Permissions.AdminUsersView]),
        Adm("POST", "/api/admin/admin-user-roles", [Permissions.AdminUsersAssignRole]),
        Adm("DELETE","/api/admin/admin-user-roles", [Permissions.AdminUsersRevokeRole]),

        // Admin sessions / security
        Adm("GET",  "/api/admin/sessions/{*}", [Permissions.SecurityViewSessions]),
        Adm("POST", "/api/admin/sessions/{*}", [Permissions.SecurityRevokeSessions]),

        // Catch-all admin (legacy — any admin role)
        new() { Method = "*", Pattern = "/api/admin/{*}", Level = AuthLevel.AdminAny },
    ];

    /// <summary>
    /// Resolve the first matching rule for a given method + path.
    /// Returns null if no rule matches (triggers default-deny in enforce mode).
    /// </summary>
    public static RouteAuthRule? Resolve(string method, string path)
    {
        foreach (var rule in _rules)
        {
            if (MethodMatches(rule.Method, method) && PatternMatches(rule.Pattern, path))
                return rule;
        }
        return null;
    }

    /// <summary>All registered rules (for startup validation and testing).</summary>
    public static IReadOnlyList<RouteAuthRule> AllRules => _rules;

    // ── Shorthand factories ──────────────────────────────────────────────────

    private static RouteAuthRule P(string method, string pattern) =>
        new() { Method = method, Pattern = pattern, Level = AuthLevel.Public };

    private static RouteAuthRule A(string method, string pattern) =>
        new() { Method = method, Pattern = pattern, Level = AuthLevel.Authenticated };

    private static RouteAuthRule Adm(string method, string pattern, string[] perms) =>
        new() { Method = method, Pattern = pattern, Level = AuthLevel.AdminPermission, RequiredPermissions = perms };

    // ── Pattern matching ─────────────────────────────────────────────────────

    private static bool MethodMatches(string ruleMethod, string requestMethod) =>
        ruleMethod == "*" || ruleMethod.Equals(requestMethod, StringComparison.OrdinalIgnoreCase);

    private static bool PatternMatches(string pattern, string path)
    {
        if (pattern == path)
            return true;

        // Wildcard suffix: /api/prefix/{*} matches /api/prefix/anything/deeper
        if (pattern.EndsWith("/{*}"))
        {
            var prefix = pattern[..^4]; // Remove /{*}
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && path.Length > prefix.Length
                && path[prefix.Length] == '/';
        }

        // Single-segment parameter: /api/resource/{id} matches /api/resource/some-uuid
        if (pattern.Contains('{') && !pattern.Contains("{*}"))
        {
            var patternParts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var pathParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (patternParts.Length != pathParts.Length)
                return false;

            for (var i = 0; i < patternParts.Length; i++)
            {
                if (patternParts[i].StartsWith('{') && patternParts[i].EndsWith('}'))
                    continue; // Wildcard segment — matches anything
                if (!patternParts[i].Equals(pathParts[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        return false;
    }
}
