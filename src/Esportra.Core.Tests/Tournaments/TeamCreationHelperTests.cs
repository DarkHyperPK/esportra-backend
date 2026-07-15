using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class TeamCreationHelperTests
{
    [Fact]
    public void BuildSoloAdapterTag_is_deterministic()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Assert.Equal($"solo-{id:N}", TeamCreationHelper.BuildSoloAdapterTag(id));
    }

    [Fact]
    public void BuildMockTag_is_truncated()
    {
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Equal(18, TeamCreationHelper.BuildMockTag(id).Length);
        Assert.StartsWith("mock-", TeamCreationHelper.BuildMockTag(id));
    }
}
