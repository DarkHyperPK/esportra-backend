namespace Esportra.Contracts.Auth;

/// <summary>
/// Platform admin permissions in resource:action format.
/// Backend constants are the source of truth; the frontend permission catalog is fetched from the API.
/// </summary>
public static class Permissions
{
    // Users
    public const string UsersView = "users:view";
    public const string UsersCreate = "users:create";
    public const string UsersEdit = "users:edit";
    public const string UsersBan = "users:ban"; // covers suspend, unsuspend, ban, unban
    public const string UsersDelete = "users:delete";
    public const string UsersExport = "users:export";
    public const string UsersImpersonate = "users:impersonate";
    public const string UsersAudit = "users:audit";

    // Profiles
    public const string ProfilesView = "profiles:view";
    public const string ProfilesEdit = "profiles:edit";
    public const string ProfilesDelete = "profiles:delete";
    public const string ProfilesRestore = "profiles:restore";
    public const string ProfilesAudit = "profiles:audit";

    // Admin users / RBAC
    public const string AdminUsersView = "admin_users:view";
    public const string AdminUsersAssignRole = "admin_users:assign_role";
    public const string AdminUsersRevokeRole = "admin_users:revoke_role";
    public const string AdminUsersAudit = "admin_users:audit";

    public const string RbacView = "rbac:view";
    public const string RbacCreateRole = "rbac:create_role";
    public const string RbacEditRole = "rbac:edit_role";
    public const string RbacDeleteRole = "rbac:delete_role";
    public const string RbacAssignPermissions = "rbac:assign_permissions";
    public const string RbacPreviewEffectivePermissions = "rbac:preview_effective_permissions";
    public const string RbacAudit = "rbac:audit";

    // God-mode controls
    public const string DataAdminView = "data_admin:view";
    public const string DataAdminCreate = "data_admin:create";
    public const string DataAdminEdit = "data_admin:edit";
    public const string DataAdminDelete = "data_admin:delete";
    public const string DataAdminRestore = "data_admin:restore";
    public const string DataAdminExport = "data_admin:export";
    public const string DataAdminOverride = "data_admin:override";
    public const string DataAdminAudit = "data_admin:audit";

    public const string ImpersonationStart = "impersonation:start";
    public const string ImpersonationStop = "impersonation:stop";
    public const string ImpersonationViewSessions = "impersonation:view_sessions";
    public const string ImpersonationAudit = "impersonation:audit";

    public const string FeatureFlagsView = "feature_flags:view";
    public const string FeatureFlagsCreate = "feature_flags:create";
    public const string FeatureFlagsEdit = "feature_flags:edit";
    public const string FeatureFlagsDelete = "feature_flags:delete";
    public const string FeatureFlagsToggle = "feature_flags:toggle";
    public const string FeatureFlagsAudit = "feature_flags:audit";

    // Broadcasts
    public const string BroadcastsView = "broadcasts:view";
    public const string BroadcastsCreate = "broadcasts:create";
    public const string BroadcastsEdit = "broadcasts:edit";
    public const string BroadcastsDelete = "broadcasts:delete";
    public const string BroadcastsSend = "broadcasts:send";

    // Ghost Mode (uses users:impersonate namespace)
    public const string GhostAudit = "ghost:audit";
    public const string UsersImpersonateFull = "users:impersonate:full";
    public const string UsersImpersonateApprove = "users:impersonate:approve";

    // Tournaments
    public const string TournamentsView = "tournaments:view";
    public const string TournamentsCreate = "tournaments:create";
    public const string TournamentsEdit = "tournaments:edit";
    public const string TournamentsApprove = "tournaments:approve"; // covers approve + reject
    public const string TournamentsFeature = "tournaments:feature"; // covers feature + unfeature
    public const string TournamentsCancel = "tournaments:cancel";
    public const string TournamentsDelete = "tournaments:delete";
    public const string TournamentsRestore = "tournaments:restore";
    public const string TournamentsExport = "tournaments:export";
    public const string TournamentsOverride = "tournaments:override";
    public const string TournamentsAudit = "tournaments:audit";

    // Brackets / matches / registration
    public const string BracketsView = "brackets:view";
    public const string BracketsEdit = "brackets:edit";
    public const string BracketsRegenerate = "brackets:regenerate";
    public const string BracketsReset = "brackets:reset";
    public const string BracketsLock = "brackets:lock"; // covers lock + unlock
    public const string BracketsOverride = "brackets:override";
    public const string BracketsAudit = "brackets:audit";

    public const string MatchesView = "matches:view";
    public const string MatchesEdit = "matches:edit";
    public const string MatchesSchedule = "matches:schedule";
    public const string MatchesReportResult = "matches:report_result";
    public const string MatchesOverrideResult = "matches:override_result";
    public const string MatchesLock = "matches:lock"; // covers lock + unlock
    public const string MatchesAudit = "matches:audit";

    public const string RegistrationsView = "registrations:view";
    public const string RegistrationsEdit = "registrations:edit";
    public const string RegistrationsApprove = "registrations:approve"; // covers approve + reject
    public const string RegistrationsCancel = "registrations:cancel";
    public const string RegistrationsRefund = "registrations:refund";
    public const string RegistrationsExport = "registrations:export";
    public const string RegistrationsAudit = "registrations:audit";

    public const string InvitationsView = "invitations:view";
    public const string InvitationsCreate = "invitations:create";
    public const string InvitationsRevoke = "invitations:revoke";
    public const string InvitationsResend = "invitations:resend";
    public const string InvitationsExport = "invitations:export";
    public const string InvitationsAudit = "invitations:audit";

    // Disputes
    public const string DisputesView = "disputes:view";
    public const string DisputesComment = "disputes:comment";
    public const string DisputesResolve = "disputes:resolve";
    public const string DisputesReject = "disputes:reject";
    public const string DisputesEscalate = "disputes:escalate";
    public const string DisputesLiftBan = "disputes:lift_ban";
    public const string DisputesDelete = "disputes:delete";
    public const string DisputesExport = "disputes:export";
    public const string DisputesAudit = "disputes:audit";

    // Venues
    public const string VenuesView = "venues:view";
    public const string VenuesCreate = "venues:create";
    public const string VenuesEdit = "venues:edit";
    public const string VenuesApprove = "venues:approve"; // covers approve + reject
    public const string VenuesPublish = "venues:publish"; // covers publish + unpublish
    public const string VenuesDelete = "venues:delete";
    public const string VenuesRestore = "venues:restore";
    public const string VenuesExport = "venues:export";
    public const string VenuesAudit = "venues:audit";

    public const string VenueStationsView = "venue_stations:view";
    public const string VenueStationsCreate = "venue_stations:create";
    public const string VenueStationsEdit = "venue_stations:edit";
    public const string VenueStationsLock = "venue_stations:lock"; // covers lock + unlock
    public const string VenueStationsDelete = "venue_stations:delete";
    public const string VenueStationsAudit = "venue_stations:audit";

    public const string VenueSessionsView = "venue_sessions:view";
    public const string VenueSessionsCreate = "venue_sessions:create";
    public const string VenueSessionsEdit = "venue_sessions:edit";
    public const string VenueSessionsEnd = "venue_sessions:end";
    public const string VenueSessionsRefund = "venue_sessions:refund";
    public const string VenueSessionsExport = "venue_sessions:export";
    public const string VenueSessionsAudit = "venue_sessions:audit";

    public const string VenueStaffView = "venue_staff:view";
    public const string VenueStaffInvite = "venue_staff:invite";
    public const string VenueStaffEdit = "venue_staff:edit";
    public const string VenueStaffRevoke = "venue_staff:revoke";
    public const string VenueStaffAudit = "venue_staff:audit";

    public const string MembersView = "members:view";
    public const string MembersCreate = "members:create";
    public const string MembersEdit = "members:edit";
    public const string MembersBan = "members:ban"; // covers ban + unban
    public const string MembersExport = "members:export";
    public const string MembersAudit = "members:audit";

    public const string BookingsView = "bookings:view";
    public const string BookingsCreate = "bookings:create";
    public const string BookingsEdit = "bookings:edit";
    public const string BookingsCancel = "bookings:cancel";
    public const string BookingsRefund = "bookings:refund";
    public const string BookingsExport = "bookings:export";
    public const string BookingsAudit = "bookings:audit";

    // Teams / organizations
    public const string TeamsView = "teams:view";
    public const string TeamsCreate = "teams:create";
    public const string TeamsEdit = "teams:edit";
    public const string TeamsDisband = "teams:disband";
    public const string TeamsRestore = "teams:restore";
    public const string TeamsTransferCaptain = "teams:transfer_captain";
    public const string TeamsRemoveMember = "teams:remove_member";
    public const string TeamsExport = "teams:export";
    public const string TeamsAudit = "teams:audit";

    public const string OrganizationsView = "organizations:view";
    public const string OrganizationsCreate = "organizations:create";
    public const string OrganizationsEdit = "organizations:edit";
    public const string OrganizationsRestore = "organizations:restore";
    public const string OrganizationsAudit = "organizations:audit";

    // Sponsors
    public const string SponsorsView = "sponsors:view";
    public const string SponsorsCreate = "sponsors:create";
    public const string SponsorsEdit = "sponsors:edit";
    public const string SponsorsApproveApplication = "sponsors:approve_application"; // covers approve + reject
    public const string SponsorsDelete = "sponsors:delete";
    public const string SponsorsExport = "sponsors:export";
    public const string SponsorsAudit = "sponsors:audit";

    // Verification / licenses
    public const string VerificationView = "verification:view";
    public const string VerificationApprove = "verification:approve"; // covers approve + reject
    public const string VerificationDelete = "verification:delete";
    public const string VerificationExport = "verification:export";
    public const string VerificationAudit = "verification:audit";

    public const string LicensesView = "licenses:view";
    public const string LicensesCreate = "licenses:create";
    public const string LicensesRevoke = "licenses:revoke";
    public const string LicensesReinstate = "licenses:reinstate";
    public const string LicensesDelete = "licenses:delete";
    public const string LicensesExport = "licenses:export";
    public const string LicensesAudit = "licenses:audit";

    // Commerce
    public const string PosView = "pos:view";
    public const string PosCreateOrder = "pos:create_order";
    public const string PosEditOrder = "pos:edit_order";
    public const string PosRefund = "pos:refund";
    public const string PosVoid = "pos:void";
    public const string PosExport = "pos:export";
    public const string PosAudit = "pos:audit";

    public const string WalletsView = "wallets:view";
    public const string WalletsAdjust = "wallets:adjust";
    public const string WalletsFreeze = "wallets:freeze"; // covers freeze + unfreeze
    public const string WalletsAudit = "wallets:audit";

    public const string LoyaltyView = "loyalty:view";
    public const string LoyaltyAdjust = "loyalty:adjust";
    public const string LoyaltyReset = "loyalty:reset";
    public const string LoyaltyAudit = "loyalty:audit";

    public const string PaymentsView = "payments:view";
    public const string PaymentsRefund = "payments:refund";
    public const string PaymentsReconcile = "payments:reconcile";
    public const string PaymentsExport = "payments:export";
    public const string PaymentsAudit = "payments:audit";

    // Analytics & Dashboard
    public const string AnalyticsView = "analytics:view";
    public const string AnalyticsExport = "analytics:export";
    public const string DashboardView = "dashboard:view";

    // System
    public const string SystemSettings = "system:settings";
    public const string SystemAudit = "system:audit";
    public const string SystemBilling = "system:billing";
    public const string SystemConfigView = "system:config_view";
    public const string SystemConfigEdit = "system:config_edit";
    public const string SystemKillSwitch = "system:kill-switch";
    public const string AuditView = "audit:view";
    public const string AuditExport = "audit:export";
    public const string SettingsView = "settings:view";
    public const string SettingsEdit = "settings:edit";
    public const string SettingsAudit = "settings:audit";

    public const string SecurityView = "security:view";
    public const string SecurityManageIpAllowlist = "security:manage_ip_allowlist";
    public const string SecurityRevokeSessions = "security:revoke_sessions";
    public const string SecurityViewSessions = "security:view_sessions";
    public const string SecurityAudit = "security:audit";

    public const string GdprView = "gdpr:view";
    public const string GdprProcess = "gdpr:process";
    public const string GdprReject = "gdpr:reject";
    public const string GdprExport = "gdpr:export";
    public const string GdprAudit = "gdpr:audit";

    public const string ReportsView = "reports:view";
    public const string ReportsCreate = "reports:create";
    public const string ReportsEdit = "reports:edit";
    public const string ReportsDelete = "reports:delete";
    public const string ReportsRun = "reports:run";
    public const string ReportsExport = "reports:export";
    public const string ReportsAudit = "reports:audit";

    public const string AlertsView = "alerts:view";
    public const string AlertsAcknowledge = "alerts:acknowledge";
    public const string AlertsResolve = "alerts:resolve";
    public const string AlertsBulkAcknowledge = "alerts:bulk_acknowledge";
    public const string AlertsAudit = "alerts:audit";

    // Content
    public const string ContentModerate = "content:moderate";
    public const string ContentCreate = "content:create";
    public const string ContentDelete = "content:delete";

    public const string ModerationView = "moderation:view";
    public const string ModerationApprove = "moderation:approve"; // covers approve, reject, dismiss
    public const string ModerationAudit = "moderation:audit";

    public const string NotificationsView = "notifications:view";
    public const string NotificationsCreate = "notifications:create";
    public const string NotificationsBroadcast = "notifications:broadcast";
    public const string NotificationsDelete = "notifications:delete";
    public const string NotificationsAudit = "notifications:audit";

    // Games catalog
    public const string GamesView = "games:view";
    public const string GamesCreate = "games:create";
    public const string GamesEdit = "games:edit";
    public const string GamesDelete = "games:delete";
    public const string GamesPublish = "games:publish";
    public const string GamesResetDraft = "games:reset_draft";
    public const string GamesUploadAssets = "games:upload_assets";
    public const string GamesAudit = "games:audit";
    public const string GamesManage = "games:manage";
}

/// <summary>
/// The 5 admin role keys (matches DB admin_roles.key column).
/// </summary>
public static class AdminRoles
{
    public const string OpsAdmin = "ops_admin";
    public const string Moderator = "moderator";
    public const string FinanceAdmin = "finance_admin";
    public const string SupportAdmin = "support_admin";
    public const string SuperAdmin = "super_admin";

    /// <summary>Permissions granted to each role (mirrors DB seeded data).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RolePermissions =
        new Dictionary<string, string[]>
        {
            [OpsAdmin] =
            [
                Permissions.UsersView, Permissions.UsersEdit, Permissions.UsersBan,
                Permissions.ProfilesView, Permissions.ProfilesEdit,
                Permissions.TournamentsView, Permissions.TournamentsCreate, Permissions.TournamentsEdit,
                Permissions.TournamentsApprove, Permissions.TournamentsFeature,
                Permissions.TournamentsCancel, Permissions.TournamentsExport,
                Permissions.BracketsView, Permissions.BracketsEdit, Permissions.BracketsLock,
                Permissions.MatchesView, Permissions.MatchesEdit, Permissions.MatchesSchedule,
                Permissions.RegistrationsView, Permissions.RegistrationsEdit, Permissions.RegistrationsApprove,
                Permissions.RegistrationsExport,
                Permissions.InvitationsView, Permissions.InvitationsCreate, Permissions.InvitationsRevoke,
                Permissions.TeamsView, Permissions.TeamsEdit, Permissions.TeamsDisband, Permissions.TeamsTransferCaptain,
                Permissions.TeamsRemoveMember, Permissions.TeamsExport,
                Permissions.OrganizationsView, Permissions.OrganizationsEdit,
                Permissions.DisputesView, Permissions.DisputesComment, Permissions.DisputesResolve,
                Permissions.DisputesEscalate, Permissions.DisputesExport,
                Permissions.VenuesView, Permissions.VenuesEdit, Permissions.VenuesApprove,
                Permissions.VenuesPublish, Permissions.VenuesExport,
                Permissions.VenueStationsView, Permissions.VenueStationsCreate, Permissions.VenueStationsEdit,
                Permissions.VenueStationsLock,
                Permissions.VenueSessionsView, Permissions.VenueSessionsEdit, Permissions.VenueSessionsEnd,
                Permissions.VenueSessionsExport,
                Permissions.VenueStaffView, Permissions.VenueStaffInvite, Permissions.VenueStaffEdit, Permissions.VenueStaffRevoke,
                Permissions.MembersView, Permissions.MembersEdit, Permissions.MembersBan, Permissions.MembersExport,
                Permissions.BookingsView, Permissions.BookingsEdit, Permissions.BookingsCancel, Permissions.BookingsExport,
                Permissions.VerificationView, Permissions.VerificationApprove, Permissions.VerificationExport,
                Permissions.ModerationView, Permissions.ModerationApprove,
                Permissions.ContentModerate, Permissions.ContentDelete,
                Permissions.AnalyticsView, Permissions.AnalyticsExport, Permissions.DashboardView,
                Permissions.AlertsView, Permissions.AlertsAcknowledge, Permissions.AlertsResolve, Permissions.AlertsBulkAcknowledge,
                Permissions.ReportsView, Permissions.ReportsCreate, Permissions.ReportsEdit, Permissions.ReportsRun, Permissions.ReportsExport,
                Permissions.GamesView, Permissions.GamesEdit, Permissions.GamesPublish, Permissions.GamesResetDraft,
                Permissions.GamesUploadAssets, Permissions.GamesManage,
                Permissions.SystemAudit, Permissions.AuditView, Permissions.SettingsView,
            ],
            [Moderator] =
            [
                Permissions.UsersView, Permissions.UsersBan,
                Permissions.ProfilesView,
                Permissions.TournamentsView, Permissions.TeamsView,
                Permissions.DisputesView, Permissions.DisputesComment, Permissions.DisputesResolve,
                Permissions.DisputesEscalate, Permissions.DisputesLiftBan,
                Permissions.ModerationView, Permissions.ModerationApprove,
                Permissions.ContentModerate, Permissions.ContentDelete,
                Permissions.VerificationView, Permissions.VerificationApprove,
                Permissions.DashboardView, Permissions.AlertsView, Permissions.AlertsAcknowledge,
            ],
            [FinanceAdmin] =
            [
                Permissions.AnalyticsView, Permissions.AnalyticsExport,
                Permissions.SystemBilling, Permissions.PaymentsView, Permissions.PaymentsRefund,
                Permissions.PaymentsReconcile, Permissions.PaymentsExport,
                Permissions.WalletsView, Permissions.WalletsAdjust,
                Permissions.LoyaltyView, Permissions.LoyaltyAdjust,
                Permissions.PosView, Permissions.PosRefund, Permissions.PosExport,
                Permissions.SponsorsView, Permissions.SponsorsCreate, Permissions.SponsorsEdit,
                Permissions.SponsorsApproveApplication, Permissions.SponsorsExport,
                Permissions.LicensesView, Permissions.LicensesCreate, Permissions.LicensesRevoke,
                Permissions.LicensesReinstate, Permissions.LicensesExport,
                Permissions.ReportsView, Permissions.ReportsCreate, Permissions.ReportsEdit,
                Permissions.ReportsRun, Permissions.ReportsExport,
                Permissions.DashboardView, Permissions.SystemAudit, Permissions.AuditView,
            ],
            [SupportAdmin] =
            [
                Permissions.UsersView, Permissions.ProfilesView,
                Permissions.DisputesView, Permissions.DisputesComment, Permissions.DisputesEscalate,
                Permissions.TournamentsView, Permissions.BracketsView, Permissions.MatchesView,
                Permissions.RegistrationsView, Permissions.InvitationsView,
                Permissions.VenuesView, Permissions.VenueStationsView, Permissions.VenueSessionsView,
                Permissions.BookingsView, Permissions.MembersView,
                Permissions.TeamsView, Permissions.OrganizationsView,
                Permissions.VerificationView, Permissions.LicensesView,
                Permissions.SecurityView, Permissions.SecurityViewSessions,
                Permissions.DashboardView, Permissions.AlertsView, Permissions.AlertsAcknowledge,
            ],
            [SuperAdmin] = typeof(Permissions)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral)
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToArray(),
        };
}
