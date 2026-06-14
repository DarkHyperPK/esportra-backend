using System.Data;

namespace Esportra.Core.Br;

public static class BrEvidenceService
{
    public static Task<IReadOnlyList<dynamic>> ListRawAsync(
        IDbConnection conn,
        Guid lobbyId,
        bool isStaff,
        Guid? viewerTeamId,
        Guid? viewerParticipantId,
        Guid? gameId = null,
        int? gameNumber = null,
        IDbTransaction? tx = null) =>
        BrEvidenceRepository.ListRawAsync(
            conn, lobbyId, isStaff, viewerTeamId, viewerParticipantId, gameId, gameNumber, tx);

    public static Task<IReadOnlyList<BrEvidenceEntry>> ListAsync(
        IDbConnection conn,
        Guid lobbyId,
        bool isStaff,
        Guid? viewerTeamId,
        Guid? viewerParticipantId,
        Guid? gameId = null,
        int? gameNumber = null,
        IDbTransaction? tx = null) =>
        BrEvidenceRepository.ListAsync(
            conn, lobbyId, isStaff, viewerTeamId, viewerParticipantId, gameId, gameNumber, tx);

    public static Task<int> CountPendingAsync(
        IDbConnection conn,
        Guid lobbyId,
        IDbTransaction? tx = null) =>
        BrEvidenceRepository.CountPendingAsync(conn, lobbyId, tx);
}
