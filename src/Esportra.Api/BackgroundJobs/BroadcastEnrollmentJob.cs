using Esportra.Core.Broadcast;

namespace Esportra.Api.BackgroundJobs;

public sealed class BroadcastEnrollmentJob(IServiceScopeFactory scopes, ILogger<BroadcastEnrollmentJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IBroadcastEnrollmentRepository>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error) { logger.LogError(error, "Broadcast stage enrollment cycle failed"); }
        }
    }
}
