using System.Collections;
using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public static class BrLobbyRepository
{
    public static async Task<BrLobbyContext?> GetContextAsync(
        IDbConnection conn,
        Guid lobbyId,
        bool includeStageConfig = false,
        IDbTransaction? tx = null)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            includeStageConfig
                ? """
                  SELECT r.stage_id AS stage_id,
                         (
                             SELECT lg.group_id
                             FROM br_lobby_groups lg
                             WHERE lg.lobby_id = r.id
                             ORDER BY lg.group_id
                             LIMIT 1
                         ) AS group_id,
                         r.wave_number AS wave_number,
                         r.status AS status,
                         ts.tournament_id AS tournament_id,
                         COALESCE(t.team_size, 1) AS team_size,
                         t.game AS game,
                         t.settings AS settings,
                         ts.config AS stage_config
                  FROM br_lobbies r
                  JOIN tournament_stages ts ON ts.id = r.stage_id
                  JOIN tournaments t ON t.id = ts.tournament_id
                  WHERE r.id = @lobbyId
                  """
                : """
                  SELECT r.stage_id AS stage_id,
                         (
                             SELECT lg.group_id
                             FROM br_lobby_groups lg
                             WHERE lg.lobby_id = r.id
                             ORDER BY lg.group_id
                             LIMIT 1
                         ) AS group_id,
                         r.wave_number AS wave_number,
                         r.status AS status,
                         ts.tournament_id AS tournament_id,
                         COALESCE(t.team_size, 1) AS team_size
                  FROM br_lobbies r
                  JOIN tournament_stages ts ON ts.id = r.stage_id
                  JOIN tournaments t ON t.id = ts.tournament_id
                  WHERE r.id = @lobbyId
                  """,
            new { lobbyId },
            tx);

        return MapContext(row);
    }

    public static BrLobbyContext? MapContext(object? row)
    {
        if (row is null or DBNull)
            return null;

        if (row is not IDictionary<string, object> values)
            return null;

        static object? Read(IDictionary<string, object> dict, string key)
        {
            foreach (var pair in dict)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    return pair.Value is DBNull ? null : pair.Value;
            }

            return null;
        }

        var stageId = Read(values, "stage_id");
        if (stageId is null)
            return null;

        return new BrLobbyContext(
            StageId: (Guid)stageId,
            GroupId: Read(values, "group_id") as Guid?,
            WaveNumber: Convert.ToInt32(Read(values, "wave_number") ?? 0),
            Status: Read(values, "status") as string ?? "pending",
            TournamentId: (Guid)Read(values, "tournament_id")!,
            TeamSize: Convert.ToInt32(Read(values, "team_size") ?? 1),
            Game: Read(values, "game") as string,
            Settings: Read(values, "settings"),
            StageConfig: Read(values, "stage_config"));
    }

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

        await BrLobbyReadinessRepository.ClearForLobbyAsync(conn, lobbyId, tx);

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
                completed_at = NULL,
                queue_timer_minutes = NULL,
                queue_started_at = NULL
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
                    completed_at = NULL,
                    queue_timer_minutes = NULL,
                    queue_started_at = NULL
                WHERE lobby_id = @lobbyId
                """,
                new { lobbyId },
                tx);
        }
    }
}
