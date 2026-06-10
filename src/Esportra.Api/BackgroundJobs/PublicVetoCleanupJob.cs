using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.BackgroundJobs;

public sealed class PublicVetoCleanupJob(
    IServiceScopeFactory scopeFactory,
    ILogger<PublicVetoCleanupJob> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        logger.LogInformation("[PublicVetoCleanup] Background job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeleteExpiredSessionsAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[PublicVetoCleanup] Error during cleanup cycle.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DeleteExpiredSessionsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

        using var conn = db.CreateConnection();
        var deleted = await conn.ExecuteAsync(
            """
            DELETE FROM public.public_veto_sessions
            WHERE expires_at <= NOW()
            """);

        if (deleted > 0)
            logger.LogInformation("[PublicVetoCleanup] Deleted {Count} expired public veto session(s).", deleted);
    }
}
