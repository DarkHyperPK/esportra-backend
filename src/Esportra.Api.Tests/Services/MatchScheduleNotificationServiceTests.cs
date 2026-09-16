using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests.Services;

public sealed class MatchScheduleNotificationServiceTests
{
    // ── FormatTimeForUser ────────────────────────────────────────────────────

    [Fact]
    public void FormatTimeForUser_ReturnsTimeTbd_WhenScheduledUtcIsNull()
    {
        var result = MatchScheduleNotificationService.FormatTimeForUser(null, "UTC");

        Assert.Equal("Time TBD", result);
    }

    [Fact]
    public void FormatTimeForUser_ReturnsTime_WhenTzIanaIsNull()
    {
        var scheduled = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);

        var result = MatchScheduleNotificationService.FormatTimeForUser(scheduled, null);

        Assert.Contains("10:00 AM", result);
        Assert.DoesNotContain("UTC", result);
    }

    [Fact]
    public void FormatTimeForUser_ReturnsTime_WhenTzIanaIsInvalid()
    {
        var scheduled = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);

        var result = MatchScheduleNotificationService.FormatTimeForUser(scheduled, "Not/A/Valid/Timezone");

        Assert.Contains("10:00 AM", result);
        Assert.DoesNotContain("UTC", result);
    }

    [Fact]
    public void FormatTimeForUser_ReturnsTimeWithoutLabel_WhenTzIanaIsUtc()
    {
        var scheduled = new DateTime(2026, 10, 5, 14, 30, 0, DateTimeKind.Utc);

        var result = MatchScheduleNotificationService.FormatTimeForUser(scheduled, "UTC");

        Assert.Contains("2:30 PM", result);
        Assert.DoesNotContain("UTC", result);
    }

    [Fact]
    public void FormatTimeForUser_FallsBackToUtcTime_WhenTzIanaIsEmpty()
    {
        var scheduled = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

        var result = MatchScheduleNotificationService.FormatTimeForUser(scheduled, "   ");

        Assert.Contains("9:00 AM", result);
        Assert.DoesNotContain("UTC", result);
    }

    // ── BuildMatchMessage ────────────────────────────────────────────────────

    [Fact]
    public void BuildMatchMessage_IncludesStageName_WhenPresent()
    {
        var result = MatchScheduleNotificationService.BuildMatchMessage(
            "Grand Finals",
            "Team A vs Team B",
            new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc),
            "Mon, Oct 5 · 10:00 AM");

        Assert.Contains("Grand Finals", result);
        Assert.Contains("Team A vs Team B", result);
        Assert.Contains("Mon, Oct 5 · 10:00 AM", result);
    }

    [Fact]
    public void BuildMatchMessage_OmitsStageName_WhenNull()
    {
        var result = MatchScheduleNotificationService.BuildMatchMessage(
            null,
            "Team A vs Team B",
            new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc),
            "Mon, Oct 5 · 10:00 AM");

        var lines = result.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("Team A vs Team B", lines[0]);
    }

    [Fact]
    public void BuildMatchMessage_ShowsClearedText_WhenScheduleIsNull()
    {
        var result = MatchScheduleNotificationService.BuildMatchMessage(
            null,
            "Team A vs Team B",
            null,
            "Time TBD");

        Assert.Contains("Organizer removed the scheduled time.", result);
        Assert.DoesNotContain("Time TBD", result);
    }

    [Fact]
    public void BuildMatchMessage_ThreeLinesWhenStageNameAndScheduledTime()
    {
        var result = MatchScheduleNotificationService.BuildMatchMessage(
            "Quarterfinals",
            "Alpha vs Beta",
            new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc),
            "Mon, Oct 5 · 10:00 AM");

        var lines = result.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("Quarterfinals", lines[0]);
        Assert.Equal("Alpha vs Beta", lines[1]);
        Assert.Equal("Mon, Oct 5 · 10:00 AM", lines[2]);
    }
}
