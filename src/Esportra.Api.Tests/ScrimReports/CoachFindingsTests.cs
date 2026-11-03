using Esportra.Api.Services.ScrimInsights;
using Esportra.Api.Services.ScrimReports;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScrimReports;

public sealed class CoachFindingsTests
{
    private static readonly Rate None = new(0, 0, null);
    private static Rate R(int c, int o) => new(c, o, o == 0 ? null : Math.Round(100.0 * c / o, 1));

    private static CoachTeam Team(Rate? postPlant = null, Rate? retake = null, TradeCounts? trades = null, Rate? led = null, Rate? anti = null, Rate? bonus = null, IReadOnlyList<CoachUserRow>? players = null, Rate? slow = null) =>
        new(trades ?? new TradeCounts(0, 0, 0), led ?? None, None, [], postPlant ?? None, [], None, retake ?? None, [], bonus ?? None, None, anti ?? None, None, None, None, null, slow ?? None, players ?? []);

    [Fact]
    public void A_thin_sample_says_nothing()
    {
        CoachFindings.Build(Team(postPlant: R(1, 7), retake: R(0, 7), trades: new TradeCounts(19, 1, 1), led: R(3, 9), anti: R(1, 3), bonus: R(0, 3), slow: R(1, 7))).Should().BeEmpty();
    }

    [Fact]
    public void Each_rule_names_what_happened_with_the_evidence()
    {
        var findings = CoachFindings.Build(Team(postPlant: R(4, 10), retake: R(1, 10), trades: new TradeCounts(30, 6, 5), led: R(7, 10), anti: R(5, 8), bonus: R(1, 5), slow: R(2, 10)))
            .ToDictionary(f => f.Kind);
        findings["post_plant"].Should().BeEquivalentTo(new { Tone = "watch", Title = "Post-plant rounds are slipping away", Detail = "You won 4 of 10 (40%) of the rounds where you planted." });
        findings["retake"].Tone.Should().Be("watch");
        findings["trades"].Detail.Should().Contain("Only 6 of 30 deaths (20%)");
        findings["thrown_leads"].Detail.Should().Contain("7 of 10 (70%)");
        findings["anti_eco"].Detail.Should().Contain("5 of 8 (62.5%)");
        findings["bonus"].Detail.Should().Contain("1 of 5 (20%)");
        findings["slow_rounds"].Tone.Should().Be("watch");
    }

    [Fact]
    public void Strengths_are_named_too_and_player_findings_point_at_the_player()
    {
        var ray = Guid.NewGuid();
        var players = new[]
        {
            new CoachUserRow(ray, "Ray", 10, 40, 20, 5, 12, 6, 4, 14, 10, 6, 5),
        };
        var findings = CoachFindings.Build(Team(postPlant: R(8, 10), retake: R(5, 10), trades: new TradeCounts(40, 24, 20), players: players)).ToDictionary(f => f.Kind);
        findings["post_plant"].Tone.Should().Be("good");
        findings["retake"].Tone.Should().Be("good");
        findings["trades"].Tone.Should().Be("good");
        findings["untraded_openers"].PlayerId.Should().Be(ray);
        findings["closer"].Detail.Should().Be("4 of 6 clutch situations won.");
        findings["entry_good"].Detail.Should().Be("10 of 14 opening duels won (71.4%).");
    }
}
