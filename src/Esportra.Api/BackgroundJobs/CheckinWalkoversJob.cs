using Esportra.Api.Services;
using Esportra.Core.Tournaments;

namespace Esportra.Api.BackgroundJobs;

/// <summary>
/// Periodically scans pending matches and awards walkovers when the check-in window
/// has closed. Also see <see cref="CheckinWalkoverProcessor"/> used on room-state reads.
/// </summary>
public sealed class CheckinWalkoversJob(
    IServiceScopeFactory scopeFactory,
    ILogger<CheckinWalkoversJob> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        logger.LogInformation("[CheckinWalkovers] Background job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessExpiredMatchesAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[CheckinWalkovers] Error during poll cycle.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessExpiredMatchesAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<CheckinWalkoverProcessor>();
        var notifier = scope.ServiceProvider.GetRequiredService<CheckinWalkoverNotifier>();

        var candidates = await processor.FindCandidateMatchIdsAsync(ct);
        if (candidates.Count == 0)
            return;

        var processed = 0;
        foreach (var matchId in candidates)
        {
            try
            {
                var outcome = await processor.TryProcessDueWalkoverAsync(matchId, DateTime.UtcNow, ct);
                if (!outcome.Processed && !outcome.NeedsOrganizerNotification)
                    continue;

                processed++;
                await notifier.DispatchAsync(matchId, outcome, ct);

                logger.LogInformation(
                    "[CheckinWalkovers] Match {MatchId}: {Status} (winner={WinnerId}).",
                    matchId,
                    outcome.Status,
                    outcome.WinnerId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[CheckinWalkovers] Failed to process match {MatchId}.", matchId);
            }
        }

        if (processed > 0)
            logger.LogInformation("[CheckinWalkovers] Processed {Count} walkover(s).", processed);
    }
}
