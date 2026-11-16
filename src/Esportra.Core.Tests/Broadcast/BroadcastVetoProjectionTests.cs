using Esportra.Core.Broadcast;
using Xunit;

namespace Esportra.Core.Tests.Broadcast;

public sealed class BroadcastVetoProjectionTests
{
    [Fact]
    public void OrderedPicksReceiveLaterSideChoiceAndDeciderIsNeutral()
    {
        var steps = BroadcastVetoProjection.Project([
            new(3, "side", "Haven", "team1", "defense"),
            new(4, "decider", "Lotus", "team2", "attack"),
            new(2, "pick", "Haven", "team2", null),
            new(1, "ban", "Ascent", "team1", null)
        ]);
        Assert.Equal(["ban", "pick", "decider"], steps.Select(step => step.Action));
        Assert.Equal(0, steps[0].Team);
        Assert.Equal(1, steps[1].PickedByTeam);
        Assert.Equal("DEF", steps[1].Side);
        Assert.Equal(-1, steps[2].Team);
        Assert.Equal("ATK", steps[2].Side);
    }

    [Fact]
    public void MissingMapsAndNonDisplayActionsNeverInventVetoState()
    {
        var steps = BroadcastVetoProjection.Project([new(1, "pick", null, "team1", null), new(2, "ignore", "Haven", "team2", null), new(3, "side", "Haven", null, "attack")]);
        Assert.Empty(steps);
    }

    [Fact]
    public void PickSideSetsThePickedMapsSideAndALeftoverMapIsTheDecider()
    {
        // As the veto service records a best of 3: ban, ban, pick, pick_side, pick, pick_side, ban, ban, pick_side (decider).
        var steps = BroadcastVetoProjection.Project([
            new(1, "ban", "Bind", "team1", null), new(2, "ban", "Abyss", "team2", null),
            new(3, "pick", "Lotus", "team1", null), new(4, "pick_side", "Lotus", "team2", "defense"),
            new(5, "pick", "Haven", "team2", null), new(6, "pick_side", "Haven", "team1", "attack"),
            new(7, "ban", "Ascent", "team1", null), new(8, "ban", "Corrode", "team2", null),
            new(9, "pick_side", "Sunset", "team1", "defense")
        ]);
        Assert.Equal(["ban:Bind", "ban:Abyss", "pick:Lotus", "pick:Haven", "ban:Ascent", "ban:Corrode", "decider:Sunset"], steps.Select(step => $"{step.Action}:{step.Map}"));
        Assert.Equal(("DEF", 1), (steps[2].Side, steps[2].SideTeam));
        Assert.Equal(("ATK", 0), (steps[3].Side, steps[3].SideTeam));
        Assert.Equal((-1, "DEF", 0), (steps[6].Team, steps[6].Side, steps[6].SideTeam));
    }

    [Fact]
    public void UnknownActorDoesNotBecomeTeamOne()
    {
        var steps = BroadcastVetoProjection.Project([new(1, "pick", "Haven", null, null)]);
        Assert.Equal(-1, steps[0].Team);
        Assert.Null(steps[0].Side);
    }
}
