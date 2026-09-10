using Esportra.Core.Bracket;

namespace Esportra.Core.Tournaments.Resolvers;

public sealed class DeStandingsResolver(
    StandingsService standings,
    PlacementResolutionService placements) : IStandingsResolver
{
    public string Format => "double_elimination";

    public async Task<List<StandingsRow>> ResolveAsync(StandingsContext ctx, CancellationToken ct)
    {
        var placementList = await placements.ComputeForStageAsync(ctx.StageId, ctx.Conn, ct);
        var standingsList = await standings.CalculateStandingsAsync(ctx.StageId, ct: ct);

        var statsLookup = standingsList.ToDictionary(s => s.TeamId);
        var placedIds = placementList.Select(p => p.TeamId).ToHashSet();

        var rows = BuildDeActiveRows(standingsList, placedIds, startRank: 1);
        rows.AddRange(SeStandingsResolver.BuildEliminatedRows(placementList, startRank: rows.Count + 1, statsLookup, bracketSide: null));
        return rows;
    }

    private static List<StandingsRow> BuildDeActiveRows(
        List<TeamStanding> allStandings,
        HashSet<Guid> placedIds,
        int startRank)
    {
        return allStandings
            .Where(s => !placedIds.Contains(s.TeamId))
            .Select(s => (Standing: s, Side: s.Losses == 0 ? "winners" : "losers"))
            .OrderBy(x => x.Side == "winners" ? 0 : 1)
            .ThenByDescending(x => x.Standing.Wins)
            .ThenBy(x => x.Standing.Losses)
            .ThenBy(x => x.Standing.TeamName)
            .Select((x, i) =>
                SeStandingsResolver.BuildRow(startRank + i, x.Standing.TeamId, x.Standing.TeamName, false, x.Standing, x.Side))
            .ToList();
    }
}
