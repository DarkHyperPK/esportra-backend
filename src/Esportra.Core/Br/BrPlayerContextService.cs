using System.Data;
using Dapper;

namespace Esportra.Core.Br;

public static class BrPlayerContextService
{
    public sealed record PlayerGroupRow(
        Guid StageId,
        string StageName,
        int StageOrder,
        Guid GroupId,
        string GroupName);

    private const string UserParticipationSql = """
        SELECT DISTINCT tp.id AS participant_id, tp.team_id
        FROM tournament_participants tp
        WHERE tp.tournament_id = @tournamentId
          AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
          AND (
              tp.user_id = @userId
              OR tp.team_captain_id = @userId
              OR (
                  tp.team_id IS NOT NULL
                  AND EXISTS (
                      SELECT 1
                      FROM team_members tm
                      WHERE tm.team_id = tp.team_id
                        AND tm.user_id = @userId
                        AND tm.is_active = TRUE
                  )
              )
          )
        """;

    public static async Task<PlayerGroupRow?> FindPlayerGroupAsync(
        IDbConnection conn,
        Guid tournamentId,
        Guid userId,
        IDbTransaction? tx = null)
    {
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            $"""
            WITH user_participation AS (
                {UserParticipationSql}
            )
            SELECT ts.id AS stage_id, ts.name AS stage_name, ts.stage_order,
                   g.id AS group_id, g.name AS group_name
            FROM user_participation up
            JOIN br_group_teams bgt ON (
                (bgt.participant_id IS NOT NULL AND bgt.participant_id = up.participant_id)
                OR (bgt.team_id IS NOT NULL AND up.team_id IS NOT NULL AND bgt.team_id = up.team_id)
            )
            JOIN br_groups g ON g.id = bgt.group_id
            JOIN tournament_stages ts ON ts.id = g.stage_id
            WHERE ts.tournament_id = @tournamentId
            ORDER BY ts.stage_order DESC, g.group_order ASC
            LIMIT 1
            """,
            new { tournamentId, userId },
            tx);

        if (row is null)
            return null;

        return new PlayerGroupRow(
            (Guid)row.stage_id,
            (string)row.stage_name,
            Convert.ToInt32(row.stage_order),
            (Guid)row.group_id,
            (string)row.group_name);
    }

    public static async Task<bool> IsUserAssignedToGroupAsync(
        IDbConnection conn,
        Guid tournamentId,
        Guid groupId,
        Guid userId,
        IDbTransaction? tx = null)
    {
        return await conn.QuerySingleAsync<bool>(
            $"""
            WITH user_participation AS (
                {UserParticipationSql}
            )
            SELECT EXISTS(
                SELECT 1
                FROM user_participation up
                JOIN br_group_teams bgt ON bgt.group_id = @groupId
                  AND (
                      (bgt.participant_id IS NOT NULL AND bgt.participant_id = up.participant_id)
                      OR (bgt.team_id IS NOT NULL AND up.team_id IS NOT NULL AND bgt.team_id = up.team_id)
                  )
            )
            """,
            new { tournamentId, groupId, userId },
            tx);
    }

    public static async Task<string> ResolveAssignmentHintAsync(
        IDbConnection conn,
        Guid tournamentId,
        Guid userId,
        IDbTransaction? tx = null)
    {
        var hasParticipation = await conn.ExecuteScalarAsync<bool>(
            $"""
            SELECT EXISTS(
                SELECT 1 FROM ({UserParticipationSql}) up
            )
            """,
            new { tournamentId, userId },
            tx);

        if (!hasParticipation)
            return "not_registered";

        var checkInRequired = await BrSeedEligibility.IsCheckInRequiredAsync(conn, tournamentId, tx);
        if (checkInRequired)
        {
            var isCheckedIn = await conn.ExecuteScalarAsync<bool>(
                $"""
                SELECT EXISTS(
                    SELECT 1
                    FROM ({UserParticipationSql}) up
                    JOIN tournament_participants tp ON tp.id = up.participant_id
                    WHERE tp.status::text = 'checked_in'
                       OR tp.checked_in_at IS NOT NULL
                )
                """,
                new { tournamentId, userId },
                tx);

            if (!isCheckedIn)
                return "check_in_required";
        }

        return "registered_not_seeded";
    }
}
