using System.Data;
using Dapper;

namespace Esportra.Api.Services.ProviderMatches;

/// <summary>
/// What a client may see of an associated Riot match: status, verification reasons and the scoreboard. PUUIDs never
/// leave the server; players are shown by Riot name, side and whether they are a roster member.
/// </summary>
public static class ProviderMatchDetails
{
    private sealed record Head(
        Guid Id, Guid MatchId, string VerificationStatus, string DetailJson, string? Team1Side, string? Team2Side,
        string? ReportedMap, string DataStatus, string? StatusDetail, string? MapId, string? QueueId, bool? IsCustom,
        bool? IsCompleted, DateTime? StartedAt, int? LengthMs);

    private sealed record PlayerRow(
        string Puuid, string RiotSide, string? GameName, string? TagLine, string? CharacterId, int Score, int RoundsPlayed,
        int Kills, int Deaths, int Assists, int DamageDealt, int Headshots, int Bodyshots, int Legshots,
        int FirstKills, int FirstDeaths, int Kills2K, int Kills3K, int Kills4K, int Kills5K, int Plants, int Defuses,
        int? Slot);

    public static async Task<object> LoadAsync(IDbConnection conn, string contextType, Guid contextId)
    {
        var head = await conn.QuerySingleOrDefaultAsync<Head>(
            """
            SELECT a.id AS Id, a.match_id AS MatchId, a.verification_status AS VerificationStatus,
                   a.verification_detail::text AS DetailJson, a.team1_riot_side AS Team1Side, a.team2_riot_side AS Team2Side,
                   a.reported_map AS ReportedMap, m.data_status AS DataStatus, m.status_detail AS StatusDetail,
                   m.map_provider_id AS MapId, m.queue_id AS QueueId, m.is_custom AS IsCustom, m.is_completed AS IsCompleted,
                   m.started_at AS StartedAt, m.length_ms AS LengthMs
            FROM public.provider_match_associations a
            JOIN public.provider_matches m ON m.id = a.match_id
            WHERE a.context_type = @contextType AND a.context_id = @contextId
            """, new { contextType, contextId });
        if (head is null) return new { status = "none", reasons = Array.Empty<string>() };

        var reasons = System.Text.Json.JsonDocument.Parse(head.DetailJson).RootElement.TryGetProperty("reasons", out var r)
            ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
        if (head.VerificationStatus == "duplicate")
            return new { status = "duplicate", reasons = new[] { "match_already_used" } };

        var players = (await conn.QueryAsync<PlayerRow>(
            """
            SELECT p.puuid AS Puuid, p.riot_side AS RiotSide, p.game_name AS GameName, p.tag_line AS TagLine,
                   p.character_id AS CharacterId, p.score AS Score, p.rounds_played AS RoundsPlayed, p.kills AS Kills,
                   p.deaths AS Deaths, p.assists AS Assists, p.damage_dealt AS DamageDealt, p.headshots AS Headshots,
                   p.bodyshots AS Bodyshots, p.legshots AS Legshots, p.first_kills AS FirstKills, p.first_deaths AS FirstDeaths,
                   p.kills_2k AS Kills2K, p.kills_3k AS Kills3K, p.kills_4k AS Kills4K, p.kills_5k AS Kills5K,
                   p.plants AS Plants, p.defuses AS Defuses, s.team_slot::int AS Slot
            FROM public.valorant_match_players p
            LEFT JOIN public.provider_match_roster_snapshots s ON s.association_id = @id AND s.puuid = p.puuid
            WHERE p.match_id = @matchId AND NOT p.is_observer
            ORDER BY p.riot_side, p.score DESC
            """, new { head.Id, head.MatchId })).ToList();
        var teams = await conn.QueryAsync<(string TeamId, bool? Won, int? RoundsWon)>(
            "SELECT team_id, won, rounds_won FROM public.valorant_match_teams WHERE match_id = @MatchId ORDER BY team_id", new { head.MatchId });

        return new
        {
            status = head.VerificationStatus,
            reasons,
            match = new
            {
                data_status = head.DataStatus,
                status_detail = head.StatusDetail,
                map = ProviderMatchVerifier.DisplayMap(head.MapId) ?? head.ReportedMap,
                queue_id = head.QueueId,
                is_custom = head.IsCustom,
                is_completed = head.IsCompleted,
                started_at = head.StartedAt,
                length_ms = head.LengthMs,
            },
            team1_side = head.Team1Side,
            team2_side = head.Team2Side,
            teams = teams.Select(t => new { side = t.TeamId, won = t.Won, rounds_won = t.RoundsWon }),
            players = players.Select(p =>
            {
                var shots = p.Headshots + p.Bodyshots + p.Legshots;
                return new
                {
                    riot_name = p.GameName is null ? null : $"{p.GameName}#{p.TagLine}",
                    riot_side = p.RiotSide,
                    team_slot = p.Slot,
                    is_roster_member = p.Slot is not null,
                    character_id = p.CharacterId,
                    score = p.Score,
                    rounds_played = p.RoundsPlayed,
                    kills = p.Kills,
                    deaths = p.Deaths,
                    assists = p.Assists,
                    acs = p.RoundsPlayed > 0 ? (int?)Math.Round((double)p.Score / p.RoundsPlayed) : null,
                    adr = p.RoundsPlayed > 0 ? (int?)Math.Round((double)p.DamageDealt / p.RoundsPlayed) : null,
                    headshot_share_pct = shots > 0 ? (double?)Math.Round(100.0 * p.Headshots / shots, 1) : null,
                    first_kills = p.FirstKills,
                    first_deaths = p.FirstDeaths,
                    multi_kills = new { k2 = p.Kills2K, k3 = p.Kills3K, k4 = p.Kills4K, k5 = p.Kills5K },
                    plants = p.Plants,
                    defuses = p.Defuses,
                };
            }),
        };
    }
}
