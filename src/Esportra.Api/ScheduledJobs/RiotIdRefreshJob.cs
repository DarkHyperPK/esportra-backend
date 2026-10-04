using Esportra.Api.Services;

namespace Esportra.Api.ScheduledJobs;

/// <summary>Every 30 minutes: re-read a batch of linked Riot accounts so renamed Riot IDs follow automatically.</summary>
public sealed class RiotIdRefreshJob(RiotIdRefreshService refresh, IConfiguration config, ILogger<RiotIdRefreshJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config["Riot:ApiKey"])) return;
        var summary = await refresh.RefreshStaleAsync(config.GetValue("Riot:IdRefreshBatchSize", 60), ct);
        if (summary.Renamed > 0 || summary.RateLimited)
            logger.LogInformation("[RiotIdRefresh] Checked {Checked}, renamed {Renamed}, rate limited {Limited}", summary.Checked, summary.Renamed, summary.RateLimited);
    }
}
