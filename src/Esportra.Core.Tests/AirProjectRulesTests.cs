using Esportra.Contracts.Air;
using Esportra.Core.Air;
using Esportra.Core.Broadcast;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests;
public sealed class AirProjectRulesTests
{
    private static AirProjectDocument Document() => new(1, [], [], [], new("manual", []));
    [Fact]
    public void Empty_project_is_a_valid_draft_without_a_tournament()
    {
        var act = () => AirProjectRules.Validate("Independent event", Document()); act.Should().NotThrow();
    }
    [Fact]
    public void Null_bracket_entries_return_validation_errors()
    {
        var document = Document() with { Bracket = new("manual", [new(Guid.NewGuid(), "Playoffs", "manual", [null!])]) };
        var act = () => AirProjectRules.Validate("Event", document); act.Should().Throw<ArgumentException>();
    }
    [Fact]
    public void Duplicate_match_team_and_impossible_scores_are_rejected()
    {
        var team = new AirTeam(Guid.NewGuid(), "Atlas", "ATL", null, []);
        var document = Document() with { Teams = [team], Matches = [new(Guid.NewGuid(), "Final", [team.Id, team.Id], 1, [], Scores: [2, 0])] };
        var act = () => AirProjectRules.Validate("Event", document); act.Should().Throw<ArgumentException>();
    }
    [Fact]
    public void Optional_people_and_sponsors_do_not_mutate_official_names()
    {
        var player = new AirPlayer(Guid.NewGuid(), "Original", "Player#TAG", HudNickname: "TV Name");
        var document = Document() with { Teams = [new(Guid.NewGuid(), "Atlas", "ATL", null, [player])], Talent = [new(Guid.NewGuid(), "Host", "Commentator")], Crew = [new(Guid.NewGuid(), "Operator", "Producer")], Sponsors = [new(Guid.NewGuid(), "Partner")] };
        AirProjectRules.Validate("Event", document); player.OriginalName.Should().Be("Original"); player.InGameName.Should().Be("Player#TAG");
    }
    [Fact]
    public void New_broadcast_templates_use_fade_300ms()
    {
        BroadcastRundownTemplates.Build(3).Items.Should().OnlyContain(item => item.Plan.Transition != null && item.Plan.Transition.Mode == "fade" && item.Plan.Transition.Name == "Fade" && item.Plan.Transition.DurationMs == 300);
    }
}
