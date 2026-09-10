using Esportra.Core.Bracket;

namespace Esportra.Core.Tournaments.Resolvers;

public sealed class SeStandingsResolver(
    StandingsService standings,
    PlacementResolutionService placements) : IStandingsResolver
{
    public string Format => "single_elimination";

    public async Task<List<StandingsRow>> ResolveAsync(StandingsContext ctx, CancellationToken ct)
    {
        var placementList = await placements.ComputeForStageAsync(ctx.StageId, ctx.Conn, ct);
        var standingsList = await standings.CalculateStandingsAsync(ctx.StageId, ct: ct);

        var statsLookup = standingsList.ToDictionary(s => s.TeamId);
        var placedIds = placementList.Select(p => p.TeamId).ToHashSet();

        var rows = BuildActiveRows(standingsList, placedIds, startRank: 1, bracketSide: null);
        rows.AddRange(BuildEliminatedRows(placementList, startRank: rows.Count + 1, statsLookup, bracketSide: null));
        return rows;
    }

    internal static List<StandingsRow> BuildEliminatedRows(
        List<ResolvedPlacement> eliminations,
        int startRank,
        Dictionary<Guid, TeamStanding> stats,
        string? bracketSide)
    {
        var ordered = eliminations.OrderBy(p => p.Placement).ToList();
        var result = new List<StandingsRow>(ordered.Count);
        int rank = startRank;
        for (int i = 0; i < ordered.Count;)
        {
            int j = i;
            while (j < ordered.Count && ordered[j].Placement == ordered[i].Placement) j++;
            for (int k = i; k < j; k++)
            {
                var s = stats.GetValueOrDefault(ordered[k].TeamId);
                result.Add(BuildRow(rank, ordered[k].TeamId, ordered[k].TeamName, ordered[k].IsTied, s, bracketSide));
            }
            rank += j - i;
            i = j;
        }
        return result;
    }

    internal static List<StandingsRow> BuildActiveRows(
        List<TeamStanding> allStandings,
        HashSet<Guid> placedIds,
        int startRank,
        string? bracketSide)
    {
        return allStandings
            .Where(s => !placedIds.Contains(s.TeamId))
            .OrderByDescending(s => s.Wins)
            .ThenBy(s => s.Losses)
            .ThenBy(s => s.TeamName)
            .Select((s, i) => BuildRow(startRank + i, s.TeamId, s.TeamName, false, s, bracketSide))
            .ToList();
    }

    internal static StandingsRow BuildRow(
        int rank, Guid teamId, string teamName, bool isTied,
        TeamStanding? stats, string? bracketSide)
    {
        return new StandingsRow
        {
            Rank = rank,
            TeamId = teamId,
            TeamName = teamName,
            IsTied = isTied,
            BracketSide = bracketSide,
            Played = stats?.Played ?? 0,
            Wins = stats?.Wins ?? 0,
            Losses = stats?.Losses ?? 0,
            Ties = stats?.Ties ?? 0,
            ScoreDiff = stats?.ScoreDiff ?? 0,
            RoundDiff = stats?.RoundDiff ?? 0,
        };
    }

}
