using Dapper;
using Esportra.Core.Br;

namespace Esportra.Core.Tournaments.Resolvers;

public sealed class BrStandingsResolver : IStandingsResolver
{
    public string Format => "battle_royale";

    public async Task<List<StandingsRow>> ResolveAsync(StandingsContext ctx, CancellationToken ct)
    {
        var configRow = await ctx.Conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT config FROM tournament_stages WHERE id = @stageId",
            new { stageId = ctx.StageId });

        object? config = configRow?.config;
        var tiebreaker = BrConfigService.ResolveTiebreaker(config);
        var leaderboard = await BrLeaderboardRepository.ListForStageAsync(ctx.Conn, ctx.StageId, tiebreaker);

        var validRows = leaderboard
            .Where(row => Guid.TryParse(row.TeamId, out _))
            .ToList();

        return AssignRanksAndTies(validRows, tiebreaker);
    }

    internal static List<StandingsRow> AssignRanksAndTies(
        List<BrLeaderboardRow> rows, BrTiebreaker tiebreaker)
    {
        var result = new List<StandingsRow>(rows.Count);
        int rank = 1;

        for (int i = 0; i < rows.Count;)
        {
            int j = FindBandEnd(rows, i, tiebreaker);
            bool isTied = j - i > 1;
            for (int k = i; k < j; k++)
            {
                Guid.TryParse(rows[k].TeamId, out var teamId);
                result.Add(ToRow(rows[k], teamId, rank, isTied));
            }
            rank += j - i;
            i = j;
        }

        return result;
    }

    private static int FindBandEnd(List<BrLeaderboardRow> rows, int start, BrTiebreaker tiebreaker)
    {
        var startAgg = ToAggregate(rows[start]);
        int j = start + 1;
        while (j < rows.Count
            && BrLeaderboardRanking.Compare(startAgg, ToAggregate(rows[j]), tiebreaker) == 0)
        {
            j++;
        }
        return j;
    }

    private static BrLeaderboardAggregate ToAggregate(BrLeaderboardRow row) =>
        new(row.TotalPoints, row.Wins, row.TotalKills, row.AvgPlacement ?? 0);

    private static StandingsRow ToRow(BrLeaderboardRow row, Guid teamId, int rank, bool isTied) =>
        new()
        {
            Rank = rank,
            IsTied = isTied,
            TeamId = teamId,
            TeamName = row.TeamName,
            Played = row.GamesPlayed,
            Points = (int)row.TotalPoints,
            Kills = row.TotalKills,
            Wins = (int)row.Wins,
        };
}
