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
        var row = await conn.QuerySingleOrDefaultAsync<(string role, bool has_assignment, string[]? permissions)>(
            $"""
            SELECT os.role,
                   (sta.id IS NOT NULL) AS has_assignment,
                   os.permissions
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
            var perms = row.permissions ?? [];
            return new StaffAccessResult(true, row.role, perms.Distinct(StringComparer.Ordinal).ToArray());
        }

        return new StaffAccessResult(false, null, []);
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

    // ── By tournament_id ────────────────────────────────────────────────────
    public static async Task<bool> CanActOnTournamentAsync(
        IDbConnection conn, Guid userId, Guid tournamentId, string? requiredPermission = null, IDbTransaction? tx = null)
    {
        return await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM tournaments t
                LEFT JOIN organizations o ON o.id = t.organization_id
                LEFT JOIN organization_staff os
                    ON os.user_id = @userId
                   AND os.status = 'active'
                   AND (os.organization_id = t.organization_id)
                LEFT JOIN staff_tournament_assignments sta
                    ON sta.organization_staff_id = os.id
                   AND sta.tournament_id = t.id
                WHERE t.id = @tournamentId
                  AND (
                      -- 1. Direct organizer or org owner
                      t.organizer_id = @userId
                      OR o.owner_id = @userId
                      -- 2. Org staff admin (role bypass — no permission check needed)
                      OR (os.role = 'admin')
                      -- 3. Assigned staff with matching permission
                      OR (sta.id IS NOT NULL AND (
                          @perm IS NULL OR @perm = ANY(os.permissions)
                      ))
                  )
            )
            """,
            new { userId, tournamentId, perm = requiredPermission }, tx);
    }

    // ── By brkt_versions.id ─────────────────────────────────────────────────
    public static async Task<bool> CanActOnBracketVersionAsync(
        IDbConnection conn, Guid userId, Guid versionId, string? requiredPermission = null)
    {
        return await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM brkt_versions v
                JOIN tournaments t ON t.id = v.tournament_id
                LEFT JOIN organizations o ON o.id = t.organization_id
                LEFT JOIN organization_staff os
                    ON os.user_id = @userId
                   AND os.status = 'active'
                   AND os.organization_id = t.organization_id
                LEFT JOIN staff_tournament_assignments sta
                    ON sta.organization_staff_id = os.id
                   AND sta.tournament_id = t.id
                WHERE v.id = @versionId
                  AND (
                      t.organizer_id = @userId
                      OR o.owner_id = @userId
                      OR (os.role = 'admin')
                      OR (sta.id IS NOT NULL AND (
                          @perm IS NULL OR @perm = ANY(os.permissions)
                      ))
                  )
            )
            """,
            new { userId, versionId, perm = requiredPermission });
    }

    // ── By tournament_stages.id ─────────────────────────────────────────────
    public static async Task<bool> CanActOnStageAsync(
        IDbConnection conn, Guid userId, Guid stageId, string? requiredPermission = null)
    {
        return await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM tournament_stages ts
                JOIN tournaments t ON t.id = ts.tournament_id
                LEFT JOIN organizations o ON o.id = t.organization_id
                LEFT JOIN organization_staff os
                    ON os.user_id = @userId
                   AND os.status = 'active'
                   AND os.organization_id = t.organization_id
                LEFT JOIN staff_tournament_assignments sta
                    ON sta.organization_staff_id = os.id
                   AND sta.tournament_id = t.id
                WHERE ts.id = @stageId
                  AND (
                      t.organizer_id = @userId
                      OR o.owner_id = @userId
                      OR (os.role = 'admin')
                      OR (sta.id IS NOT NULL AND (
                          @perm IS NULL OR @perm = ANY(os.permissions)
                      ))
                  )
            )
            """,
            new { userId, stageId, perm = requiredPermission });
    }

    // ── By brkt_matches.id ──────────────────────────────────────────────────
    public static async Task<bool> CanActOnBracketMatchAsync(
        IDbConnection conn, Guid userId, Guid matchId, string? requiredPermission = null)
    {
        return await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM brkt_matches bm
                JOIN brkt_versions v ON v.id = bm.version_id
                JOIN tournaments t ON t.id = v.tournament_id
                LEFT JOIN organizations o ON o.id = t.organization_id
                LEFT JOIN organization_staff os
                    ON os.user_id = @userId
                   AND os.status = 'active'
                   AND os.organization_id = t.organization_id
                LEFT JOIN staff_tournament_assignments sta
                    ON sta.organization_staff_id = os.id
                   AND sta.tournament_id = t.id
                WHERE bm.id = @matchId
                  AND (
                      t.organizer_id = @userId
                      OR o.owner_id = @userId
                      OR (os.role = 'admin')
                      OR (sta.id IS NOT NULL AND (
                          @perm IS NULL OR @perm = ANY(os.permissions)
                      ))
                  )
            )
            """,
            new { userId, matchId, perm = requiredPermission });
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
