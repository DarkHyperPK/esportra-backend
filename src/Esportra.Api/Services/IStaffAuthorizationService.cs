using Esportra.Api.Helpers;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Services;

/// <summary>
/// Single source of truth for tournament staff authorization (reads and writes).
/// </summary>
public interface IStaffAuthorizationService
{
    Task<StaffAccessResult> ResolveAccessAsync(
        Guid userId, Guid tournamentId, CancellationToken ct = default);

    Task<TournamentAccessDto> ResolveTournamentAccessAsync(
        UserContext userCtx, Guid tournamentId, CancellationToken ct = default);

    Task<bool> CanActAsync(
        Guid userId, Guid tournamentId, string? requiredPermission = null, CancellationToken ct = default);

    Task<bool> CanManageStaffAsync(
        Guid userId, Guid tournamentId, CancellationToken ct = default);

    Task<bool> CanActOnStageAsync(
        Guid userId, Guid stageId, string? requiredPermission = null, CancellationToken ct = default);

    Task<bool> CanActOnBracketVersionAsync(
        Guid userId, Guid versionId, string? requiredPermission = null, CancellationToken ct = default);

    Task<bool> CanActOnBracketMatchAsync(
        Guid userId, Guid matchId, string? requiredPermission = null, CancellationToken ct = default);

    Task<Guid?> ResolveTournamentIdBySlugAsync(string slugOrId, CancellationToken ct = default);
}

public sealed record TournamentAccessDto(
    Guid TournamentId,
    string Role,
    string[] Permissions,
    bool IsOrganizer,
    bool IsPlatformAdmin);
