using System.Data;
using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Helpers;

/// <summary>
/// Centralised tournament staff authorization.
/// Every write endpoint that requires organizer/staff access should use one of these methods
/// instead of copy-pasting SQL.  The check hierarchy is:
///
///   1. User is the tournament organizer (or org owner)  → full access
///   2. User is org staff with role = 'admin'            → full access (org-level god mode)
///   3. User is org staff assigned to the tournament
///      with the required permission in their text[]     → scoped access
///   4. User holds a platform-admin role                 → full access (super_admin bypass)
///
/// If none match, the caller should return Results.Forbid().
/// </summary>
public sealed record StaffAccessResult(bool CanAccess, string? Role, string[] Permissions);

public static class StaffAuthHelper
{
    /// <summary>All scoped staff permissions (mirrors frontend StaffPermission type).</summary>
    public static readonly string[] AllStaffPermissions =
    [
        PermBracketEdit,
        PermScoresUpdate,
        PermTeamsManage,
        PermAnnouncementsSend,
        PermDisputesAssist,
    ];

    /// <summary>SQL: org staff row applies to tournament t (handles null t.organization_id for org admins).</summary>
    public const string StaffOrgTournamentLinkSql = """
        (
            (t.organization_id IS NOT NULL AND os.organization_id = t.organization_id)
            OR (
                os.role = 'admin'
                AND EXISTS (
                    SELECT 1 FROM organizations o
                    WHERE o.id = os.organization_id
                      AND o.owner_id = t.organizer_id
                )
            )
        )
        """;

    /// <summary>
    /// Resolves staff access for a user on a tournament (org admin bypass + assigned staff).
    /// Use for read endpoints that expose staffPermissions / route UX gates.
    /// </summary>
    public static async Task<StaffAccessResult> ResolveStaffAccessAsync(
        IDbConnection conn, Guid userId, Guid tournamentId, IDbTransaction? tx = null)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(string role, bool has_assignment, string[]? org_permissions, string[]? assignment_permissions)>(
            $"""
            SELECT os.role,
                   (sta.id IS NOT NULL) AS has_assignment,
                   os.permissions AS org_permissions,
                   sta.permissions AS assignment_permissions
            FROM tournaments t
            JOIN organization_staff os
              ON os.user_id = @userId
             AND os.status = 'active'
             AND {StaffOrgTournamentLinkSql}
            LEFT JOIN staff_tournament_assignments sta
              ON sta.organization_staff_id = os.id
             AND sta.tournament_id = t.id
            WHERE t.id = @tournamentId
            LIMIT 1
            """,
            new { userId, tournamentId }, tx);

        if (row.role is null)
            return new StaffAccessResult(false, null, []);

        if (string.Equals(row.role, "admin", StringComparison.OrdinalIgnoreCase))
            return new StaffAccessResult(true, "admin", AllStaffPermissions);

        if (row.has_assignment)
        {
            var perms = ResolveEffectivePermissions(row.org_permissions, row.assignment_permissions);
            return new StaffAccessResult(true, row.role, perms);
        }

        return new StaffAccessResult(false, null, []);
    }

    /// <summary>
    /// NULL assignment permissions inherit org defaults; non-null assignment replaces for that tournament.
    /// </summary>
    public static string[] ResolveEffectivePermissions(string[]? orgPermissions, string[]? assignmentPermissions)
    {
        var source = assignmentPermissions ?? orgPermissions ?? [];
        return source
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Validates and normalizes a permission list against the known staff permission catalog.</summary>
    public static string[] NormalizeStaffPermissions(IEnumerable<string>? permissions)
    {
        if (permissions is null)
            return [];

        var allowed = new HashSet<string>(AllStaffPermissions, StringComparer.Ordinal);
        return permissions
            .Where(p => !string.IsNullOrWhiteSpace(p) && allowed.Contains(p))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Returns false when any permission is outside the known catalog.</summary>
    public static bool TryNormalizeStaffPermissions(
        IEnumerable<string>? permissions,
        out string[] normalized,
        out string? error)
    {
        normalized = [];
        error = null;
        if (permissions is null)
            return true;

        var list = permissions.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var allowed = new HashSet<string>(AllStaffPermissions, StringComparer.Ordinal);
        foreach (var perm in list)
        {
            if (!allowed.Contains(perm))
            {
                error = $"Unknown staff permission: {perm}";
                return false;
            }
        }

        normalized = list.Distinct(StringComparer.Ordinal).ToArray();
        return true;
    }

    /// <summary>SQL fragment: user has staff visibility on tournament t (admin or assigned).</summary>
    public const string StaffTournamentAccessExistsSql = $"""
        EXISTS (
            SELECT 1 FROM organization_staff os
            LEFT JOIN staff_tournament_assignments sta
              ON sta.organization_staff_id = os.id
             AND sta.tournament_id = t.id
            WHERE os.user_id = @userId
              AND os.status = 'active'
              AND {StaffOrgTournamentLinkSql}
              AND (os.role = 'admin' OR sta.id IS NOT NULL)
        )
        """;

    /// <summary>True when user is tournament organizer or linked organization owner.</summary>
    public static async Task<bool> IsTournamentOrganizerOrOrgOwnerAsync(
        IDbConnection conn, Guid userId, Guid tournamentId, IDbTransaction? tx = null)
    {
        return await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM tournaments t
                LEFT JOIN organizations o ON o.id = t.organization_id
                WHERE t.id = @tournamentId
                  AND (
                      t.organizer_id = @userId
                      OR o.owner_id = @userId
                      OR EXISTS (
                          SELECT 1 FROM organizations o2
                          WHERE o2.owner_id = @userId
                            AND (
                                o2.id = t.organization_id
                                OR (
                                    t.organization_id IS NULL
                                    AND (
                                        t.organizer_id = o2.owner_id
                                        OR EXISTS (
                                            SELECT 1 FROM organization_staff os
                                            WHERE os.organization_id = o2.id
                                              AND os.user_id = t.organizer_id
                                              AND os.status = 'active'
                                        )
                                    )
                                )
                            )
                      )
                  )
            )
            """,
            new { userId, tournamentId }, tx);
    }

    /// <summary>Org staff admin (not assigned-only moderator).</summary>
    public static async Task<bool> CanManageStaffOnTournamentAsync(
        IDbConnection conn, Guid userId, Guid tournamentId, IDbTransaction? tx = null)
    {
        if (await IsTournamentOrganizerOrOrgOwnerAsync(conn, userId, tournamentId, tx))
            return true;

        var access = await ResolveStaffAccessAsync(conn, userId, tournamentId, tx);
        return access.CanAccess
            && string.Equals(access.Role, "admin", StringComparison.OrdinalIgnoreCase);
    }

    // ── By tournament_id ────────────────────────────────────────────────────
    public static async Task<bool> CanActOnTournamentAsync(
        IDbConnection conn, Guid userId, Guid tournamentId, string? requiredPermission = null, IDbTransaction? tx = null)
    {
        if (await IsTournamentOrganizerOrOrgOwnerAsync(conn, userId, tournamentId, tx))
            return true;

        var access = await ResolveStaffAccessAsync(conn, userId, tournamentId, tx);
        if (!access.CanAccess)
            return false;

        if (requiredPermission is null)
            return true;

        return access.Permissions.Contains(requiredPermission, StringComparer.Ordinal);
    }

    // ── By brkt_versions.id ─────────────────────────────────────────────────
    public static async Task<bool> CanActOnBracketVersionAsync(
        IDbConnection conn, Guid userId, Guid versionId, string? requiredPermission = null)
    {
        var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT tournament_id FROM brkt_versions WHERE id = @versionId",
            new { versionId });
        if (tournamentId is null)
            return false;

        return await CanActOnTournamentAsync(conn, userId, tournamentId.Value, requiredPermission);
    }

    // ── By tournament_stages.id ─────────────────────────────────────────────
    public static async Task<bool> CanActOnStageAsync(
        IDbConnection conn, Guid userId, Guid stageId, string? requiredPermission = null)
    {
        var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT tournament_id FROM tournament_stages WHERE id = @stageId",
            new { stageId });
        if (tournamentId is null)
            return false;

        return await CanActOnTournamentAsync(conn, userId, tournamentId.Value, requiredPermission);
    }

    // ── By brkt_matches.id ──────────────────────────────────────────────────
    public static async Task<bool> CanActOnBracketMatchAsync(
        IDbConnection conn, Guid userId, Guid matchId, string? requiredPermission = null)
    {
        var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT v.tournament_id
            FROM brkt_matches bm
            JOIN brkt_versions v ON v.id = bm.version_id
            WHERE bm.id = @matchId
            """,
            new { matchId });
        if (tournamentId is null)
            return false;

        return await CanActOnTournamentAsync(conn, userId, tournamentId.Value, requiredPermission);
    }

    public static async Task<bool> CanAccessMatchRoomAsync(
        IDbConnection conn, Guid userId, Guid matchId, UserContext? userCtx = null)
    {
        var isParticipant = await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM brkt_matches bm
                JOIN team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
                WHERE bm.id = @matchId
                  AND tm.user_id = @userId
                  AND tm.is_active = TRUE
                  AND tm.role != 'coach'
            )
            OR EXISTS(
                SELECT 1
                FROM brkt_matches bm
                JOIN tournament_participants tp
                  ON tp.id IN (bm.team1_id, bm.team2_id)
                 AND tp.user_id = @userId
                WHERE bm.id = @matchId
            )
            OR EXISTS(
                SELECT 1
                FROM brkt_matches bm
                JOIN tournament_participants tp
                  ON tp.team_id IN (bm.team1_id, bm.team2_id)
                WHERE bm.id = @matchId
                  AND (tp.user_id = @userId OR tp.team_captain_id = @userId)
            )
            """,
            new { userId, matchId });
        if (isParticipant) return true;

        var hasBracketAccess = await CanActOnBracketMatchAsync(conn, userId, matchId, PermBracketEdit);
        if (hasBracketAccess) return true;

        var hasDisputeAccess = await CanActOnBracketMatchAsync(conn, userId, matchId, PermDisputesAssist);
        if (hasDisputeAccess) return true;

        return userCtx is not null && HasMatchRoomAdminPermission(userCtx);
    }

    public static async Task<bool> IsMatchOrganizerOrStaffAsync(
        IDbConnection conn, Guid userId, Guid matchId, UserContext? userCtx = null)
    {
        var hasBracketAccess = await CanActOnBracketMatchAsync(conn, userId, matchId, PermBracketEdit);
        if (hasBracketAccess) return true;

        var hasDisputeAccess = await CanActOnBracketMatchAsync(conn, userId, matchId, PermDisputesAssist);
        if (hasDisputeAccess) return true;

        return userCtx is not null && HasMatchRoomAdminPermission(userCtx);
    }

    public static async Task<bool> CanViewTournamentAsync(
        IDbConnection conn, Guid userId, Guid tournamentId, UserContext? userCtx = null)
    {
        var visible = await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM tournaments t
                WHERE t.id = @tournamentId
                  AND t.deleted_at IS NULL
                  AND (
                      t.is_public = TRUE
                      OR t.organizer_id = @userId
                      OR EXISTS(
                          SELECT 1 FROM tournament_participants tp
                          WHERE tp.tournament_id = t.id
                            AND (tp.user_id = @userId OR tp.team_captain_id = @userId)
                      )
                  )
            )
            """,
            new { userId, tournamentId });
        if (visible) return true;

        if (await CanActOnTournamentAsync(conn, userId, tournamentId)) return true;
        return userCtx is not null && HasTournamentViewPermission(userCtx);
    }

    public static async Task<bool> CanViewBracketVersionAsync(
        IDbConnection conn, Guid userId, Guid versionId, UserContext? userCtx = null)
    {
        var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT tournament_id FROM brkt_versions WHERE id = @versionId",
            new { versionId });
        if (tournamentId is null) return false;

        if (await CanViewTournamentAsync(conn, userId, tournamentId.Value, userCtx)) return true;
        return await CanActOnBracketVersionAsync(conn, userId, versionId);
    }

    public static bool HasMatchRoomAdminPermission(UserContext userCtx)
    {
        if (userCtx.IsSuperAdmin) return true;
        return userCtx.Permissions.Contains(Permissions.MatchesView, StringComparer.OrdinalIgnoreCase)
            || userCtx.Permissions.Contains(Permissions.DisputesView, StringComparer.OrdinalIgnoreCase)
            || userCtx.Permissions.Contains(Permissions.DisputesResolve, StringComparer.OrdinalIgnoreCase)
            || userCtx.Permissions.Contains(Permissions.TournamentsEdit, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasTournamentViewPermission(UserContext userCtx)
    {
        if (userCtx.IsSuperAdmin) return true;
        return userCtx.Permissions.Contains(Permissions.TournamentsView, StringComparer.OrdinalIgnoreCase)
            || userCtx.Permissions.Contains(Permissions.TournamentsEdit, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Platform admin: super_admin or any admin_user_roles entry (AdminRoles on UserContext).
    /// </summary>
    public static bool IsPlatformAdmin(UserContext userCtx)
        => userCtx.IsSuperAdmin || userCtx.AdminRoles.Length > 0;

    // ── Permission constants (mirror frontend StaffPermission type) ──────────
    public const string PermBracketEdit       = "bracket:edit";
    public const string PermScoresUpdate      = "scores:update";
    public const string PermTeamsManage       = "teams:manage";
    public const string PermAnnouncementsSend = "announcements:send";
    public const string PermDisputesAssist    = "disputes:assist";
}
