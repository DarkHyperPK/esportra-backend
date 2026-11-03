using System.Text.Json;
using Esportra.Api.Services.ScrimInsights;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ScrimInsights.InsightsFixture;

namespace Esportra.Api.Tests.ScrimInsights;

public sealed class TimelineBuilderTests
{
    private static string Locations(params (string Puuid, int X, int Y, double View)[] ps) =>
        JsonSerializer.Serialize(ps.Select(p => new { puuid = p.Puuid, viewRadians = p.View, location = new { x = p.X, y = p.Y } }));

    private static MatchTimeline Build(IReadOnlyDictionary<(int, int), string?>? locations = null) => TimelineBuilder.Build(Sample(), locations ?? new Dictionary<(int, int), string?>());

    [Fact]
    public void Players_are_opaque_keys_with_the_viewers_team_first_and_no_puuid()
    {
        var t = Build();
        t.Players.Select(p => (p.Key, p.Name, p.Mine)).Should().Equal(("p1", "A1", true), ("p2", "A2", true), ("p3", "B1", false), ("p4", "B2", false));
        JsonSerializer.Serialize(t).Should().NotContain("\"a1\"").And.NotContain("\"b2\"");
    }

    [Fact]
    public void Rounds_carry_result_side_plant_and_kills_in_time_order()
    {
        var t = Build();
        t.Rounds.Select(r => (r.Num, r.Won, r.MyRole)).Should().Equal((1, true, "attack"), (2, false, "attack"), (3, true, "defense"));
        var r1 = t.Rounds[0];
        (r1.PlantSite, r1.PlantMs, r1.Planter).Should().Be(("A", 30000, "p1"));
        r1.Kills.Select(k => (k.Ms, k.Killer, k.Victim)).Should().Equal((5000, "p1", "p3"), (7000, "p4", "p2"), (9000, "p1", "p4"));
        t.Rounds[1].PlantSite.Should().BeNull();
    }

    [Fact]
    public void A_kills_recorded_positions_become_keyed_positions_with_facing_and_unusable_ones_are_dropped()
    {
        var json = Locations(("a1", 10, 20, 1.5), ("b1", 99, 98, 0.25), ("a2", 0, 0, 0.0), ("ghost", 5, 5, 0.0));
        var t = Build(new Dictionary<(int, int), string?> { [(1, 1)] = json });
        t.Rounds[0].Kills[0].Positions.Should().Equal(new TimelinePosition("p1", 10, 20, 1.5), new TimelinePosition("p3", 99, 98, 0.25));
        t.Rounds[0].Kills[1].Positions.Should().BeEmpty();
    }

    [Fact]
    public void Round_numbers_from_riot_start_at_zero_and_the_positions_still_line_up()
    {
        var m = Sample();
        var zero = m with
        {
            Rounds = m.Rounds.Select(r => r with { Num = r.Num - 1 }).ToList(),
            Kills = m.Kills.Select(k => k with { Round = k.Round - 1 }).ToList(),
            Economy = m.Economy.Select(e => e with { Round = e.Round - 1 }).ToList(),
            Damage = m.Damage.Select(d => d with { Round = d.Round - 1 }).ToList(),
        };
        var t = TimelineBuilder.Build(zero, new Dictionary<(int, int), string?> { [(0, 1)] = Locations(("a1", 10, 20, 1.0)) });
        t.Rounds.Select(r => r.Num).Should().Equal(1, 2, 3);
        t.Rounds[0].Kills[0].Positions.Should().ContainSingle().Which.Key.Should().Be("p1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""[{"puuid":"a1"}]""")]
    public void Missing_or_unreadable_positions_are_nothing_not_a_guess(string? json)
    {
        TimelineBuilder.Positions(json, new Dictionary<string, string> { ["a1"] = "p1" }).Should().BeEmpty();
    }
}
