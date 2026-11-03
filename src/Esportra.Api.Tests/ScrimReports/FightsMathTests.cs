using System.Text.Json;
using Esportra.Api.Services.ScrimReports;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ScrimInsights.InsightsFixture;

namespace Esportra.Api.Tests.ScrimReports;

public sealed class FightsMathTests
{
    private static string Json(params (string Puuid, int X, int Y)[] players) =>
        JsonSerializer.Serialize(players.Select(p => new { puuid = p.Puuid, location = new { x = p.X, y = p.Y } }));

    // Round 1 kill 2: a2 died at (100,200); teammate a1 stood 300 away, the enemy b1 was right next to a2 and must not count.
    private static readonly Dictionary<(int, int), string?> Locations = new() { [(1, 2)] = Json(("a2", 100, 200), ("a1", 100, 500), ("b1", 101, 200)) };

    [Fact]
    public void Every_fight_is_won_or_lost_from_the_teams_side_at_the_victims_position()
    {
        var fights = FightsMath.Build(Sample(), Locations, null);
        fights.Should().HaveCount(7);
        fights.Count(f => f.Won).Should().Be(4);
        fights.Should().OnlyContain(f => f.X == 100 && f.Y == 200);
    }

    [Fact]
    public void Only_the_teams_own_deaths_carry_spacing_and_trade_information()
    {
        var fights = FightsMath.Build(Sample(), Locations, null);
        fights.Where(f => f.Won).Should().OnlyContain(f => f.NearestAlly == null && f.Traded == null);
        var lost = fights.Where(f => !f.Won).ToList();
        lost.Should().HaveCount(3).And.OnlyContain(f => f.Traded != null);
        lost.Single(f => f.NearestAlly == 300).Round.Should().Be(1);
        lost.Single(f => f.Round == 1).Traded.Should().BeTrue();
        lost.Where(f => f.Round == 2).Should().OnlyContain(f => f.Traded == false && f.NearestAlly == null);
    }

    [Fact]
    public void The_side_filter_keeps_only_rounds_played_on_that_side()
    {
        FightsMath.Build(Sample(), Locations, "attack").Should().HaveCount(5);
        FightsMath.Build(Sample(), Locations, "defense").Should().HaveCount(2).And.OnlyContain(f => f.Won);
    }

    [Fact]
    public void A_fight_without_a_recorded_position_is_left_out()
    {
        var m = Sample();
        var blank = m with { Kills = m.Kills.Select((k, i) => i == 0 ? k with { X = null, Y = null } : k).ToList() };
        FightsMath.Build(blank, Locations, null).Should().HaveCount(6);
    }
}
