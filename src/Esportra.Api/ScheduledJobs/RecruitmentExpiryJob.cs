using Dapper;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Expires recruitment openings whose expires_at has passed.
/// Runs every hour; idempotent — safe to re-run at any time.
/// </summary>
[Queue("default")]
public sealed class RecruitmentExpiryJob(IDbConnectionFactory db, ILogger<RecruitmentExpiryJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            """
            UPDATE public.recruitment_openings
            SET status = 'expired'::recruitment_opening_status, updated_at = now()
            WHERE status = 'open' AND expires_at < now()
            """);

        if (affected > 0)
            logger.LogInformation("[RecruitmentExpiry] Expired {Count} opening(s).", affected);
    }
}
