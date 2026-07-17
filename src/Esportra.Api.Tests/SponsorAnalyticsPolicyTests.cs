using Esportra.Api.SponsorAnalytics;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class SponsorAnalyticsPolicyTests
{
    [Theory]
    [InlineData(7, true)]
    [InlineData(30, true)]
    [InlineData(90, true)]
    [InlineData(14, false)]
    public void IsSupportedPeriod_ReturnsExpected(int days, bool expected)
    {
        Assert.Equal(expected, SponsorAnalyticsPolicy.IsSupportedPeriod(days));
    }

    [Fact]
    public void CreateWindow_IncludesCurrentUtcDay()
    {
        var now = new DateTimeOffset(2026, 7, 17, 19, 0, 0, TimeSpan.Zero);

        var window = SponsorAnalyticsPolicy.CreateWindow(7, now);

        Assert.Equal(new DateOnly(2026, 7, 11), window.Start);
        Assert.Equal(new DateOnly(2026, 7, 18), window.EndExclusive);
    }

    [Theory]
    [InlineData("pk", "PK")]
    [InlineData(" US ", "US")]
    [InlineData("unknown", null)]
    public void NormalizeCountry_ReturnsExpected(string value, string? expected)
    {
        Assert.Equal(expected, SponsorAnalyticsPolicy.NormalizeCountry(value));
    }

    [Theory]
    [InlineData("2010-07-17", "13_17")]
    [InlineData("2002-07-17", "18_24")]
    [InlineData("1995-07-17", "25_34")]
    [InlineData("1985-07-17", "35_44")]
    [InlineData("1975-07-17", "45_54")]
    [InlineData("1960-07-17", "55_plus")]
    public void CalculateAgeBand_ReturnsExpected(string date, string expected)
    {
        Assert.Equal(expected, SponsorAnalyticsPolicy.CalculateAgeBand(
            DateTime.Parse(date), new DateOnly(2026, 7, 17)));
    }

    [Fact]
    public void Suppress_HidesEntireAudienceBelowThreshold()
    {
        var result = SponsorAnalyticsPolicy.Suppress(
            new Dictionary<string, long> { ["PK"] = 9 }, 9, 9, 10);

        Assert.Equal("suppressed", result.Status);
        Assert.Empty(result.Segments);
        Assert.Null(result.KnownAudience);
    }

    [Fact]
    public void Suppress_AppliesComplementarySuppression()
    {
        var result = SponsorAnalyticsPolicy.Suppress(
            new Dictionary<string, long> { ["PK"] = 12, ["US"] = 8 }, 20, 20, 10);

        Assert.Equal("suppressed", result.Status);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public void Suppress_PublishesMultipleSafeSegments()
    {
        var result = SponsorAnalyticsPolicy.Suppress(
            new Dictionary<string, long> { ["PK"] = 15, ["US"] = 10 }, 25, 25, 10);

        Assert.Equal("available", result.Status);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(60m, result.Segments[0].PercentageOfKnown);
    }
}
