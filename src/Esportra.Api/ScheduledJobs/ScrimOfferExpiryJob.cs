using Dapper;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Expires scrim offers older than 48 hours.
/// Runs every hour; idempotent — safe to re-run at any time.
/// </summary>
[Queue("default")]
public sealed class ScrimOfferExpiryJob(IDbConnectionFactory db, ILogger<ScrimOfferExpiryJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            """
            UPDATE public.scrim_offers
            SET status = 'expired'::scrim_offer_status, updated_at = now()
            WHERE status = 'pending'
              AND created_at < now() - INTERVAL '48 hours'
            """);

        if (affected > 0)
            logger.LogInformation("[ScrimOfferExpiry] Expired {Count} offer(s).", affected);
    }
}
