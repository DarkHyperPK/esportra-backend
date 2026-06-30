using Esportra.Api.Helpers;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class TournamentTimelineValidatorTests
{
    private static readonly DateTimeOffset June14MidnightUtc =
      new(2026, 6, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsRegistrationDeadlineOpen_treats_utc_midnight_as_end_of_day()
    {
        var afternoon = new DateTimeOffset(2026, 6, 14, 15, 0, 0, TimeSpan.Zero);
        var dayBefore = new DateTimeOffset(2026, 6, 13, 23, 0, 0, TimeSpan.Zero);
        var afterEnd = new DateTimeOffset(2026, 6, 15, 0, 0, 1, TimeSpan.Zero);

        Assert.True(TournamentTimelineValidator.IsRegistrationDeadlineOpen(June14MidnightUtc, afternoon));
        Assert.True(TournamentTimelineValidator.IsRegistrationDeadlineOpen(June14MidnightUtc, dayBefore));
        Assert.False(TournamentTimelineValidator.IsRegistrationDeadlineOpen(June14MidnightUtc, afterEnd));
    }

    [Fact]
    public void ValidateRegistrationWindow_rejects_closed_status()
    {
        var error = TournamentTimelineValidator.ValidateRegistrationWindow(
          "draft",
          June14MidnightUtc,
          new DateTimeOffset(2026, 6, 15, 18, 0, 0, TimeSpan.Zero));

        Assert.Equal("Tournament is not accepting registrations.", error);
    }

    [Fact]
    public void ValidateRegistrationWindow_rejects_past_deadline()
    {
        var now = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var error = TournamentTimelineValidator.ValidateRegistrationWindow(
          "published",
          June14MidnightUtc,
          new DateTimeOffset(2026, 6, 20, 18, 0, 0, TimeSpan.Zero),
          registrationOpens: null,
          now: now);

        Assert.Equal("Registration deadline has passed.", error);
    }

    [Fact]
    public void ValidateRegistrationWindow_rejects_before_opens()
    {
        var opens = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var error = TournamentTimelineValidator.ValidateRegistrationWindow(
          "open",
          June14MidnightUtc,
          new DateTimeOffset(2026, 6, 20, 18, 0, 0, TimeSpan.Zero),
          opens,
          now);

        Assert.Equal("Registration has not opened yet.", error);
    }

    [Fact]
    public void ValidateRegistrationWindow_rejects_after_start()
    {
        var start = new DateTimeOffset(2026, 6, 13, 18, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 6, 13, 19, 0, 0, TimeSpan.Zero);
        var error = TournamentTimelineValidator.ValidateRegistrationWindow(
          "published",
          June14MidnightUtc,
          start,
          registrationOpens: null,
          now: now);

        Assert.Equal("Tournament has already started.", error);
    }

    [Fact]
    public void ValidateRegistrationWindow_allows_open_window()
    {
        var opens = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 6, 15, 18, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

        var error = TournamentTimelineValidator.ValidateRegistrationWindow(
          "published",
          June14MidnightUtc,
          start,
          opens,
          now);

        Assert.Null(error);
    }

    [Fact]
    public void ParseRegistrationOpensAt_reads_settings_json()
    {
        var settings = """{"registrationOpensAt":"2026-06-10T12:00:00Z"}""";
        var parsed = TournamentTimelineValidator.ParseRegistrationOpensAt(settings);

        Assert.NotNull(parsed);
        Assert.Equal(2026, parsed!.Value.Year);
        Assert.Equal(6, parsed.Value.Month);
        Assert.Equal(10, parsed.Value.Day);
    }
}
