using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class RosterLineupSnapshotHelperTests
{
    [Fact]
    public void Build_groups_members_by_lineup_role()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var userC = Guid.NewGuid();

        var lineup = RosterLineupSnapshotHelper.Build([
            new RosterLineupSnapshotHelper.Row(userA, "alpha", null, "starter", true),
            new RosterLineupSnapshotHelper.Row(userB, "beta", null, "substitute", false),
            new RosterLineupSnapshotHelper.Row(userC, "coach1", null, "coach", false),
        ]);

        Assert.Single(lineup.Starters);
        Assert.Single(lineup.Substitutes);
        Assert.Single(lineup.Coaches);
        Assert.Equal(userA.ToString(), lineup.Starters[0].UserId);
    }
}
