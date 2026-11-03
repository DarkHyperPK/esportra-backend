using Esportra.Api.Services.ScrimReports;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ScrimReports.ReportFixture;

namespace Esportra.Api.Tests.ScrimReports;

public sealed class TrendsMathTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Maps 1-2 even (10-10), maps 3-7 lost on rounds (7-13), maps 8-12 won on rounds (13-7).</summary>
    private static List<ReportMatch> Twelve() => Enumerable.Range(1, 12)
        .Select(i => i <= 2 ? Map("Lotus", "win", 10, 10, i) : i <= 7 ? Map("Bind", "loss", 7, 13, i) : Map("Lotus", "win", 13, 7, i)).ToList();

    [Fact]
    public void The_series_carries_per_map_figures_and_a_trailing_mean_only_once_there_are_three_maps()
    {
        var t = TrendsMath.Build(Twelve(), capped: false, Now);
        t.Series.Should().HaveCount(12);
        t.Series.Take(3).Select(p => p.RollingRoundWinPct).Should().Equal(null, null, 45.0);
        t.Series[4].RollingRoundWinPct.Should().Be(41.0);
        (t.Series[0].RoundWinPct, t.Series[0].TeamAcs, t.Series[0].FirstKillPct, t.Series[0].TradedPct).Should().Be((50.0, 150.0, 66.7, 33.3));
    }

    [Fact]
    public void The_last_five_maps_are_set_against_the_five_before_with_a_noise_threshold()
    {
        var w = TrendsMath.Build(Twelve(), capped: false, Now).Windows[0];
        (w.Enough, w.CurrentMaps, w.PreviousMaps).Should().Be((true, 5, 5));
        var round = w.Metrics.Single(m => m.Key == "round_win");
        (round.Current, round.Previous, round.Change, round.Verdict).Should().Be((65.0, 35.0, 30.0, "up"));
        w.Metrics.Single(m => m.Key == "acs").Verdict.Should().Be("flat");
        w.Metrics.Should().HaveCount(6);
    }

    [Fact]
    public void A_window_without_enough_maps_in_both_halves_says_so_and_compares_nothing()
    {
        var t = TrendsMath.Build(Twelve(), capped: false, Now);
        t.Windows[1].Should().Be(new TrendWindow("Last 10 maps against the 10 before", false, 10, 2, []));
        (t.Windows[2].Enough, t.Windows[2].CurrentMaps, t.Windows[2].PreviousMaps).Should().Be((false, 12, 0));
    }

    [Fact]
    public void No_maps_gives_an_empty_trend()
    {
        var t = TrendsMath.Build([], capped: false, Now);
        t.Series.Should().BeEmpty();
        t.Windows.Should().OnlyContain(w => !w.Enough);
    }
}
