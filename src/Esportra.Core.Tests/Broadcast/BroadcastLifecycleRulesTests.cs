using Esportra.Core.Broadcast;
using Xunit;

namespace Esportra.Core.Tests.Broadcast;

public sealed class BroadcastLifecycleRulesTests
{
    [Theory]
    [InlineData("draft", true)]
    [InlineData("scheduled", true)]
    [InlineData("live", false)]
    [InlineData("ended", false)]
    [InlineData("cancelled", false)]
    public void OnlyPreparationStatusesEditPlan(string status, bool expected) => Assert.Equal(expected, BroadcastShowRules.CanEditPlan(status));

    [Theory]
    [InlineData("draft", false, true)]
    [InlineData("scheduled", false, true)]
    [InlineData("cancelled", false, true)]
    [InlineData("live", false, false)]
    [InlineData("ended", false, false)]
    [InlineData("draft", true, false)]
    [InlineData("cancelled", true, false)]
    public void DeletePreservesBroadcastHistory(string status, bool history, bool expected) => Assert.Equal(expected, BroadcastShowRules.CanDelete(status, history));

    [Theory]
    [InlineData("live", true)]
    [InlineData("ended", false)]
    [InlineData("cancelled", false)]
    public void ClosedShowsCannotSendConfiguration(string status, bool expected) => Assert.Equal(expected, BroadcastShowRules.CanSendConfiguration(status));

    [Fact]
    public void DuplicateTitleRespectsLengthLimit() => Assert.Equal(200, BroadcastShowRules.DuplicateTitle(new string('a', 200)).Length);
}
