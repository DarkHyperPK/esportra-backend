using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public static class BrEvidenceRepository
{
    public static async Task<IReadOnlyList<dynamic>> ListRawAsync(
        IDbConnection conn,
        Guid lobbyId,
        bool isStaff,
        Guid? viewerTeamId,
        Guid? viewerParticipantId,
        Guid? gameId = null,
        int? gameNumber = null,
        IDbTransaction? tx = null)
    {
        if (!await BrSchemaRepository.TableExistsAsync(conn, "br_lobby_evidence", tx))
            return Array.Empty<dynamic>();

        if (!await BrSchemaRepository.BrGamesModelReadyAsync(conn, tx))
            return Array.Empty<dynamic>();

        var evidenceHasParticipantId = await BrSchemaRepository.ColumnExistsAsync(
            conn, "br_lobby_evidence", "participant_id", tx);

        if (isStaff && gameId is null && gameNumber is null)
        {
            var rows = await conn.QueryAsync<dynamic>(
                evidenceHasParticipantId ? BrEvidenceSql.StaffParticipantLobby : BrEvidenceSql.StaffTeamLobby,
                new { lobbyId },
                tx);
            return rows.AsList();
        }

        var targetGameId = await BrGameRepository.ResolveTargetGameIdAsync(conn, lobbyId, gameId, gameNumber, tx);
        if (targetGameId is null)
            return Array.Empty<dynamic>();

        if (isStaff)
        {
            var rows = await conn.QueryAsync<dynamic>(
                evidenceHasParticipantId ? BrEvidenceSql.StaffParticipantGame : BrEvidenceSql.StaffTeamGame,
                new { targetGameId },
                tx);
            return rows.AsList();
        }

        if (evidenceHasParticipantId)
        {
            var rows = await conn.QueryAsync<dynamic>(
                BrEvidenceSql.PlayerParticipantGame,
                new { targetGameId, viewerTeamId, viewerParticipantId },
                tx);
            return rows.AsList();
        }

        if (viewerTeamId is null)
            return Array.Empty<dynamic>();

        var teamRows = await conn.QueryAsync<dynamic>(
            BrEvidenceSql.PlayerTeamGame,
            new { targetGameId, viewerTeamId },
            tx);
        return teamRows.AsList();
    }

    public static async Task<IReadOnlyList<BrEvidenceEntry>> ListAsync(
        IDbConnection conn,
        Guid lobbyId,
        bool isStaff,
        Guid? viewerTeamId,
        Guid? viewerParticipantId,
        Guid? gameId = null,
        int? gameNumber = null,
        IDbTransaction? tx = null)
    {
        var rows = await ListRawAsync(
            conn, lobbyId, isStaff, viewerTeamId, viewerParticipantId, gameId, gameNumber, tx);

        var mapped = new List<BrEvidenceEntry>(rows.Count);
        foreach (var row in rows)
        {
            var entry = TryMapRow((object)row, HasColumn(row, "game_number"));
            if (entry is not null)
                mapped.Add(entry);
        }

        return mapped;
    }

    private static bool HasColumn(object row, string column) =>
        row is IDictionary<string, object> values
        && values.Keys.Any(key => string.Equals(key, column, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> CountPendingAsync(
        IDbConnection conn,
        Guid lobbyId,
        IDbTransaction? tx = null)
    {
        if (!await BrSchemaRepository.BrGamesModelReadyAsync(conn, tx))
            return 0;

        return await conn.QuerySingleAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM br_lobby_evidence re
            JOIN br_games g ON g.id = re.game_id
            WHERE g.lobby_id = @lobbyId
              AND re.reviewed = FALSE
            """,
            new { lobbyId },
            tx);
    }

    public static BrEvidenceEntry? TryMapRow(object? row, bool includeGameNumber = false)
    {
        if (row is null or DBNull)
            return null;

        if (row is not IDictionary<string, object> values)
            return null;

        var entityRaw = ReadValue(values, "entity_id") ?? ReadValue(values, "team_id");
        if (entityRaw is null)
            return null;

        if (TryReadGuid(entityRaw) is not Guid resolvedEntityId)
            return null;

        var imageUrl = ReadNullableString(ReadValue(values, "image_url"));
        if (string.IsNullOrWhiteSpace(imageUrl))
            return null;

        int? gameNumber = null;
        if (includeGameNumber)
        {
            var gameNumberRaw = ReadValue(values, "game_number");
            if (gameNumberRaw is not null)
                gameNumber = Convert.ToInt32(gameNumberRaw);
        }

        return new BrEvidenceEntry(
            resolvedEntityId.ToString(),
            ReadNullableString(ReadValue(values, "entity_name")) ?? ReadNullableString(ReadValue(values, "team_name")) ?? "Unknown",
            ReadNullableString(ReadValue(values, "logo_url")),
            imageUrl,
            FormatTimestamp(ReadValue(values, "submitted_at")),
            ReadNullableInt(ReadValue(values, "placement")),
            ReadNullableInt(ReadValue(values, "kills")),
            ReadNullableBool(ReadValue(values, "reviewed")) ?? false,
            gameNumber);
    }

    public static BrEvidenceEntry MapRow(object row, bool includeGameNumber = false) =>
        TryMapRow(row, includeGameNumber)
        ?? throw new InvalidOperationException("Evidence row is missing required fields.");

    private static object? ReadValue(IDictionary<string, object> dict, string key)
    {
        foreach (var pair in dict)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value is DBNull ? null : pair.Value;
        }

        return null;
    }

    private static string FormatTimestamp(object? value) =>
        value switch
        {
            DateTimeOffset dto => dto.ToString("o"),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToString("o"),
            string s => s,
            _ => DateTimeOffset.UtcNow.ToString("o"),
        };

    private static Guid? TryReadGuid(object? value) =>
        value switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => null,
        };

    private static string? ReadNullableString(object? value) =>
        value is null or DBNull ? null : Convert.ToString(value);

    private static int? ReadNullableInt(object? value) =>
        value is null or DBNull ? null : Convert.ToInt32(value);

    private static bool? ReadNullableBool(object? value) =>
        value is null or DBNull ? null : Convert.ToBoolean(value);
}
