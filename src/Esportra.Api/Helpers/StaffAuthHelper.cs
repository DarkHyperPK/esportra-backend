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
public static class StaffAuthHelper
{
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
        IDbConnection conn, Guid userId, Guid matchId)
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
                JOIN tournament_participants tp ON tp.id IN (bm.team1_id, bm.team2_id)
                WHERE bm.id = @matchId
                  AND tp.participant_type = 'solo'
                  AND tp.user_id = @userId
            )
            """,
            new { userId, matchId });
        if (isParticipant) return true;

        var hasBracketAccess = await CanActOnBracketMatchAsync(conn, userId, matchId, PermBracketEdit);
        if (hasBracketAccess) return true;

        var hasDisputeAccess = await CanActOnBracketMatchAsync(conn, userId, matchId, PermDisputesAssist);
        if (hasDisputeAccess) return true;

        return await conn.QuerySingleAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM admin_user_roles WHERE user_id = @userId)",
            new { userId });
    }

    public static async Task<bool> IsMatchOrganizerOrStaffAsync(
        IDbConnection conn, Guid userId, Guid matchId)
    {
        var hasBracketAccess = await CanActOnBracketMatchAsync(conn, userId, matchId, PermBracketEdit);
        if (hasBracketAccess) return true;

        var hasDisputeAccess = await CanActOnBracketMatchAsync(conn, userId, matchId, PermDisputesAssist);
        if (hasDisputeAccess) return true;

        return await conn.QuerySingleAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM admin_user_roles WHERE user_id = @userId)",
            new { userId });
    }

    /// <summary>
    /// Quick check: is the user a platform admin (any admin role)?
    /// Use as a final fallback after the SQL check returns false.
    /// </summary>
    public static bool IsPlatformAdmin(UserContext userCtx)
        => userCtx.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase)
           || userCtx.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase);

    // ── Permission constants (mirror frontend StaffPermission type) ──────────
    public const string PermBracketEdit       = "bracket:edit";
    public const string PermScoresUpdate      = "scores:update";
    public const string PermTeamsManage       = "teams:manage";
    public const string PermAnnouncementsSend = "announcements:send";
    public const string PermDisputesAssist    = "disputes:assist";
}
