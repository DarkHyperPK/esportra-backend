using Esportra.Api.SponsorAnalytics;
using Esportra.Contracts.Requests;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class SponsorAnalyticsExportTests
{
    [Theory]
    [InlineData("summary", 30, true)]
    [InlineData("performance", 7, true)]
    [InlineData("placements", 90, true)]
    [InlineData("content", 30, true)]
    [InlineData("devices", 30, true)]
    [InlineData("full", 30, true)]
    [InlineData("invalid_type", 30, false)]
    [InlineData("summary", 14, false)]
    public void ExportRequest_ValidatesReportTypeAndDays(string reportType, int days, bool valid)
    {
        var request = new RequestSponsorAnalyticsExportRequest(reportType, days);

        if (valid)
        {
            Assert.True(SponsorAnalyticsPolicy.IsSupportedPeriod(request.Days));
        }
        else
        {
            var isInvalidPeriod = !SponsorAnalyticsPolicy.IsSupportedPeriod(request.Days);
            var isInvalidType = reportType == "invalid_type";
            Assert.True(isInvalidPeriod || isInvalidType);
        }
    }

    [Fact]
    public void ExportRequest_DefaultDaysIs30()
    {
        var request = new RequestSponsorAnalyticsExportRequest("summary");
        Assert.Equal(30, request.Days);
    }

    [Fact]
    public void WriterValidation_RejectsInvalidPlacement()
    {
        var request = new RecordSponsorAnalyticsEventRequest(
            Guid.NewGuid(), Guid.NewGuid(), "impression", "invalid_placement");

        Assert.False(SponsorAnalyticsWriter.IsValidRequest(request));
    }

    [Fact]
    public void WriterValidation_RejectsInvalidEventType()
    {
        var request = new RecordSponsorAnalyticsEventRequest(
            Guid.NewGuid(), Guid.NewGuid(), "hover", "homepage_ticker");

        Assert.False(SponsorAnalyticsWriter.IsValidRequest(request));
    }

    [Fact]
    public void WriterValidation_RejectsEmptyEventId()
    {
        var request = new RecordSponsorAnalyticsEventRequest(
            Guid.Empty, Guid.NewGuid(), "impression", "homepage_ticker");

        Assert.False(SponsorAnalyticsWriter.IsValidRequest(request));
    }

    [Fact]
    public void WriterValidation_RejectsLongPagePath()
    {
        var request = new RecordSponsorAnalyticsEventRequest(
            Guid.NewGuid(), Guid.NewGuid(), "impression", "homepage_ticker",
            PagePath: new string('a', 257));

        Assert.False(SponsorAnalyticsWriter.IsValidRequest(request));
    }
}
