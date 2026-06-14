using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class StaffTournamentAuditServiceTests
{
    [Theory]
    [InlineData(3, 0, "Round 1, Match 3")]
    [InlineData(1, -1, "Match 1")]
    public void FormatMatchLabel_usesRoundWhenPresent(int matchNumber, int roundIndex, string expected)
    {
        Assert.Equal(expected, StaffTournamentAuditService.FormatMatchLabel(matchNumber, roundIndex));
    }
}
