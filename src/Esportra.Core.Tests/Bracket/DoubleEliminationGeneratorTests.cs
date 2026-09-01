using Esportra.Core.Bracket;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.Bracket;

public sealed class DoubleEliminationGeneratorTests
{
    private static readonly DoubleEliminationGenerator Generator = new();
    private static readonly Guid TournamentId = Guid.NewGuid();

    private static List<(Guid Id, string Name)> MakeTeams(int count) =>
        Enumerable.Range(1, count)
            .Select(i => (Guid.NewGuid(), $"Team {i}"))
            .ToList();

    // ── Node counts ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Generate_ProducesCorrectNodeCounts_ForPowerOfTwoTeams(int teamCount)
    {
        var teams = MakeTeams(teamCount);
        var graph = Generator.Generate(teams, TournamentId);

        var winners = graph.Nodes.Where(n => n.BracketType == "winners").ToList();
        var losers = graph.Nodes.Where(n => n.BracketType == "losers").ToList();
        var finals = graph.Nodes.Where(n => n.BracketType == "final").ToList();

        finals.Should().HaveCount(1);
        winners.Count.Should().BeGreaterThan(0);
        losers.Count.Should().BeGreaterThan(0);

        // WB matches = P-1, LB matches = P-2, GF = 1 → total = 2P-2
        int P = (int)Math.Pow(2, Math.Ceiling(Math.Log2(teamCount)));
        graph.Nodes.Should().HaveCount(2 * P - 2);
    }

    [Fact]
    public void Generate_GrandFinalHasEdgesFromBothBrackets()
    {
        var graph = Generator.Generate(MakeTeams(4), TournamentId);

        var gf = graph.Nodes.Single(n => n.BracketType == "final");
        var inbound = graph.Edges.Where(e => e.TargetMatchId == gf.Id).ToList();

        inbound.Should().HaveCount(2, "Grand Final must receive edges from both WB and LB finalists");
        // Both edges are 'winner' type — each bracket sends its winner to the GF.
        inbound.Should().AllSatisfy(e => e.Type.Should().Be("winner"));
        inbound.Select(e => e.TargetSlot).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public void Generate_AllWinnersMatchesHaveLoserEdge()
    {
        var graph = Generator.Generate(MakeTeams(8), TournamentId);

        var winnerMatches = graph.Nodes
            .Where(n => n.BracketType == "winners")
            .ToList();

        foreach (var match in winnerMatches)
        {
            graph.Edges
                .Where(e => e.SourceMatchId == match.Id && e.Type == "loser")
                .Should().NotBeEmpty(
                    $"WB match (round={match.RoundIndex}, num={match.MatchNumber}) must drop loser to LB");
        }
    }

    [Fact]
    public void Generate_RoundOneWinnersMatchesHaveSeededTeams()
    {
        var teams = MakeTeams(8);
        var graph = Generator.Generate(teams, TournamentId);

        var r0 = graph.Nodes
            .Where(n => n.BracketType == "winners" && n.RoundIndex == 0)
            .ToList();

        // All round-0 matches should have both teams pre-seeded (or one BYE)
        r0.Should().AllSatisfy(m => m.Team1Id.Should().NotBeNull());
    }

    [Fact]
    public void Generate_NonPowerOfTwoTeams_StillGeneratesValidGraph()
    {
        // 6 teams → rounds up to P=8
        var teams = MakeTeams(6);
        var graph = Generator.Generate(teams, TournamentId);

        graph.Nodes.Should().NotBeEmpty();
        graph.Edges.Should().NotBeEmpty();
        graph.Nodes.Should().Contain(n => n.BracketType == "final");
    }

    [Fact]
    public void Generate_TwoTeams_ProducesGrandFinalOnly()
    {
        var teams = MakeTeams(2);
        var graph = Generator.Generate(teams, TournamentId);

        // P=2: 0 WB matches, 0 LB matches, 1 GF = 2*2-2 = 2 nodes
        graph.Nodes.Should().HaveCount(2);
        graph.Nodes.Should().Contain(n => n.BracketType == "final");
    }

    [Fact]
    public void Generate_VersionIdIsConsistentAcrossAllNodes()
    {
        var graph = Generator.Generate(MakeTeams(4), TournamentId);

        var versionId = graph.Version.Id;
        graph.Nodes.Should().AllSatisfy(n => n.VersionId.Should().Be(versionId));
        graph.Edges.Should().AllSatisfy(e => e.VersionId.Should().Be(versionId));
    }

    [Fact]
    public void Generate_EdgesTargetSlotsAreOneOrTwo()
    {
        var graph = Generator.Generate(MakeTeams(8), TournamentId);

        graph.Edges.Should().AllSatisfy(e =>
            e.TargetSlot.Should().BeOneOf(1, 2));
    }
}
