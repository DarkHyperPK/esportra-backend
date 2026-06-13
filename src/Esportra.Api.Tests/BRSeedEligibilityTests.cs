using Esportra.Api.Helpers;
using Xunit;

namespace Esportra.Api.Tests;

public class BRSeedEligibilityTests
{
    [Theory]
    [InlineData(false, "approved", "checked_in")]
    [InlineData(true, "checked_in")]
    public void ResolveStatuses_respects_check_in_flag(bool checkInRequired, params string[] expected)
    {
        Assert.Equal(expected, BRSeedEligibility.ResolveStatuses(checkInRequired));
    }

    [Fact]
    public void ParticipantSeedMessage_mentions_check_in_when_required()
    {
        Assert.Contains("check in", BRSeedEligibility.ParticipantSeedMessage(true), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved", BRSeedEligibility.ParticipantSeedMessage(false), StringComparison.OrdinalIgnoreCase);
    }
}
