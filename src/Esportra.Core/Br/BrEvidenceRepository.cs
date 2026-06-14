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
            var allEvidence = await conn.QueryAsync<dynamic>(
                BuildSelectSql(evidenceHasParticipantId, includeGameNumber: true) + """
                JOIN br_games g ON g.id = re.game_id
                WHERE g.lobby_id = @lobbyId
                ORDER BY g.game_number ASC, re.submitted_at DESC
                """,
                new { lobbyId },
                tx);

            return MapRows(allEvidence, includeGameNumber: true);
        }

        var targetGameId = await BrGameRepository.ResolveTargetGameIdAsync(conn, lobbyId, gameId, gameNumber, tx);
        if (targetGameId is null)
            return Array.Empty<BrEvidenceEntry>();

        var evidence = await conn.QueryAsync<dynamic>(
            BuildSelectSql(evidenceHasParticipantId) + """
            WHERE re.game_id = @targetGameId
              AND (
                @isStaff::boolean
                OR (@viewerTeamId IS NOT NULL AND re.team_id = @viewerTeamId)
                OR (@viewerParticipantId IS NOT NULL AND re.participant_id = @viewerParticipantId)
              )
            ORDER BY re.submitted_at DESC
            """,
            new { targetGameId, isStaff, viewerTeamId, viewerParticipantId },
            tx);

        return MapRows(evidence);
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

    public static BrEvidenceEntry? TryMapRow(dynamic row, bool includeGameNumber = false)
    {
        if (row.entity_id is null or DBNull) return null;

        var entityId = TryReadGuid(row.entity_id);
        if (entityId is not Guid resolvedEntityId) return null;

        var imageUrl = ReadNullableString(row.image_url);
        if (string.IsNullOrWhiteSpace(imageUrl)) return null;

        int? gameNumber = null;
        if (includeGameNumber && row is IDictionary<string, object> dict
            && dict.TryGetValue("game_number", out var gn) && gn is not null and not DBNull)
        {
            gameNumber = Convert.ToInt32(gn);
        }

        return new BrEvidenceEntry(
            resolvedEntityId.ToString(),
            ReadNullableString(row.entity_name) ?? "Unknown",
            ReadNullableString(row.logo_url),
            imageUrl,
            FormatTimestamp(row.submitted_at),
            ReadNullableInt(row.placement),
            ReadNullableInt(row.kills),
            ReadNullableBool(row.reviewed) ?? false,
            gameNumber);
    }

    public static BrEvidenceEntry MapRow(dynamic row, bool includeGameNumber = false) =>
        TryMapRow(row, includeGameNumber)
        ?? throw new InvalidOperationException("Evidence row is missing required fields.");

    private static IReadOnlyList<BrEvidenceEntry> MapRows(IEnumerable<dynamic> evidence, bool includeGameNumber = false) =>
        evidence
            .Select<dynamic, BrEvidenceEntry?>(row => TryMapRow(row, includeGameNumber))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToList();

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

    private static Guid ReadGuid(object? value) =>
        TryReadGuid(value)
        ?? throw new InvalidOperationException("Evidence row is missing entity id.");
}
