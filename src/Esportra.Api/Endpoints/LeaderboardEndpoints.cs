using Dapper;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Server-side leaderboard RP calculation.
/// AIO leaderboard: combines bracket match stats + BR tournament stats.
/// RP = (bracket_wins × 50) - (bracket_losses × 10) + (tournament_wins × 500) + (mvps × 25) + br_total_points
/// </summary>
public static class LeaderboardEndpoints
{
    public static void MapLeaderboardEndpoints(this WebApplication app)
    {
        // ── GET /api/leaderboards/teams ──────────────────────────────────────
        app.MapGet("/api/leaderboards/teams", async (
            IDbConnectionFactory db,
            [FromQuery] string?  game    = null,
            [FromQuery] string?  country = null,
            [FromQuery] int      limit   = 50,
            [FromQuery] int      offset  = 0,
            CancellationToken    ct      = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(game))
                conditions.Add("game ILIKE @game");
            if (!string.IsNullOrWhiteSpace(country))
                conditions.Add("country_code = @country");

            var filterWhere = conditions.Count > 0
                ? "AND " + string.Join(" AND ", conditions)
                : "";

            var teams = await conn.QueryAsync<dynamic>(
                $"""
                WITH bracket_stats AS (
                    SELECT
                        t.id AS team_id,
                        COALESCE(SUM(CASE WHEN m.winner_id = t.id THEN 1 ELSE 0 END), 0) AS wins,
                        COALESCE(SUM(CASE
                            WHEN m.status = 'completed' AND m.winner_id IS NOT NULL AND m.winner_id != t.id
                            THEN 1 ELSE 0
                        END), 0) AS losses
                    FROM teams t
                    LEFT JOIN brkt_matches m ON (m.team1_id = t.id OR m.team2_id = t.id) AND m.status = 'completed'
                    GROUP BY t.id
                ),
                br_team_stats AS (
                    SELECT
                        (r->>'teamId')::uuid AS team_id,
                        COALESCE(SUM((r->>'totalPoints')::int), 0) AS br_points,
                        COALESCE(SUM((r->>'kills')::int), 0) AS br_kills,
                        COUNT(*) AS br_games,
                        SUM(CASE WHEN (r->>'placement')::int = 1 THEN 1 ELSE 0 END) AS br_first_places
                    FROM br_game_data bgd
                    CROSS JOIN LATERAL jsonb_each(bgd.games) AS ge(gn, gd)
                    CROSS JOIN LATERAL jsonb_array_elements(gd->'results') AS r
                    WHERE gd->>'status' = 'completed'
                      AND (r->>'teamId') IS NOT NULL
                    GROUP BY (r->>'teamId')::uuid
                ),
                tournament_win_counts AS (
                    SELECT winner_id AS team_id, COUNT(*) AS t_wins
                    FROM tournaments
                    WHERE status = 'completed' AND winner_id IS NOT NULL
                    GROUP BY winner_id
                ),
                combined AS (
                    SELECT
                        t.id, t.name, t.logo_url, t.game, t.country_code,
                        COALESCE(bs.wins, 0) AS wins,
                        COALESCE(bs.losses, 0) AS losses,
                        (COALESCE(bs.wins, 0) + COALESCE(bs.losses, 0)) AS matches_played,
                        CASE WHEN (COALESCE(bs.wins, 0) + COALESCE(bs.losses, 0)) > 0
                             THEN ROUND(COALESCE(bs.wins, 0) * 100.0 / (COALESCE(bs.wins, 0) + COALESCE(bs.losses, 0)), 1)
                             ELSE 0 END AS win_rate,
                        COALESCE(twc.t_wins, 0) AS tournaments_won,
                        COALESCE(brs.br_points, 0) AS br_total_points,
                        COALESCE(brs.br_kills, 0) AS br_total_kills,
                        COALESCE(brs.br_games, 0) AS br_games_played,
                        COALESCE(brs.br_first_places, 0) AS br_first_places,
                        (COALESCE(bs.wins, 0) * 50
                         + COALESCE(twc.t_wins, 0) * 500
                         - COALESCE(bs.losses, 0) * 10
                         + COALESCE(brs.br_points, 0)) AS rp
                    FROM teams t
                    LEFT JOIN bracket_stats bs ON bs.team_id = t.id
                    LEFT JOIN br_team_stats brs ON brs.team_id = t.id
                    LEFT JOIN tournament_win_counts twc ON twc.team_id = t.id
                )
                SELECT * FROM combined
                WHERE (matches_played + br_games_played) > 0
                  {filterWhere}
                ORDER BY rp DESC, wins DESC
                LIMIT @limit OFFSET @offset
                """,
                new { game, country, limit, offset });

            return Results.Ok(teams);
        });

        // ── GET /api/leaderboards/players ────────────────────────────────────
        app.MapGet("/api/leaderboards/players", async (
            IDbConnectionFactory db,
            [FromQuery] string?  game    = null,
            [FromQuery] string?  country = null,
            [FromQuery] int      limit   = 50,
            [FromQuery] int      offset  = 0,
            CancellationToken    ct      = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(game))
                conditions.Add("game ILIKE @game");
            if (!string.IsNullOrWhiteSpace(country))
                conditions.Add("country_code = @country");

            var filterWhere = conditions.Count > 0
                ? "AND " + string.Join(" AND ", conditions)
                : "";

            var players = await conn.QueryAsync<dynamic>(
                $"""
                WITH player_bracket_stats AS (
                    SELECT
                        p.id AS player_id,
                        p.username,
                        p.avatar_url,
                        p.country_code AS player_country,
                        t.id AS team_id,
                        t.name AS team_name,
                        t.game,
                        COALESCE(SUM(CASE WHEN m.winner_id = t.id THEN 1 ELSE 0 END), 0) AS wins,
                        COALESCE(SUM(CASE
                            WHEN m.status = 'completed' AND m.winner_id IS NOT NULL AND m.winner_id != t.id
                            THEN 1 ELSE 0
                        END), 0) AS losses,
                        COALESCE((
                            SELECT COUNT(*) FROM tournaments tr WHERE tr.winner_id = t.id AND tr.status = 'completed'
                        ), 0) AS tournament_wins
                    FROM profiles p
                    INNER JOIN team_members tm ON tm.user_id = p.id AND tm.is_active = TRUE
                    INNER JOIN teams t ON t.id = tm.team_id
                    LEFT JOIN brkt_matches m ON (m.team1_id = t.id OR m.team2_id = t.id) AND m.status = 'completed'
                    GROUP BY p.id, p.username, p.avatar_url, p.country_code, t.id, t.name, t.game
                ),
                br_player_results AS (
                    SELECT
                        (r->>'teamId')::uuid AS team_id,
                        COALESCE((r->>'totalPoints')::int, 0) AS total_points,
                        COALESCE((r->>'kills')::int, 0) AS kills,
                        CASE WHEN (r->>'placement')::int = 1 THEN 1 ELSE 0 END AS is_first
                    FROM br_game_data bgd
                    CROSS JOIN LATERAL jsonb_each(bgd.games) AS ge(gn, gd)
                    CROSS JOIN LATERAL jsonb_array_elements(gd->'results') AS r
                    WHERE gd->>'status' = 'completed'
                      AND (r->>'teamId') IS NOT NULL
                ),
                player_br_stats AS (
                    SELECT
                        tm.user_id AS player_id,
                        SUM(bpr.total_points) AS br_points,
                        SUM(bpr.kills) AS br_kills,
                        COUNT(*) AS br_games,
                        SUM(bpr.is_first) AS br_first_places
                    FROM team_members tm
                    INNER JOIN br_player_results bpr ON bpr.team_id = tm.team_id
                    WHERE tm.is_active = TRUE
                    GROUP BY tm.user_id
                ),
                mvp_counts AS (
                    SELECT mvp_id AS player_id, COUNT(*) AS mvp_awards
                    FROM brkt_match_games
                    WHERE mvp_id IS NOT NULL
                    GROUP BY mvp_id
                ),
                combined AS (
                    SELECT
                        pbs.player_id AS id,
                        pbs.username,
                        pbs.avatar_url,
                        pbs.player_country AS country_code,
                        pbs.team_name,
                        pbs.game,
                        pbs.wins,
                        pbs.losses,
                        (pbs.wins + pbs.losses) AS matches_played,
                        CASE WHEN (pbs.wins + pbs.losses) > 0
                             THEN ROUND(pbs.wins * 100.0 / (pbs.wins + pbs.losses), 1)
                             ELSE 0 END AS win_rate,
                        pbs.tournament_wins AS tournaments_won,
                        COALESCE(mc.mvp_awards, 0) AS mvps,
                        COALESCE(pbrs.br_points, 0) AS br_total_points,
                        COALESCE(pbrs.br_kills, 0) AS br_total_kills,
                        COALESCE(pbrs.br_games, 0) AS br_games_played,
                        COALESCE(pbrs.br_first_places, 0) AS br_first_places,
                        (pbs.wins * 50
                         + pbs.tournament_wins * 500
                         - pbs.losses * 10
                         + COALESCE(mc.mvp_awards, 0) * 25
                         + COALESCE(pbrs.br_points, 0)) AS rp
                    FROM player_bracket_stats pbs
                    LEFT JOIN player_br_stats pbrs ON pbrs.player_id = pbs.player_id
                    LEFT JOIN mvp_counts mc ON mc.player_id = pbs.player_id
                )
                SELECT * FROM combined
                WHERE (matches_played + br_games_played) > 0
                  {filterWhere}
                ORDER BY rp DESC, wins DESC
                LIMIT @limit OFFSET @offset
                """,
                new { game, country, limit, offset });

            return Results.Ok(players);
        });

        // ── GET /api/leaderboards/filters ────────────────────────────────────
        app.MapGet("/api/leaderboards/filters", async (
            IDbConnectionFactory db,
            CancellationToken ct = default) =>
        {
            using var conn = db.CreateConnection();

            var games = (await conn.QueryAsync<string>(
                """
                SELECT DISTINCT game FROM (
                    SELECT DISTINCT ON (LOWER(t.game)) t.game
                    FROM teams t
                    INNER JOIN brkt_matches m ON (m.team1_id = t.id OR m.team2_id = t.id) AND m.status = 'completed'
                    WHERE t.game IS NOT NULL
                    UNION
                    SELECT DISTINCT ON (LOWER(tn.game)) tn.game
                    FROM tournaments tn
                    WHERE tn.game IS NOT NULL AND tn.status IN ('ongoing', 'completed')
                ) AS all_games
                ORDER BY game
                """)).AsList();

            var countries = (await conn.QueryAsync<string>(
                """
                SELECT DISTINCT country_code
                FROM (
                    SELECT t.country_code FROM teams t
                    INNER JOIN brkt_matches m ON (m.team1_id = t.id OR m.team2_id = t.id) AND m.status = 'completed'
                    WHERE t.country_code IS NOT NULL AND t.country_code <> ''
                    UNION
                    SELECT p.country_code FROM profiles p
                    INNER JOIN team_members tm ON tm.user_id = p.id AND tm.is_active = TRUE
                    INNER JOIN teams t ON t.id = tm.team_id
                    INNER JOIN brkt_matches m ON (m.team1_id = t.id OR m.team2_id = t.id) AND m.status = 'completed'
                    WHERE p.country_code IS NOT NULL AND p.country_code <> ''
                    UNION
                    SELECT t.country_code FROM teams t
                    INNER JOIN tournament_participants tp ON tp.team_id = t.id
                    INNER JOIN tournaments tn ON tn.id = tp.tournament_id AND tn.status IN ('ongoing', 'completed')
                    WHERE t.country_code IS NOT NULL AND t.country_code <> ''
                ) AS active_countries
                ORDER BY country_code
                """)).AsList();

            return Results.Ok(new { games, countries });
        });
    }
}
