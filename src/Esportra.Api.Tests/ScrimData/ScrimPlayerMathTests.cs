using Esportra.Api.Services.ProviderMatches.Career;
using Esportra.Api.Services.ScrimData;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScrimData;

public sealed class ScrimPlayerMathTests
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Dictionary<Guid, (string? Username, string? Role)> People = new() { [Me] = ("Nova", "member") };

    private static ScrimGame Game(string tier, string result, params ScrimPlayerLine[] lines)
    {
        var id = lines.FirstOrDefault()?.GameId ?? Guid.NewGuid();
        var row = new ScrimMapRow(id, Guid.NewGuid(), 1, DateTime.UtcNow, "riot", "Ascent", 13, 7, 1, 1, "Opp", null, null, null);
        return new ScrimGame(row, tier, result, "Ascent", new TeamGameDetail(id, DateTime.UtcNow, 1, "Ascent", 13, 7, 20, [], 0, 0, 0, 0), lines);
    }

    private static ScrimPlayerLine Line(string tier, Guid? user = null, int? k = 20, int? d = 10, int? a = 5, double? acs = 250, int? rounds = 20, double? adr = 150,
        double? hs = 25, int? fk = 2, int? fd = null, int? plants = 1, string? agent = "agent-1") =>
        new(Guid.NewGuid(), tier, user ?? Me, "Fallback", agent, null, k, d, a, acs, rounds, adr, hs, fk, fd, plants, 0);

    [Fact]
    public void Ratios_are_weighted_by_rounds_so_a_long_map_counts_for_more()
    {
        var games = new[]
        {
            Game(DataTier.Full, "win", Line(DataTier.Full, acs: 100, rounds: 10)),
            Game(DataTier.Screenshot, "loss", Line(DataTier.Screenshot, acs: 300, rounds: 30)),
        };
        var p = ScrimPlayerMath.Players(games, People).Single();
        p.Acs.Should().Be(250.0);      // (100*10 + 300*30) / 40, not the plain mean of 200
        (p.Games, p.Wins, p.FullGames, p.ScreenshotGames).Should().Be((2, 1, 1, 1));
        p.WinRatePct.Should().Be(50.0);
        p.Name.Should().Be("Nova");
    }

    [Fact]
    public void First_deaths_exist_only_where_the_source_provides_them_and_say_how_many_games()
    {
        var games = new[]
        {
            Game(DataTier.Full, "win", Line(DataTier.Full, fd: 3)),
            Game(DataTier.Screenshot, "win", Line(DataTier.Screenshot, fd: null)),
        };
        var p = ScrimPlayerMath.Players(games, People).Single();
        (p.FirstDeaths, p.FirstDeathGames, p.FirstKills).Should().Be((3, 1, 4));

        var onlyScreenshots = ScrimPlayerMath.Players([Game(DataTier.Screenshot, "win", Line(DataTier.Screenshot))], People).Single();
        (onlyScreenshots.FirstDeaths, onlyScreenshots.FirstDeathGames).Should().Be((null, 0));
    }

    [Fact]
    public void Kd_uses_only_maps_with_kills_deaths_and_assists_and_is_null_without_any()
    {
        var p = ScrimPlayerMath.Players([Game(DataTier.Full, "win", Line(DataTier.Full, k: 20, d: 10, a: 5)), Game(DataTier.Screenshot, "win", Line(DataTier.Screenshot, k: 30, d: null, a: 5))], People).Single();
        (p.Kd, p.KdaGames).Should().Be((2.0, 1));
        var none = ScrimPlayerMath.Players([Game(DataTier.Screenshot, "win", Line(DataTier.Screenshot, k: null, d: null, a: null, acs: null))], People).Single();
        (none.Kd, none.Kda, none.Acs, none.KdaGames).Should().Be((null, null, null, 0));
    }

    [Fact]
    public void Players_without_a_roster_match_are_not_listed()
    {
        var game = Game(DataTier.Screenshot, "win", Line(DataTier.Screenshot), new ScrimPlayerLine(Guid.NewGuid(), DataTier.Screenshot, null, "Stranger", null, null, 1, 1, 1, 100, 20, 100, 10, 0, null, 0, 0));
        ScrimPlayerMath.Players([game], People).Should().ContainSingle();
    }

    [Fact]
    public void Agents_are_counted_per_player_most_used_first()
    {
        var games = new[]
        {
            Game(DataTier.Full, "win", Line(DataTier.Full, agent: "b")), Game(DataTier.Full, "win", Line(DataTier.Full, agent: "a")),
            Game(DataTier.Full, "win", Line(DataTier.Full, agent: "a")), Game(DataTier.Full, "win", Line(DataTier.Full, agent: null)),
        };
        ScrimPlayerMath.Players(games, People).Single().Agents.Select(x => (x.CharacterId, x.Games)).Should().Equal(("a", 2), ("b", 1));
    }
}
