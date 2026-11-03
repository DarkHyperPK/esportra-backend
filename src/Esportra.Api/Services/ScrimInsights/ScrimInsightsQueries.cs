using System.Data;
using Dapper;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Services.ScrimInsights;

/// <summary>Reads for the per-map report. Every query is parameterized and scoped to one scrim map's Riot match.</summary>
public static class ScrimInsightsQueries
{
    public sealed record GameRef(
        Guid GameId, Guid Team1Id, Guid Team2Id, string Team1Name, string Team2Name, string Source, string? MapName,
        int Team1Score, int Team2Score, int? WinnerSlot, string Status);

    public sealed record MatchHead(
        Guid AssociationId, Guid MatchId, string Verification, string DetailJson, string? Team1Side, string? Team2Side,
        string DataStatus, bool? IsCompleted, string? MapId, string? QueueId, bool? IsCustom, DateTime? StartedAt, int? LengthMs);

    public static Task<GameRef?> GameAsync(IDbConnection conn, Guid sessionId, int gameNumber) =>
        conn.QuerySingleOrDefaultAsync<GameRef>(
            """
            SELECT g.id AS GameId, s.team1_id AS Team1Id, s.team2_id AS Team2Id, t1.name AS Team1Name, t2.name AS Team2Name,
                   g.source AS Source, g.map_name AS MapName, g.team1_score AS Team1Score, g.team2_score AS Team2Score,
                   g.winner_slot AS WinnerSlot, g.status AS Status
            FROM public.scrim_session_games g
            JOIN public.scrim_sessions s ON s.id = g.session_id
            JOIN public.teams t1 ON t1.id = s.team1_id
            JOIN public.teams t2 ON t2.id = s.team2_id
            WHERE g.session_id = @sessionId AND g.game_number = @gameNumber
            """, new { sessionId, gameNumber });

    /// <summary>Teams of the scrim the viewer is an active member of (admins see the scrim from team 1's side unless they pick).</summary>
    public static async Task<Guid[]> MemberTeamsAsync(IDbConnection conn, Guid userId, Guid team1, Guid team2) =>
        (await conn.QueryAsync<Guid>(
            "SELECT DISTINCT team_id FROM public.team_members WHERE user_id = @userId AND is_active = TRUE AND team_id IN (@team1, @team2)",
            new { userId, team1, team2 })).ToArray();

    public static Task<MatchHead?> HeadAsync(IDbConnection conn, Guid gameId) =>
        conn.QuerySingleOrDefaultAsync<MatchHead>(
            """
            SELECT a.id AS AssociationId, a.match_id AS MatchId, a.verification_status AS Verification, a.verification_detail::text AS DetailJson,
                   a.team1_riot_side AS Team1Side, a.team2_riot_side AS Team2Side, m.data_status AS DataStatus, m.is_completed AS IsCompleted,
                   m.map_provider_id AS MapId, m.queue_id AS QueueId, m.is_custom AS IsCustom, m.started_at AS StartedAt, m.length_ms AS LengthMs
            FROM public.provider_match_associations a JOIN public.provider_matches m ON m.id = a.match_id
            WHERE a.context_type = 'scrim_game' AND a.context_id = @gameId
            """, new { gameId });

    public static async Task<InMatch> LoadMatchAsync(IDbConnection conn, MatchHead head, string mySide)
    {
        var matchId = head.MatchId;
        var players = (await conn.QueryAsync<InPlayer>(
            """
            SELECT p.puuid AS Puuid, p.riot_side AS Side, p.game_name AS Name, p.tag_line AS Tag, p.character_id AS Agent, s.team_slot::int AS Slot,
                   p.score AS Score, p.rounds_played AS Rounds, p.kills AS Kills, p.deaths AS Deaths, p.assists AS Assists,
                   p.damage_dealt AS Damage, p.headshots AS Head, p.bodyshots AS Body, p.legshots AS Leg,
                   p.first_kills AS FirstKills, p.first_deaths AS FirstDeaths, p.kills_2k AS K2, p.kills_3k AS K3, p.kills_4k AS K4, p.kills_5k AS K5,
                   p.plants AS Plants, p.defuses AS Defuses, p.grenade_casts AS Grenade, p.ability1_casts AS Ability1,
                   p.ability2_casts AS Ability2, p.ultimate_casts AS Ultimate
            FROM public.valorant_match_players p
            LEFT JOIN public.provider_match_roster_snapshots s ON s.association_id = @assoc AND s.puuid = p.puuid
            WHERE p.match_id = @matchId AND NOT p.is_observer
            """, new { assoc = head.AssociationId, matchId })).ToList();
        var rounds = (await conn.QueryAsync<InRound>(
            """
            SELECT round_num AS Num, winning_team AS WinnerSide, winning_team_role AS WinnerRole, round_result_code AS ResultCode,
                   bomb_planter AS Planter, bomb_defuser AS Defuser, plant_round_time_ms AS PlantMs, plant_site AS Site,
                   plant_x AS PlantX, plant_y AS PlantY, defuse_round_time_ms AS DefuseMs, defuse_x AS DefuseX, defuse_y AS DefuseY
            FROM public.valorant_match_rounds WHERE match_id = @matchId ORDER BY round_num
            """, new { matchId })).ToList();
        var kills = (await conn.QueryAsync<InKill>(
            """
            SELECT round_num AS Round, kill_ordinal AS Ordinal, killer AS Killer, victim AS Victim, assistants AS Assistants,
                   time_since_round_ms AS RoundMs, victim_x AS X, victim_y AS Y, damage_item AS Item, damage_type AS Type
            FROM public.valorant_match_kills WHERE match_id = @matchId ORDER BY round_num, kill_ordinal
            """, new { matchId })).ToList();
        var economy = (await conn.QueryAsync<InEcon>(
            """
            SELECT round_num AS Round, puuid AS Puuid, loadout_value AS Loadout, weapon AS Weapon, armor AS Armor, spent AS Spent, remaining AS Remaining
            FROM public.valorant_round_player_stats WHERE match_id = @matchId
            """, new { matchId })).ToList();
        var damage = (await conn.QueryAsync<InDamage>(
            """
            SELECT round_num AS Round, puuid AS "From", receiver AS "To", damage AS Damage, headshots AS Head, bodyshots AS Body, legshots AS Leg
            FROM public.valorant_round_damage WHERE match_id = @matchId
            """, new { matchId })).ToList();
        return new InMatch(mySide, players, rounds, kills, economy, damage);
    }
}
