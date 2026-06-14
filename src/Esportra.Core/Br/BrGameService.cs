using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public static class BrGameService
{
    public static async Task<string?> ValidateResetAllowedAsync(
        IDbConnection conn,
        Guid gameId,
        IDbTransaction? tx = null)
    {
        if (!await BrSchemaRepository.BrGamesModelReadyAsync(conn, tx))
            return "Game reset is not available on this server yet.";

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT game_number, lobby_id
            FROM br_games
            WHERE id = @gameId
            """,
            new { gameId },
            tx);

        if (row is null)
            return "Game not found.";

        var lobbyId = (Guid)row.lobby_id;
        var gameNumber = Convert.ToInt32(row.game_number);

        var laterStarted = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM br_games
                WHERE lobby_id = @lobbyId
                  AND game_number > @gameNumber
                  AND status IN ('active', 'completed')
            )
            """,
            new { lobbyId, gameNumber },
            tx);

        return laterStarted
            ? "Cannot reset this game while a later game has already started or completed."
            : null;
    }

    public static async Task ResetGameAsync(
        IDbConnection conn,
        Guid gameId,
        IDbTransaction tx)
    {
        await conn.ExecuteAsync(
            "DELETE FROM br_lobby_results WHERE game_id = @gameId",
            new { gameId },
            tx);

        await conn.ExecuteAsync(
            "DELETE FROM br_lobby_evidence WHERE game_id = @gameId",
            new { gameId },
            tx);

        await conn.ExecuteAsync(
            """
            UPDATE br_games
            SET status = 'pending',
                started_at = NULL,
                completed_at = NULL,
                queue_started_at = NULL
            WHERE id = @gameId
            """,
            new { gameId },
            tx);

        var lobbyId = await conn.QuerySingleAsync<Guid>(
            "SELECT lobby_id FROM br_games WHERE id = @gameId",
            new { gameId },
            tx);

        await BrGameRepository.SyncLobbyStatusFromGamesAsync(conn, lobbyId, tx);
    }
}
