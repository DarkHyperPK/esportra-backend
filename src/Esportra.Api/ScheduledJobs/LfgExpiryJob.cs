using Dapper;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Expires published LFG listings whose expires_at has passed.
/// Runs every 15 minutes; idempotent — safe to re-run at any time.
/// </summary>
[Queue("default")]
public sealed class LfgExpiryJob(IDbConnectionFactory db, ILogger<LfgExpiryJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            """
            UPDATE public.lfg_listings
            SET status = 'expired'::lfg_listing_status, updated_at = now()
            WHERE status = 'published' AND expires_at < now()
            """);

        if (affected > 0)
            logger.LogInformation("[LfgExpiry] Expired {Count} listing(s).", affected);
    }
}
