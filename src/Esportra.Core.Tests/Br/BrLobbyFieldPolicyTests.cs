using Esportra.Core.Br;
using Xunit;

namespace Esportra.Core.Tests.Br;

public class BrLobbyFieldPolicyTests
{
    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, true, true, false)]
    public void RejectsLobbyPerGameFieldsInBody_matches_contract(
        bool gamesModelReady,
        bool hasScheduledAt,
        bool hasQueueTimerMinutes,
        bool hasMap,
        bool expected)
    {
        var result = BrLobbyFieldPolicy.RejectsLobbyPerGameFieldsInBody(
            gamesModelReady,
            hasScheduledAt,
            hasQueueTimerMinutes,
            hasMap);

        Assert.Equal(expected, result);
    }
}
