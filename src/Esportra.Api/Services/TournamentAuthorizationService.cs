using System.Data;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;

namespace Esportra.Api.Services;

/// <summary>
/// Central tournament resource authorization. Backend endpoints should use this
/// instead of copy-pasted owner/admin SQL or misleading policy names.
/// </summary>
public sealed class TournamentAuthorizationService(IDbConnectionFactory db)
{
    public static bool IsPlatformAdmin(UserContext userCtx)
        => StaffAuthHelper.IsPlatformAdmin(userCtx);

    public async Task<bool> CanManageTournamentAsync(
        UserContext userCtx,
        Guid tournamentId,
        string? requiredPermission = null,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanActOnTournamentAsync(
            conn, userCtx.UserIdGuid, tournamentId, requiredPermission);
    }

    public async Task<bool> CanManageStaffAsync(
        UserContext userCtx,
        Guid tournamentId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        using var conn = db.CreateConnection();
        return await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM tournaments t
                LEFT JOIN organizations o ON o.id = t.organization_id
                LEFT JOIN organization_staff os
                    ON os.user_id = @userId
                   AND os.status = 'active'
                   AND os.organization_id = t.organization_id
                WHERE t.id = @tournamentId
                  AND (
                      t.organizer_id = @userId
                      OR o.owner_id = @userId
                      OR os.role = 'admin'
                  )
            )
            """,
            new { userId = userCtx.UserIdGuid, tournamentId });
    }

    public async Task<bool> CanEditBracketByStageAsync(
        UserContext userCtx,
        Guid stageId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanActOnStageAsync(
            conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
    }

    public async Task<bool> CanEditBracketByVersionAsync(
        UserContext userCtx,
        Guid versionId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanActOnBracketVersionAsync(
            conn, userCtx.UserIdGuid, versionId, StaffAuthHelper.PermBracketEdit);
    }

    public async Task<bool> CanEditBracketByMatchAsync(
        UserContext userCtx,
        Guid matchId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanActOnBracketMatchAsync(
            conn, userCtx.UserIdGuid, matchId, StaffAuthHelper.PermBracketEdit);
    }

    public async Task<bool> CanSubmitEvidenceAsync(
        UserContext userCtx,
        Guid matchId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanAccessMatchRoomAsync(conn, userCtx.UserIdGuid, matchId, userCtx);
    }

    public async Task<bool> CanAccessMatchRoomAsync(
        UserContext userCtx,
        Guid matchId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanAccessMatchRoomAsync(conn, userCtx.UserIdGuid, matchId, userCtx);
    }

    public async Task<bool> RequireManageTournamentOrForbid(
        UserContext userCtx,
        Guid tournamentId,
        string? requiredPermission = null)
        => await CanManageTournamentAsync(userCtx, tournamentId, requiredPermission);

    public async Task<Guid?> ResolveTournamentIdBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM tournaments WHERE slug = @slug LIMIT 1",
            new { slug });
    }
}
