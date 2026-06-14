using System.Data;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;

namespace Esportra.Api.Services;

public sealed class StaffAuthorizationService(IDbConnectionFactory db) : IStaffAuthorizationService
{
    public static bool IsPlatformAdmin(UserContext userCtx)
        => StaffAuthHelper.IsPlatformAdmin(userCtx);

    public async Task<StaffAccessResult> ResolveAccessAsync(
        Guid userId, Guid tournamentId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await StaffAuthHelper.ResolveStaffAccessAsync(conn, userId, tournamentId);
    }

    public async Task<TournamentAccessDto> ResolveTournamentAccessAsync(
        UserContext userCtx, Guid tournamentId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var organizerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT organizer_id FROM tournaments WHERE id = @tournamentId AND deleted_at IS NULL",
            new { tournamentId });

        if (organizerId is null)
        {
            return new TournamentAccessDto(
                tournamentId, "none", [], false, IsPlatformAdmin(userCtx));
        }

        var isPlatformAdmin = IsPlatformAdmin(userCtx);
        if (isPlatformAdmin)
        {
            return new TournamentAccessDto(
                tournamentId, "admin", StaffAuthHelper.AllStaffPermissions, true, true);
        }

        var isOrganizer = await StaffAuthHelper.IsTournamentOrganizerOrOrgOwnerAsync(
            conn, userCtx.UserIdGuid, tournamentId);
        if (isOrganizer)
        {
            return new TournamentAccessDto(
                tournamentId, "admin", StaffAuthHelper.AllStaffPermissions, true, false);
        }

        var staff = await StaffAuthHelper.ResolveStaffAccessAsync(
            conn, userCtx.UserIdGuid, tournamentId);

        if (!staff.CanAccess)
        {
            return new TournamentAccessDto(
                tournamentId, "none", [], false, false);
        }

        var role = string.Equals(staff.Role, "admin", StringComparison.OrdinalIgnoreCase)
            ? "admin"
            : "assigned";

        return new TournamentAccessDto(
            tournamentId, role, staff.Permissions, false, false);
    }

    public async Task<bool> CanActAsync(
        Guid userId, Guid tournamentId, string? requiredPermission = null, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanActOnTournamentAsync(
            conn, userId, tournamentId, requiredPermission);
    }

    public async Task<bool> CanManageStaffAsync(
        Guid userId, Guid tournamentId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanManageStaffOnTournamentAsync(conn, userId, tournamentId);
    }

    public async Task<bool> CanActOnStageAsync(
        Guid userId, Guid stageId, string? requiredPermission = null, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanActOnStageAsync(conn, userId, stageId, requiredPermission);
    }

    public async Task<bool> CanActOnBracketVersionAsync(
        Guid userId, Guid versionId, string? requiredPermission = null, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanActOnBracketVersionAsync(conn, userId, versionId, requiredPermission);
    }

    public async Task<bool> CanActOnBracketMatchAsync(
        Guid userId, Guid matchId, string? requiredPermission = null, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await StaffAuthHelper.CanActOnBracketMatchAsync(conn, userId, matchId, requiredPermission);
    }

    public async Task<Guid?> ResolveTournamentIdBySlugAsync(string slugOrId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        if (Guid.TryParse(slugOrId, out var id))
        {
            var exists = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM tournaments WHERE id = @id AND deleted_at IS NULL LIMIT 1",
                new { id });
            return exists;
        }

        return await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM tournaments WHERE slug = @slug AND deleted_at IS NULL LIMIT 1",
            new { slug = slugOrId });
    }
}
