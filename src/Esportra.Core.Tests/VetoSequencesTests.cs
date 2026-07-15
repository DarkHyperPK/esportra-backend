using Esportra.Core.Match;
using Xunit;

namespace Esportra.Core.Tests;

public class VetoSequencesTests
{
    [Fact]
    public void Valorant_Bo1_Has_Seven_Actions_BanPickSide()
    {
        var steps = VetoSequences.GetSequence(1, "valorant", 7);
        Assert.Equal(7, steps.Count);
        Assert.Equal("ban", steps[0].Action);
        Assert.Equal("pick", steps[^2].Action);
        Assert.Equal("pick_side", steps[^1].Action);
    }

    [Fact]
    public void Cs2_Bo1_PureBan_Has_Seven_Actions()
    {
        var steps = VetoSequences.GetSequence(1, "cs2", 7);
        Assert.Equal(7, steps.Count);
        Assert.Equal(6, steps.Count(s => s.Action == "ban"));
        Assert.True(steps[^1].IsDecider);
        Assert.Equal("pick_side", steps[^1].Action);
    }

    [Fact]
    public void R6_Bo1_PureBan_With_Nine_Maps_Has_Eight_Bans_Plus_Decider_Side()
    {
        var steps = VetoSequences.GetSequence(1, "rainbow six siege", 9);
        Assert.Equal(9, steps.Count);
        Assert.Equal(8, steps.Count(s => s.Action == "ban"));
        Assert.True(steps[^1].IsDecider);
        Assert.Equal("pick_side", steps[^1].Action);
        Assert.Equal("T1", steps[^1].Team);
    }

    [Fact]
    public void R6_Bo3_With_Nine_Maps_Has_Expected_Action_Count()
    {
        var steps = VetoSequences.GetSequence(3, "r6s", 9);
        // 2 bans + 2 picks + 2 side picks + 4 remaining bans + decider side = 11
        Assert.Equal(11, steps.Count);
        Assert.True(steps[^1].IsDecider);
    }

    [Fact]
    public void R6_Bo5_With_Nine_Maps_Has_Expected_Action_Count()
    {
        var steps = VetoSequences.GetSequence(5, "r6s", 9);
        // 2 bans + 4 picks + 4 side picks + 2 remaining bans + decider = 13
        Assert.Equal(13, steps.Count);
    }

    [Fact]
    public void GetGameConfig_R6_Has_Pool_Size_Nine()
    {
        var config = VetoSequences.GetGameConfig("r6");
        Assert.Equal(9, config.MapPoolSize);
        Assert.Equal(VetoSequences.Bo1Style.PureBan, config.Bo1Style);
    }

    [Fact]
    public void Backward_Compatible_GetSequence_Defaults_To_Valorant()
    {
        var legacy = VetoSequences.GetSequence(1);
        var explicitValorant = VetoSequences.GetSequence(1, "valorant", 7);
        Assert.Equal(legacy.Count, explicitValorant.Count);
    }
}
