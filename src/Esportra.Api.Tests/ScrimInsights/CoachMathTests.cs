using Esportra.Api.Services.ScrimInsights;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ScrimInsights.InsightsFixture;

namespace Esportra.Api.Tests.ScrimInsights;

/// <summary>The Sample match: R1 a1 opens, a2 is killed and traded, a1 wins a 1v1 and plants; R2 a1 dies untraded then a2 loses a 1v2; R3 a2 opens and a1 closes.</summary>
public sealed class CoachMathTests
{
    private static readonly CoachMap Coach = ScrimInsightsBuilder.Build(Sample()).Coach;

    [Fact]
    public void Trades_count_deaths_avenged_within_five_seconds_and_the_avenging_kills()
    {
        Coach.Trades.Should().Be(new TradeCounts(3, 1, 1));
    }

    [Fact]
    public void Leads_and_trailing_are_judged_from_who_was_alive_not_from_totals()
    {
        Coach.LedRounds.Should().Be(new Rate(2, 2, 100));
        Coach.TrailedRounds.Should().Be(new Rate(0, 1, 0));
    }

    [Fact]
    public void A_clutch_is_the_last_player_alive_against_at_least_one_enemy_and_says_how_many()
    {
        Coach.Clutches.Should().Equal(new ClutchLine(1, "p1", 1, true), new ClutchLine(2, "p2", 2, false));
    }

    [Fact]
    public void Plants_retakes_and_attack_rounds_without_a_plant_are_separate_questions()
    {
        Coach.PostPlant.Should().Be(new Rate(1, 1, 100));
        Coach.PostPlantBySite.Should().ContainSingle().Which.Should().Be(new SiteRate("A", new Rate(1, 1, 100)));
        Coach.NoPlantOnAttack.Should().Be(new Rate(0, 1, 0));
        Coach.Retake.Should().Be(new Rate(0, 0, null));
    }

    [Fact]
    public void Economy_conversion_follows_the_pistol_round_and_the_loadout_gap()
    {
        Coach.AfterPistolWin.Should().Be(new Rate(0, 1, 0));
        Coach.AfterPistolLoss.Should().Be(new Rate(0, 0, null));
        Coach.EcoUpsets.Should().Be(new Rate(0, 1, 0));
        Coach.AntiEco.Should().Be(new Rate(0, 0, null));
        (Coach.LoadoutAhead, Coach.LoadoutBehind).Should().Be((new Rate(1, 1, 100), new Rate(0, 1, 0)));
    }

    [Fact]
    public void Tempo_is_the_time_of_the_first_kill_and_slow_rounds_need_a_slow_first_kill()
    {
        Coach.AvgFirstKillMs.Should().Be(4000.0);
        Coach.SlowRounds.Should().Be(new Rate(0, 0, null));
    }

    [Fact]
    public void Each_player_gets_their_own_trade_clutch_entry_and_multi_kill_lines()
    {
        var p1 = Coach.Players.Single(p => p.Key == "p1");
        (p1.Deaths, p1.Traded, p1.UntradedFirstDeaths, p1.TradeKills, p1.ClutchAttempts, p1.ClutchWins, p1.EntryDuels, p1.EntryWins, p1.MultiKillRounds, p1.MultiKillRoundsWon)
            .Should().Be((1, 0, 1, 1, 1, 1, 2, 1, 1, 1));
        var p2 = Coach.Players.Single(p => p.Key == "p2");
        (p2.Deaths, p2.Traded, p2.UntradedFirstDeaths, p2.TradeKills, p2.ClutchAttempts, p2.ClutchWins, p2.EntryDuels, p2.EntryWins, p2.MultiKillRounds)
            .Should().Be((2, 1, 0, 0, 1, 0, 1, 1, 0));
        Coach.Players.Should().HaveCount(2);
    }
}
