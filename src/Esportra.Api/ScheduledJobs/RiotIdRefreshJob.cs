using Esportra.Api.Services;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Keeps linked Riot IDs current. <see cref="ExecuteAsync"/> is the scheduled safety-net sweep; the other
/// methods are queued by <see cref="IRiotIdRefreshTrigger"/> when a player registers, checks in or joins a team.
/// </summary>
public sealed class RiotIdRefreshJob(RiotIdRefreshService refresh, IConfiguration config, ILogger<RiotIdRefreshJob> logger)
{
    // An event refresh skips accounts read from Riot within this window (a team registering and then
    // checking in an hour later costs one lookup per player, not two).
    private static readonly TimeSpan EventMaxAge = TimeSpan.FromHours(1);

    private bool Configured => !string.IsNullOrWhiteSpace(config["Riot:ApiKey"]);

    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Configured) return;
        Log("sweep", await refresh.RefreshStaleAsync(config.GetValue("Riot:IdRefreshBatchSize", 60), ct));
    }

    public async Task RefreshTournamentEntryAsync(Guid tournamentId, Guid userId, CancellationToken ct)
    {
        if (Configured) Log("tournament entry", await refresh.RefreshTournamentEntryAsync(tournamentId, userId, EventMaxAge, ct));
    }

    public async Task RefreshCompetitorsAsync(Guid[] competitorIds, CancellationToken ct)
    {
        if (Configured) Log("competitors", await refresh.RefreshCompetitorsAsync(competitorIds, EventMaxAge, ct));
    }

    public async Task RefreshUsersAsync(Guid[] userIds, CancellationToken ct)
    {
        if (Configured) Log("users", await refresh.RefreshUsersAsync(userIds, EventMaxAge, ct));
    }

    private void Log(string scope, RiotIdRefreshSummary summary)
    {
        if (summary.Renamed > 0 || summary.RateLimited)
            logger.LogInformation("[RiotIdRefresh] {Scope}: checked {Checked}, renamed {Renamed}, rate limited {Limited}",
                scope, summary.Checked, summary.Renamed, summary.RateLimited);
    }
}
