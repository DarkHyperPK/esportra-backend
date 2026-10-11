using Dapper;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>Queues token-free overlay observations for the existing managed Hangfire worker.</summary>
public sealed class PublicVetoOverlayUsageScheduler(
    IBackgroundJobClient jobs, ILogger<PublicVetoOverlayUsageScheduler> logger)
{
    public void Enqueue(Guid sessionId, DateTime loadedAt)
    {
        try
        {
            jobs.Enqueue<PublicVetoOverlayUsageJob>(job => job.RecordAsync(sessionId, loadedAt, CancellationToken.None));
        }
        catch (Exception ex)
        {
            // Analytics must never prevent a broadcaster from loading a valid overlay.
            logger.LogWarning(ex, "Could not queue first overlay load for session {SessionId}; a later load will retry", sessionId);
        }
    }
}

/// <summary>Hangfire resolves a fresh scope, persists failures, and retries the idempotent write.</summary>
public sealed class PublicVetoOverlayUsageJob(IDbConnectionFactory db)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RecordAsync(Guid sessionId, DateTime loadedAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE public.public_veto_sessions
            SET overlay_first_loaded_at = @loadedAt
            WHERE id = @sessionId AND overlay_first_loaded_at IS NULL
            """, new { sessionId, loadedAt }, commandTimeout: 5, cancellationToken: ct));
    }
}
