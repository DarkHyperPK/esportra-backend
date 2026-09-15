using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Deletes developer API audit log entries older than 90 days.
/// IP addresses stored in audit logs are PII under GDPR Article 4 and must be purged.
/// Runs daily via PeriodicTimer.
/// </summary>
public sealed class DeveloperApiAuditLogPurgeJob(
    IDbConnectionFactory db,
    ILogger<DeveloperApiAuditLogPurgeJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await PurgeExpiredEntriesAsync(ct);
        }
    }

    private async Task PurgeExpiredEntriesAsync(CancellationToken ct)
    {
        try
        {
            using var conn = db.CreateConnection();
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM developer_api_audit_log WHERE created_at < NOW() - INTERVAL '90 days'",
                commandTimeout: 120);

            if (deleted > 0)
                logger.LogInformation(
                    "[DeveloperApiAuditLogPurge] Deleted {Count} audit entries older than 90 days", deleted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[DeveloperApiAuditLogPurge] Failed to purge audit log");
        }
    }
}
