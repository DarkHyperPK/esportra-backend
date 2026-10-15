using Esportra.Core.Broadcast;
using Xunit;

namespace Esportra.Core.Tests.Broadcast;

public sealed class BroadcastRundownTemplateTests
{
    [Theory]
    [InlineData(1, 9)]
    [InlineData(3, 17)]
    public void Templates_cover_each_map_without_enabling_automatic_takes(int bestOf, int count)
    {
        var template = BroadcastRundownTemplates.Build(bestOf);
        Assert.Equal(count, template.Items.Length);
        Assert.Equal(bestOf, template.Items.Count(item => item.Type == "match"));
        Assert.Equal(bestOf - 1, template.Items.Count(item => item.Type == "break"));
        Assert.All(template.Items, item => { Assert.Null(BroadcastSegmentPlans.Validate(item.Plan)); Assert.Equal("manual", item.Plan.Mode); Assert.Equal("fade", item.Plan.Transition!.Mode); Assert.Equal("Fade", item.Plan.Transition.Name); Assert.Equal(300, item.Plan.Transition.DurationMs); });
        Assert.Equal(Enumerable.Range(1, bestOf), template.Items.Where(item => item.Type == "match").Select(item => item.Plan.MapNumber!.Value));
    }
    [Fact]
    public void Unsafe_automatic_cues_and_invalid_windows_are_rejected()
    {
        var plan = new BroadcastSegmentPlan("round_live", "automatic", "window", 5, ["ingame"]);
        Assert.Null(BroadcastSegmentPlans.Validate(plan));
        Assert.NotNull(BroadcastSegmentPlans.Validate(plan with { Trigger = "check_in_complete" }));
        Assert.NotNull(BroadcastSegmentPlans.Validate(plan with { HoldSeconds = 0 }));
        Assert.NotNull(BroadcastSegmentPlans.Validate(plan with { Graphics = ["../control"] }));
        Assert.NotNull(BroadcastSegmentPlans.Validate(plan with { Scene = "bad\nscene" }));
        Assert.NotNull(BroadcastSegmentPlans.Validate(plan with { Transition = new("fade", "Fade", 10) }));
    }
}
