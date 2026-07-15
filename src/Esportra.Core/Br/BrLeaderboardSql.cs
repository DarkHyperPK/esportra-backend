namespace Esportra.Core.Br;

/// <summary>
/// Standings SQL — aggregates persisted br_lobby_results. Game lifecycle status does not gate standings.
/// </summary>
public static class BrLeaderboardSql
{
    public const string GroupParticipant = """
        SELECT
            COALESCE(rr.team_id, rr.participant_id) AS team_id,
            CASE
                WHEN rr.team_id IS NOT NULL THEN t.name
                ELSE COALESCE(p.username, tp.team_name, t.name, 'Unknown')
            END AS team_name,
            CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
            COUNT(DISTINCT rr.game_id) AS games_played,
            SUM(rr.placement_points) AS total_placement_points,
            SUM(rr.kill_points) AS total_kill_points,
            SUM(rr.total_points) AS total_points,
            SUM(rr.kills) AS total_kills,
            COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
            MIN(rr.placement) AS best_placement,
            AVG(rr.placement::numeric) AS avg_placement
        FROM br_lobby_results rr
        JOIN br_games g ON g.id = rr.game_id
        JOIN br_lobbies r ON r.id = g.lobby_id
        LEFT JOIN teams t ON t.id = rr.team_id
        LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
        LEFT JOIN profiles p ON p.id = tp.user_id
        WHERE EXISTS (
            SELECT 1 FROM br_lobby_groups lg
            WHERE lg.lobby_id = r.id AND lg.group_id = @groupId
        )
        GROUP BY COALESCE(rr.team_id, rr.participant_id),
                 CASE
                     WHEN rr.team_id IS NOT NULL THEN t.name
                     ELSE COALESCE(p.username, tp.team_name, t.name, 'Unknown')
                 END,
                 CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END
        """;

    public const string GroupTeamOnly = """
        SELECT
            rr.team_id AS team_id,
            t.name AS team_name,
            t.logo_url AS logo_url,
            COUNT(DISTINCT rr.game_id) AS games_played,
            SUM(rr.placement_points) AS total_placement_points,
            SUM(rr.kill_points) AS total_kill_points,
            SUM(rr.total_points) AS total_points,
            SUM(rr.kills) AS total_kills,
            COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
            MIN(rr.placement) AS best_placement,
            AVG(rr.placement::numeric) AS avg_placement
        FROM br_lobby_results rr
        JOIN br_games g ON g.id = rr.game_id
        JOIN br_lobbies r ON r.id = g.lobby_id
        LEFT JOIN teams t ON t.id = rr.team_id
        WHERE EXISTS (
            SELECT 1 FROM br_lobby_groups lg
            WHERE lg.lobby_id = r.id AND lg.group_id = @groupId
        )
        GROUP BY rr.team_id, t.name, t.logo_url
        """;

    public const string StageGlobal = """
        SELECT
            COALESCE(rr.team_id, rr.participant_id) AS team_id,
            CASE
                WHEN rr.team_id IS NOT NULL THEN t.name
                ELSE COALESCE(p.username, tp.team_name, t.name, 'Unknown')
            END AS team_name,
            CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
            COUNT(DISTINCT rr.game_id) AS games_played,
            SUM(rr.placement_points) AS total_placement_points,
            SUM(rr.kill_points) AS total_kill_points,
            SUM(rr.total_points) AS total_points,
            SUM(rr.kills) AS total_kills,
            COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
            MIN(rr.placement) AS best_placement,
            AVG(rr.placement::numeric) AS avg_placement
        FROM br_lobby_results rr
        JOIN br_games g ON g.id = rr.game_id
        JOIN br_lobbies l ON l.id = g.lobby_id
        LEFT JOIN teams t ON t.id = rr.team_id
        LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
        LEFT JOIN profiles p ON p.id = tp.user_id
        WHERE l.stage_id = @stageId
        GROUP BY COALESCE(rr.team_id, rr.participant_id),
                 CASE
                     WHEN rr.team_id IS NOT NULL THEN t.name
                     ELSE COALESCE(p.username, tp.team_name, t.name, 'Unknown')
                 END,
                 CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END
        """;
}
