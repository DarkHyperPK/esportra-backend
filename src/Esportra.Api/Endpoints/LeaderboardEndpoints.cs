using Dapper;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Server-side leaderboard RP calculation.
/// Replaces Leaderboards.tsx client-side computation that fetches ALL teams/matches/players.
/// Prevents browser OOM at scale.
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
                conditions.Add("t.game ILIKE @game");
            if (!string.IsNullOrWhiteSpace(country))
                conditions.Add("t.country_code = @country");

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            // RP formula: (wins * 50) + (tournament_wins * 500) - (losses * 10)
            var teams = await conn.QueryAsync<dynamic>(
                $"""
                WITH team_stats AS (
                    SELECT
                        t.id,
                        t.name,
                        t.logo_url,
                        t.game,
                        t.country_code,
                        COALESCE(SUM(CASE WHEN m.winner_id = t.id THEN 1 ELSE 0 END), 0) AS wins,
                        COALESCE(SUM(CASE
                            WHEN m.status = 'completed' AND m.winner_id IS NOT NULL AND m.winner_id != t.id
                            THEN 1 ELSE 0
                        END), 0) AS losses,
                        COALESCE((
                            SELECT COUNT(*) FROM tournaments tr
                            WHERE tr.winner_id = t.id AND tr.status = 'completed'
                        ), 0) AS tournament_wins
                    FROM teams t
                    LEFT JOIN brkt_matches m ON (m.team1_id = t.id OR m.team2_id = t.id) AND m.status = 'completed'
                    {where}
                    GROUP BY t.id, t.name, t.logo_url, t.game, t.country_code
                )
                SELECT
                    id, name, logo_url, game, country_code,
                    wins, losses,
                    (wins + losses) AS matches_played,
                    CASE WHEN (wins + losses) > 0
                         THEN ROUND(wins * 100.0 / (wins + losses), 1)
                         ELSE 0 END AS win_rate,
                    tournament_wins AS tournaments_won,
                    (wins * 50 + tournament_wins * 500 - losses * 10) AS rp
                FROM team_stats
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
                conditions.Add("t.game ILIKE @game");
            if (!string.IsNullOrWhiteSpace(country))
                conditions.Add("p.country_code = @country");

            var where = conditions.Count > 0
                ? "AND " + string.Join(" AND ", conditions)
                : "";

            // Player RP = team-contributed RP + individual MVP bonus
            var players = await conn.QueryAsync<dynamic>(
                $"""
                WITH player_team_stats AS (
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
                    WHERE TRUE {where}
                    GROUP BY p.id, p.username, p.avatar_url, p.country_code, t.id, t.name, t.game
                ),
                mvp_counts AS (
                    SELECT mvp_id AS player_id, COUNT(*) AS mvp_awards
                    FROM brkt_match_games
                    WHERE mvp_id IS NOT NULL
                    GROUP BY mvp_id
                )
                SELECT
                    pts.player_id AS id,
                    pts.username,
                    pts.avatar_url,
                    pts.player_country AS country_code,
                    pts.team_name,
                    pts.game,
                    pts.wins,
                    pts.losses,
                    (pts.wins + pts.losses) AS matches_played,
                    CASE WHEN (pts.wins + pts.losses) > 0
                         THEN ROUND(pts.wins * 100.0 / (pts.wins + pts.losses), 1)
                         ELSE 0 END AS win_rate,
                    pts.tournament_wins AS tournaments_won,
                    COALESCE(mc.mvp_awards, 0) AS mvps,
                    (pts.wins * 50 + pts.tournament_wins * 500 - pts.losses * 10 + COALESCE(mc.mvp_awards, 0) * 25) AS rp
                FROM player_team_stats pts
                LEFT JOIN mvp_counts mc ON mc.player_id = pts.player_id
                ORDER BY rp DESC, wins DESC
                LIMIT @limit OFFSET @offset
                """,
                new { game, country, limit, offset });

            return Results.Ok(players);
        });

        // ── GET /api/leaderboards/filters ────────────────────────────────────
        // Returns only games / country codes that have actual leaderboard entries.
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
                ) AS active_countries
                ORDER BY country_code
                """)).AsList();

            return Results.Ok(new { games, countries });
        });
    }
}
