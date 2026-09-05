namespace Esportra.Core.Tournaments;

using Microsoft.Extensions.Logging;

/// <summary>
/// Fired by Core write paths (match finalization, winner assignment, placement
/// resolution) whenever leaderboard source data may have changed. The API layer
/// registers an implementation that enqueues the Hangfire refresh job — Core
/// itself stays scheduler-free.
///
/// Implementations MUST be cheap, non-throwing best-effort calls; write-path
/// hosts wrap invocations defensively and treat hook failure as non-fatal.
/// </summary>
public interface ILeaderboardSourceChangeHook
{
    Task OnSourceChangedAsync(CancellationToken ct = default);
}

/// <summary>
/// Safe invocation helper so every write path gets identical defensive semantics.
/// </summary>
public static class LeaderboardSourceChangeHooks
{
    public static async Task FireAsync(
        IEnumerable<ILeaderboardSourceChangeHook>? hooks,
        Microsoft.Extensions.Logging.ILogger logger,
        string source,
        CancellationToken ct = default)
    {
        if (hooks is null) return;

        foreach (var hook in hooks)
        {
            try
            {
                await hook.OnSourceChangedAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[Leaderboard] Source-change hook from {Source} failed (non-fatal).", source);
            }
        }
    }
}
