using Esportra.Api.Services.ScrimInsights;
using Esportra.Api.Services.ScrimReports;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScrimReports;

public sealed class RatingMathTests
{
    private static PlayerRow Row(int maps, double? acs, double? kast) =>
        new(Guid.NewGuid(), "P", maps, maps * 20, acs, null, null, kast, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, []);

    private static CoachUserRow Coach(Guid id, int entryWins, int entryDuels, int traded, int deaths, int clutchWins, int clutchTries) =>
        new(id, "P", 3, deaths, traded, 0, 0, clutchTries, clutchWins, entryDuels, entryWins, 0, 0);

    [Fact]
    public void Scale_is_linear_between_floor_and_ceiling_and_clamped()
    {
        RatingMath.Scale(225, 150, 300).Should().Be(50);
        RatingMath.Scale(100, 150, 300).Should().Be(0);
        RatingMath.Scale(400, 150, 300).Should().Be(100);
    }

    [Fact]
    public void A_small_sample_is_pulled_toward_the_typical_value()
    {
        RatingMath.Shrink(1, 1, 0.5, 4).Should().BeApproximately(0.6, 1e-9);      // one duel won: (1 + 2) / (1 + 4)
        RatingMath.Shrink(0, 0, 0.2, 3).Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void A_player_rating_weights_each_part_hand_computed()
    {
        var row = Row(4, 225, 70);                                  // ACS 50, KAST 50
        var coach = Coach(row.UserId, 12, 16, 6, 14, 1, 2);
        // opening (12+2)/(16+4) = 70% -> 100; traded (6+1.5)/(14+6) = 37.5% -> 91.7; clutch (1+0.6)/(2+3) = 32% -> 80
        var r = RatingMath.Player(row, coach);
        r.Parts.Single(p => p.Key == "opening").Score.Should().Be(100);
        r.Parts.Single(p => p.Key == "traded").Score.Should().Be(91.7);
        r.Parts.Single(p => p.Key == "clutch").Score.Should().Be(80);
        // 50*.35 + 50*.20 + 100*.20 + 91.7*.15 + 80*.10 = 17.5 + 10 + 20 + 13.755 + 8 = 69.255
        r.Value.Should().Be(69.3);
    }

    [Fact]
    public void A_player_needs_three_maps_and_missing_parts_share_their_weight()
    {
        RatingMath.Player(Row(2, 300, 85), null).Value.Should().BeNull();
        // Only ACS 100 (.35) and KAST 0 (.20) have data: (100*.35 + 0*.20) / .55 = 63.6
        RatingMath.Player(Row(3, 300, 50), null).Value.Should().Be(63.6);
    }

    [Fact]
    public void The_team_rating_hand_computed()
    {
        // round win (130+13)/(240+26) = 53.76% -> 68.8; opening 60/120 = 50% -> 50; traded 30/100 = 30% -> 66.7;
        // post-plant 30/50 = 60% -> 50; retake 5/20 = 25% -> 50
        var r = RatingMath.Team(130, 240, new Rate(60, 120, 50), new TradeCounts(100, 30, 0), new Rate(30, 50, 60), new Rate(5, 20, 25));
        r.Parts.Single(p => p.Key == "round_win").Score.Should().Be(68.8);
        // 68.8*.4 + 50*.15 + 66.7*.15 + 50*.15 + 50*.15 = 27.52 + 7.5 + 10.005 + 7.5 + 7.5 = 60.025
        r.Value.Should().Be(60.0);
    }

    [Fact]
    public void A_team_with_no_rounds_has_no_rating()
    {
        RatingMath.Team(0, 0, new Rate(0, 0, null), new TradeCounts(0, 0, 0), new Rate(0, 0, null), new Rate(0, 0, null)).Value.Should().BeNull();
    }
}
