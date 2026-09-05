using Esportra.Core.Tournaments;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Api-side implementation of ILeaderboardSourceChangeHook. Core write paths
/// (match finalization, winner assignment, placement resolution) fire this after
/// commit; it enqueues the Hangfire refresh job so leaderboards update within
/// seconds of any source change. Hangfire deduplicates/retries; the job itself
/// no-ops when its fingerprint shows nothing changed (burst coalescing).
/// </summary>
public sealed class LeaderboardRefreshTrigger(IBackgroundJobClient jobs) : ILeaderboardSourceChangeHook
{
    public Task OnSourceChangedAsync(CancellationToken ct = default)
    {
        jobs.Enqueue<LeaderboardRefreshJob>(j => j.ExecuteAsync(CancellationToken.None));
        return Task.CompletedTask;
    }
}
