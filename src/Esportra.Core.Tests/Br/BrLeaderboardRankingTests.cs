using Esportra.Core.Br;
using Xunit;

namespace Esportra.Core.Tests.Br;

public class BrLeaderboardRankingTests
{
    [Fact]
    public void Compare_orders_by_total_points_desc()
    {
        var high = new BrLeaderboardAggregate(100, 2, 10, 3);
        var low = new BrLeaderboardAggregate(50, 5, 20, 1);

        Assert.True(BrLeaderboardRanking.Compare(high, low, BrTiebreaker.MostWins) < 0);
        Assert.True(BrLeaderboardRanking.Compare(low, high, BrTiebreaker.MostWins) > 0);
    }

    [Fact]
    public void Compare_most_wins_breaks_ties()
    {
        var a = new BrLeaderboardAggregate(100, 3, 5, 4);
        var b = new BrLeaderboardAggregate(100, 2, 20, 2);

        Assert.True(BrLeaderboardRanking.Compare(a, b, BrTiebreaker.MostWins) < 0);
    }

    [Fact]
    public void Compare_most_kills_breaks_ties()
    {
        var a = new BrLeaderboardAggregate(100, 2, 30, 4);
        var b = new BrLeaderboardAggregate(100, 5, 10, 2);

        Assert.True(BrLeaderboardRanking.Compare(a, b, BrTiebreaker.MostKills) < 0);
    }

    [Fact]
    public void Compare_head_to_head_uses_avg_placement()
    {
        var a = new BrLeaderboardAggregate(100, 1, 1, 2);
        var b = new BrLeaderboardAggregate(100, 5, 50, 8);

        Assert.True(BrLeaderboardRanking.Compare(a, b, BrTiebreaker.HeadToHead) < 0);
    }
}
