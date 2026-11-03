using Esportra.Api.Services.ProviderMatches.Career;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Career;

public sealed class CareerMathTests
{
    private static PlayerMatchRow Row(
        int? won = 13, int? lost = 7, string context = "scrim_game", string? agent = "agent-a", string? map = "/Game/Maps/Ascent/Ascent",
        int kills = 20, int deaths = 10, int assists = 4, int score = 5000, int rounds = 20, int damage = 3000,
        int hs = 10, int body = 30, int leg = 10, DateTime? at = null, Guid? team = null, string? teamName = "Alpha") =>
        new(Guid.NewGuid(), at ?? new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), context, Guid.NewGuid(), null, null, 1,
            map, team ?? Guid.Empty, teamName, Guid.NewGuid(), "Opp", won, lost, agent, score, rounds, kills, deaths, assists, damage,
            hs, body, leg, 2, 1, 1, 0, 0, 0, 1, 0);

    [Theory]
    [InlineData(13, 7, "win")]
    [InlineData(7, 13, "loss")]
    [InlineData(12, 12, "draw")]
    [InlineData(null, 5, "draw")]
    public void Result_comes_from_rounds(int? won, int? lost, string expected) => CareerMath.Result(won, lost).Should().Be(expected);

    [Fact]
    public void Ratios_are_weighted_over_all_rounds_not_averaged_per_match()
    {
        // Match 1: 10 rounds, 1000 score. Match 2: 30 rounds, 9000 score. Weighted ACS = 10000/40 = 250, not (100+300)/2 = 200.
        var rows = new[] { Row(score: 1000, rounds: 10, damage: 800), Row(score: 9000, rounds: 30, damage: 4200) };
        var t = CareerMath.Totals(rows);
        t.Acs.Should().Be(250.0);
        t.Adr.Should().Be(125.0);
        t.Rounds.Should().Be(40);
    }

    [Fact]
    public void Kd_and_kda_use_documented_zero_death_rule()
    {
        var t = CareerMath.Totals(new[] { Row(kills: 12, deaths: 0, assists: 3) });
        t.Kd.Should().Be(12.0);
        t.Kda.Should().Be(15.0);
        CareerMath.Totals(new[] { Row(kills: 10, deaths: 5, assists: 5) }).Kd.Should().Be(2.0);
    }

    [Fact]
    public void Win_rate_counts_scrims_and_tournaments_separately_in_the_split()
    {
        var rows = new[] { Row(), Row(won: 5, lost: 13), Row(context: "tournament_game"), Row(won: 10, lost: 10) };
        var t = CareerMath.Totals(rows);
        (t.Matches, t.ScrimMatches, t.TournamentMatches).Should().Be((4, 3, 1));
        (t.Wins, t.Losses, t.Draws).Should().Be((2, 1, 1));
        t.WinRatePct.Should().Be(50.0);
    }

    [Fact]
    public void Headshot_share_is_location_of_hits_and_is_null_without_hits()
    {
        CareerMath.Totals(new[] { Row(hs: 10, body: 30, leg: 10) }).HeadshotSharePct.Should().Be(20.0);
        CareerMath.Totals(new[] { Row(hs: 0, body: 0, leg: 0) }).HeadshotSharePct.Should().BeNull();
    }

    [Fact]
    public void An_empty_career_has_null_ratios_not_zeroes_or_errors()
    {
        var t = CareerMath.Totals(Array.Empty<PlayerMatchRow>());
        (t.Matches, t.WinRatePct, t.Acs, t.Adr, t.Kd, t.Kda).Should().Be((0, null, null, null, null, null));
        CareerMath.SampleOf(Array.Empty<PlayerMatchRow>()).From.Should().BeNull();
    }

    [Fact]
    public void Agents_are_grouped_case_insensitively_and_ordered_by_matches()
    {
        var rows = new[] { Row(agent: "AGENT-A"), Row(agent: "agent-a", won: 5, lost: 13), Row(agent: "agent-b"), Row(agent: null) };
        var agents = CareerMath.ByAgent(rows);
        agents.Select(a => (a.CharacterId, a.Matches, a.Wins)).Should().Equal(("agent-a", 2, 1), ("agent-b", 1, 1));
    }

    [Fact]
    public void Maps_show_names_and_skip_matches_without_a_map()
    {
        var rows = new[] { Row(map: "/Game/Maps/Triad/Triad"), Row(map: "/Game/Maps/Triad/Triad", won: 5, lost: 13), Row(map: null) };
        var maps = CareerMath.ByMap(rows);
        maps.Should().ContainSingle().Which.Should().Match<MapLine>(m => m.Map == "Haven" && m.Matches == 2 && m.WinRatePct == 50.0);
    }

    [Fact]
    public void Teams_keep_each_team_separate_so_history_follows_the_player()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var rows = new[] { Row(team: a, teamName: "Alpha", at: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)), Row(team: a, teamName: "Alpha", at: new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc)), Row(team: b, teamName: "Bravo", at: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)) };
        var teams = CareerMath.ByTeam(rows);
        teams.Select(t => (t.TeamName, t.Matches)).Should().Equal(("Bravo", 1), ("Alpha", 2));
        teams[1].FirstPlayed.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Activity_buckets_by_month_in_order()
    {
        var rows = new[]
        {
            Row(at: new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc)), Row(at: new DateTime(2026, 9, 30, 23, 59, 0, DateTimeKind.Utc)),
            Row(at: new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), won: 1, lost: 13),
        };
        CareerMath.Activity(rows).Select(m => (m.Month, m.Matches, m.Wins)).Should().Equal(("2026-09", 1, 1), ("2026-10", 2, 1));
    }

    private static TeamMatchRow T(int? won, int? lost, string context = "scrim_game", int rounds = 20, int kills = 100, int deaths = 90, int damage = 12000, int fk = 10, int fd = 8, DateTime? at = null) =>
        new(Guid.NewGuid(), at ?? DateTime.UtcNow, context, Guid.NewGuid(), null, null, 1, "/Game/Maps/Ascent/Ascent", Guid.NewGuid(), "Opp", won, lost, rounds, kills, deaths, 30, damage, fk, fd);

    [Fact]
    public void Team_totals_use_rounds_for_rates_and_matches_for_averages()
    {
        var t = CareerMath.Totals(new[] { T(13, 7, rounds: 20, fk: 10), T(8, 13, rounds: 21, fk: 11) });
        (t.Matches, t.Wins, t.Losses).Should().Be((2, 1, 1));
        t.RoundWinRatePct.Should().Be(Math.Round(100.0 * 21 / 41, 1));
        t.AvgRoundsWon.Should().Be(10.5);
        t.AvgRoundsLost.Should().Be(10.0);
        t.FirstKillRatePct.Should().Be(Math.Round(100.0 * 21 / 41, 1));
    }

    [Fact]
    public void Form_is_newest_first_and_capped()
    {
        var rows = Enumerable.Range(0, 7).Select(i => T(i % 2 == 0 ? 13 : 5, i % 2 == 0 ? 5 : 13, at: new DateTime(2026, 10, 1 + i, 0, 0, 0, DateTimeKind.Utc))).ToList();
        CareerMath.Form(rows).Should().Equal("W", "L", "W", "L", "W");
        CareerMath.Form(rows, take: 2).Should().Equal("W", "L");
    }
}
