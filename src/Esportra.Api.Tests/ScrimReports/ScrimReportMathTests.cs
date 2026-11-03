using Esportra.Api.Endpoints;
using Esportra.Api.Services.ScrimInsights;
using Esportra.Api.Services.ScrimReports;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ScrimReports.ReportFixture;

namespace Esportra.Api.Tests.ScrimReports;

public sealed class ScrimReportMathTests
{
    private static TeamReport Two() => ScrimReportMath.Team([Map("Lotus", "win", 13, 7, 1), Map("Bind", "loss", 5, 13, 2)], capped: false);

    [Fact]
    public void Totals_series_and_maps_follow_the_results_in_date_order()
    {
        var r = Two();
        (r.Sample.Maps, r.Wins, r.Losses, r.WinRatePct, r.RoundWinPct).Should().Be((2, 1, 1, 50.0, 47.4));
        r.Series.Select(s => (s.Index, s.Map, s.Result, s.RoundWinPct)).Should().Equal((1, "Lotus", "win", 65.0), (2, "Bind", "loss", 27.8));
        r.Maps.Select(m => (m.Map, m.Games, m.Wins)).Should().Equal(("Bind", 1, 0), ("Lotus", 1, 1));
    }

    [Fact]
    public void Each_series_point_carries_team_acs_adr_first_blood_and_pistol()
    {
        var p = Two().Series[0];
        (p.TeamAcs, p.TeamAdr, p.FirstBloodPct, p.PistolWon).Should().Be((150.0, 66.7, 66.7, true));
    }

    [Fact]
    public void Sides_openings_pistols_and_buys_add_up_across_maps_with_their_samples()
    {
        var r = Two();
        (r.Attack, r.Defense).Should().Be((new Rate(2, 4, 50), new Rate(2, 2, 100)));
        (r.FirstBloodRounds, r.WinAfterFirstBlood, r.WinAfterFirstDeath).Should().Be((new Rate(4, 6, 66.7), new Rate(4, 4, 100), new Rate(0, 2, 0)));
        r.Pistol.Should().Be(new Rate(2, 2, 100));
        r.Buys.Select(b => (b.Buy, b.RoundsWon.Count, b.RoundsWon.Of)).Should().Equal(("pistol", 2, 2), ("eco", 0, 2), ("full", 2, 2));
    }

    [Fact]
    public void Players_are_round_weighted_from_the_roster_and_say_how_many_maps()
    {
        var nova = Two().Players.Single(p => p.UserId == Nova);
        (nova.Maps, nova.Rounds, nova.Acs, nova.Kills, nova.Deaths, nova.Kd, nova.HsPct, nova.AcsSd).Should().Be((2, 6, 200.0, 4, 2, 2.0, 20.0, 0.0));
        nova.Agents.Should().ContainSingle().Which.Maps.Should().Be(2);
        Two().Players.Single(p => p.UserId == Ray).Acs.Should().Be(100.0);
    }

    [Fact]
    public void Highlights_appear_only_when_the_sample_supports_them_and_name_their_evidence()
    {
        Two().Highlights.Should().BeEmpty();   // two maps support nothing
        var six = ScrimReportMath.Team(Six(), capped: false);
        six.Highlights.Select(h => h.Kind).Should().BeEquivalentTo(["best_map", "weakest_map", "pistol", "top_acs"]);
        six.Highlights.Single(h => h.Kind == "best_map").Detail.Should().Contain("3–0 over 3 maps");
        six.Highlights.Single(h => h.Kind == "weakest_map").Title.Should().Be("Weakest map: Bind");
        six.Highlights.Single(h => h.Kind == "top_acs").PlayerId.Should().Be(Nova);
    }

    [Fact]
    public void A_regular_member_sees_team_figures_and_only_their_own_row_and_findings()
    {
        var six = ScrimReportMath.Team(Six(), capped: false);
        var scoped = ScrimReportEndpoints.Scope(six, Ray);
        scoped.Players.Should().ContainSingle().Which.UserId.Should().Be(Ray);
        scoped.Highlights.Should().NotContain(h => h.PlayerId == Nova);
        scoped.Highlights.Should().Contain(h => h.Kind == "best_map");
        scoped.Wins.Should().Be(six.Wins);
    }

    [Fact]
    public void An_empty_team_has_empty_figures_not_zeros()
    {
        var r = ScrimReportMath.Team([], capped: false);
        (r.Sample.Maps, r.WinRatePct, r.RoundWinPct, r.Pistol.Pct).Should().Be((0, null, null, null));
        r.Series.Should().BeEmpty();
    }
}
