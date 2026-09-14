using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests.Services;

public sealed class BrScheduleNotificationServiceTests
{
    // ── FormatScheduleTimeForUser ────────────────────────────────────────────

    [Fact]
    public void FormatScheduleTimeForUser_ReturnsTbd_WhenScheduledAtIsNull()
    {
        var result = BrScheduleNotificationService.FormatScheduleTimeForUser(null, "UTC");

        Assert.Equal("TBD", result);
    }

    [Fact]
    public void FormatScheduleTimeForUser_ReturnsTime_WhenTzIanaIsNull()
    {
        var scheduled = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

        var result = BrScheduleNotificationService.FormatScheduleTimeForUser(scheduled, null);

        Assert.Contains("10:00 AM", result);
        Assert.DoesNotContain("UTC", result);
    }

    [Fact]
    public void FormatScheduleTimeForUser_ReturnsTime_WhenTzIanaIsInvalid()
    {
        var scheduled = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

        var result = BrScheduleNotificationService.FormatScheduleTimeForUser(scheduled, "Invalid/Timezone/Id");

        Assert.Contains("10:00 AM", result);
        Assert.DoesNotContain("UTC", result);
    }

    [Fact]
    public void FormatScheduleTimeForUser_ReturnsTimeWithoutLabel_WhenTzIanaIsEmpty()
    {
        var scheduled = new DateTimeOffset(2026, 10, 5, 14, 30, 0, TimeSpan.Zero);

        var result = BrScheduleNotificationService.FormatScheduleTimeForUser(scheduled, "   ");

        Assert.Contains("2:30 PM", result);
        Assert.DoesNotContain("UTC", result);
    }

    [Fact]
    public void FormatScheduleTimeForUser_ReturnsTimeWithoutLabel_WhenTzIanaIsUtc()
    {
        var scheduled = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

        var result = BrScheduleNotificationService.FormatScheduleTimeForUser(scheduled, "UTC");

        Assert.Contains("8:00 AM", result);
        Assert.DoesNotContain("UTC", result);
    }
}
