using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public sealed record BrLobbyReadinessEntry(
    string UserId,
    string DisplayName,
    string? TeamId,
    string? ParticipantId,
    string CheckedInAt);

public static class BrLobbyReadinessRepository
{
    public static async Task<bool> TableExistsAsync(IDbConnection conn, IDbTransaction? tx = null) =>
        await BrSchemaRepository.TableExistsAsync(conn, "br_lobby_readiness", tx);

    public static async Task ClearForLobbyAsync(
        IDbConnection conn,
        Guid lobbyId,
        IDbTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, tx))
            return;

        await conn.ExecuteAsync(
            "DELETE FROM br_lobby_readiness WHERE lobby_id = @lobbyId AND game_id IS NULL",
            new { lobbyId },
            tx);
    }

    public static async Task<IReadOnlyList<BrLobbyReadinessEntry>> ListAsync(
        IDbConnection conn,
        Guid lobbyId,
        IDbTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, tx))
            return Array.Empty<BrLobbyReadinessEntry>();

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT lr.user_id,
                   COALESCE(t.name, p.username, tp.team_name, 'Player') AS display_name,
                   lr.team_id,
                   lr.participant_id,
                   lr.checked_in_at
            FROM br_lobby_readiness lr
            LEFT JOIN teams t ON t.id = lr.team_id
            LEFT JOIN tournament_participants tp ON tp.id = lr.participant_id
            LEFT JOIN profiles p ON p.id = lr.user_id
            WHERE lr.lobby_id = @lobbyId
              AND lr.game_id IS NULL
            ORDER BY lr.checked_in_at ASC
            """,
            new { lobbyId },
            tx);

        return rows.Select(row => new BrLobbyReadinessEntry(
            ((Guid)row.user_id).ToString(),
            (string)row.display_name,
            row.team_id is null ? null : ((Guid)row.team_id).ToString(),
            row.participant_id is null ? null : ((Guid)row.participant_id).ToString(),
            FormatTimestamp(row.checked_in_at))).ToList();
    }

    public static async Task UpsertAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid userId,
        Guid? teamId,
        Guid? participantId,
        IDbTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, tx))
            throw new InvalidOperationException("br_lobby_readiness table is not available.");

        if (teamId is not null)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO br_lobby_readiness (lobby_id, team_id, participant_id, user_id, game_id)
                VALUES (@lobbyId, @teamId, NULL, @userId, NULL)
                ON CONFLICT (lobby_id, team_id) WHERE team_id IS NOT NULL AND game_id IS NULL
                DO UPDATE SET user_id = EXCLUDED.user_id, checked_in_at = now()
                """,
                new { lobbyId, teamId, userId },
                tx);
            return;
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO br_lobby_readiness (lobby_id, team_id, participant_id, user_id, game_id)
            VALUES (@lobbyId, NULL, @participantId, @userId, NULL)
            ON CONFLICT (lobby_id, participant_id) WHERE participant_id IS NOT NULL AND game_id IS NULL
            DO UPDATE SET user_id = EXCLUDED.user_id, checked_in_at = now()
            """,
            new { lobbyId, participantId, userId },
            tx);
    }

    public static async Task DeleteForEntityAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid? teamId,
        Guid? participantId,
        IDbTransaction? tx = null)
    {
        if (!await TableExistsAsync(conn, tx))
            return;

        if (teamId is not null)
        {
            await conn.ExecuteAsync(
                "DELETE FROM br_lobby_readiness WHERE lobby_id = @lobbyId AND team_id = @teamId AND game_id IS NULL",
                new { lobbyId, teamId },
                tx);
            return;
        }

        if (participantId is not null)
        {
            await conn.ExecuteAsync(
                "DELETE FROM br_lobby_readiness WHERE lobby_id = @lobbyId AND participant_id = @participantId AND game_id IS NULL",
                new { lobbyId, participantId },
                tx);
        }
    }

    private static string FormatTimestamp(object? value) =>
        value switch
        {
            DateTimeOffset dto => dto.ToString("o"),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToString("o"),
            string s => s,
            _ => DateTimeOffset.UtcNow.ToString("o"),
        };
}
