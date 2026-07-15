using System.Data;
using Esportra.Core.Br;

namespace Esportra.Api.Services;

/// <summary>
/// Temporary forwarding shim — delegates to Esportra.Core.Br repositories (removed in Phase 2a).
/// </summary>
public static class BrGameRouteHelper
{
    public static Task<Guid?> ResolveTargetGameIdAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid? gameId = null,
        int? gameNumber = null,
        IDbTransaction? tx = null) =>
        BrGameRepository.ResolveTargetGameIdAsync(conn, lobbyId, gameId, gameNumber, tx);

    public static async Task<IReadOnlyList<object>> ListLobbyEvidenceAsync(
        IDbConnection conn,
        Guid lobbyId,
        bool isStaff,
        Guid? viewerTeamId,
        Guid? viewerParticipantId,
        Guid? gameId = null,
        int? gameNumber = null,
        IDbTransaction? tx = null)
    {
        var entries = await BrEvidenceService.ListAsync(
            conn, lobbyId, isStaff, viewerTeamId, viewerParticipantId, gameId, gameNumber, tx);

        return entries.Select(MapEvidenceRow).Cast<object>().ToList();
    }

    public static Task SyncLobbyStatusFromGamesAsync(
        IDbConnection conn,
        Guid lobbyId,
        IDbTransaction? tx = null) =>
        BrGameRepository.SyncLobbyStatusFromGamesAsync(conn, lobbyId, tx);

    public static object MapEvidenceRow(BrEvidenceEntry entry) => new
    {
        teamId = entry.TeamId,
        teamName = entry.TeamName,
        logoUrl = entry.LogoUrl,
        imageUrl = entry.ImageUrl,
        submittedAt = entry.SubmittedAt,
        placement = entry.Placement,
        kills = entry.Kills,
        reviewed = entry.Reviewed,
        gameNumber = entry.GameNumber,
    };
}
