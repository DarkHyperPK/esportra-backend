using Esportra.Core.Tournaments;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Rebuilds the persisted leaderboard_team_stats table and invalidates the
/// public leaderboard cache.
///
/// Invoked by:
///   • event triggers (match finalized / winner set / placements resolved) via
///     LeaderboardRefreshTrigger → BackgroundJob.Enqueue — second-level freshness
///   • startup enqueue — backfills after deploys/downtime
///   • hourly Hangfire cron — self-healing sweep for anything a future write
///     path forgets to trigger
///
/// A source-data fingerprint makes duplicate triggers free: when nothing has
/// changed since the last completed rebuild, the job exits without touching
/// the table. Only genuinely new data pays for a rebuild.
/// </summary>
public sealed class LeaderboardRefreshJob(
    LeaderboardStatsService stats,
    HybridCache cache,
    ILogger<LeaderboardRefreshJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var fingerprint = await stats.GetSourceFingerprintAsync(ct);
            if (fingerprint is not null && fingerprint == await stats.GetLastFingerprintAsync(ct))
            {
                logger.LogDebug("[Leaderboard] Sources unchanged; skipping rebuild.");
                return;
            }

            await stats.RecomputeAsync(ct);

            if (fingerprint is not null)
                await stats.MarkRecomputedAsync(fingerprint, ct);

            try
            {
                await cache.RemoveByTagAsync("leaderboard", ct);
            }
            catch (Exception invalidationEx)
            {
                // Never fail the job over cache invalidation — entries expire in ≤60s
                // regardless — but make the failure visible instead of silently stale.
                logger.LogWarning(invalidationEx,
                    "[Leaderboard] Cache invalidation failed; entries may be up to 60s stale.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Leaderboard] Refresh job failed.");
            throw;
        }
    }
}
