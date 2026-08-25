using Esportra.Core.Tournaments;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Rebuilds the persisted leaderboard_team_stats table and invalidates the
/// public leaderboard cache. Runs every 2 minutes (cheap full recompute) plus
/// once at startup so a fresh deployment is populated immediately.
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
            await stats.RecomputeAsync(ct);
            try
            {
                await cache.RemoveByTagAsync("leaderboard", ct);
            }
            catch
            {
                // Cache invalidation is best effort — entries expire in ≤60s regardless.
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Leaderboard] Refresh job failed.");
            throw;
        }
    }
}
