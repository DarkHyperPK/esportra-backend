using Esportra.Api.Services.ProviderMatches.Career;
using Esportra.Api.Services.ScrimData;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScrimData;

public sealed class OpponentMathTests
{
    private static readonly Guid Omega = Guid.NewGuid(), Nova = Guid.NewGuid();
    private static readonly Guid S1 = Guid.NewGuid(), V1 = Guid.NewGuid(), S4 = Guid.NewGuid(), V4 = Guid.NewGuid();

    private static ScrimGame Game(Guid opponent, string name, Guid session, int number, int day, string map, int mine, int theirs, string[]? agents = null)
    {
        var id = Guid.NewGuid();
        var row = new ScrimMapRow(id, session, number, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(day), "riot", map, mine, theirs, mine > theirs ? 1 : 2, 1, name, null, null, null, null, opponent);
        var detail = new TeamGameDetail(id, row.PlayedAt, number, map, mine, theirs, mine + theirs, agents ?? [], 0, 0, 0, 0);
        return new ScrimGame(row, DataTier.ScoreOnly, mine > theirs ? "win" : "loss", map, detail, []);
    }

    private static IReadOnlyList<OpponentRow> Build() => OpponentMath.Build(
        [
            Game(Omega, "Omega", S1, 1, 1, "Lotus", 13, 7, ["jett", "sova"]), Game(Omega, "Omega", S1, 2, 1, "Bind", 8, 13), Game(Omega, "Omega", Guid.NewGuid(), 1, 5, "Lotus", 13, 11),
            Game(Nova, "Nova", S4, 1, 3, "Haven", 13, 3),
        ],
        [new VetoSessionRow(S1, V1, 1, "Ascent", Omega), new VetoSessionRow(S4, V4, 1, "Split", Nova)],
        [new VetoActionRow(V1, "team1", "pick", "Lotus", 3, null), new VetoActionRow(V1, "team2", "pick", "Bind", 4, null), new VetoActionRow(V1, "team2", "ban", "Haven", 2, null),
         new VetoActionRow(V4, "team1", "ban", "Icebox", 1, null)],
        [new VetoGameRow(S1, 1, "Lotus", 13, 7, 1, "confirmed"), new VetoGameRow(S1, 2, "Bind", 8, 13, 2, "confirmed")]);

    [Fact]
    public void Each_partner_is_a_record_with_round_win_rate_ordered_by_maps_played()
    {
        var o = Build();
        o.Select(x => (x.Name, x.Maps, x.Wins, x.Losses)).Should().Equal(("Omega", 3, 2, 1), ("Nova", 1, 1, 0));
        (o[0].WinRatePct, o[0].RoundsWon, o[0].RoundsLost, o[0].RoundWinPct).Should().Be((66.7, 34, 31, 52.3));
    }

    [Fact]
    public void Maps_and_meetings_are_listed_newest_first_with_what_the_team_played()
    {
        var omega = Build()[0];
        omega.MapsPlayed.Select(m => (m.Map, m.Games, m.Wins)).Should().Equal(("Lotus", 2, 2), ("Bind", 1, 0));
        omega.Meetings.Select(m => (m.Map, m.Result)).Should().Equal(("Lotus", "win"), ("Lotus", "win"), ("Bind", "loss"));
        omega.Meetings.Last().Agents.Should().BeEmpty();
        omega.Meetings[1].Agents.Should().Equal("jett", "sova");
        (omega.FirstPlayed, omega.LastPlayed).Should().Be((new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void The_veto_read_for_a_partner_uses_only_the_vetoes_against_that_team()
    {
        var omega = Build()[0].Veto;
        omega.Vetoes.Should().Be(1);
        omega.Maps.Single(m => m.Map == "Lotus").YouPicked.Should().Be(1);
        omega.Maps.Single(m => m.Map == "Bind").TheyPicked.Should().Be(1);
        omega.Maps.Should().NotContain(m => m.Map == "Icebox");
        Build()[1].Veto.Maps.Single(m => m.Map == "Icebox").YouBanned.Should().Be(1);
    }

    [Fact]
    public void Maps_without_a_known_opponent_are_left_out_and_nothing_is_invented_for_an_empty_set()
    {
        OpponentMath.Build([], [], [], []).Should().BeEmpty();
    }
}
