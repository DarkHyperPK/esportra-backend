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
            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(game))
                conditions.Add("t.game ILIKE @game");
            if (!string.IsNullOrWhiteSpace(country))
                conditions.Add("t.country_code = @country");

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            // RP formula: (wins * 3) + (tournament_wins * 10) - (losses * 1)
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
                    wins, losses, tournament_wins,
                    (wins * 3 + tournament_wins * 10 - losses) AS rp
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
                    pts.tournament_wins,
                    COALESCE(mc.mvp_awards, 0) AS mvp_awards,
                    (pts.wins * 3 + pts.tournament_wins * 10 - pts.losses + COALESCE(mc.mvp_awards, 0) * 2) AS rp
                FROM player_team_stats pts
                LEFT JOIN mvp_counts mc ON mc.player_id = pts.player_id
                ORDER BY rp DESC, wins DESC
                LIMIT @limit OFFSET @offset
                """,
                new { game, country, limit, offset });

            return Results.Ok(players);
        });
    }
}
