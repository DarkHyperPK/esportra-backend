using Esportra.Core.Licensing;
using FluentAssertions;
using Xunit;

namespace Esportra.Core.Tests.Licensing;

public sealed class EventPassPricingTests
{
    [Theory]
    [InlineData(33, 12000)]
    [InlineData(64, 12000)]
    [InlineData(65, 25000)]
    [InlineData(128, 25000)]
    [InlineData(129, 45000)]
    [InlineData(256, 45000)]
    [InlineData(257, 80000)]
    [InlineData(512, 80000)]
    public void ResolvesExistingPkrLaunchRate(int teams, int expected) =>
        EventPassPricing.EstimatePkr(teams).Should().Be(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(513)]
    [InlineData(10000)]
    public void NonStandardRequestRequiresAQuote_NotAFreePass(int teams) =>
        EventPassPricing.EstimatePkr(teams).Should().BeNull();
}
