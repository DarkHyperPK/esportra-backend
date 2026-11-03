using Esportra.Api.Services.ScrimReports;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ScrimInsights.InsightsFixture;

namespace Esportra.Api.Tests.ScrimReports;

public sealed class HeatmapMathTests
{
    private static string Json(string puuid, int x, int y) => System.Text.Json.JsonSerializer.Serialize(new object[]
    {
        new { puuid, viewRadians = 1.0, location = new { x, y } }, new { puuid = "other", location = new { x = 99, y = 99 } },
    });

    private static readonly Dictionary<(int, int), string?> Locations = new() { [(1, 1)] = Json("a1", 10, 20), [(3, 1)] = Json("a2", 30, 40) };

    [Fact]
    public void Kills_are_where_the_killer_stood_and_only_the_teams_own_kills_with_a_recorded_location()
    {
        var pts = HeatmapMath.Points(Sample(), Locations, "kills", null);
        pts.Should().BeEquivalentTo([new HeatPoint(10, 20, 1, "attack"), new HeatPoint(30, 40, 3, "defense")]);
    }

    [Fact]
    public void The_side_filter_keeps_only_rounds_the_team_played_on_that_side()
    {
        HeatmapMath.Points(Sample(), Locations, "kills", "attack").Should().ContainSingle().Which.Round.Should().Be(1);
        HeatmapMath.Points(Sample(), Locations, "kills", "defense").Should().ContainSingle().Which.Round.Should().Be(3);
    }

    [Fact]
    public void Opening_kills_use_only_the_first_kill_of_each_round()
    {
        HeatmapMath.Points(Sample(), Locations, "opening_kills", null).Select(p => p.Round).Should().Equal(1, 3);
    }

    [Fact]
    public void Deaths_are_where_the_victim_fell_and_opening_deaths_only_the_first_death_of_a_round()
    {
        HeatmapMath.Points(Sample(), new Dictionary<(int, int), string?>(), "deaths", null).Should().HaveCount(3).And.OnlyContain(p => p.X == 100 && p.Y == 200);
        HeatmapMath.Points(Sample(), new Dictionary<(int, int), string?>(), "opening_deaths", null).Should().ContainSingle().Which.Round.Should().Be(2);
        HeatmapMath.Points(Sample(), new Dictionary<(int, int), string?>(), "deaths", "defense").Should().BeEmpty();
    }

    [Fact]
    public void Plants_and_defuses_are_the_teams_own_with_recorded_coordinates()
    {
        var m = Sample();
        var withCoords = m with { Rounds = m.Rounds.Select(r => r.Planter is null ? r : r with { PlantX = 5, PlantY = 6 }).ToList() };
        HeatmapMath.Points(withCoords, new Dictionary<(int, int), string?>(), "plants", null).Should().ContainSingle().Which.Should().Be(new HeatPoint(5, 6, 1, "attack"));
        HeatmapMath.Points(m, new Dictionary<(int, int), string?>(), "plants", null).Should().BeEmpty();
        HeatmapMath.Points(m, new Dictionary<(int, int), string?>(), "defuses", null).Should().BeEmpty();
    }

    [Fact]
    public void Round_numbers_from_riot_start_at_zero_and_the_points_still_line_up()
    {
        var m = Sample();
        var zero = m with
        {
            Rounds = m.Rounds.Select(r => r with { Num = r.Num - 1 }).ToList(),
            Kills = m.Kills.Select(k => k with { Round = k.Round - 1 }).ToList(),
            Economy = m.Economy.Select(e => e with { Round = e.Round - 1 }).ToList(),
            Damage = m.Damage.Select(d => d with { Round = d.Round - 1 }).ToList(),
        };
        var zeroLocations = new Dictionary<(int, int), string?> { [(0, 1)] = Json("a1", 10, 20), [(2, 1)] = Json("a2", 30, 40) };
        HeatmapMath.Points(zero, zeroLocations, "kills", null).Select(p => (p.X, p.Round)).Should().Equal((10, 1), (30, 3));
    }

    [Theory]
    [InlineData(null, "a1")]
    [InlineData("not json", "a1")]
    [InlineData("""[{"puuid":"zz","location":{"x":1,"y":2}}]""", "a1")]
    [InlineData("""[{"puuid":"a1"}]""", "a1")]
    public void A_missing_or_unreadable_location_is_nothing_not_a_guess(string? json, string puuid)
    {
        HeatmapMath.LocationOf(json, puuid).Should().BeNull();
    }

    [Fact]
    public void A_point_at_the_origin_is_an_unrecorded_location_not_a_position()
    {
        var m = Sample();
        var zeroed = m with { Kills = m.Kills.Select(k => k with { X = 0, Y = 0 }).ToList() };
        HeatmapMath.Points(zeroed, new Dictionary<(int, int), string?>(), "deaths", null).Should().BeEmpty();
    }
}
