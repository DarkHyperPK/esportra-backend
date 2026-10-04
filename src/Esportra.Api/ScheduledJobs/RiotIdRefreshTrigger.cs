using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Re-reads players' Riot IDs at the moments they matter (registration, check-in, joining a team).
/// The refresh runs as a background job after the request commits, so Riot being slow, down or rate
/// limited never fails or delays the player's action.
/// </summary>
public interface IRiotIdRefreshTrigger
{
    /// <summary>Everyone on the user's entries in this tournament.</summary>
    void TournamentEntry(Guid tournamentId, Guid userId);

    /// <summary>Members of these competitors: team ids, or solo participant ids.</summary>
    void Competitors(params Guid[] competitorIds);

    void Users(params Guid[] userIds);
}

public sealed class RiotIdRefreshTrigger(IBackgroundJobClient jobs) : IRiotIdRefreshTrigger
{
    public void TournamentEntry(Guid tournamentId, Guid userId) =>
        jobs.Enqueue<RiotIdRefreshJob>(j => j.RefreshTournamentEntryAsync(tournamentId, userId, CancellationToken.None));

    public void Competitors(params Guid[] competitorIds)
    {
        if (competitorIds.Length > 0)
            jobs.Enqueue<RiotIdRefreshJob>(j => j.RefreshCompetitorsAsync(competitorIds, CancellationToken.None));
    }

    public void Users(params Guid[] userIds)
    {
        if (userIds.Length > 0)
            jobs.Enqueue<RiotIdRefreshJob>(j => j.RefreshUsersAsync(userIds, CancellationToken.None));
    }
}
