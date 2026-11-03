using Esportra.Api.Services.ScrimReports;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ScrimReports.ReportFixture;

namespace Esportra.Api.Tests.ScrimReports;

public sealed class PlayerReportMathTests
{
    [Fact]
    public void A_players_report_has_their_series_splits_hits_and_comparison_with_the_team()
    {
        var r = PlayerReportMath.Build(Nova, Six(), capped: false)!;
        r.Summary.Maps.Should().Be(6);
        r.Series.Select(s => s.Map).Should().Equal("Lotus", "Lotus", "Lotus", "Bind", "Bind", "Bind");
        r.ByMap.Select(s => (s.Key, s.Maps, s.Wins, s.WinRatePct)).Should().Equal(("Bind", 3, 0, 0.0), ("Lotus", 3, 3, 100.0));
        r.ByAgent.Should().ContainSingle().Which.Maps.Should().Be(6);
        r.Hits.Should().Be(new HitShare(12, 36, 12, 20.0, 60.0, 20.0));
        (r.Comparison.TeamAcs, r.Comparison.AcsVsTeam, r.Comparison.AcsChange).Should().Be((150.0, 50.0, null));
    }

    [Fact]
    public void Form_compares_the_last_five_maps_with_the_five_before_only_with_ten_maps()
    {
        var ten = Enumerable.Range(1, 10).Select(i => Map("Lotus", "win", 13, 7, i)).ToList();
        var r = PlayerReportMath.Build(Nova, ten, capped: false)!;
        (r.Comparison.Last5Acs, r.Comparison.Previous5Acs, r.Comparison.AcsChange).Should().Be((200.0, 200.0, 0.0));
    }

    [Fact]
    public void Highlights_say_when_a_player_is_consistent_and_never_compare_equal_maps()
    {
        var r = PlayerReportMath.Build(Nova, Six(), capped: false)!;
        r.Highlights.Select(h => h.Kind).Should().Equal("consistency");
        r.Highlights.Single().Title.Should().Be("Consistent from map to map");
    }

    [Fact]
    public void A_player_with_no_verified_maps_has_no_report()
    {
        PlayerReportMath.Build(Guid.NewGuid(), Six(), capped: false).Should().BeNull();
    }
}
