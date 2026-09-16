using Esportra.Core.Match;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.Veto;

public sealed class VetoSequenceResolverTests
{
    private static MatchMapVeto MakeVeto(int bestOf = 1, string game = "valorant") =>
        new() { BestOf = bestOf, Game = game, SelectedMapPool = [] };

    // ── DefaultVetoSequenceResolver ───────────────────────────────────────────

    [Fact]
    public void DefaultResolver_Resolve_ReturnsValorantBo1Sequence()
    {
        var resolver = new DefaultVetoSequenceResolver();
        var veto = MakeVeto(bestOf: 1, game: "valorant");

        var sequence = resolver.Resolve(veto, 7);

        sequence.Should().HaveCount(7);
        sequence.Should().Contain(s => s.IsDecider);
    }

    [Fact]
    public void DefaultResolver_GetStep_ReturnsCorrectStep()
    {
        var resolver = new DefaultVetoSequenceResolver();
        var veto = MakeVeto(bestOf: 1, game: "valorant");

        var step = resolver.GetStep(veto, 1, 7);

        step.Should().NotBeNull();
        step!.ActionNumber.Should().Be(1);
        step.Action.Should().Be("ban");
    }

    [Fact]
    public void DefaultResolver_GetStep_ReturnsNull_WhenActionNumberExceedsSequence()
    {
        var resolver = new DefaultVetoSequenceResolver();
        var veto = MakeVeto(bestOf: 1, game: "valorant");

        var step = resolver.GetStep(veto, 99, 7);

        step.Should().BeNull();
    }

    // ── CustomVetoSequenceResolver ────────────────────────────────────────────

    [Fact]
    public void CustomResolver_Resolve_ReturnsProvidedSequence()
    {
        var custom = new[] { new VetoStep(1, "ban", "T1"), new VetoStep(2, "pick_side", "T1", IsDecider: true) };
        var resolver = new CustomVetoSequenceResolver(custom);
        var veto = MakeVeto();

        var result = resolver.Resolve(veto, 7);

        result.Should().HaveCount(2);
        result[0].ActionNumber.Should().Be(1);
    }

    [Fact]
    public void CustomResolver_GetStep_FindsByActionNumber()
    {
        var custom = new[] { new VetoStep(1, "ban", "T1"), new VetoStep(2, "pick_side", "T2", IsDecider: true) };
        var resolver = new CustomVetoSequenceResolver(custom);
        var veto = MakeVeto();

        var step = resolver.GetStep(veto, 2, 7);

        step.Should().NotBeNull();
        step!.Action.Should().Be("pick_side");
        step.IsDecider.Should().BeTrue();
    }

    [Fact]
    public void CustomResolver_GetStep_ReturnsNull_WhenNotFound()
    {
        var custom = new[] { new VetoStep(1, "ban", "T1") };
        var resolver = new CustomVetoSequenceResolver(custom);

        var step = resolver.GetStep(MakeVeto(), 99, 7);

        step.Should().BeNull();
    }

    [Fact]
    public void CustomResolver_Constructor_Throws_WhenSequenceNull()
    {
        var act = () => new CustomVetoSequenceResolver(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CustomResolver_Constructor_Throws_WhenSequenceEmpty()
    {
        var act = () => new CustomVetoSequenceResolver([]);
        act.Should().Throw<ArgumentException>();
    }

    // ── VetoSequenceResolverFactory ───────────────────────────────────────────

    [Fact]
    public void Factory_Create_ReturnsDefault_WhenSettingsNull()
    {
        var resolver = VetoSequenceResolverFactory.Create(null);
        resolver.Should().BeOfType<DefaultVetoSequenceResolver>();
    }

    [Fact]
    public void Factory_Create_ReturnsDefault_WhenModeIsDefault()
    {
        var settings = new VetoSettings(Guid.NewGuid(), VetoMode.Default, null);
        var resolver = VetoSequenceResolverFactory.Create(settings);
        resolver.Should().BeOfType<DefaultVetoSequenceResolver>();
    }

    [Fact]
    public void Factory_Create_ReturnsDefault_WhenModeCustomButSequenceNull()
    {
        var settings = new VetoSettings(Guid.NewGuid(), VetoMode.Custom, null);
        var resolver = VetoSequenceResolverFactory.Create(settings);
        resolver.Should().BeOfType<DefaultVetoSequenceResolver>();
    }

    [Fact]
    public void Factory_Create_ReturnsCustom_WhenModeCustomAndSequenceNonEmpty()
    {
        var seq = new[] { new VetoStep(1, "pick_side", "T1", IsDecider: true) };
        var settings = new VetoSettings(Guid.NewGuid(), VetoMode.Custom, seq);
        var resolver = VetoSequenceResolverFactory.Create(settings);
        resolver.Should().BeOfType<CustomVetoSequenceResolver>();
    }
}
