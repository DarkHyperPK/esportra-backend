using Esportra.Api.Endpoints;
using Esportra.Core.Match;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed class PublicVetoSequenceTests
{
    [Fact]
    public void ValidatePublicVetoSequence_AcceptsMovedPickWithItsSideChoice()
    {
        var standard = VetoSequences.GetSequence(3, "valorant", 7).ToArray();
        var moved = new[] { standard[2], standard[3], standard[0], standard[1] }
            .Concat(standard.Skip(4))
            .Select((step, index) => step with { ActionNumber = index + 1 })
            .ToArray();

        Assert.Null(PublicToolEndpoints.ValidatePublicVetoSequence(moved, 3, 7));
    }

    [Fact]
    public void ValidatePublicVetoSequence_RejectsSeparatedPickAndSideChoice()
    {
        var standard = VetoSequences.GetSequence(3, "valorant", 7).ToArray();
        var moved = new[] { standard[2], standard[0], standard[3], standard[1] }
            .Concat(standard.Skip(4))
            .Select((step, index) => step with { ActionNumber = index + 1 })
            .ToArray();

        Assert.NotNull(PublicToolEndpoints.ValidatePublicVetoSequence(moved, 3, 7));
    }
}
