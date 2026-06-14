using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public sealed record BrLobbyReadinessSummary(
    int ReadyCount,
    int TotalAssigned,
    bool IsReady,
    IReadOnlyList<BrLobbyReadinessEntry>? Entries);

public static class BrLobbyReadinessService
{
    public static Task<int> GetAssignedCountAsync(
        IDbConnection conn,
        Guid groupId,
        IDbTransaction? tx = null) =>
        conn.QuerySingleAsync<int>(
            "SELECT COUNT(*)::int FROM br_group_teams WHERE group_id = @groupId",
            new { groupId },
            tx);

    public static async Task<int> GetReadyCountAsync(
        IDbConnection conn,
        Guid lobbyId,
        IDbTransaction? tx = null)
    {
        if (!await BrLobbyReadinessRepository.TableExistsAsync(conn, tx))
            return 0;

        return await conn.QuerySingleAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM br_lobby_readiness
            WHERE lobby_id = @lobbyId AND game_id IS NULL
            """,
            new { lobbyId },
            tx);
    }

    public static async Task<BrLobbyReadinessSummary> GetSummaryAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid groupId,
        bool includeEntries,
        Guid? viewerTeamId = null,
        Guid? viewerParticipantId = null,
        IDbTransaction? tx = null)
    {
        var totalAssigned = groupId != Guid.Empty
            ? await GetAssignedCountAsync(conn, groupId, tx)
            : 0;

        var entries = includeEntries
            ? await BrLobbyReadinessRepository.ListAsync(conn, lobbyId, tx)
            : Array.Empty<BrLobbyReadinessEntry>();

        var readyCount = entries.Count > 0 || includeEntries
            ? entries.Count
            : await GetReadyCountAsync(conn, lobbyId, tx);

        var viewerEntityId = viewerTeamId ?? viewerParticipantId;
        var isReady = viewerEntityId is not null && entries.Any(entry =>
            (viewerTeamId is not null && entry.TeamId == viewerTeamId.Value.ToString())
            || (viewerParticipantId is not null && entry.ParticipantId == viewerParticipantId.Value.ToString()));

        return new BrLobbyReadinessSummary(
            readyCount,
            totalAssigned,
            isReady,
            includeEntries ? entries : null);
    }
}
