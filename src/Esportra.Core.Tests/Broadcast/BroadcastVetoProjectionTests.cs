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
    public void UnknownActorDoesNotBecomeTeamOne()
    {
        var steps = BroadcastVetoProjection.Project([new(1, "pick", "Haven", null, null)]);
        Assert.Equal(-1, steps[0].Team);
        Assert.Null(steps[0].Side);
    }
}
