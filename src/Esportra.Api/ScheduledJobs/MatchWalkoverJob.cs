using Esportra.Api.Services;
using Esportra.Core.Tournaments;

namespace Esportra.Api.ScheduledJobs;

public sealed class MatchWalkoverJob(
    CheckinWalkoverProcessor processor,
    CheckinWalkoverNotifier notifier,
    ILogger<MatchWalkoverJob> logger)
{
    public async Task ExecuteAsync(Guid matchId, CancellationToken ct)
    {
        try
        {
            var outcome = await processor.TryProcessDueWalkoverAsync(matchId, DateTime.UtcNow, ct);
            if (!outcome.Processed && !outcome.NeedsOrganizerNotification)
                return;

            await notifier.DispatchAsync(matchId, outcome, ct);

            logger.LogInformation(
                "[MatchWalkover] Match {MatchId}: {Status} (winner={WinnerId}).",
                matchId, outcome.Status, outcome.WinnerId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MatchWalkover] Failed to process match {MatchId}.", matchId);
            throw;
        }
    }
}
