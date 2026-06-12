using System.Data;
using Dapper;

namespace Esportra.Api.Services;

public static class BrGameRouteHelper
{
    public static async Task<Guid?> ResolveTargetGameIdAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid? gameId = null,
        int? gameNumber = null,
        IDbTransaction? tx = null)
    {
        if (gameId is not null)
            return gameId;

        if (gameNumber is > 0)
        {
            return await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT id FROM br_games
                WHERE lobby_id = @lobbyId AND game_number = @gameNumber
                """,
                new { lobbyId, gameNumber },
                tx);
        }

        var active = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT id FROM br_games
            WHERE lobby_id = @lobbyId AND status = 'active'
            ORDER BY game_number
            LIMIT 1
            """,
            new { lobbyId },
            tx);

        if (active is not null)
            return active;

        return await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT id FROM br_games
            WHERE lobby_id = @lobbyId
            ORDER BY game_number
            LIMIT 1
            """,
            new { lobbyId },
            tx);
    }

    public static async Task SyncLobbyStatusFromGamesAsync(
        IDbConnection conn,
        Guid lobbyId,
        IDbTransaction? tx = null)
    {
        var stats = await conn.QuerySingleAsync<dynamic>(
            """
            SELECT
                COUNT(*)::int AS total,
                COUNT(*) FILTER (WHERE status = 'completed')::int AS completed,
                COUNT(*) FILTER (WHERE status = 'active')::int AS active
            FROM br_games
            WHERE lobby_id = @lobbyId
            """,
            new { lobbyId },
            tx);

        var total = Convert.ToInt32(stats.total);
        if (total == 0)
            return;

        var completed = Convert.ToInt32(stats.completed);
        var active = Convert.ToInt32(stats.active);

        string status;
        DateTimeOffset? completedAt = null;
        if (completed == total)
        {
            status = "completed";
            completedAt = DateTimeOffset.UtcNow;
        }
        else if (active > 0 || completed > 0)
        {
            status = "active";
        }
        else
        {
            status = "pending";
        }

        await conn.ExecuteAsync(
            """
            UPDATE br_lobbies
            SET status = @status,
                completed_at = CASE WHEN @status = 'completed' THEN COALESCE(completed_at, @completedAt) ELSE completed_at END
            WHERE id = @lobbyId
            """,
            new { lobbyId, status, completedAt },
            tx);
    }
}
