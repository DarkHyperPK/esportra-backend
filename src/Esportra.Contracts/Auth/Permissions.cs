namespace Esportra.Contracts.Auth;

/// <summary>
/// The 26 platform permissions in resource:action format.
/// These mirror the frontend useAdminPermissions.ts constants exactly.
/// </summary>
public static class Permissions
{
    // Users
    public const string UsersView    = "users:view";
    public const string UsersEdit    = "users:edit";
    public const string UsersBan     = "users:ban";
    public const string UsersDelete  = "users:delete";

    // Tournaments
    public const string TournamentsView   = "tournaments:view";
    public const string TournamentsEdit   = "tournaments:edit";
    public const string TournamentsDelete = "tournaments:delete";
    public const string TournamentsCreate = "tournaments:create";

    // Disputes
    public const string DisputesView    = "disputes:view";
    public const string DisputesResolve = "disputes:resolve";
    public const string DisputesDelete  = "disputes:delete";

    // Venues
    public const string VenuesView    = "venues:view";
    public const string VenuesApprove = "venues:approve";
    public const string VenuesDelete  = "venues:delete";

    // Sponsors
    public const string SponsorsView   = "sponsors:view";
    public const string SponsorsCreate = "sponsors:create";
    public const string SponsorsEdit   = "sponsors:edit";
    public const string SponsorsDelete = "sponsors:delete";

    // Analytics
    public const string AnalyticsView   = "analytics:view";
    public const string AnalyticsExport = "analytics:export";

    // System
    public const string SystemSettings = "system:settings";
    public const string SystemAudit    = "system:audit";
    public const string SystemBilling  = "system:billing";

    // Content
    public const string ContentModerate = "content:moderate";
    public const string ContentCreate   = "content:create";
    public const string ContentDelete   = "content:delete";
}

/// <summary>
/// The 5 admin role keys (matches DB admin_roles.key column).
/// </summary>
public static class AdminRoles
{
    public const string OpsAdmin      = "ops_admin";
    public const string Moderator     = "moderator";
    public const string FinanceAdmin  = "finance_admin";
    public const string SupportAdmin  = "support_admin";
    public const string SuperAdmin    = "super_admin";

    /// <summary>Permissions granted to each role (mirrors DB seeded data).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RolePermissions =
        new Dictionary<string, string[]>
        {
            [OpsAdmin] =
            [
                Permissions.UsersView, Permissions.UsersEdit, Permissions.UsersBan,
                Permissions.TournamentsView, Permissions.TournamentsEdit, Permissions.TournamentsDelete,
                Permissions.DisputesView, Permissions.DisputesResolve,
                Permissions.VenuesView, Permissions.VenuesApprove,
                Permissions.AnalyticsView, Permissions.SystemAudit,
            ],
            [Moderator] =
            [
                Permissions.UsersView, Permissions.UsersBan,
                Permissions.DisputesView, Permissions.DisputesResolve,
                Permissions.ContentModerate, Permissions.ContentDelete,
                Permissions.TournamentsView,
            ],
            [FinanceAdmin] =
            [
                Permissions.AnalyticsView, Permissions.AnalyticsExport,
                Permissions.SystemBilling, Permissions.SponsorsView,
                Permissions.SponsorsCreate, Permissions.SponsorsEdit,
            ],
            [SupportAdmin] =
            [
                Permissions.UsersView, Permissions.DisputesView,
                Permissions.TournamentsView, Permissions.VenuesView,
            ],
            [SuperAdmin] = typeof(Permissions)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral)
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToArray(),
        };
}
