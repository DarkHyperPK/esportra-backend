using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public static class BrEvidenceRepository
{
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
        if (!await BrSchemaRepository.TableExistsAsync(conn, "br_lobby_evidence", tx))
            return Array.Empty<BrEvidenceEntry>();

        if (!await BrSchemaRepository.BrGamesModelReadyAsync(conn, tx))
            return Array.Empty<BrEvidenceEntry>();

        var evidenceHasParticipantId = await BrSchemaRepository.ColumnExistsAsync(
            conn, "br_lobby_evidence", "participant_id", tx);

        if (isStaff && gameId is null && gameNumber is null)
        {
            return await QueryAsync(
                conn,
                evidenceHasParticipantId,
                """
                JOIN br_games g ON g.id = re.game_id
                WHERE g.lobby_id = @lobbyId
                ORDER BY g.game_number ASC, re.submitted_at DESC
                """,
                new { lobbyId },
                tx,
                includeGameNumber: true);
        }

        var targetGameId = await BrGameRepository.ResolveTargetGameIdAsync(conn, lobbyId, gameId, gameNumber, tx);
        if (targetGameId is null)
            return Array.Empty<BrEvidenceEntry>();

        if (isStaff)
        {
            return await QueryAsync(
                conn,
                evidenceHasParticipantId,
                """
                WHERE re.game_id = @targetGameId
                ORDER BY re.submitted_at DESC
                """,
                new { targetGameId },
                tx);
        }

        if (evidenceHasParticipantId)
        {
            return await QueryAsync(
                conn,
                evidenceHasParticipantId: true,
                """
                WHERE re.game_id = @targetGameId
                  AND (
                    (@viewerTeamId IS NOT NULL AND re.team_id = @viewerTeamId)
                    OR (@viewerParticipantId IS NOT NULL AND re.participant_id = @viewerParticipantId)
                  )
                ORDER BY re.submitted_at DESC
                """,
                new { targetGameId, viewerTeamId, viewerParticipantId },
                tx);
        }

        if (viewerTeamId is null)
            return Array.Empty<BrEvidenceEntry>();

        return await QueryAsync(
            conn,
            evidenceHasParticipantId: false,
            """
            WHERE re.game_id = @targetGameId
              AND re.team_id = @viewerTeamId
            ORDER BY re.submitted_at DESC
            """,
            new { targetGameId, viewerTeamId },
            tx);
    }

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

        var entityRaw = ReadValue(values, "entity_id");
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
            ReadNullableString(ReadValue(values, "entity_name")) ?? "Unknown",
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

    private static async Task<IReadOnlyList<BrEvidenceEntry>> QueryAsync(
        IDbConnection conn,
        bool evidenceHasParticipantId,
        string whereAndOrderSql,
        object parameters,
        IDbTransaction? tx = null,
        bool includeGameNumber = false)
    {
        var rows = await conn.QueryAsync<dynamic>(
            BuildSelectSql(evidenceHasParticipantId, includeGameNumber) + whereAndOrderSql,
            parameters,
            tx);

        return MapRows(rows, includeGameNumber);
    }

    private static IReadOnlyList<BrEvidenceEntry> MapRows(IEnumerable<dynamic> evidence, bool includeGameNumber = false)
    {
        var entries = new List<BrEvidenceEntry>();
        foreach (var row in evidence)
        {
            var entry = TryMapRow(row, includeGameNumber);
            if (entry is not null)
                entries.Add(entry);
        }

        return entries;
    }

    private static string BuildSelectSql(bool hasParticipantId, bool includeGameNumber = false)
    {
        var gameNumberSelect = includeGameNumber ? ", g.game_number" : string.Empty;
        return hasParticipantId
            ? $"""
              SELECT COALESCE(re.team_id, re.participant_id) AS entity_id,
                     CASE
                         WHEN re.team_id IS NOT NULL THEN t.name
                         ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                     END AS entity_name,
                     CASE WHEN re.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
                     re.image_url,
                     re.submitted_at,
                     re.placement,
                     re.kills,
                     re.reviewed{gameNumberSelect}
              FROM br_lobby_evidence re
              LEFT JOIN teams t ON t.id = re.team_id
              LEFT JOIN tournament_participants tp ON tp.id = re.participant_id
              LEFT JOIN profiles p ON p.id = tp.user_id
              """
            : $"""
              SELECT re.team_id AS entity_id,
                     t.name AS entity_name,
                     t.logo_url AS logo_url,
                     re.image_url,
                     re.submitted_at,
                     re.placement,
                     re.kills,
                     re.reviewed{gameNumberSelect}
              FROM br_lobby_evidence re
              LEFT JOIN teams t ON t.id = re.team_id
              """;
    }

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
