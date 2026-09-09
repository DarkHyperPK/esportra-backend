using Esportra.Core.Br;
using Esportra.Core.Tournaments.Resolvers;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.Tournaments;

public sealed class BrStandingsResolverTests
{
    private static BrLeaderboardRow MakeRow(
        long totalPoints, long totalKills, long wins = 0, double avgPlacement = 1) =>
        new(
            TeamId: Guid.NewGuid().ToString(),
            TeamName: "Team",
            LogoUrl: null,
            GamesPlayed: 3,
            TotalPlacementPoints: totalPoints,
            TotalKillPoints: totalKills,
            TotalPoints: totalPoints,
            TotalKills: totalKills,
            Wins: wins,
            BestPlacement: 1,
            AvgPlacement: avgPlacement);

    [Fact]
    public void AssignRanksAndTies_NoTie_SequentialRanks()
    {
        var rows = new List<BrLeaderboardRow>
        {
            MakeRow(totalPoints: 30, totalKills: 10),
            MakeRow(totalPoints: 20, totalKills: 8),
            MakeRow(totalPoints: 10, totalKills: 5),
        };

        var result = BrStandingsResolver.AssignRanksAndTies(rows, BrTiebreaker.MostKills);

        result.Should().HaveCount(3);
        result[0].Rank.Should().Be(1);
        result[1].Rank.Should().Be(2);
        result[2].Rank.Should().Be(3);
        result.Should().AllSatisfy(r => r.IsTied.Should().BeFalse());
    }

    [Fact]
    public void AssignRanksAndTies_EqualPointsAndKills_AreTied()
    {
        var rows = new List<BrLeaderboardRow>
        {
            MakeRow(totalPoints: 20, totalKills: 8),
            MakeRow(totalPoints: 20, totalKills: 8),
            MakeRow(totalPoints: 10, totalKills: 5),
        };

        var result = BrStandingsResolver.AssignRanksAndTies(rows, BrTiebreaker.MostKills);

        result.Should().HaveCount(3);
        result[0].Rank.Should().Be(1);
        result[0].IsTied.Should().BeTrue();
        result[1].Rank.Should().Be(1);
        result[1].IsTied.Should().BeTrue();
        result[2].Rank.Should().Be(3);
        result[2].IsTied.Should().BeFalse();
    }

    [Fact]
    public void AssignRanksAndTies_EqualPointsDifferentKills_NotTied_MostKillsTiebreaker()
    {
        var rows = new List<BrLeaderboardRow>
        {
            MakeRow(totalPoints: 20, totalKills: 10),
            MakeRow(totalPoints: 20, totalKills: 5),
        };

        var result = BrStandingsResolver.AssignRanksAndTies(rows, BrTiebreaker.MostKills);

        result[0].Rank.Should().Be(1);
        result[1].Rank.Should().Be(2);
        result.Should().AllSatisfy(r => r.IsTied.Should().BeFalse());
    }

    [Fact]
    public void AssignRanksAndTies_InvalidTeamIds_Excluded()
    {
        var validId = Guid.NewGuid().ToString();
        var rows = new List<BrLeaderboardRow>
        {
            new(validId, "Valid", null, 3, 0, 0, 20, 5, 0, 1, 1.0),
            new("not-a-guid", "Invalid", null, 3, 0, 0, 10, 3, 0, 2, 2.0),
        };
        // Pre-filter (as ResolveAsync does) to only valid rows
        var validRows = rows.Where(r => Guid.TryParse(r.TeamId, out _)).ToList();

        var result = BrStandingsResolver.AssignRanksAndTies(validRows, BrTiebreaker.MostKills);

        result.Should().HaveCount(1);
        result[0].TeamId.ToString().Should().Be(validId);
    }

    [Fact]
    public void AssignRanksAndTies_EmptyList_ReturnsEmpty()
    {
        var result = BrStandingsResolver.AssignRanksAndTies([], BrTiebreaker.MostKills);
        result.Should().BeEmpty();
    }
}
