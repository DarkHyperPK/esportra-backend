using Esportra.Api.ScheduledJobs;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.ScheduledJobs;

/// <summary>Unit tests for <see cref="StaleTemplateRulesJob"/>.</summary>
public sealed class StaleTemplateRulesJobTests
{
    // ── Flagging stale templates ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FlagsTemplatesOlderThan90Days()
    {
        var db = new FakeDbConnectionFactory();
        db.EnqueueNonQueryResult(1); // first UPDATE: 1 template flagged as stale
        db.EnqueueNonQueryResult(0); // second UPDATE: 0 templates cleared
        var logger = new CapturingLogger<StaleTemplateRulesJob>();
        var job = new StaleTemplateRulesJob(db, logger);

        await job.ExecuteAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle(
            m => m.Contains("flagged") && m.Contains("1"),
            "should log that 1 template was flagged as stale");
    }

    // ── Clearing the stale flag ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ClearsStaleFlag_WhenRecentlyUpdated()
    {
        var db = new FakeDbConnectionFactory();
        db.EnqueueNonQueryResult(0); // first UPDATE: 0 templates flagged
        db.EnqueueNonQueryResult(2); // second UPDATE: 2 templates cleared
        var logger = new CapturingLogger<StaleTemplateRulesJob>();
        var job = new StaleTemplateRulesJob(db, logger);

        await job.ExecuteAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle(
            m => m.Contains("cleared") && m.Contains("2"),
            "should log that 2 templates had their stale flag cleared");
    }

    // ── No-op log when nothing changes ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LogsNoOpMessage_WhenNoChanges()
    {
        var db = new FakeDbConnectionFactory();
        db.EnqueueNonQueryResult(0); // first UPDATE: no rows affected
        db.EnqueueNonQueryResult(0); // second UPDATE: no rows affected
        var logger = new CapturingLogger<StaleTemplateRulesJob>();
        var job = new StaleTemplateRulesJob(db, logger);

        await job.ExecuteAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle(
            m => m.Contains("no stale templates found"),
            "should log the no-op message when neither UPDATE touches any rows");
    }

    // ── Both flagged and cleared in same run ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LogsFlaggedAndCleared_WhenBothNonZero()
    {
        var db = new FakeDbConnectionFactory();
        db.EnqueueNonQueryResult(3); // 3 flagged
        db.EnqueueNonQueryResult(1); // 1 cleared
        var logger = new CapturingLogger<StaleTemplateRulesJob>();
        var job = new StaleTemplateRulesJob(db, logger);

        await job.ExecuteAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle(
            m => m.Contains("3") && m.Contains("1"),
            "log line must contain both the flagged count (3) and the cleared count (1)");
        logger.Messages.Should().NotContain(
            m => m.Contains("no stale templates found"),
            "no-op message must not be logged when changes occurred");
    }

    // ── Queue attribute ───────────────────────────────────────────────────────

    [Fact]
    public void ExecuteAsync_HasDefaultQueueAttribute()
    {
        var method = typeof(StaleTemplateRulesJob)
            .GetMethod(nameof(StaleTemplateRulesJob.ExecuteAsync))!;
        var attr = method
            .GetCustomAttributes(typeof(Hangfire.QueueAttribute), inherit: false)
            .Cast<Hangfire.QueueAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull("StaleTemplateRulesJob.ExecuteAsync must be decorated with [Queue]");
        attr!.Queue.Should().Be("default");
    }
}
