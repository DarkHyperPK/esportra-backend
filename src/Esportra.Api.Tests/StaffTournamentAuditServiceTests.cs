using System.Text.Json;
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

    [Fact]
    public void MergeDetails_preservesSnakeCaseScoreKeys()
    {
        var merged = StaffTournamentAuditService.MergeDetailsForTest(
            new Dictionary<string, object?>
            {
                ["tournament_name"] = "Summer Cup",
                ["match_label"] = "Round 1, Match 3",
            },
            new { team1_score = 2, team2_score = 1 });

        Assert.Equal(2, Convert.ToInt32(merged["team1_score"]));
        Assert.Equal(1, Convert.ToInt32(merged["team2_score"]));
        Assert.Equal("Summer Cup", merged["tournament_name"]);
    }
}
