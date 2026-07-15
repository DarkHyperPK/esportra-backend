using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public class RosterPoolModeRulesTests
{
    [Theory]
    [InlineData("team", "skirmish_2v2", null, null, true)]
    [InlineData("team", "valorant_skirmish_2v2", "Skirmish", null, true)]
    [InlineData("team", "skirmish_2v2", null, "skirmish", true)]
    [InlineData("solo", "skirmish_1v1", "Skirmish", null, false)]
    [InlineData("team", "5v5", "Standard", null, false)]
    public void UsesRosterPoolSelection_matches_expected(
        string participantMode,
        string modeKey,
        string? modeGroup,
        string? mapPoolFilter,
        bool expected) =>
        Assert.Equal(
            expected,
            RosterPoolModeRules.UsesRosterPoolSelection(participantMode, modeKey, modeGroup, mapPoolFilter));

    [Theory]
    [InlineData("5v5", "5v5", true)]
    [InlineData("5v5", "competitive", true)]
    [InlineData("5v5", "2v2", false)]
    public void RosterMatchesPoolSource_matches_format_and_aliases(
        string poolModeKey,
        string rosterFormat,
        bool expected) =>
        Assert.Equal(
            expected,
            RosterPoolModeRules.RosterMatchesPoolSource(poolModeKey, rosterFormat, ["competitive"]));
}
