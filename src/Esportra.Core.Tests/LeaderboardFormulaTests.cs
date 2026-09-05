using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests;

public class LeaderboardFormulaTests
{
    [Theory]
    [InlineData(1, 500)]
    [InlineData(2, 300)]
    [InlineData(3, 200)]
    [InlineData(4, 100)]
    [InlineData(5, 50)]
    [InlineData(8, 50)]
    [InlineData(9, 25)]
    [InlineData(16, 25)]
    public void Placement_Points_Follow_Buckets(int placement, int expected)
    {
        Assert.Equal(expected, LeaderboardFormula.PlacementPoints(placement));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(64)]
    public void Placement_Outside_Top16_Earns_Nothing(int placement)
    {
        Assert.Equal(0, LeaderboardFormula.PlacementPoints(placement));
    }

    [Fact]
    public void Champion_Placement_Always_At_Least_Title_Value()
    {
        // The 1st-place bucket must never be worth less than the title bonus,
        // otherwise the GREATEST floor in the recompute SQL would be dead weight.
        Assert.True(LeaderboardFormula.Placement1st >= LeaderboardFormula.TournamentWinPoints);
    }

    [Fact]
    public void Win_Exceeds_Loss_Penalty_So_Playing_Net_Positive_Is_Rewarded()
    {
        Assert.True(LeaderboardFormula.WinPoints > LeaderboardFormula.LossPoints);
    }
}
