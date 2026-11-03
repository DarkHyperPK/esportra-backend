using System.Data;
using Dapper;
using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

/// <summary>Reads for the team and player reports: the team's recent verified maps, and each map's Riot record, six round trips in all.</summary>
public static class ScrimReportQueries
{
    public const int MapCap = 30;

    public sealed record Head(
        Guid GameId, Guid AssociationId, Guid MatchId, int MySlot, string MySide, DateTime? StartedAt, string? MapId, string? MapName,
        int Team1Score, int Team2Score, int? WinnerSlot, Guid OpponentId, string OpponentName);

    private sealed record PlayerM(
        Guid MatchId, string Puuid, string Side, string? Name, string? Tag, string? Agent, int? Slot, int Score, int Rounds, int Kills, int Deaths, int Assists,
        int Damage, int Head, int Body, int Leg, int FirstKills, int FirstDeaths, int K2, int K3, int K4, int K5, int Plants, int Defuses,
        int? Grenade, int? Ability1, int? Ability2, int? Ultimate)
    {
        public InPlayer ToIn() => new(Puuid, Side, Name, Tag, Agent, Slot, Score, Rounds, Kills, Deaths, Assists, Damage, Head, Body, Leg, FirstKills, FirstDeaths, K2, K3, K4, K5, Plants, Defuses, Grenade, Ability1, Ability2, Ultimate);
    }

    private sealed record RoundM(Guid MatchId, int Num, string? WinnerSide, string? WinnerRole, string? ResultCode, string? Planter, string? Defuser, int? PlantMs, string? Site, int? PlantX, int? PlantY, int? DefuseMs, int? DefuseX, int? DefuseY)
    {
        public InRound ToIn() => new(Num, WinnerSide, WinnerRole, ResultCode, Planter, Defuser, PlantMs, Site, PlantX, PlantY, DefuseMs, DefuseX, DefuseY);
    }

    private sealed record KillM(Guid MatchId, int Round, int Ordinal, string? Killer, string Victim, string[] Assistants, int? RoundMs, int? X, int? Y, string? Item, string? Type)
    {
        public InKill ToIn() => new(Round, Ordinal, Killer, Victim, Assistants, RoundMs, X, Y, Item, Type);
    }

    private sealed record EconM(Guid MatchId, int Round, string Puuid, int? Loadout, string? Weapon, string? Armor, int? Spent, int? Remaining)
    {
        public InEcon ToIn() => new(Round, Puuid, Loadout, Weapon, Armor, Spent, Remaining);
    }

    private sealed record DamageM(Guid MatchId, int Round, string From, string To, int Damage, int Head, int Body, int Leg)
    {
        public InDamage ToIn() => new(Round, From, To, Damage, Head, Body, Leg);
    }

    private sealed record UserM(Guid AssociationId, string Puuid, Guid UserId, string? Name);

    /// <summary>The team's most recent verified, confirmed Riot maps, newest first, one more than the cap so the caller can say whether older maps were left out.</summary>
    public static async Task<List<Head>> HeadsAsync(IDbConnection conn, Guid teamId) =>
        (await conn.QueryAsync<Head>(
            """
            SELECT g.id AS GameId, a.id AS AssociationId, a.match_id AS MatchId, CASE WHEN a.team1_id = @teamId THEN 1 ELSE 2 END AS MySlot,
                   CASE WHEN a.team1_id = @teamId THEN a.team1_riot_side ELSE a.team2_riot_side END AS MySide,
                   m.started_at AS StartedAt, m.map_provider_id AS MapId, g.map_name AS MapName, g.team1_score AS Team1Score, g.team2_score AS Team2Score,
                   g.winner_slot AS WinnerSlot, CASE WHEN a.team1_id = @teamId THEN a.team2_id ELSE a.team1_id END AS OpponentId, ot.name AS OpponentName
            FROM public.provider_match_associations a
            JOIN public.scrim_session_games g ON g.id = a.context_id
            JOIN public.provider_matches m ON m.id = a.match_id AND m.data_status = 'retrieved' AND m.is_completed = TRUE
            JOIN public.teams ot ON ot.id = CASE WHEN a.team1_id = @teamId THEN a.team2_id ELSE a.team1_id END
            WHERE a.context_type = 'scrim_game' AND (a.team1_id = @teamId OR a.team2_id = @teamId)
              AND a.verification_status IN ('verified', 'partially_verified') AND a.team1_riot_side IS NOT NULL AND a.team2_riot_side IS NOT NULL
              AND (g.status = 'confirmed' OR (g.status = 'resolved' AND g.winner_slot IS NOT NULL))
            ORDER BY m.started_at DESC NULLS LAST, g.id
            LIMIT @limit
            """, new { teamId, limit = MapCap + 1 })).ToList();

    /// <summary>Every match's Riot record at once, grouped by match, plus which roster user each of the team's Riot players was.</summary>
    public static async Task<(Dictionary<Guid, InMatch> Matches, Dictionary<Guid, Dictionary<string, RosterUser>> Users)> LoadAsync(IDbConnection conn, IReadOnlyList<Head> heads)
    {
        var matchIds = heads.Select(h => h.MatchId).Distinct().ToArray();
        var assocIds = heads.Select(h => h.AssociationId).ToArray();
        var players = (await conn.QueryAsync<PlayerM>(
            """
            SELECT p.match_id AS MatchId, p.puuid AS Puuid, p.riot_side AS Side, p.game_name AS Name, p.tag_line AS Tag, p.character_id AS Agent, NULL::int AS Slot,
                   p.score AS Score, p.rounds_played AS Rounds, p.kills AS Kills, p.deaths AS Deaths, p.assists AS Assists, p.damage_dealt AS Damage,
                   p.headshots AS Head, p.bodyshots AS Body, p.legshots AS Leg, p.first_kills AS FirstKills, p.first_deaths AS FirstDeaths,
                   p.kills_2k AS K2, p.kills_3k AS K3, p.kills_4k AS K4, p.kills_5k AS K5, p.plants AS Plants, p.defuses AS Defuses,
                   p.grenade_casts AS Grenade, p.ability1_casts AS Ability1, p.ability2_casts AS Ability2, p.ultimate_casts AS Ultimate
            FROM public.valorant_match_players p WHERE p.match_id = ANY(@matchIds) AND NOT p.is_observer
            """, new { matchIds })).ToList();
        var rounds = (await conn.QueryAsync<RoundM>(
            """
            SELECT match_id AS MatchId, round_num AS Num, winning_team AS WinnerSide, winning_team_role AS WinnerRole, round_result_code AS ResultCode,
                   bomb_planter AS Planter, bomb_defuser AS Defuser, plant_round_time_ms AS PlantMs, plant_site AS Site, plant_x AS PlantX, plant_y AS PlantY,
                   defuse_round_time_ms AS DefuseMs, defuse_x AS DefuseX, defuse_y AS DefuseY
            FROM public.valorant_match_rounds WHERE match_id = ANY(@matchIds) ORDER BY match_id, round_num
            """, new { matchIds })).ToList();
        var kills = (await conn.QueryAsync<KillM>(
            """
            SELECT match_id AS MatchId, round_num AS Round, kill_ordinal AS Ordinal, killer AS Killer, victim AS Victim, assistants AS Assistants,
                   time_since_round_ms AS RoundMs, victim_x AS X, victim_y AS Y, damage_item AS Item, damage_type AS Type
            FROM public.valorant_match_kills WHERE match_id = ANY(@matchIds) ORDER BY match_id, round_num, kill_ordinal
            """, new { matchIds })).ToList();
        var econ = (await conn.QueryAsync<EconM>(
            """
            SELECT match_id AS MatchId, round_num AS Round, puuid AS Puuid, loadout_value AS Loadout, weapon AS Weapon, armor AS Armor, spent AS Spent, remaining AS Remaining
            FROM public.valorant_round_player_stats WHERE match_id = ANY(@matchIds)
            """, new { matchIds })).ToList();
        var damage = (await conn.QueryAsync<DamageM>(
            """
            SELECT match_id AS MatchId, round_num AS Round, puuid AS "From", receiver AS "To", damage AS Damage, headshots AS Head, bodyshots AS Body, legshots AS Leg
            FROM public.valorant_round_damage WHERE match_id = ANY(@matchIds)
            """, new { matchIds })).ToList();
        var users = (await conn.QueryAsync<UserM>(
            """
            SELECT s.association_id AS AssociationId, s.puuid AS Puuid, s.user_id AS UserId, pr.username AS Name
            FROM public.provider_match_roster_snapshots s LEFT JOIN public.profiles pr ON pr.id = s.user_id
            WHERE s.association_id = ANY(@assocIds) AND s.user_id IS NOT NULL AND s.puuid IS NOT NULL
            """, new { assocIds })).ToList();

        var pl = players.ToLookup(p => p.MatchId); var ro = rounds.ToLookup(r => r.MatchId); var ki = kills.ToLookup(k => k.MatchId);
        var ec = econ.ToLookup(e => e.MatchId); var da = damage.ToLookup(d => d.MatchId);
        var matches = new Dictionary<Guid, InMatch>();
        var userMap = new Dictionary<Guid, Dictionary<string, RosterUser>>();
        foreach (var h in heads)
        {
            matches[h.GameId] = new InMatch(h.MySide, pl[h.MatchId].Select(p => p.ToIn()).ToList(), ro[h.MatchId].Select(r => r.ToIn()).ToList(),
                ki[h.MatchId].Select(k => k.ToIn()).ToList(), ec[h.MatchId].Select(e => e.ToIn()).ToList(), da[h.MatchId].Select(d => d.ToIn()).ToList());
            userMap[h.GameId] = users.Where(u => u.AssociationId == h.AssociationId).GroupBy(u => u.Puuid).ToDictionary(g => g.Key, g => new RosterUser(g.First().UserId, g.First().Name));
        }
        return (matches, userMap);
    }
}
