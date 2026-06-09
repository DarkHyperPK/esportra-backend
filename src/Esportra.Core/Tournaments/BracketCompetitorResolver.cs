using System.Data;
using Dapper;

namespace Esportra.Core.Tournaments;

/// <summary>
/// Resolves bracket slot IDs (stored in brkt_matches.team1_id / team2_id) to real teams,
/// solo participants, or mock participants; provides auth helpers for match actions.
/// </summary>
public static class BracketCompetitorResolver
{
    /// <summary>
    /// Bracket slot ID the user may act for in this match (participant id for solo, team id for teams).
    /// </summary>
    public static Task<Guid?> GetUserCompetitorIdInMatchAsync(
        IDbConnection conn,
        Guid userId,
        Guid matchId,
        IDbTransaction? tx = null)
        => conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT competitor_id FROM (
                SELECT tp.id AS competitor_id
                FROM brkt_matches bm
                JOIN tournament_participants tp
                  ON tp.id IN (bm.team1_id, bm.team2_id)
                 AND tp.user_id = @userId
                WHERE bm.id = @matchId
                UNION ALL
                SELECT tp.team_id
                FROM brkt_matches bm
                JOIN tournament_participants tp
                  ON tp.team_id IN (bm.team1_id, bm.team2_id)
                WHERE bm.id = @matchId
                  AND tp.team_id IS NOT NULL
                  AND (tp.user_id = @userId OR tp.team_captain_id = @userId)
                UNION ALL
                SELECT tm.team_id
                FROM brkt_matches bm
                JOIN team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
                WHERE bm.id = @matchId
                  AND tm.user_id = @userId
                  AND tm.role = 'captain'
                  AND tm.is_active = TRUE
            ) u
            LIMIT 1
            """,
            new { userId, matchId },
            tx);

    public static Task<bool> IsCompetitorInMatchAsync(
        IDbConnection conn,
        Guid matchId,
        Guid competitorId,
        IDbTransaction? tx = null)
        => conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM brkt_matches
                WHERE id = @matchId
                  AND (team1_id = @competitorId OR team2_id = @competitorId)
            )
            """,
            new { matchId, competitorId },
            tx);

    /// <summary>Captain for teams; solo participant owner for solo entries.</summary>
    public static Task<bool> CanUserReportForCompetitorAsync(
        IDbConnection conn,
        Guid userId,
        Guid matchId,
        Guid competitorId,
        IDbTransaction? tx = null)
        => conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM brkt_matches bm
                WHERE bm.id = @matchId
                  AND (bm.team1_id = @competitorId OR bm.team2_id = @competitorId)
                  AND (
                    EXISTS (
                        SELECT 1 FROM tournament_participants tp
                        WHERE tp.id = @competitorId
                          AND tp.participant_type = 'solo'
                          AND tp.user_id = @userId
                    )
                    OR EXISTS (
                        SELECT 1 FROM team_members tm
                        WHERE tm.team_id = @competitorId
                          AND tm.user_id = @userId
                          AND tm.role = 'captain'
                          AND tm.is_active = TRUE
                    )
                  )
            )
            """,
            new { userId, matchId, competitorId },
            tx);

    /// <summary>Non-coach roster member for teams; solo participant owner for solo entries.</summary>
    public static Task<bool> CanUserCheckInForCompetitorAsync(
        IDbConnection conn,
        Guid userId,
        Guid matchId,
        Guid competitorId,
        IDbTransaction? tx = null)
        => conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM brkt_matches bm
                WHERE bm.id = @matchId
                  AND (bm.team1_id = @competitorId OR bm.team2_id = @competitorId)
                  AND (
                    EXISTS (
                        SELECT 1 FROM tournament_participants tp
                        WHERE tp.id = @competitorId
                          AND tp.participant_type = 'solo'
                          AND tp.user_id = @userId
                    )
                    OR EXISTS (
                        SELECT 1 FROM team_members tm
                        WHERE tm.team_id = @competitorId
                          AND tm.user_id = @userId
                          AND tm.is_active = TRUE
                          AND tm.role != 'coach'
                    )
                  )
            )
            """,
            new { userId, matchId, competitorId },
            tx);

    public static Task<Guid?> GetPrimaryUserIdForCompetitorAsync(
        IDbConnection conn,
        Guid competitorId,
        IDbTransaction? tx = null)
        => conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT COALESCE(
                (SELECT user_id FROM tournament_participants
                 WHERE id = @competitorId AND participant_type = 'solo'
                 LIMIT 1),
                (SELECT user_id FROM team_members
                 WHERE team_id = @competitorId AND role = 'captain' AND is_active = TRUE
                 LIMIT 1)
            )
            """,
            new { competitorId },
            tx);

    public static Task<string?> GetCompetitorDisplayNameAsync(
        IDbConnection conn,
        Guid competitorId,
        IDbTransaction? tx = null)
        => conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT COALESCE(
                (SELECT name FROM teams WHERE id = @competitorId),
                (SELECT COALESCE(tp.team_name, p.username)
                 FROM tournament_participants tp
                 LEFT JOIN profiles p ON p.id = tp.user_id
                 WHERE tp.id = @competitorId
                 LIMIT 1)
            )
            """,
            new { competitorId },
            tx);

    /// <summary>Competitor slot ID for a user who proposed a match time.</summary>
    public static Task<string?> GetProposerCompetitorIdAsync(
        IDbConnection conn,
        Guid matchId,
        Guid proposalId,
        IDbTransaction? tx = null)
        => conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT COALESCE(
                (SELECT tp.id::text
                 FROM match_time_proposals mtp
                 JOIN brkt_matches bm ON bm.id = mtp.match_id
                 JOIN tournament_participants tp
                   ON tp.id IN (bm.team1_id, bm.team2_id)
                  AND tp.user_id = mtp.proposed_by
                  AND tp.participant_type = 'solo'
                 WHERE mtp.id = @proposalId AND mtp.match_id = @matchId
                 LIMIT 1),
                (SELECT tm.team_id::text
                 FROM match_time_proposals mtp
                 JOIN brkt_matches bm ON bm.id = mtp.match_id
                 JOIN team_members tm
                   ON tm.team_id IN (bm.team1_id, bm.team2_id)
                  AND tm.user_id = mtp.proposed_by
                  AND tm.role = 'captain'
                  AND tm.is_active = TRUE
                 WHERE mtp.id = @proposalId AND mtp.match_id = @matchId
                 LIMIT 1)
            )
            """,
            new { matchId, proposalId },
            tx);
}
