using Esportra.Api.Services.ScrimInsights;
using FluentAssertions;
using Xunit;
using static Esportra.Api.Tests.ScrimInsights.InsightsFixture;

namespace Esportra.Api.Tests.ScrimInsights;

public sealed class ScrimInsightsBuilderTests
{
    private static readonly InsightsReport Report = ScrimInsightsBuilder.Build(Sample());

    [Fact]
    public void Keys_hide_puuids_and_put_the_viewers_team_first()
    {
        Report.Players.Select(p => (p.Key, p.Mine)).Should().Equal(("p1", true), ("p2", true), ("p3", false), ("p4", false));
        Report.Players.Select(p => p.Name).Should().Equal("A1", "A2", "B1", "B2");
    }

    [Fact]
    public void Rounds_carry_result_role_score_progression_and_who_opened()
    {
        Report.Rounds.Select(r => r.Won).Should().Equal(true, false, true);
        Report.Rounds.Select(r => r.MyRole).Should().Equal("attack", "attack", "defense");
        Report.Rounds.Select(r => (r.MyScore, r.TheirScore)).Should().Equal((1, 0), (1, 1), (2, 1));
        Report.Rounds.Select(r => r.FirstKiller).Should().Equal("p1", "p3", "p2");
        Report.Rounds.Select(r => r.FirstBloodMine).Should().Equal(true, false, true);
        Report.Rounds[0].PlantSite.Should().Be("A");
    }

    [Fact]
    public void Streaks_and_halves_use_only_rounds_with_a_result()
    {
        Report.Streaks.Should().Be(new Streaks(1, 1));
        Report.Halves.First.Should().Be(new Rate(2, 3, 66.7));
        Report.Halves.Second.Should().Be(new Rate(0, 0, null));
    }

    [Fact]
    public void Opening_duels_count_first_kills_and_what_followed()
    {
        var o = Report.Opening;
        o.WinAfterFirstBlood.Should().Be(new Rate(2, 2, 100));
        o.WinAfterFirstDeath.Should().Be(new Rate(0, 1, 0));
        o.FirstBloodRounds.Should().Be(new Rate(2, 3, 66.7));
        o.AttackFirstBlood.Should().Be(new Rate(1, 2, 50));
        o.DefenseFirstBlood.Should().Be(new Rate(1, 1, 100));
        var p1 = o.Players.Single(p => p.Key == "p1");
        (p1.Kills, p1.Deaths, p1.DuelsWon.Pct, p1.AvgMsToFirstKill, p1.AvgMsToFirstDeath).Should().Be((1, 1, 50.0, 5000.0, 4000.0));
        o.Players.Single(p => p.Key == "p4").AvgMsToFirstKill.Should().BeNull();
    }

    [Fact]
    public void Kast_counts_kills_assists_survival_and_trades()
    {
        // a2: traded in R1, dies untraded in R2, opens R3.  a1: kill R1, dies R2 untraded, kill R3.
        Report.Players.Single(p => p.Key == "p2").Kast.Should().Be(66.7);
        Report.Players.Single(p => p.Key == "p1").Kast.Should().Be(66.7);
    }

    [Fact]
    public void Player_lines_derive_rates_from_riots_totals()
    {
        var p1 = Report.Players.Single(p => p.Key == "p1");
        (p1.Acs, p1.Adr, p1.Kd, p1.HsPct, p1.BodyPct, p1.LegPct).Should().Be((200.0, 100.0, 2.0, 20.0, 60.0, 20.0));
        (p1.DamageReceived, p1.DamageShare, p1.KillShare, p1.DamagePerKill).Should().Be((100, 75.0, 66.7, 150.0));
    }

    [Fact]
    public void Economy_labels_buys_and_splits_results_by_them()
    {
        var e = Report.Economy;
        Report.Rounds.Select(r => r.MyBuy).Should().Equal("pistol", "eco", "full");
        Report.Rounds.Select(r => r.TheirBuy).Should().Equal("pistol", "full", "force");
        e.WinRateByBuy.Select(b => (b.Buy, b.RoundsWon.Count, b.RoundsWon.Of)).Should().Equal(("pistol", 1, 1), ("eco", 0, 1), ("full", 1, 1));
        (e.AvgLoadoutMine, e.AvgLoadoutTheirs, e.AvgLoadoutInWonRounds, e.AvgLoadoutInLostRounds).Should().Be((1933.0, 2600.0, 2400.0, 1000.0));
        e.PistolRounds.Should().Be(new Rate(1, 1, 100));
        e.Weapons.Should().Equal(new WeaponUse("Classic", 2), new WeaponUse("Sheriff", 2), new WeaponUse("Vandal", 2));
    }

    [Fact]
    public void Abilities_are_match_totals_and_a_missing_slot_is_not_zero()
    {
        var a1 = Report.Abilities.Single(a => a.Key == "p1");
        (a1.Grenade, a1.Ability1, a1.Ability2, a1.Ultimate, a1.PerRound).Should().Be((3, 6, null, 1, 3.3));
        Report.Abilities.Single(a => a.Key == "p2").PerRound.Should().BeNull();
    }

    [Fact]
    public void Objectives_summarise_plants_by_site_timing_and_what_followed()
    {
        var o = Report.Objectives;
        o.ByPlayer.Should().ContainSingle().Which.Should().Be(new PlantRow("p1", 1, 0));
        o.Sites.Should().ContainSingle().Which.Should().Be(new WeaponUse("A", 1));
        (o.AvgPlantMs, o.AvgDefuseMs).Should().Be((30000.0, null));
        (o.WinWhenPlanting, o.WinWhenOpponentPlants).Should().Be((new Rate(1, 1, 100), new Rate(0, 0, null)));
    }

    [Fact]
    public void The_kill_feed_is_ordered_and_marks_whose_kill_it_was()
    {
        Report.Kills.Select(k => (k.Round, k.Killer, k.Victim, k.KillerMine)).Should().Equal(
            (1, "p1", "p3", true), (1, "p4", "p2", false), (1, "p1", "p4", true),
            (2, "p3", "p1", false), (2, "p4", "p2", false), (3, "p2", "p4", true), (3, "p1", "p3", true));
    }

    [Fact]
    public void Rounds_numbered_from_zero_by_riot_are_shown_from_one_so_pistol_rounds_and_halves_are_right()
    {
        var zeroBased = Sample() with
        {
            Rounds = Sample().Rounds.Select(r => r with { Num = r.Num - 1 }).ToList(),
            Kills = Sample().Kills.Select(k => k with { Round = k.Round - 1 }).ToList(),
            Economy = Sample().Economy.Select(e => e with { Round = e.Round - 1 }).ToList(),
            Damage = Sample().Damage.Select(d => d with { Round = d.Round - 1 }).ToList(),
        };
        var shifted = ScrimInsightsBuilder.Build(zeroBased);
        shifted.Rounds.Select(r => r.Num).Should().Equal(1, 2, 3);
        shifted.Rounds.Select(r => r.Pistol).Should().Equal(true, false, false);
        shifted.Economy.Should().BeEquivalentTo(Report.Economy);
        shifted.Opening.Should().BeEquivalentTo(Report.Opening);
    }
}
