using Esportra.Api.Services.ProviderMatches.Career;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Career;

public sealed class IntelligenceMathTests
{
    private const string Ascent = "Ascent";
    private const string Haven = "Haven";
    private static readonly string[] Five = ["a", "b", "c", "d", "e"];
    private static int clock;

    private static TeamGameDetail Game(
        bool win = true, string? map = Ascent, string[]? agents = null, Guid? match = null,
        int atkPlayed = 12, int atkWon = 7, int defPlayed = 8, int defWon = 6) =>
        new(match ?? Guid.NewGuid(), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(clock++), 1, map, win ? 13 : 7, win ? 7 : 13, 20,
            agents ?? Five, atkPlayed, atkWon, defPlayed, defWon);

    private static List<TeamGameDetail> Games(int wins, int losses, string map = Ascent) =>
        Enumerable.Range(0, wins).Select(_ => Game(true, map)).Concat(Enumerable.Range(0, losses).Select(_ => Game(false, map))).ToList();

    [Theory]
    [InlineData(0, 0)]
    public void Wilson_has_no_interval_without_games(int wins, int games) => IntelligenceMath.Wilson(wins, games).Should().BeNull();

    [Fact]
    public void Wilson_interval_narrows_with_more_games_around_the_same_rate()
    {
        var small = IntelligenceMath.Wilson(3, 5)!.Value;
        var large = IntelligenceMath.Wilson(30, 50)!.Value;
        (large.High - large.Low).Should().BeLessThan(small.High - small.Low);
        small.Low.Should().BeLessThan(60).And.BeGreaterThan(0);
        IntelligenceMath.Wilson(5, 5)!.Value.High.Should().Be(100.0);
    }

    [Fact]
    public void A_map_is_never_called_a_strength_or_weakness_from_a_handful_of_games()
    {
        var games = Games(4, 0, Ascent).Concat(Games(0, 4, Haven)).ToList();   // 4-0 and 0-4: dramatic, but only 4 games each
        var maps = IntelligenceMath.Maps(games);
        maps.Should().OnlyContain(m => m.Signal == IntelligenceMath.Insufficient);
    }

    [Fact]
    public void A_map_is_strong_or_weak_only_when_its_interval_clears_the_teams_overall_record()
    {
        var games = Games(20, 0, Ascent).Concat(Games(0, 20, Haven)).ToList();   // overall 50%
        var maps = IntelligenceMath.Maps(games).ToDictionary(m => m.Map);
        maps["Ascent"].Signal.Should().Be(IntelligenceMath.Strong);
        maps["Haven"].Signal.Should().Be(IntelligenceMath.Weak);
    }

    [Fact]
    public void An_ordinary_map_with_enough_games_is_typical()
    {
        var games = Games(6, 4, Ascent).Concat(Games(6, 4, Haven)).ToList();
        IntelligenceMath.Maps(games).Should().OnlyContain(m => m.Signal == IntelligenceMath.Typical);
    }

    [Fact]
    public void Map_lines_carry_attack_and_defense_from_rounds_and_a_win_interval()
    {
        var m = IntelligenceMath.Maps([Game(), Game(), Game(false)]).Single();
        (m.Games, m.Wins, m.Map).Should().Be((3, 2, "Ascent"));
        (m.Attack.Played, m.Attack.Won, m.Attack.Pct).Should().Be((36, 21, 58.3));
        (m.Defense.Played, m.Defense.Won, m.Defense.Pct).Should().Be((24, 18, 75.0));
        m.WinLowPct.Should().BeLessThan(m.WinRatePct!.Value);
        m.WinHighPct.Should().BeGreaterThan(m.WinRatePct!.Value);
    }

    [Fact]
    public void Attack_and_defense_are_null_when_no_side_data_exists_not_zero()
    {
        var m = IntelligenceMath.Maps([Game(atkPlayed: 0, atkWon: 0, defPlayed: 0, defWon: 0)]).Single();
        (m.Attack.Pct, m.Defense.Pct).Should().Be((null, null));
    }

    [Fact]
    public void Unplayed_pool_maps_are_listed_so_least_practised_includes_never_played()
    {
        var played = IntelligenceMath.Maps(Games(2, 0, Ascent));
        IntelligenceMath.Unplayed(["Ascent", "Bind", "Haven", "ascent"], played).Should().Equal("Bind", "Haven");
    }

    [Fact]
    public void Compositions_group_the_same_five_agents_in_any_order_and_split_by_map()
    {
        string[] order1 = ["a", "b", "c", "d", "e"], order2 = ["e", "D", "c", "b", "a"], other = ["a", "b", "c", "d", "z"];
        var games = new[] { Game(true, Ascent, order1), Game(false, Haven, order2), Game(true, Ascent, order1), Game(true, Ascent, other) };

        var comps = IntelligenceMath.Compositions(games);

        comps.Should().HaveCount(2);
        var main = comps[0];
        (main.Games, main.Wins, main.WinRatePct).Should().Be((3, 2, 66.7));
        main.Agents.Should().Equal("a", "b", "c", "d", "e");
        main.Maps.Select(m => (m.Map, m.Games, m.Wins)).Should().Equal(("Ascent", 2, 2), ("Haven", 1, 0));
    }

    [Fact]
    public void Games_without_five_known_agents_are_not_compositions()
    {
        IntelligenceMath.Compositions([Game(agents: ["a", "b", "c", "d"]), Game(agents: [])]).Should().BeEmpty();
    }

    private static (List<TeamGameDetail> Games, List<GamePlayer> Players, Guid[] Ids) Season()
    {
        var ids = Enumerable.Range(0, 6).Select(i => new Guid(i + 1, 0, 0, new byte[8])).ToArray();   // 0-4 starters, 5 substitute
        string?[] names = ["Ann", "Bo", "Cy", "Di", "Ed", "Sub"];
        var games = new List<TeamGameDetail>();
        var players = new List<GamePlayer>();
        void Play(bool win, params int[] who)
        {
            var g = Game(win);
            games.Add(g);
            players.AddRange(who.Select(i => new GamePlayer(g.MatchId, ids[i], names[i], i == 5 ? "substitute" : "member")));
        }
        for (var i = 0; i < 5; i++) Play(true, 0, 1, 2, 3, 4);
        Play(false, 0, 1, 2, 3, 4);
        for (var i = 0; i < 3; i++) Play(false, 0, 1, 2, 3, 5);   // Ed out, substitute in
        Play(true, 0, 1, 2, 5);                                   // incomplete roster known
        return (games, players, ids);
    }

    [Fact]
    public void Lineups_are_per_set_of_players_and_flag_incomplete_ones()
    {
        var (games, players, _) = Season();
        var lineups = IntelligenceMath.Lineups(games, players);

        lineups.Should().HaveCount(3);
        lineups[0].Should().Match<LineupIntel>(l => l.Games == 6 && l.Wins == 5 && l.Complete);
        lineups[1].Should().Match<LineupIntel>(l => l.Games == 3 && l.Wins == 0 && l.Complete);
        lineups[2].Complete.Should().BeFalse();
    }

    [Fact]
    public void Stability_reports_how_much_of_the_record_belongs_to_one_lineup()
    {
        var (games, players, _) = Season();
        var s = IntelligenceMath.Stability(IntelligenceMath.Lineups(games, players), players);
        (s.DistinctLineups, s.CompleteLineups, s.PlayersUsed, s.GamesWithKnownPlayers).Should().Be((3, 2, 6, 10));
        s.MostUsedSharePct.Should().Be(60.0);
    }

    [Fact]
    public void Substitute_impact_compares_games_with_and_without_a_substitute_without_claiming_cause()
    {
        var (games, players, _) = Season();
        var sub = IntelligenceMath.Substitutes(games, players);
        (sub.GamesWith, sub.WinsWith, sub.GamesWithout, sub.WinsWithout).Should().Be((4, 1, 6, 5));
        sub.WinRateWithPct.Should().Be(25.0);
        sub.WinRateWithoutPct.Should().BeApproximately(83.3, 0.05);
    }

    [Fact]
    public void Pairs_show_the_record_together_and_when_one_is_missing()
    {
        var (games, players, ids) = Season();
        var pair = IntelligenceMath.Pairs(games, players).Single(p => p.Names.SequenceEqual(new[] { "Cy", "Ed" }) || p.Names.SequenceEqual(new[] { "Ed", "Cy" }));
        pair.Together.Should().Be(new RecordLine(6, 5));
        // Whichever of the two is listed first, one of them has the 4-game stretch without the other (Cy plays with Sub, Ed does not).
        new[] { pair.FirstWithoutSecond, pair.SecondWithoutFirst }.Should().Contain(new RecordLine(4, 1));
        ids.Length.Should().Be(6);
    }

    [Fact]
    public void Pairs_need_three_games_together()
    {
        var g = new[] { Game(), Game() };
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var players = g.SelectMany(x => new[] { new GamePlayer(x.MatchId, a, "A", "member"), new GamePlayer(x.MatchId, b, "B", "member") });
        IntelligenceMath.Pairs(g, players).Should().BeEmpty();
    }
}
