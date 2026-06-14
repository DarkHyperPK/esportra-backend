using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public enum BrEvidenceApprovalStatus
{
    Success,
    NotFound,
    MissingReportedStats,
    InvalidStats,
    PlacementConflict,
    RosterMismatch,
}

public sealed record BrEvidenceApprovalOutcome(
    BrEvidenceApprovalStatus Status,
    int Placement = 0,
    int Kills = 0);

public static class BrEvidenceApprovalService
{
    public static async Task<BrEvidenceApprovalOutcome> ApproveAndApplyResultAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid entityId,
        Guid targetGameId,
        Guid groupId,
        BrScoringSettings scoring,
        int rosterSize,
        Guid reviewedBy,
        IDbTransaction tx)
    {
        var evidence = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT placement, kills, team_id, participant_id
            FROM br_lobby_evidence
            WHERE game_id = @targetGameId
              AND (team_id = @entityId OR participant_id = @entityId)
            """,
            new { targetGameId, entityId },
            tx);

        if (evidence is null)
            return new BrEvidenceApprovalOutcome(BrEvidenceApprovalStatus.NotFound);

        var values = (IDictionary<string, object>)evidence;
        var placementRaw = ReadValue(values, "placement");
        var killsRaw = ReadValue(values, "kills");

        if (placementRaw is null || killsRaw is null)
            return new BrEvidenceApprovalOutcome(BrEvidenceApprovalStatus.MissingReportedStats);

        var placement = Convert.ToInt32(placementRaw);
        var kills = Convert.ToInt32(killsRaw);

        if (placement < 1 || placement > rosterSize)
            return new BrEvidenceApprovalOutcome(BrEvidenceApprovalStatus.InvalidStats);
        if (kills < 0)
            return new BrEvidenceApprovalOutcome(BrEvidenceApprovalStatus.InvalidStats);

        var roundResultsHasParticipantId = await BrSchemaRepository.ColumnExistsAsync(
            conn, "br_lobby_results", "participant_id", tx);
        var roundResultsTeamIdAllowsNull = await ColumnAllowsNullAsync(conn, "br_lobby_results", "team_id", tx);
        var canPersistParticipantBackedResults = roundResultsHasParticipantId && roundResultsTeamIdAllowsNull;

        var rosterRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT team_id, participant_id
            FROM br_group_teams
            WHERE group_id = @groupId
              AND (team_id = @entityId OR participant_id = @entityId)
            """,
            new { groupId, entityId },
            tx);

        if (rosterRow is null)
            return new BrEvidenceApprovalOutcome(BrEvidenceApprovalStatus.RosterMismatch);

        var roster = (IDictionary<string, object>)rosterRow;
        var teamId = TryReadGuid(ReadValue(roster, "team_id"));
        var participantId = TryReadGuid(ReadValue(roster, "participant_id"));

        var conflictEntityExpression = roundResultsHasParticipantId
            ? "COALESCE(team_id, participant_id)"
            : "team_id";

        var placementConflict = await conn.QuerySingleOrDefaultAsync<Guid?>(
            $"""
             SELECT {conflictEntityExpression}
             FROM br_lobby_results
             WHERE game_id = @targetGameId
               AND placement = @placement
               AND {conflictEntityExpression} <> @entityId
             LIMIT 1
             """,
            new { targetGameId, placement, entityId },
            tx);

        if (placementConflict is not null)
            return new BrEvidenceApprovalOutcome(BrEvidenceApprovalStatus.PlacementConflict, placement, kills);

        var (placementPoints, killPoints, _) = BrConfigService.CalculatePoints(placement, kills, scoring);

        var existingResultId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            roundResultsHasParticipantId
                ? """
                  SELECT id
                  FROM br_lobby_results
                  WHERE game_id = @targetGameId
                    AND (
                      (team_id IS NOT NULL AND team_id = @teamId)
                      OR (participant_id IS NOT NULL AND participant_id = @participantId)
                    )
                  LIMIT 1
                  """
                : """
                  SELECT id
                  FROM br_lobby_results
                  WHERE game_id = @targetGameId
                    AND team_id = @teamId
                  LIMIT 1
                  """,
            new { targetGameId, teamId, participantId },
            tx);

        if (existingResultId is not null)
        {
            if (canPersistParticipantBackedResults && teamId is null && participantId is not null)
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE br_lobby_results
                    SET placement = @placement,
                        kills = @kills,
                        placement_points = @placementPoints,
                        kill_points = @killPoints,
                        lobby_id = @lobbyId
                    WHERE id = @id
                    """,
                    new
                    {
                        id = existingResultId,
                        placement,
                        kills,
                        placementPoints,
                        killPoints,
                        lobbyId,
                    },
                    tx);
            }
            else
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE br_lobby_results
                    SET placement = @placement,
                        kills = @kills,
                        placement_points = @placementPoints,
                        kill_points = @killPoints,
                        lobby_id = @lobbyId
                    WHERE id = @id
                    """,
                    new
                    {
                        id = existingResultId,
                        placement,
                        kills,
                        placementPoints,
                        killPoints,
                        lobbyId,
                    },
                    tx);
            }
        }
        else if (canPersistParticipantBackedResults && teamId is null && participantId is not null)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO br_lobby_results (
                    game_id, lobby_id, participant_id, placement, kills, placement_points, kill_points
                )
                VALUES (@targetGameId, @lobbyId, @participantId, @placement, @kills, @placementPoints, @killPoints)
                """,
                new
                {
                    targetGameId,
                    lobbyId,
                    participantId,
                    placement,
                    kills,
                    placementPoints,
                    killPoints,
                },
                tx);
        }
        else if (teamId is not null)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO br_lobby_results (
                    game_id, lobby_id, team_id, placement, kills, placement_points, kill_points
                )
                VALUES (@targetGameId, @lobbyId, @teamId, @placement, @kills, @placementPoints, @killPoints)
                """,
                new
                {
                    targetGameId,
                    lobbyId,
                    teamId,
                    placement,
                    kills,
                    placementPoints,
                    killPoints,
                },
                tx);
        }
        else
        {
            return new BrEvidenceApprovalOutcome(BrEvidenceApprovalStatus.RosterMismatch);
        }

        await conn.ExecuteAsync(
            """
            UPDATE br_lobby_evidence
            SET reviewed = TRUE,
                reviewed_at = NOW(),
                reviewed_by = @reviewedBy
            WHERE game_id = @targetGameId
              AND (team_id = @entityId OR participant_id = @entityId)
            """,
            new { targetGameId, entityId, reviewedBy },
            tx);

        return new BrEvidenceApprovalOutcome(BrEvidenceApprovalStatus.Success, placement, kills);
    }

    private static async Task<bool> ColumnAllowsNullAsync(
        IDbConnection conn,
        string table,
        string column,
        IDbTransaction? tx = null)
    {
        return await conn.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT is_nullable = 'YES'
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @table
              AND column_name = @column
            """,
            new { table, column },
            tx);
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

    private static Guid? TryReadGuid(object? value) =>
        value switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
}
