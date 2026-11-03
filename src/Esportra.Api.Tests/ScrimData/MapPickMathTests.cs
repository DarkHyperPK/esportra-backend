using Esportra.Api.Services.ScrimData;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScrimData;

public sealed class MapPickMathTests
{
    private static readonly Guid S1 = Guid.NewGuid(), V1 = Guid.NewGuid(), S2 = Guid.NewGuid(), V2 = Guid.NewGuid(), S3 = Guid.NewGuid(), V3 = Guid.NewGuid();

    private static VetoActionRow Act(Guid veto, string side, string type, string map, int n, string? sideChosen = null) => new(veto, side, type, map, n, sideChosen);
    private static VetoGameRow Game(Guid s, int n, string map, int t1, int t2, int? winner, string status = "confirmed") => new(s, n, map, t1, t2, winner, status);

    /// <summary>
    /// S1: Bo3, we are team 1. We ban Haven and pick Lotus; they ban Split and pick Bind (we choose attack on Bind); decider Ascent.
    ///     Lotus 13-7 W, Bind 8-13 L, Ascent 13-11 W.
    /// S2: Bo1, we are team 2. We ban Bind, they ban Sunset; decider Fracture. 9-13 for team 1, so we lose.
    /// S3: Bo3, we are team 1. We pick Lotus, they pick Bind, decider Ascent; game 1 (Lotus) is voided and replayed as game 2, won 13-5.
    /// </summary>
    private static MapPicks Sample() => MapPickMath.Build(
        [new(S1, V1, 1, "Ascent"), new(S2, V2, 2, "Fracture"), new(S3, V3, 1, "Ascent")],
        [
            Act(V1, "team1", "ban", "Haven", 1), Act(V1, "team2", "ban", "Split", 2), Act(V1, "team1", "pick", "Lotus", 3), Act(V1, "team2", "pick", "Bind", 4),
            Act(V1, "team1", "pick_side", "Bind", 5, "Attack"),
            Act(V2, "team2", "ban", "Bind", 1), Act(V2, "team1", "ban", "Sunset", 2),
            Act(V3, "team1", "pick", "Lotus", 1), Act(V3, "team2", "pick", "Bind", 2),
        ],
        [
            Game(S1, 1, "Lotus", 13, 7, 1), Game(S1, 2, "Bind", 8, 13, 2), Game(S1, 3, "Ascent", 13, 11, 1),
            Game(S2, 1, "Fracture", 13, 9, 1),
            Game(S3, 1, "Lotus", 6, 6, null, "resolved"), Game(S3, 2, "Lotus", 13, 5, 1),
        ]);

    [Fact]
    public void Counts_picks_bans_and_deciders_from_the_teams_point_of_view()
    {
        var m = Sample().Maps.ToDictionary(r => r.Map);
        (m["Lotus"].YouPicked, m["Lotus"].TheyPicked).Should().Be((2, 0));
        (m["Bind"].TheyPicked, m["Bind"].YouBanned, m["Bind"].YouPicked).Should().Be((2, 1, 0));
        (m["Haven"].YouBanned, m["Split"].TheyBanned).Should().Be((1, 1));
        (m["Sunset"].TheyBanned, m["Sunset"].YouBanned).Should().Be((1, 0));   // S2: team 1 is the opponent
        (m["Ascent"].Decider, m["Fracture"].Decider).Should().Be((2, 1));
        Sample().Vetoes.Should().Be(3);
    }

    [Fact]
    public void Counts_maps_played_from_confirmed_games_only_and_orders_by_play()
    {
        var m = Sample().Maps;
        m.Single(r => r.Map == "Lotus").Played.Should().Be(2);   // the voided Lotus game is not played
        m.First().Map.Should().Be("Lotus");
    }

    [Fact]
    public void Splits_results_by_who_chose_the_map_with_voided_games_shifting_the_order()
    {
        var by = Sample().ByPicker.ToDictionary(p => p.PickedBy);
        // you picked: S1 Lotus 13-7 W, S3 game 2 (the Lotus replay) 13-5 W
        (by["you"].Games, by["you"].Wins, by["you"].RoundsWon, by["you"].RoundsLost, by["you"].RoundWinRatePct).Should().Be((2, 2, 26, 12, 68.4));
        // they picked: S1 Bind 8-13 L
        (by["opponent"].Games, by["opponent"].Wins, by["opponent"].WinRatePct).Should().Be((1, 0, 0));
        // decider: S1 Ascent 13-11 W, S2 Fracture lost 9-13 (we are team 2)
        (by["decider"].Games, by["decider"].Wins, by["decider"].WinRatePct).Should().Be((3 - 1, 1, 50));
        Sample().Unattributed.Should().Be(0);
    }

    [Fact]
    public void Records_the_side_the_team_chose_after_an_opponent_pick()
    {
        Sample().YourSideChoices.Should().ContainSingle().Which.Should().Be(new SideChoice("attack", 1));
    }

    [Fact]
    public void A_team_with_no_finished_vetoes_has_nothing_and_no_invented_rates()
    {
        var empty = MapPickMath.Build([], [], []);
        (empty.Vetoes, empty.Maps.Count, empty.ByPicker.Count).Should().Be((0, 0, 0));
    }

    [Fact]
    public void A_game_beyond_the_veto_is_counted_as_unattributed_not_guessed()
    {
        var picks = MapPickMath.Build([new(S1, V1, 1, "Ascent")], [Act(V1, "team1", "pick", "Lotus", 1)], [Game(S1, 1, "Lotus", 13, 1, 1), Game(S1, 2, "Ascent", 13, 1, 1), Game(S1, 3, "Bind", 13, 1, 1)]);
        picks.Unattributed.Should().Be(1);
        picks.ByPicker.Sum(p => p.Games).Should().Be(2);
    }
}
