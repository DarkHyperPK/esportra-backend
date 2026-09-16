using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Revokes developer API keys whose grace period has expired.
/// Keys in 'rotating' status with an expired grace_period_until become 'revoked'.
/// Runs every 15 minutes.
/// </summary>
public sealed class DeveloperApiKeyGraceCleanupJob(
    IDbConnectionFactory db,
    ILogger<DeveloperApiKeyGraceCleanupJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            """
            UPDATE developer_api_keys
            SET status = 'revoked', revoked_at = NOW()
            WHERE grace_period_until IS NOT NULL
              AND grace_period_until < NOW()
              AND status = 'rotating'
            """);

        if (affected > 0)
            logger.LogInformation(
                "[DeveloperApiKeyGraceCleanup] Revoked {Count} expired rotating key(s).", affected);
    }
}
