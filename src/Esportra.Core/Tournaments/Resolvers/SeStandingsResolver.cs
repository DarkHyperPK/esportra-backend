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

        var rows = BuildEliminatedRows(placementList, statsLookup, bracketSide: null);
        rows.AddRange(BuildActiveRows(standingsList, placedIds, ComputeNextRank(placementList), bracketSide: null));
        return rows;
    }

    private static List<StandingsRow> BuildEliminatedRows(
        List<ResolvedPlacement> eliminations,
        Dictionary<Guid, TeamStanding> stats,
        string? bracketSide)
    {
        return eliminations.Select(p =>
        {
            var s = stats.GetValueOrDefault(p.TeamId);
            return BuildRow(p.Placement, p.TeamId, p.TeamName, p.IsTied, s, bracketSide);
        }).ToList();
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
        };
    }

    internal static int ComputeNextRank(List<ResolvedPlacement> placed)
    {
        if (placed.Count == 0) return 1;
        return placed.GroupBy(p => p.Placement).Max(g => g.Key + g.Count());
    }
}
