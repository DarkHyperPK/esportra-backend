using Esportra.Api.Services.ProviderMatches.Career;
using Esportra.Api.Services.ScrimData;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScrimData;

public sealed class ScrimAgentMathTests
{
    private static readonly Guid Nova = Guid.Parse("00000000-0000-0000-0000-00000000000a"), Ray = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Dictionary<Guid, (string? Username, string? Role)> People = new() { [Nova] = ("Nova", "member"), [Ray] = ("Ray", "member") };

    private static ScrimGame Game(string map, string result, params ScrimPlayerLine[] lines)
    {
        var id = Guid.NewGuid();
        var row = new ScrimMapRow(id, Guid.NewGuid(), 1, DateTime.UtcNow, "riot", map, 13, 7, 1, 1, "Opp", null, null, null);
        return new ScrimGame(row, DataTier.Full, result, map, new TeamGameDetail(id, DateTime.UtcNow, 1, map, 13, 7, 20, [], 0, 0, 0, 0), lines);
    }

    private static ScrimPlayerLine Line(Guid user, string agent, int? k = 20, int? d = 10, double? acs = 250, int? rounds = 20) =>
        new(Guid.NewGuid(), DataTier.Full, user, "Fallback", agent, null, k, d, 5, acs, rounds, 150, 25, 2, 1, 0, 0);

    [Fact]
    public void An_agent_row_counts_games_wins_pick_share_and_maps()
    {
        var games = new[]
        {
            Game("Lotus", "win", Line(Nova, "Jett"), Line(Ray, "Sage")),
            Game("Lotus", "loss", Line(Nova, "Jett"), Line(Ray, "Omen")),
            Game("Bind", "win", Line(Nova, "Sage"), Line(Ray, "Omen")),
        };
        var agents = ScrimAgentMath.Agents(games).ToDictionary(a => a.CharacterId);
        var jett = agents["jett"];
        (jett.Games, jett.Wins, jett.WinRatePct, jett.PickSharePct).Should().Be((2, 1, 50.0, 33.3));
        jett.Maps.Should().ContainSingle().Which.Should().Be(new AgentMap("Lotus", 2, 1));
        agents["omen"].Maps.Select(m => (m.Map, m.Games, m.Wins)).Should().Equal(("Bind", 1, 1), ("Lotus", 1, 0));
        ScrimAgentMath.Agents(games).First().CharacterId.Should().BeOneOf("jett", "omen", "sage");
    }

    [Fact]
    public void Figures_are_round_weighted_use_only_maps_that_have_them_and_say_how_many()
    {
        var games = new[]
        {
            Game("Lotus", "win", Line(Nova, "Jett", k: 20, d: 10, acs: 100, rounds: 10)),
            Game("Lotus", "win", Line(Nova, "Jett", k: 10, d: 10, acs: 300, rounds: 30)),
            Game("Lotus", "win", Line(Nova, "Jett", k: null, d: null, acs: null, rounds: null)),
        };
        var jett = ScrimAgentMath.Agents(games).Single();
        (jett.Kd, jett.KdGames).Should().Be((1.5, 2));            // 30 kills / 20 deaths over the two maps that have them
        (jett.Acs, jett.StatGames).Should().Be((250.0, 2));       // (100*10 + 300*30) / 40
    }

    [Fact]
    public void Missing_figures_are_null_not_zero()
    {
        var jett = ScrimAgentMath.Agents([Game("Lotus", "win", Line(Nova, "Jett", k: null, d: null, acs: null, rounds: null))]).Single();
        (jett.Kd, jett.Acs, jett.KdGames, jett.StatGames).Should().Be((null, null, 0, 0));
    }

    [Fact]
    public void Players_are_split_by_agent_with_their_own_record()
    {
        var games = new[]
        {
            Game("Lotus", "win", Line(Nova, "Jett"), Line(Ray, "Sage")),
            Game("Bind", "loss", Line(Nova, "Sage", k: 10, d: 20)),
            Game("Bind", "win", Line(Nova, "Jett")),
        };
        var rows = ScrimAgentMath.PlayerAgents(games, People);
        var novaJett = rows.Single(r => r.Name == "Nova" && r.CharacterId == "jett");
        (novaJett.Games, novaJett.Wins, novaJett.WinRatePct).Should().Be((2, 2, 100.0));
        var novaSage = rows.Single(r => r.Name == "Nova" && r.CharacterId == "sage");
        (novaSage.Games, novaSage.Wins, novaSage.Kd).Should().Be((1, 0, 0.5));
        rows.Count.Should().Be(3);
    }

    [Fact]
    public void Lines_without_an_agent_or_a_matched_player_are_left_out_of_the_player_split()
    {
        var anonymous = Line(Nova, "Jett") with { UserId = null };
        var noAgent = Line(Nova, "Jett") with { CharacterId = null };
        ScrimAgentMath.PlayerAgents([Game("Lotus", "win", anonymous, noAgent)], People).Should().BeEmpty();
        ScrimAgentMath.Agents([Game("Lotus", "win", anonymous, noAgent)]).Should().ContainSingle();
    }
}
