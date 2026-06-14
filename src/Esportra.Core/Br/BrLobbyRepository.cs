using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public static class BrLobbyRepository
{
    public static async Task ClearScoredStateAsync(
        IDbConnection conn,
        Guid lobbyId,
        IDbTransaction tx)
    {
        if (!await BrSchemaRepository.BrGamesModelReadyAsync(conn, tx))
            return;

        await conn.ExecuteAsync(
            """
            DELETE FROM br_lobby_results
            WHERE game_id IN (SELECT id FROM br_games WHERE lobby_id = @lobbyId)
            """,
            new { lobbyId },
            tx);

        await conn.ExecuteAsync(
            """
            DELETE FROM br_lobby_evidence
            WHERE game_id IN (SELECT id FROM br_games WHERE lobby_id = @lobbyId)
            """,
            new { lobbyId },
            tx);

        var gamesHasMap = await BrSchemaRepository.ColumnExistsAsync(conn, "br_games", "map", tx);
        if (gamesHasMap)
        {
            await conn.ExecuteAsync(
                """
                UPDATE br_games
                SET status = 'pending',
                    map = NULL,
                    scheduled_at = NULL,
                    started_at = NULL,
                    completed_at = NULL
                WHERE lobby_id = @lobbyId
                """,
                new { lobbyId },
                tx);
        }
        else
        {
            await conn.ExecuteAsync(
                """
                UPDATE br_games
                SET status = 'pending',
                    scheduled_at = NULL,
                    started_at = NULL,
                    completed_at = NULL
                WHERE lobby_id = @lobbyId
                """,
                new { lobbyId },
                tx);
        }
    }
}
