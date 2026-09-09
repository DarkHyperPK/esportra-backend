using Dapper;
using Esportra.Core.Bracket;

namespace Esportra.Core.Tournaments.Resolvers;

public sealed class RrStandingsResolver(StandingsService standings) : IStandingsResolver
{
    public string Format => "round_robin";

    public async Task<List<StandingsRow>> ResolveAsync(StandingsContext ctx, CancellationToken ct)
    {
        var standingsList = await standings.CalculateStandingsAsync(ctx.StageId, ct: ct);
        if (standingsList.Count == 0) return [];

        var rows = standingsList.Select(MapToRow).ToList();

        var tiedGroups = FindTiedGroups(standingsList, rows);
        foreach (var (group, tiedIds) in tiedGroups)
        {
            var h2hMatches = await LoadH2HMatchesAsync(ctx, tiedIds, ct);
            var reranked = ApplyH2HTiebreak(group, h2hMatches);
            UpdateRows(rows, reranked);
        }

        return rows;
    }

    private static StandingsRow MapToRow(TeamStanding s, int idx) =>
        new()
        {
            Rank = idx + 1,
            TeamId = s.TeamId,
            TeamName = s.TeamName,
            Played = s.Played,
            Wins = s.Wins,
            Losses = s.Losses,
            Ties = s.Ties,
            Points = s.Points,
            Buchholz = s.Buchholz,
            ScoreDiff = s.ScoreDiff,
        };

    private static List<(List<StandingsRow> Group, Guid[] TiedIds)> FindTiedGroups(
        List<TeamStanding> standings, List<StandingsRow> rows)
    {
        return standings
            .GroupBy(s => (s.Points, s.Buchholz, s.ScoreDiff))
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var tiedIds = g.Select(s => s.TeamId).ToArray();
                var groupRows = rows.Where(r => tiedIds.Contains(r.TeamId)).ToList();
                return (groupRows, tiedIds);
            })
            .ToList();
    }

    private static async Task<List<H2HMatch>> LoadH2HMatchesAsync(
        StandingsContext ctx, Guid[] tiedIds, CancellationToken ct)
    {
        var results = await ctx.Conn.QueryAsync<H2HMatch>(
            """
            SELECT team1_id, team2_id, winner_id,
                   COALESCE(team1_score, 0) AS team1_score,
                   COALESCE(team2_score, 0) AS team2_score
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            WHERE v.stage_id = @stageId
              AND m.status = 'completed'
              AND m.team1_id = ANY(@tiedTeamIds)
              AND m.team2_id = ANY(@tiedTeamIds)
            """,
            new { stageId = ctx.StageId, tiedTeamIds = tiedIds });
        return results.ToList();
    }

    private static void UpdateRows(List<StandingsRow> rows, List<StandingsRow> reranked)
    {
        foreach (var updated in reranked)
        {
            var idx = rows.FindIndex(r => r.TeamId == updated.TeamId);
            if (idx >= 0) rows[idx] = updated;
        }
    }

    internal static List<StandingsRow> ApplyH2HTiebreak(
        List<StandingsRow> group, List<H2HMatch> h2hMatches)
    {
        int startRank = group.Min(r => r.Rank);
        var groupIds = group.Select(r => r.TeamId).ToHashSet();

        var h2hWins = group.ToDictionary(r => r.TeamId, _ => 0);
        var h2hSd = group.ToDictionary(r => r.TeamId, _ => 0);

        foreach (var m in h2hMatches)
        {
            if (!groupIds.Contains(m.Team1Id) || !groupIds.Contains(m.Team2Id)) continue;
            if (m.WinnerId == m.Team1Id) h2hWins[m.Team1Id]++;
            else if (m.WinnerId == m.Team2Id) h2hWins[m.Team2Id]++;
            h2hSd[m.Team1Id] += m.Team1Score - m.Team2Score;
            h2hSd[m.Team2Id] += m.Team2Score - m.Team1Score;
        }

        var sorted = group
            .OrderByDescending(r => h2hWins[r.TeamId])
            .ThenByDescending(r => h2hSd[r.TeamId])
            .ThenByDescending(r => r.ScoreDiff)
            .ThenBy(r => r.TeamName)
            .ToList();

        return AssignRanksWithTies(sorted, h2hWins, h2hSd, startRank);
    }

    private static List<StandingsRow> AssignRanksWithTies(
        List<StandingsRow> sorted,
        Dictionary<Guid, int> h2hWins,
        Dictionary<Guid, int> h2hSd,
        int startRank)
    {
        var result = new List<StandingsRow>(sorted.Count);
        int rank = startRank;

        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j < sorted.Count
                && h2hWins[sorted[j].TeamId] == h2hWins[sorted[i].TeamId]
                && h2hSd[sorted[j].TeamId] == h2hSd[sorted[i].TeamId]
                && sorted[j].ScoreDiff == sorted[i].ScoreDiff)
            {
                j++;
            }

            bool isTied = j - i > 1;
            for (int k = i; k < j; k++)
                result.Add(WithRank(sorted[k], rank, isTied));

            rank += j - i;
            i = j;
        }

        return result;
    }

    private static StandingsRow WithRank(StandingsRow row, int rank, bool isTied) =>
        new()
        {
            Rank = rank,
            IsTied = isTied,
            TeamId = row.TeamId,
            TeamName = row.TeamName,
            Played = row.Played,
            Wins = row.Wins,
            Losses = row.Losses,
            Ties = row.Ties,
            Points = row.Points,
            Buchholz = row.Buchholz,
            ScoreDiff = row.ScoreDiff,
        };
}

internal sealed record H2HMatch(
    Guid Team1Id, Guid Team2Id, Guid? WinnerId, int Team1Score, int Team2Score);
