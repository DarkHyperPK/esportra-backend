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
public sealed class TournamentAuthorizationService(
    IDbConnectionFactory db,
    IStaffAuthorizationService staffAuth)
{
    public static bool IsPlatformAdmin(UserContext userCtx)
        => StaffAuthorizationService.IsPlatformAdmin(userCtx);

    public async Task<bool> CanManageTournamentAsync(
        UserContext userCtx,
        Guid tournamentId,
        string? requiredPermission = null,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        return await staffAuth.CanActAsync(
            userCtx.UserIdGuid, tournamentId, requiredPermission, ct);
    }

    public async Task<bool> CanManageStaffAsync(
        UserContext userCtx,
        Guid tournamentId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        return await staffAuth.CanManageStaffAsync(userCtx.UserIdGuid, tournamentId, ct);
    }

    public async Task<bool> CanEditBracketByStageAsync(
        UserContext userCtx,
        Guid stageId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        return await staffAuth.CanActOnStageAsync(
            userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit, ct);
    }

    public async Task<bool> CanEditBracketByVersionAsync(
        UserContext userCtx,
        Guid versionId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        return await staffAuth.CanActOnBracketVersionAsync(
            userCtx.UserIdGuid, versionId, StaffAuthHelper.PermBracketEdit, ct);
    }

    public async Task<bool> CanEditBracketByMatchAsync(
        UserContext userCtx,
        Guid matchId,
        CancellationToken ct = default)
    {
        if (IsPlatformAdmin(userCtx))
            return true;

        return await staffAuth.CanActOnBracketMatchAsync(
            userCtx.UserIdGuid, matchId, StaffAuthHelper.PermBracketEdit, ct);
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
        => await staffAuth.ResolveTournamentIdBySlugAsync(slug, ct);
}
