using Esportra.Core.Match;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.Veto;

/// <summary>
/// Tests for VetoSettingsService internal validation helpers.
/// InternalsVisibleTo in Core.csproj allows access.
/// </summary>
public sealed class VetoSettingsServiceValidationTests
{
    private static VetoStep[] Bo1Sequence() =>
        VetoSequences.GetSequence(1, "valorant", 7).ToArray();

    private static VetoStep[] Bo3Sequence() =>
        VetoSequences.GetSequence(3, "valorant", 7).ToArray();

    // ── ValidateActionNumbers ─────────────────────────────────────────────────

    [Fact]
    public void ValidateActionNumbers_PassesForConsecutiveSequence()
    {
        var seq = Bo1Sequence();
        var act = () => VetoSettingsService.ValidateActionNumbers(seq);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateActionNumbers_ThrowsForGap()
    {
        var seq = new[] { new VetoStep(1, "ban", "T1"), new VetoStep(3, "pick_side", "T1", IsDecider: true) };
        var act = () => VetoSettingsService.ValidateActionNumbers(seq);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*consecutive*");
    }

    [Fact]
    public void ValidateActionNumbers_ThrowsForStartingAtTwo()
    {
        var seq = new[] { new VetoStep(2, "ban", "T1") };
        var act = () => VetoSettingsService.ValidateActionNumbers(seq);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*Expected 1, got 2*");
    }

    // ── ValidateStepActions ───────────────────────────────────────────────────

    [Fact]
    public void ValidateStepActions_PassesForValidSequence()
    {
        var seq = Bo1Sequence();
        var act = () => VetoSettingsService.ValidateStepActions(seq);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateStepActions_ThrowsForInvalidAction()
    {
        var seq = new[] { new VetoStep(1, "teleport", "T1") };
        var act = () => VetoSettingsService.ValidateStepActions(seq);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*invalid action*");
    }

    [Fact]
    public void ValidateStepActions_ThrowsForInvalidTeam()
    {
        var seq = new[] { new VetoStep(1, "ban", "T3") };
        var act = () => VetoSettingsService.ValidateStepActions(seq);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*invalid team*");
    }

    [Fact]
    public void ValidateStepActions_PassesIgnoreWithAnyTeam()
    {
        // ignore steps don't require valid team
        var seq = new[] { new VetoStep(1, "ignore", "T1"), new VetoStep(2, "pick_side", "T1", IsDecider: true) };
        var act = () => VetoSettingsService.ValidateStepActions(seq);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateStepActions_ThrowsWhenLastStepIsIgnore()
    {
        var seq = new[] { new VetoStep(1, "ban", "T1"), new VetoStep(2, "ignore", "T1") };
        var act = () => VetoSettingsService.ValidateStepActions(seq);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*last step*");
    }

    // ── ValidateDeciderCount ──────────────────────────────────────────────────

    [Fact]
    public void ValidateDeciderCount_PassesForExactlyOne()
    {
        var seq = Bo1Sequence();
        var act = () => VetoSettingsService.ValidateDeciderCount(seq);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateDeciderCount_ThrowsForNoDecider()
    {
        var seq = Bo1Sequence()
            .Select(s => new VetoStep(s.ActionNumber, s.Action, s.Team, IsDecider: false))
            .ToArray();
        var act = () => VetoSettingsService.ValidateDeciderCount(seq);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*IsDecider*Found 0*");
    }

    [Fact]
    public void ValidateDeciderCount_ThrowsForTwoDeciders()
    {
        var seq = Bo1Sequence()
            .Select(s => new VetoStep(s.ActionNumber, s.Action, s.Team, IsDecider: true))
            .ToArray();
        var act = () => VetoSettingsService.ValidateDeciderCount(seq);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*IsDecider*");
    }

    // ── ValidatePicksForFormat ────────────────────────────────────────────────

    [Fact]
    public void ValidatePicksForFormat_BO1_NoPick_Passes()
    {
        var seq = Bo1Sequence();
        var act = () => VetoSettingsService.ValidatePicksForFormat(seq, bestOf: 1);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidatePicksForFormat_BO3_WithPick_Passes()
    {
        var seq = Bo3Sequence();
        var act = () => VetoSettingsService.ValidatePicksForFormat(seq, bestOf: 3);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidatePicksForFormat_BO3_NoPick_Throws()
    {
        var seq = Bo3Sequence()
            .Select(s => new VetoStep(s.ActionNumber, s.IsDecider ? "pick_side" : "ban", s.Team, s.IsDecider))
            .ToArray();
        var act = () => VetoSettingsService.ValidatePicksForFormat(seq, bestOf: 3);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*BO3*pick*");
    }

    [Fact]
    public void ValidatePicksForFormat_BO3_OnePick_Throws()
    {
        // BO3 requires exactly 2 picks; a sequence with only 1 must be rejected
        var seq = Bo3Sequence().ToArray();
        var firstPickIdx = Array.FindIndex(seq, s => s.Action == "pick");
        seq[firstPickIdx] = new VetoStep(seq[firstPickIdx].ActionNumber, "ban", seq[firstPickIdx].Team, seq[firstPickIdx].IsDecider);
        var act = () => VetoSettingsService.ValidatePicksForFormat(seq, bestOf: 3);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*BO3*exactly*2*");
    }

    // ── ValidateSequenceLength ────────────────────────────────────────────────

    [Fact]
    public void ValidateSequenceLength_BO1_CorrectLength_Passes()
    {
        var seq = Bo1Sequence();
        var act = () => VetoSettingsService.ValidateSequenceLength(seq, "valorant", 1);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateSequenceLength_BO1_WrongLength_Throws()
    {
        var seq = Bo1Sequence().Take(3).ToArray();
        var act = () => VetoSettingsService.ValidateSequenceLength(seq, "valorant", 1);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("INVALID_SEQUENCE*expected 7*got 3*");
    }
}
