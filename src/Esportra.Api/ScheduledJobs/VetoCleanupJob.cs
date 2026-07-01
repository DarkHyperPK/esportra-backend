using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.ScheduledJobs;

public sealed class VetoCleanupJob(
    IDbConnectionFactory db,
    ILogger<VetoCleanupJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var deleted = await conn.ExecuteAsync(
            """
            DELETE FROM public.public_veto_sessions
            WHERE expires_at IS NOT NULL
              AND expires_at <= NOW()
            """);

        if (deleted > 0)
            logger.LogInformation("[VetoCleanup] Deleted {Count} expired public veto session(s).", deleted);
    }
}
