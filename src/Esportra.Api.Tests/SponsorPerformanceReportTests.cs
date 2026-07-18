using Esportra.Api.SponsorAnalytics;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class SponsorPerformanceReportTests
{
    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(10, 100, 10.0)]
    [InlineData(1, 3, 33.33)]
    [InlineData(5, 0, 0)]
    public void ComputeCtr_ReturnsExpected(long clicks, long impressions, decimal expected)
    {
        Assert.Equal(expected, SponsorPerformanceReportService.ComputeCtr(clicks, impressions));
    }

    [Theory]
    [InlineData(100, 50, 100.0)]
    [InlineData(50, 100, -50.0)]
    [InlineData(100, 100, 0)]
    [InlineData(100, 0, 0)]
    public void ComputeChangePercent_ReturnsExpected(long current, long previous, decimal expected)
    {
        Assert.Equal(expected, SponsorPerformanceReportService.ComputeChangePercent(current, previous));
    }

    [Theory]
    [InlineData("partner", "partner", true)]
    [InlineData("ascendant", "partner", true)]
    [InlineData("radiant", "partner", true)]
    [InlineData("radiant", "ascendant", true)]
    [InlineData("radiant", "radiant", true)]
    [InlineData("partner", "ascendant", false)]
    [InlineData("partner", "radiant", false)]
    [InlineData("ascendant", "radiant", false)]
    public void TierAtLeast_ReturnsExpected(string actual, string required, bool expected)
    {
        Assert.Equal(expected, SponsorAnalyticsEndpoints.TierAtLeast(actual, required));
    }

    [Theory]
    [InlineData("standard", "partner", true)]
    [InlineData("diamond", "partner", true)]
    [InlineData("diamond", "ascendant", false)]
    public void TierAtLeast_HandlesLegacyTierNames(string actual, string required, bool expected)
    {
        Assert.Equal(expected, SponsorAnalyticsEndpoints.TierAtLeast(actual, required));
    }

    [Fact]
    public void ComputeCtr_RoundsToTwoDecimals()
    {
        var result = SponsorPerformanceReportService.ComputeCtr(1, 7);
        Assert.Equal(14.29m, result);
    }

    [Fact]
    public void ComputeChangePercent_RoundsToOneDecimal()
    {
        var result = SponsorPerformanceReportService.ComputeChangePercent(7, 3);
        Assert.Equal(133.3m, result);
    }
}
