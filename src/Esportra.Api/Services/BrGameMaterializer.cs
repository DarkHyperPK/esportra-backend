using System.Data;
using Dapper;
using Esportra.Api.Services;

namespace Esportra.Api.Services;

/// <summary>
/// Creates and manages br_games rows for persistent lobbies.
/// </summary>
public static class BrGameMaterializer
{
    public static async Task<int> ResolveGamesPerLobbyAsync(
        IDbConnection conn,
        Guid stageId,
        IDbTransaction? tx = null)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT t.settings, ts.config AS stage_config
            FROM tournament_stages ts
            JOIN tournaments t ON t.id = ts.tournament_id
            WHERE ts.id = @stageId
            """,
            new { stageId },
            tx);

        return BattleRoyaleConfigResolver.ResolveGamesPerLobby(row?.settings, row?.stage_config) ?? 6;
    }

    public static async Task<IReadOnlyList<Guid>> EnsureGamesForLobbyAsync(
        IDbConnection conn,
        Guid lobbyId,
        int gamesPerLobby,
        object? tournamentSettings,
        object? stageConfig,
        object? catalogBrConfig,
        IDbTransaction? tx = null)
    {
        var existing = (await conn.QueryAsync<dynamic>(
            """
            SELECT id, game_number, map, status
            FROM br_games
            WHERE lobby_id = @lobbyId
            ORDER BY game_number
            """,
            new { lobbyId },
            tx)).ToList();

        if (existing.Count >= gamesPerLobby)
            return existing.Select(r => (Guid)r.id).ToList();

        var lobby = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT wave_number, map, status, scheduled_at
            FROM br_lobbies
            WHERE id = @lobbyId
            """,
            new { lobbyId },
            tx);

        if (lobby is null)
            return Array.Empty<Guid>();

        var mapConfig = BattleRoyaleConfigResolver.ResolveMapConfig(
            tournamentSettings, stageConfig, catalogBrConfig);

        var created = new List<Guid>();
        for (var gameNumber = existing.Count + 1; gameNumber <= gamesPerLobby; gameNumber++)
        {
            var map = BattleRoyaleConfigResolver.ResolveMapForRound(mapConfig, gameNumber, null)
                      ?? (string?)lobby.map;

            var gameId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO br_games (lobby_id, game_number, map, status, scheduled_at)
                VALUES (@lobbyId, @gameNumber, @map, 'pending', @scheduledAt)
                ON CONFLICT (lobby_id, game_number) DO UPDATE SET lobby_id = EXCLUDED.lobby_id
                RETURNING id
                """,
                new
                {
                    lobbyId,
                    gameNumber,
                    map,
                    scheduledAt = (DateTimeOffset?)lobby.scheduled_at,
                },
                tx);

            created.Add(gameId);
        }

        var all = existing.Select(r => (Guid)r.id).Concat(created).ToList();
        return all;
    }

    public static async Task MaterializeStageGamesAsync(
        IDbConnection conn,
        Guid stageId,
        int? gamesPerLobbyOverride = null,
        object? tournamentSettings = null,
        object? stageConfig = null,
        object? catalogBrConfig = null,
        IDbTransaction? tx = null)
    {
        var gamesPerLobby = gamesPerLobbyOverride
            ?? BattleRoyaleConfigResolver.ResolveGamesPerLobby(tournamentSettings, stageConfig)
            ?? await ResolveGamesPerLobbyAsync(conn, stageId, tx);

        var lobbyIds = (await conn.QueryAsync<Guid>(
            "SELECT id FROM br_lobbies WHERE stage_id = @stageId",
            new { stageId },
            tx)).ToList();

        foreach (var lobbyId in lobbyIds)
        {
            await EnsureGamesForLobbyAsync(
                conn, lobbyId, gamesPerLobby, tournamentSettings, stageConfig, catalogBrConfig, tx);
        }
    }
}
