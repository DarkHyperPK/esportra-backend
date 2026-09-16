using Esportra.Core.Tournaments;
using Esportra.Core.Tournaments.Resolvers;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class RrStandingsResolverTests
{
    private static StandingsRow MakeRow(int rank, Guid teamId, string name, int scoreDiff = 0) =>
        new()
        {
            Rank = rank,
            TeamId = teamId,
            TeamName = name,
            Points = 6,
            Buchholz = 3,
            ScoreDiff = scoreDiff,
        };

    [Fact]
    public void HeadToHead_BreaksTie_WhenTwoTeamsEqualPoints()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var group = new List<StandingsRow>
        {
            MakeRow(1, teamA, "Alpha"),
            MakeRow(2, teamB, "Beta"),
        };
        // A beat B in H2H
        var h2h = new List<H2HMatch>
        {
            new(teamA, teamB, WinnerId: teamA, Team1Score: 2, Team2Score: 0),
        };

        var result = RrStandingsResolver.ApplyH2HTiebreak(group, h2h);

        result.Should().HaveCount(2);
        result.Single(r => r.TeamId == teamA).Rank.Should().Be(1);
        result.Single(r => r.TeamId == teamB).Rank.Should().Be(2);
        result.Should().AllSatisfy(r => r.IsTied.Should().BeFalse());
    }

    [Fact]
    public void DifferentNames_SameStats_NoH2H_AreTied()
    {
        // TeamName is only used for ORDER within a tied band, not to break the tie.
        // Two teams with identical H2HWins, H2HScoreDiff, and ScoreDiff are always tied
        // regardless of their names.
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var group = new List<StandingsRow>
        {
            MakeRow(1, teamA, "Alpha", scoreDiff: 0),
            MakeRow(1, teamB, "Beta", scoreDiff: 0),
        };
        var h2h = new List<H2HMatch>();

        var result = RrStandingsResolver.ApplyH2HTiebreak(group, h2h);

        // Both share the same H2HWins(0), H2HScoreDiff(0), and ScoreDiff(0) — genuine tie.
        result.Should().HaveCount(2);
        result.Should().AllSatisfy(r =>
        {
            r.Rank.Should().Be(1);
            r.IsTied.Should().BeTrue();
        });
    }

    [Fact]
    public void InseparableTie_SameNameAndStats_SetIsTiedTrue()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var group = new List<StandingsRow>
        {
            MakeRow(1, teamA, "Identical", scoreDiff: 0),
            MakeRow(1, teamB, "Identical", scoreDiff: 0),
        };
        var h2h = new List<H2HMatch>();

        var result = RrStandingsResolver.ApplyH2HTiebreak(group, h2h);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(r =>
        {
            r.Rank.Should().Be(1);
            r.IsTied.Should().BeTrue();
        });
    }

    [Fact]
    public void NoTie_IsTied_False()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var group = new List<StandingsRow>
        {
            MakeRow(1, teamA, "Alpha"),
            MakeRow(2, teamB, "Beta"),
        };
        // A won against B
        var h2h = new List<H2HMatch>
        {
            new(teamA, teamB, WinnerId: teamA, Team1Score: 1, Team2Score: 0),
        };

        var result = RrStandingsResolver.ApplyH2HTiebreak(group, h2h);

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(r => r.IsTied.Should().BeFalse());
    }
}
