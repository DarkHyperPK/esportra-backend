using Esportra.Contracts.Broadcast;
using Esportra.Core.Broadcast;
using Xunit;

namespace Esportra.Core.Tests.Broadcast;

public sealed class NodeRuntimeRulesTests
{
    private static readonly NodeRuntimeStatus Valid = new("production", true, true, "live", false, false, true, "0.1.0");

    [Theory]
    [InlineData("live")]
    [InlineData("stale")]
    [InlineData("waiting")]
    [InlineData("idle")]
    public void Known_feed_states_are_valid(string state) => Assert.True(NodeRuntimeRules.IsValid(Valid with { FeedState = state }));

    [Fact]
    public void Invalid_status_values_are_rejected()
    {
        Assert.False(NodeRuntimeRules.IsValid(Valid with { Mode = "admin" }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { FeedState = "raw-gep" }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { AppVersion = new string('x', 65) }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { AppVersion = "0.1\n" }));
    }

    [Fact]
    public void Rundown_versions_are_positive_safe_integers_and_pending_is_newer()
    {
        var plan = new NodeRundownPlan(Guid.NewGuid(), 2, 3);
        Assert.True(NodeRuntimeRules.IsValid(Valid with { Plan = plan }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { Plan = plan with { Version = 0 } }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { Plan = plan with { PendingVersion = 1 } }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { Plan = plan with { Version = 9007199254740992 } }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { Mode = "capture-only", Plan = plan }));
    }

    [Fact]
    public void Output_health_is_bounded_and_contains_only_valid_metadata()
    {
        var output = new NodeOutputHealth(Guid.NewGuid(), "Gameplay", 1, DateTimeOffset.UtcNow);
        Assert.True(NodeRuntimeRules.IsValid(Valid with { Outputs = [output] }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { Outputs = [output with { Id = Guid.Empty }] }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { Outputs = [output with { Name = "bad\nname" }] }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { Outputs = [output with { ConnectionCount = -1 }] }));
        Assert.False(NodeRuntimeRules.IsValid(Valid with { Outputs = Enumerable.Repeat(output, 101).ToArray() }));
    }
}
