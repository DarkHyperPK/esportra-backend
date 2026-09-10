using Esportra.Core.Bracket;
using Esportra.Core.Tournaments.Resolvers;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class SwissStandingsResolverTests
{
    private static TeamStanding MakeStanding(
        int points, int buchholz, int scoreDiff, int wins, string name = "Team") =>
        new(
            TeamId: Guid.NewGuid(),
            TeamName: name,
            Played: wins + 1,
            Wins: wins,
            Losses: 1,
            Ties: 0,
            Points: points,
            Buchholz: buchholz,
            ScoreDiff: scoreDiff,
            RoundDiff: 0,
            Rank: 0);

    [Fact]
    public void AssignRanksAndTies_NoTie_SequentialRanks()
    {
        var standings = new List<TeamStanding>
        {
            MakeStanding(points: 9, buchholz: 6, scoreDiff: 5, wins: 3),
            MakeStanding(points: 6, buchholz: 5, scoreDiff: 2, wins: 2),
            MakeStanding(points: 3, buchholz: 4, scoreDiff: -1, wins: 1),
        };

        var result = SwissStandingsResolver.AssignRanksAndTies(standings, []);

        result.Should().HaveCount(3);
        result[0].Rank.Should().Be(1);
        result[1].Rank.Should().Be(2);
        result[2].Rank.Should().Be(3);
        result.Should().AllSatisfy(r => r.IsTied.Should().BeFalse());
    }

    [Fact]
    public void AssignRanksAndTies_TwoTeamsTied_SameRankAndIsTiedTrue()
    {
        var standings = new List<TeamStanding>
        {
            MakeStanding(points: 6, buchholz: 5, scoreDiff: 2, wins: 2, "Alpha"),
            MakeStanding(points: 6, buchholz: 5, scoreDiff: 2, wins: 2, "Beta"),
            MakeStanding(points: 3, buchholz: 4, scoreDiff: -1, wins: 1),
        };

        var result = SwissStandingsResolver.AssignRanksAndTies(standings, []);

        result.Should().HaveCount(3);
        result[0].Rank.Should().Be(1);
        result[0].IsTied.Should().BeTrue();
        result[1].Rank.Should().Be(1);
        result[1].IsTied.Should().BeTrue();
        result[2].Rank.Should().Be(3);
        result[2].IsTied.Should().BeFalse();
    }

    [Fact]
    public void AssignRanksAndTies_PartialTiebreak_OnlyIdenticalBandsAreTied()
    {
        // Teams with same Points but different Buchholz are NOT tied
        var standings = new List<TeamStanding>
        {
            MakeStanding(points: 6, buchholz: 7, scoreDiff: 0, wins: 2),
            MakeStanding(points: 6, buchholz: 5, scoreDiff: 0, wins: 2),
        };

        var result = SwissStandingsResolver.AssignRanksAndTies(standings, []);

        result[0].Rank.Should().Be(1);
        result[1].Rank.Should().Be(2);
        result.Should().AllSatisfy(r => r.IsTied.Should().BeFalse());
    }

    [Fact]
    public void AssignRanksAndTies_EmptyList_ReturnsEmpty()
    {
        var result = SwissStandingsResolver.AssignRanksAndTies([], []);
        result.Should().BeEmpty();
    }

    [Fact]
    public void AssignRanksAndTies_WithRoundResults_PopulatesRoundResultsOnRow()
    {
        var teamId = Guid.NewGuid();
        var standings = new List<TeamStanding>
        {
            new(TeamId: teamId, TeamName: "Alpha", Played: 2, Wins: 2,
                Losses: 0, Ties: 0, Points: 6, Buchholz: 4, ScoreDiff: 3, RoundDiff: 0, Rank: 0),
        };
        var roundResults = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [teamId] = ["win", "win"],
        };

        var result = SwissStandingsResolver.AssignRanksAndTies(standings, roundResults);

        result.Should().HaveCount(1);
        result[0].RoundResults.Should().Equal("win", "win");
    }

    [Fact]
    public void AssignRanksAndTies_TeamMissingFromRoundResults_RoundResultsIsNull()
    {
        var standings = new List<TeamStanding>
        {
            MakeStanding(points: 6, buchholz: 5, scoreDiff: 2, wins: 2, "Beta"),
        };

        var result = SwissStandingsResolver.AssignRanksAndTies(standings, []);

        result[0].RoundResults.Should().BeNull();
    }

    [Fact]
    public void BuildAllTeamRoundResults_WinLossAndBye_ReturnsCorrectResults()
    {
        var team1 = Guid.NewGuid();
        var team2 = Guid.NewGuid();
        var team3 = Guid.NewGuid();
        // Round 0: team1 vs team2 → team1 wins
        // Round 1: team1 vs team3 → team3 wins (team2 absent → bye)
        var matches = new List<MatchRoundRow>
        {
            new(RoundIndex: 0, WinnerId: team1, Team1Id: team1, Team2Id: team2),
            new(RoundIndex: 1, WinnerId: team3, Team1Id: team1, Team2Id: team3),
        };

        var results = SwissStandingsResolver.BuildAllTeamRoundResults(matches);

        results[team1].Should().Equal("win", "loss");
        results[team2].Should().Equal("loss", "bye");
        results[team3].Should().Equal("bye", "win");
    }

    [Fact]
    public void BuildAllTeamRoundResults_EmptyMatches_ReturnsEmptyDictionary()
    {
        var results = SwissStandingsResolver.BuildAllTeamRoundResults([]);
        results.Should().BeEmpty();
    }
}
