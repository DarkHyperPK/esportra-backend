using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.Bracket;

public sealed class StandingsService(IDbConnectionFactory db)
{
    public async Task<List<TeamStanding>> CalculateStandingsAsync(
        string stageId, string? groupId = null, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        // 1. Fetch completed matches for the stage
        var sql = @"
            SELECT m.id, m.team1_id, m.team2_id,
                   m.team1_score, m.team2_score,
                   m.winner_id, m.loser_id
            FROM public.brkt_matches m
            JOIN public.brkt_versions v ON v.id = m.version_id
            WHERE v.stage_id = @stageId
              AND m.status = 'completed'";

        if (groupId is not null) sql += " AND m.group_id = @groupId";

        var matches = (await conn.QueryAsync(sql, new { stageId, groupId })).AsList();

        // 2. Build standings map
        var map = new Dictionary<string, (
            int Played, int Wins, int Losses, int Ties, int Points, int Buchholz, int ScoreDiff)>();

        void Ensure(string teamId)
        {
            if (!map.ContainsKey(teamId)) map[teamId] = (0, 0, 0, 0, 0, 0, 0);
        }

        foreach (var m in matches)
        {
            if (m.team1_id is null && m.team2_id is null) continue;

            int t1Score = m.team1_score ?? 0;
            int t2Score = m.team2_score ?? 0;

            if (m.team1_id is not null && m.team2_id is not null)
            {
                Ensure(m.team1_id); Ensure(m.team2_id);
                var t1 = map[m.team1_id];
                var t2 = map[m.team2_id];

                t1.Played++; t2.Played++;
                t1.ScoreDiff += t1Score - t2Score;
                t2.ScoreDiff += t2Score - t1Score;

                if (m.winner_id == m.team1_id)      { t1.Wins++; t1.Points += 3; t2.Losses++; }
                else if (m.winner_id == m.team2_id) { t2.Wins++; t2.Points += 3; t1.Losses++; }
                else                                 { t1.Ties++; t1.Points++; t2.Ties++; t2.Points++; }

                map[m.team1_id] = t1;
                map[m.team2_id] = t2;
            }
            else if (m.team1_id is not null)
            {
                Ensure(m.team1_id);
                var t1 = map[m.team1_id];
                t1.Played++; t1.Wins++; t1.Points += 3; t1.ScoreDiff += t1Score;
                map[m.team1_id] = t1;
            }
            else if (m.team2_id is not null)
            {
                Ensure(m.team2_id);
                var t2 = map[m.team2_id];
                t2.Played++; t2.Wins++; t2.Points += 3; t2.ScoreDiff += t2Score;
                map[m.team2_id] = t2;
            }
        }

        // 3. Buchholz: sum of opponents' points (second pass)
        foreach (var m in matches)
        {
            if (m.team1_id is null || m.team2_id is null) continue;
            if (!map.ContainsKey(m.team1_id) || !map.ContainsKey(m.team2_id)) continue;

            var t1 = map[m.team1_id]; var t2 = map[m.team2_id];
            t1.Buchholz += t2.Points; t2.Buchholz += t1.Points;
            map[m.team1_id] = t1; map[m.team2_id] = t2;
        }

        // 4. Fetch team names
        var teamIds = map.Keys.ToList();
        var teamNames = new Dictionary<string, string>();
        if (teamIds.Count > 0)
        {
            var teams = await conn.QueryAsync(
                "SELECT id, name FROM public.teams WHERE id = ANY(@ids)",
                new { ids = teamIds.ToArray() });
            foreach (var t in teams)
                teamNames[(string)t.id] = (string)t.name;
        }

        // 5. Sort: Points → Buchholz → ScoreDiff → Wins
        var sorted = map
            .Select(kvp => (Id: kvp.Key, Stats: kvp.Value))
            .OrderByDescending(x => x.Stats.Points)
            .ThenByDescending(x => x.Stats.Buchholz)
            .ThenByDescending(x => x.Stats.ScoreDiff)
            .ThenByDescending(x => x.Stats.Wins)
            .Select((x, idx) => new TeamStanding(
                TeamId:    x.Id,
                TeamName:  teamNames.GetValueOrDefault(x.Id, "Unknown"),
                Played:    x.Stats.Played,
                Wins:      x.Stats.Wins,
                Losses:    x.Stats.Losses,
                Ties:      x.Stats.Ties,
                Points:    x.Stats.Points,
                Buchholz:  x.Stats.Buchholz,
                ScoreDiff: x.Stats.ScoreDiff,
                Rank:      idx + 1))
            .ToList();

        return sorted;
    }
}
