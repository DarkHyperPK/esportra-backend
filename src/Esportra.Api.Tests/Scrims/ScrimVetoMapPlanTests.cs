using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public sealed class ScrimVetoMapPlanTests
{
    private static ScrimVetoMapPlan Bo3(params int[] voided) => new(
        [new VetoMapEntry(1, "Lotus", 1, false), new VetoMapEntry(2, "Bind", 2, false), new VetoMapEntry(3, "Haven", null, true)], voided);

    [Fact]
    public void Games_follow_the_veto_order_and_the_last_is_the_decider()
    {
        var plan = Bo3();
        plan.ForGame(1)!.MapName.Should().Be("Lotus");
        plan.ForGame(2)!.PickedBySlot.Should().Be(2);
        plan.ForGame(3)!.IsDecider.Should().BeTrue();
    }

    [Fact]
    public void A_game_outside_the_series_has_no_expected_map()
    {
        Bo3().ForGame(0).Should().BeNull();
        Bo3().ForGame(4).Should().BeNull();
    }

    [Fact]
    public void A_voided_map_is_replayed_so_later_game_numbers_shift_back()
    {
        var plan = Bo3(voided: 1);          // game 1 (Lotus) was voided
        plan.ForGame(1)!.MapName.Should().Be("Lotus");
        plan.ForGame(2)!.MapName.Should().Be("Lotus");   // the replay
        plan.ForGame(3)!.MapName.Should().Be("Bind");
        plan.ForGame(4)!.MapName.Should().Be("Haven");
    }

    [Theory]
    [InlineData("Lotus", "Lotus", false, true, true)]
    [InlineData("Lotus", "lotus", false, true, true)]
    [InlineData("Lotus", "Bind", false, false, false)]
    [InlineData("Lotus", "Bind", true, true, false)]
    [InlineData("Lotus", null, false, true, false)]
    [InlineData(null, "Bind", false, true, false)]
    public void Autofetch_lists_only_the_veto_map_unless_asked_for_all(string? expected, string? played, bool all, bool show, bool onVeto) =>
        ScrimVetoMaps.Classify(expected, played, all).Should().Be((show, onVeto));
}
