using Esportra.Api.ScheduledJobs;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScheduledJobs;

/// <summary>Unit tests for <see cref="ChatNotificationJob"/> guard logic.</summary>
public sealed class ChatNotificationJobTests
{
    // ── IsTerminalStatus ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("completed", true)]
    [InlineData("COMPLETED", true)]
    [InlineData("Completed", true)]
    [InlineData("cancelled", true)]
    [InlineData("CANCELLED", true)]
    [InlineData("bye", true)]
    [InlineData("BYE", true)]
    [InlineData(null, true)]
    public void IsTerminalStatus_ReturnsTrue_ForTerminalOrMissingStatus(string? status, bool expected)
    {
        ChatNotificationJob.IsTerminalStatus(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("pending", false)]
    [InlineData("in_progress", false)]
    [InlineData("scheduled", false)]
    [InlineData("ready", false)]
    [InlineData("check_in", false)]
    [InlineData("", false)]
    public void IsTerminalStatus_ReturnsFalse_ForActiveStatus(string status, bool expected)
    {
        ChatNotificationJob.IsTerminalStatus(status).Should().Be(expected);
    }

    // ── Queue attribute ───────────────────────────────────────────────────────

    [Fact]
    public void ChatNotificationJob_HasNotificationsQueueAttribute()
    {
        var attr = typeof(ChatNotificationJob)
            .GetCustomAttributes(typeof(Hangfire.QueueAttribute), inherit: false)
            .Cast<Hangfire.QueueAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull("ChatNotificationJob must be decorated with [Queue]");
        attr!.Queue.Should().Be("notifications");
    }
}
