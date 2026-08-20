using Esportra.Api.Services;

namespace Esportra.Api.BackgroundJobs;

public sealed class SponsorAssetCleanupJob(
    IServiceScopeFactory scopeFactory,
    ILogger<SponsorAssetCleanupJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<SponsorAssetCleanupService>();
                await service.ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Sponsor asset cleanup cycle failed"); }
        }
    }
}
