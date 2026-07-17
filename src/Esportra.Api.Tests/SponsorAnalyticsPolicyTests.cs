using Esportra.Api.SponsorAnalytics;
using Esportra.Contracts.Requests;
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
            DateOnly.Parse(date), new DateOnly(2026, 7, 17)));
    }

    [Fact]
    public void CreateDimension_PublishesSinglePersonSegment()
    {
        var result = SponsorAnalyticsPolicy.CreateDimension(
            new Dictionary<string, long> { ["PK"] = 1 }, 1, 1);

        Assert.Equal("available", result.Status);
        Assert.Single(result.Segments);
        Assert.Equal(1, result.Segments[0].Audience);
    }

    [Fact]
    public void CreateDimension_PublishesAllSegments()
    {
        var result = SponsorAnalyticsPolicy.CreateDimension(
            new Dictionary<string, long> { ["PK"] = 12, ["US"] = 8 }, 20, 20);

        Assert.Equal("available", result.Status);
        Assert.Equal(2, result.Segments.Count);
    }

    [Fact]
    public void CreateDimension_ReturnsExactUnknownCoverage()
    {
        var result = SponsorAnalyticsPolicy.CreateDimension(
            new Dictionary<string, long> { ["PK"] = 1 }, 2, 1);

        Assert.Equal("available", result.Status);
        Assert.Equal(1, result.KnownAudience);
        Assert.Equal(1, result.UnknownAudience);
        Assert.Equal(50m, result.CoveragePercent);
    }

    [Fact]
    public void CreateDimension_ReturnsUnavailableWhenAllUnknown()
    {
        var result = SponsorAnalyticsPolicy.CreateDimension(
            new Dictionary<string, long>(), 1, 0);

        Assert.Equal("unavailable", result.Status);
        Assert.Equal(0, result.KnownAudience);
        Assert.Equal(1, result.UnknownAudience);
        Assert.Equal(0m, result.CoveragePercent);
    }

    [Fact]
    public void WriterValidation_AcceptsOptionalPagePath()
    {
        var request = new RecordSponsorAnalyticsEventRequest(
            Guid.NewGuid(), Guid.NewGuid(), "impression", "partner_showcase");

        Assert.True(SponsorAnalyticsWriter.IsValidRequest(request));
    }

    [Fact]
    public void WriterValidation_AcceptsRelativePagePath()
    {
        var request = new RecordSponsorAnalyticsEventRequest(
            Guid.NewGuid(), Guid.NewGuid(), "impression", "partner_showcase", PagePath: "/partners");

        Assert.True(SponsorAnalyticsWriter.IsValidRequest(request));
    }
}
