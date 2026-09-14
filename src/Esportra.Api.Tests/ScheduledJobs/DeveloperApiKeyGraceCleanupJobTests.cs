using Esportra.Api.ScheduledJobs;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Esportra.Api.Tests.ScheduledJobs;

public sealed class DeveloperApiKeyGraceCleanupJobTests
{
    // ── No expired keys ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoRowsAffected_DoesNotLog()
    {
        var db = new FakeDbConnectionFactory();
        db.EnqueueNonQueryResult(0); // ExecuteAsync returns row count 0
        var logger = new CapturingLogger<DeveloperApiKeyGraceCleanupJob>();
        var job = new DeveloperApiKeyGraceCleanupJob(db, logger);

        await job.ExecuteAsync(CancellationToken.None);

        logger.Messages.Should().NotContain(m => m.Contains("Revoked"));
    }

    // ── Expired keys revoked ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RowsAffected_LogsCount()
    {
        var db = new FakeDbConnectionFactory();
        db.EnqueueNonQueryResult(3); // ExecuteAsync returns 3 rows affected
        var logger = new CapturingLogger<DeveloperApiKeyGraceCleanupJob>();
        var job = new DeveloperApiKeyGraceCleanupJob(db, logger);

        await job.ExecuteAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle(m => m.Contains("Revoked") && m.Contains("3"));
    }
}
