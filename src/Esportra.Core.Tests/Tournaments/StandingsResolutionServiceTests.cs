using Esportra.Core.Tournaments;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class StandingsResolutionServiceTests
{
    private static StandingsRow MakeRow(Guid teamId) =>
        new() { Rank = 1, TeamId = teamId, TeamName = "Team A" };

    private static Dictionary<Guid, (string Label, decimal Amount, bool IsTied)> NoConfirmed() =>
        new();

    private static Dictionary<Guid, string> NoLiveOpponents() =>
        new();

    [Fact]
    public void RankStatus_Active_WhenNoConfirmedPlacements()
    {
        var teamId = Guid.NewGuid();
        var row = MakeRow(teamId);

        var enriched = StandingsResolutionService.EnrichRow(
            row, NoConfirmed(), isProvisional: false, NoLiveOpponents());

        enriched.RankStatus.Should().Be("active");
    }

    [Fact]
    public void RankStatus_Confirmed_WhenPlacementExists()
    {
        var teamId = Guid.NewGuid();
        var row = MakeRow(teamId);
        var confirmed = new Dictionary<Guid, (string, decimal, bool)>
        {
            [teamId] = ("1st", 500m, false),
        };

        var enriched = StandingsResolutionService.EnrichRow(
            row, confirmed, isProvisional: false, NoLiveOpponents());

        enriched.RankStatus.Should().Be("confirmed");
        enriched.PrizeAmount.Should().Be(500m);
        enriched.PlacementLabel.Should().Be("1st");
    }

    [Fact]
    public void RankStatus_Provisional_WhenNoActiveMatchesAndNoConfirmedPlacements()
    {
        var teamId = Guid.NewGuid();
        var row = MakeRow(teamId);

        var enriched = StandingsResolutionService.EnrichRow(
            row, NoConfirmed(), isProvisional: true, NoLiveOpponents());

        enriched.RankStatus.Should().Be("provisional");
    }

    [Fact]
    public void IsLive_True_WhenTeamHasInProgressMatch()
    {
        var teamId = Guid.NewGuid();
        var row = MakeRow(teamId);
        var liveOpponents = new Dictionary<Guid, string>
        {
            [teamId] = "Opponent B",
        };

        var enriched = StandingsResolutionService.EnrichRow(
            row, NoConfirmed(), isProvisional: false, liveOpponents);

        enriched.IsLive.Should().BeTrue();
        enriched.LiveOpponent.Should().Be("Opponent B");
    }

    [Fact]
    public void IsLive_False_WhenNoInProgressMatches()
    {
        var teamId = Guid.NewGuid();
        var row = MakeRow(teamId);

        var enriched = StandingsResolutionService.EnrichRow(
            row, NoConfirmed(), isProvisional: false, NoLiveOpponents());

        enriched.IsLive.Should().BeFalse();
        enriched.LiveOpponent.Should().BeNull();
    }

    [Fact]
    public void IsLive_True_WhenTeamIsInBrLiveSet_LiveOpponentIsNull()
    {
        var teamId = Guid.NewGuid();
        var row = MakeRow(teamId);
        var brLiveTeams = new HashSet<Guid> { teamId };

        var enriched = StandingsResolutionService.EnrichRow(
            row, NoConfirmed(), isProvisional: false, NoLiveOpponents(), brLiveTeams);

        enriched.IsLive.Should().BeTrue();
        enriched.LiveOpponent.Should().BeNull();
    }

    [Fact]
    public void IsLive_False_WhenTeamNotInBrLiveSet()
    {
        var teamId = Guid.NewGuid();
        var row = MakeRow(teamId);
        var brLiveTeams = new HashSet<Guid> { Guid.NewGuid() };

        var enriched = StandingsResolutionService.EnrichRow(
            row, NoConfirmed(), isProvisional: false, NoLiveOpponents(), brLiveTeams);

        enriched.IsLive.Should().BeFalse();
        enriched.LiveOpponent.Should().BeNull();
    }
}
