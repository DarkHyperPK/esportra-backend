namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Escalates matches where the round deadline has passed with no accepted scheduled time.
/// Notifies organizers so they can force-schedule or award a walkover.
/// Phase 2 implementation — stub until round deadline enforcement is built.
/// </summary>
public sealed class RoundDeadlineEscalationJob(ILogger<RoundDeadlineEscalationJob> logger)
{
    public Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogDebug("[RoundDeadlineEscalation] Job stub — not yet implemented.");
        return Task.CompletedTask;
    }
}
