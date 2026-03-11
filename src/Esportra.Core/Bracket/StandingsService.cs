using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.Bracket;

public sealed class StandingsService(IDbConnectionFactory db)
{
    public async Task<List<TeamStanding>> CalculateStandingsAsync(
        Guid stageId, string? groupId = null, CancellationToken ct = default)
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
        var map = new Dictionary<Guid, (
            int Played, int Wins, int Losses, int Ties, int Points, int Buchholz, int ScoreDiff)>();

        void Ensure(Guid teamId)
        {
            if (!map.ContainsKey(teamId)) map[teamId] = (0, 0, 0, 0, 0, 0, 0);
        }

        foreach (var m in matches)
        {
            if (m.team1_id is null && m.team2_id is null) continue;

            int t1Score = m.team1_score ?? 0;
            int t2Score = m.team2_score ?? 0;

            if (m.team1_id is Guid t1Id && m.team2_id is Guid t2Id)
            {
                Ensure(t1Id); Ensure(t2Id);
                var t1 = map[t1Id];
                var t2 = map[t2Id];

                t1.Played++; t2.Played++;
                t1.ScoreDiff += t1Score - t2Score;
                t2.ScoreDiff += t2Score - t1Score;

                if ((Guid?)m.winner_id == t1Id)      { t1.Wins++; t1.Points += 3; t2.Losses++; }
                else if ((Guid?)m.winner_id == t2Id)  { t2.Wins++; t2.Points += 3; t1.Losses++; }
                else                                   { t1.Ties++; t1.Points++; t2.Ties++; t2.Points++; }

                map[t1Id] = t1;
                map[t2Id] = t2;
            }
            else if (m.team1_id is Guid onlyT1)
            {
                Ensure(onlyT1);
                var t1 = map[onlyT1];
                t1.Played++; t1.Wins++; t1.Points += 3; t1.ScoreDiff += t1Score;
                map[onlyT1] = t1;
            }
            else if (m.team2_id is Guid onlyT2)
            {
                Ensure(onlyT2);
                var t2 = map[onlyT2];
                t2.Played++; t2.Wins++; t2.Points += 3; t2.ScoreDiff += t2Score;
                map[onlyT2] = t2;
            }
        }

        // 3. Buchholz: sum of opponents' points (second pass)
        foreach (var m in matches)
        {
            if (m.team1_id is not Guid bt1 || m.team2_id is not Guid bt2) continue;
            if (!map.ContainsKey(bt1) || !map.ContainsKey(bt2)) continue;

            var t1 = map[bt1]; var t2 = map[bt2];
            t1.Buchholz += t2.Points; t2.Buchholz += t1.Points;
            map[bt1] = t1; map[bt2] = t2;
        }

        // 4. Fetch team names
        var teamIds = map.Keys.ToList();
        var teamNames = new Dictionary<Guid, string>();
        if (teamIds.Count > 0)
        {
            var teams = await conn.QueryAsync(
                "SELECT id, name FROM public.teams WHERE id = ANY(@ids)",
                new { ids = teamIds.ToArray() });
            foreach (var t in teams)
                teamNames[(Guid)t.id] = (string)t.name;
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
