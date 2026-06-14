using Esportra.Core.Br;
using Xunit;

namespace Esportra.Core.Tests.Br;

public sealed class BrSeedingServiceTests
{
    [Fact]
    public void BuildSnakeAssignments_snakes_groups()
    {
        var teams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var groups = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var assignments = BrSeedingService.BuildSnakeAssignments(teams, groups);

        Assert.Equal(4, assignments.Count);
        Assert.Equal(groups[0], assignments[0].groupId);
        Assert.Equal(groups[1], assignments[1].groupId);
        Assert.Equal(groups[1], assignments[2].groupId);
        Assert.Equal(groups[0], assignments[3].groupId);
    }

    [Fact]
    public void BuildRoundRobinAssignments_distributes_evenly()
    {
        var teams = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var groups = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var assignments = BrSeedingService.BuildRoundRobinAssignments(teams, groups);

        Assert.Equal(5, assignments.Count);
        Assert.Equal(3, assignments.Count(a => a.groupId == groups[0]));
        Assert.Equal(2, assignments.Count(a => a.groupId == groups[1]));
    }
}
