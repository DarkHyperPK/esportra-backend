using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public class ScrimSeriesTests
{
    private static ScrimGameState G(int n, string status, int? winner) => new(n, status, winner);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    public void WinsNeeded_ReturnsMajority(int bestOf, int expected) =>
        ScrimSeries.WinsNeeded(bestOf).Should().Be(expected);

    [Fact]
    public void Wins_CountsOnlyConfirmedAndResolvedMaps()
    {
        var games = new[] { G(1, "confirmed", 1), G(2, "reported", 2), G(3, "disputed", 2), G(4, "resolved", 2) };
        ScrimSeries.Wins(games).Should().Be((1, 1));
    }

    [Fact]
    public void SeriesWinner_ReturnsTeam_WhenMajorityConfirmed()
    {
        var games = new[] { G(1, "confirmed", 2), G(2, "confirmed", 2) };
        ScrimSeries.SeriesWinner(3, games).Should().Be(2);
    }

    [Fact]
    public void SeriesWinner_IsNull_WhenDecidingMapOnlyReported()
    {
        var games = new[] { G(1, "confirmed", 1), G(2, "reported", 1) };
        ScrimSeries.SeriesWinner(3, games).Should().BeNull();
    }

    [Fact]
    public void ValidateReport_Rejects_WhenPreviousMapAwaitsConfirmation()
    {
        var games = new[] { G(1, "reported", null) };
        ScrimSeries.ValidateReport(3, 2, games, 13, 5).Should().Contain("confirmation");
    }

    [Fact]
    public void ValidateReport_Allows_NextMap_WhenPreviousIsDisputed()
    {
        var games = new[] { G(1, "disputed", null) };
        ScrimSeries.ValidateReport(3, 2, games, 13, 5).Should().BeNull();
    }

    [Fact]
    public void ValidateReport_Rejects_SkippingAMap()
    {
        ScrimSeries.ValidateReport(3, 2, [], 13, 5).Should().Contain("game 1");
    }

    [Fact]
    public void ValidateReport_Rejects_DecidedSeries()
    {
        var games = new[] { G(1, "confirmed", 1), G(2, "confirmed", 1) };
        ScrimSeries.ValidateReport(3, 3, games, 13, 5).Should().Contain("decided");
    }

    [Fact]
    public void ValidateReport_Rejects_DrawAndNegative()
    {
        ScrimSeries.ValidateReport(1, 1, [], 12, 12).Should().NotBeNull();
        ScrimSeries.ValidateReport(1, 1, [], -1, 12).Should().NotBeNull();
    }

    [Fact]
    public void ValidateReport_Rejects_GameBeyondFormat_UnlessAMapWasVoided()
    {
        var played = new[] { G(1, "confirmed", 1), G(2, "confirmed", 2) };
        ScrimSeries.ValidateReport(3, 4, played, 13, 5).Should().NotBeNull();

        var voided = new[] { G(1, "confirmed", 1), G(2, "resolved", null), G(3, "confirmed", 2) };
        ScrimSeries.ValidateReport(3, 4, voided, 13, 5).Should().BeNull();
    }

    [Fact]
    public void NextGameNumber_ReturnsFirstUnreportedOrPendingMap()
    {
        ScrimSeries.NextGameNumber(3, []).Should().Be(1);
        ScrimSeries.NextGameNumber(3, [G(1, "reported", 1)]).Should().Be(1);
        ScrimSeries.NextGameNumber(3, [G(1, "confirmed", 1), G(2, "disputed", null)]).Should().Be(3);
        ScrimSeries.NextGameNumber(3, [G(1, "confirmed", 1), G(2, "confirmed", 1)]).Should().BeNull();
    }
}

public class ScrimSeriesDeadlineTests
{
    private static readonly DateTime T = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AcceptDeadline_AddsTheWindow() =>
        ScrimSeries.AcceptDeadline(T, 120).Should().Be(T.AddMinutes(120));

    [Fact]
    public void AcceptDeadline_NeverShorterThanFiveMinutes() =>
        ScrimSeries.AcceptDeadline(T, 0).Should().Be(T.AddMinutes(5));

    [Theory]
    [InlineData("reported", true)]
    [InlineData("disputed", false)]
    [InlineData("confirmed", false)]
    public void IsDue_OnlyForAnUnansweredReportPastItsDeadline(string status, bool expected) =>
        ScrimSeries.IsDue(status, T.AddMinutes(-1), T).Should().Be(expected);

    [Fact]
    public void IsDue_IsFalse_BeforeTheDeadlineOrWithoutOne()
    {
        ScrimSeries.IsDue("reported", T.AddMinutes(1), T).Should().BeFalse();
        ScrimSeries.IsDue("reported", null, T).Should().BeFalse();
    }
}
