using System.Data;
using Dapper;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Services.ProviderMatches.Career;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Services.ScrimData;

/// <summary>SQL behind "Your scrims data". Only scrim maps count, and only ones both teams confirmed (or staff resolved).</summary>
public static class ScrimDataQueries
{
    public const int MapCap = 1000;
    private const string Counted = "('verified', 'partially_verified')";

    /// <summary>The team's roster members and admins may read the team's scrim data; nobody else.</summary>
    public static async Task<bool> CanViewAsync(IDbConnection conn, UserContext viewer, Guid teamId) =>
        viewer.AdminRoles.Length > 0 || await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM public.team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE)",
            new { teamId, userId = viewer.UserIdGuid });

    public static Task<bool> TeamExistsAsync(IDbConnection conn, Guid teamId) =>
        conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM public.teams WHERE id = @teamId)", new { teamId });

    /// <summary>Confirmed maps (or staff-resolved with a winner), newest first. Only OCR maps carry their scoreboard, and only its player rows: the stored match data can be megabytes.</summary>
    public static async Task<List<ScrimMapRow>> MapRowsAsync(IDbConnection conn, Guid teamId, int limit) =>
        (await conn.QueryAsync<ScrimMapRow>(
            """
            SELECT g.id AS GameId, g.session_id AS SessionId, g.game_number AS GameNumber, COALESCE(m.started_at, g.reported_at) AS PlayedAt,
                   g.source AS Source, g.map_name AS MapName, g.team1_score AS Team1Score, g.team2_score AS Team2Score,
                   g.winner_slot AS WinnerSlot, CASE WHEN s.team1_id = @teamId THEN 1 ELSE 2 END AS TeamSlot, ot.name AS OpponentName,
                   a.verification_status AS AssociationStatus, m.data_status AS MatchDataStatus,
                   CASE WHEN g.source = 'ocr' THEN jsonb_build_object('players', g.match_data -> 'players')::text END AS OcrMatchData,
                   m.is_custom AS IsCustom
            FROM public.scrim_session_games g
            JOIN public.scrim_sessions s ON s.id = g.session_id AND (s.team1_id = @teamId OR s.team2_id = @teamId)
            LEFT JOIN public.teams ot ON ot.id = CASE WHEN s.team1_id = @teamId THEN s.team2_id ELSE s.team1_id END
            LEFT JOIN public.provider_match_associations a ON a.context_type = 'scrim_game' AND a.context_id = g.id
            LEFT JOIN public.provider_matches m ON m.id = a.match_id
            WHERE g.status = 'confirmed' OR (g.status = 'resolved' AND g.winner_slot IS NOT NULL)
            ORDER BY COALESCE(m.started_at, g.reported_at) DESC, g.id
            LIMIT @limit
            """, new { teamId, limit })).ToList();

    /// <summary>Maps still waiting for the other team, and disputed ones: not counted yet, but worth saying so.</summary>
    public static async Task<(int Awaiting, int Disputed)> UncountedAsync(IDbConnection conn, Guid teamId) =>
        await conn.QuerySingleAsync<(int, int)>(
            """
            SELECT COUNT(*) FILTER (WHERE g.status = 'reported')::int, COUNT(*) FILTER (WHERE g.status = 'disputed')::int
            FROM public.scrim_session_games g
            JOIN public.scrim_sessions s ON s.id = g.session_id AND (s.team1_id = @teamId OR s.team2_id = @teamId)
            """, new { teamId });

    /// <summary>The team's side of each verified Riot game: agents, rounds and attack/defense rounds. A team's role in a round is the winner's role when it won, else the opposite.</summary>
    public static async Task<Dictionary<Guid, RiotGameRow>> RiotGamesAsync(IDbConnection conn, Guid teamId, Guid[] gameIds)
    {
        if (gameIds.Length == 0) return [];
        var rows = await conn.QueryAsync<RiotGameRow>(
            $"""
            WITH side AS (
                SELECT a.*, CASE WHEN a.team1_id = @teamId THEN a.team1_riot_side ELSE a.team2_riot_side END AS my_side
                FROM public.provider_match_associations a
                WHERE a.context_type = 'scrim_game' AND a.context_id = ANY(@gameIds)
                  AND (a.team1_id = @teamId OR a.team2_id = @teamId) AND a.verification_status IN {Counted}
            )
            SELECT x.context_id AS GameId, m.map_provider_id AS MapId, COALESCE(mine.rounds_played, 0) AS RoundsPlayed,
                   COALESCE(ag.agents, ARRAY[]::text[]) AS Agents,
                   COALESCE(rd.atk_played, 0) AS AttackPlayed, COALESCE(rd.atk_won, 0) AS AttackWon,
                   COALESCE(rd.def_played, 0) AS DefensePlayed, COALESCE(rd.def_won, 0) AS DefenseWon
            FROM side x
            JOIN public.provider_matches m ON m.id = x.match_id AND m.data_status = 'retrieved' AND m.is_completed = TRUE
            JOIN public.valorant_match_teams mine ON mine.match_id = m.id AND mine.team_id = x.my_side
            LEFT JOIN LATERAL (
                SELECT array_agg(lower(p.character_id) ORDER BY lower(p.character_id)) AS agents
                FROM public.valorant_match_players p
                WHERE p.match_id = m.id AND p.riot_side = x.my_side AND NOT p.is_observer AND p.character_id IS NOT NULL
            ) ag ON TRUE
            LEFT JOIN LATERAL (
                SELECT COUNT(*) FILTER (WHERE r.role = 'attacker')::int AS atk_played,
                       COUNT(*) FILTER (WHERE r.role = 'attacker' AND r.winning_team = x.my_side)::int AS atk_won,
                       COUNT(*) FILTER (WHERE r.role = 'defender')::int AS def_played,
                       COUNT(*) FILTER (WHERE r.role = 'defender' AND r.winning_team = x.my_side)::int AS def_won
                FROM (
                    SELECT rr.winning_team,
                           CASE WHEN rr.winning_team = x.my_side THEN lower(rr.winning_team_role)
                                WHEN lower(rr.winning_team_role) = 'attacker' THEN 'defender'
                                WHEN lower(rr.winning_team_role) = 'defender' THEN 'attacker' END AS role
                    FROM public.valorant_match_rounds rr WHERE rr.match_id = m.id
                ) r
            ) rd ON TRUE
            """, new { teamId, gameIds });
        return rows.ToDictionary(r => r.GameId);
    }

    /// <summary>Every player on the team's side of those Riot games, with their roster identity as frozen when the map was reported.</summary>
    public static async Task<List<RiotPlayerRow>> RiotPlayersAsync(IDbConnection conn, Guid teamId, Guid[] gameIds) =>
        gameIds.Length == 0 ? [] : (await conn.QueryAsync<RiotPlayerRow>(
            $"""
            SELECT a.context_id AS GameId, s.user_id AS UserId, COALESCE(pr.username, p.game_name) AS Name, p.character_id AS CharacterId,
                   s.member_role AS MemberRole, p.kills AS Kills, p.deaths AS Deaths, p.assists AS Assists, p.score AS Score,
                   p.rounds_played AS RoundsPlayed, p.damage_dealt AS DamageDealt, p.headshots AS Headshots, p.bodyshots AS Bodyshots,
                   p.legshots AS Legshots, p.first_kills AS FirstKills, p.first_deaths AS FirstDeaths, p.plants AS Plants, p.defuses AS Defuses
            FROM public.provider_match_associations a
            JOIN public.valorant_match_players p ON p.match_id = a.match_id AND NOT p.is_observer
                 AND p.riot_side = CASE WHEN a.team1_id = @teamId THEN a.team1_riot_side ELSE a.team2_riot_side END
            LEFT JOIN public.provider_match_roster_snapshots s ON s.association_id = a.id AND s.puuid = p.puuid AND s.user_id IS NOT NULL
            LEFT JOIN public.profiles pr ON pr.id = s.user_id
            WHERE a.context_type = 'scrim_game' AND a.context_id = ANY(@gameIds) AND (a.team1_id = @teamId OR a.team2_id = @teamId)
              AND a.verification_status IN {Counted}
            """, new { teamId, gameIds })).ToList();

    /// <summary>Usernames and current team roles, to name screenshot-matched players.</summary>
    public static async Task<Dictionary<Guid, (string? Username, string? Role)>> PeopleAsync(IDbConnection conn, Guid teamId, Guid[] userIds)
    {
        if (userIds.Length == 0) return [];
        var rows = await conn.QueryAsync<(Guid Id, string? Username, string? Role)>(
            """
            SELECT p.id, p.username, tm.role::text
            FROM public.profiles p
            LEFT JOIN public.team_members tm ON tm.user_id = p.id AND tm.team_id = @teamId AND tm.is_active = TRUE
            WHERE p.id = ANY(@userIds)
            """, new { teamId, userIds });
        return rows.ToDictionary(r => r.Id, r => (r.Username, r.Role));
    }

    /// <summary>Active Valorant maps in the catalog, limited to the competitive maps this project can identify.</summary>
    public static async Task<List<string>> PoolMapsAsync(IDbConnection conn)
    {
        var names = await conn.QueryAsync<string>("SELECT DISTINCT map_name FROM public.game_maps WHERE lower(game) = 'valorant' AND is_active = TRUE");
        var known = ProviderMatchVerifier.KnownMapNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Where(known.Contains).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }
}
