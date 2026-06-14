using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public static class BrGameRepository
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

        if (!await BrSchemaRepository.TableExistsAsync(conn, "br_games", tx))
            return null;

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
        if (!await BrSchemaRepository.TableExistsAsync(conn, "br_games", tx))
            return;

        var stats = await conn.QuerySingleAsync<dynamic>(
            """
            SELECT
                COUNT(*)::int AS total,
                COUNT(*) FILTER (WHERE status = 'completed')::int AS completed
            FROM br_games
            WHERE lobby_id = @lobbyId
            """,
            new { lobbyId },
            tx);

        var total = Convert.ToInt32(stats.total);
        if (total == 0)
            return;

        var completed = Convert.ToInt32(stats.completed);
        if (completed != total)
            return;

        var completedAt = DateTimeOffset.UtcNow;
        await conn.ExecuteAsync(
            """
            UPDATE br_lobbies
            SET status = 'completed',
                completed_at = COALESCE(completed_at, @completedAt)
            WHERE id = @lobbyId
              AND status <> 'completed'
            """,
            new { lobbyId, completedAt },
            tx);
    }

    public static DateTimeOffset? CoerceTimestamp(object? value) =>
        value switch
        {
            null or DBNull => null,
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string text when DateTimeOffset.TryParse(text, out var parsed) => parsed,
            _ => null,
        };

    public static async Task<DateTimeOffset?> QuerySingleTimestampOrDefaultAsync(
        IDbConnection conn,
        string sql,
        object? param = null,
        IDbTransaction? tx = null)
    {
        var raw = await conn.QuerySingleOrDefaultAsync<object?>(sql, param, tx);
        return CoerceTimestamp(raw);
    }

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

        return BrConfigService.ResolveGamesPerLobby(row?.settings, row?.stage_config) ?? 6;
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
        if (!await BrSchemaRepository.TableExistsAsync(conn, "br_games", tx))
            return Array.Empty<Guid>();

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

        var mapConfig = BrConfigService.ResolveMapConfig(tournamentSettings, stageConfig, catalogBrConfig);
        var created = new List<Guid>();

        for (var gameNumber = existing.Count + 1; gameNumber <= gamesPerLobby; gameNumber++)
        {
            var map = BrConfigService.ResolveMapForRound(mapConfig, gameNumber, null)
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
                    scheduledAt = CoerceTimestamp(lobby.scheduled_at),
                },
                tx);

            created.Add(gameId);
        }

        return existing.Select(r => (Guid)r.id).Concat(created).ToList();
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
        if (!await BrSchemaRepository.TableExistsAsync(conn, "br_games", tx))
            return;

        var gamesPerLobby = gamesPerLobbyOverride
            ?? BrConfigService.ResolveGamesPerLobby(tournamentSettings, stageConfig)
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
