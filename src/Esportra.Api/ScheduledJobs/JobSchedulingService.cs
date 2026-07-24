using Dapper;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

public sealed class JobSchedulingService(IDbConnectionFactory db)
{
    public async Task ScheduleTournamentCheckinDeadlineAsync(
        Guid tournamentId, DateTime deadline, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var existingJobId = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT checkin_job_id FROM tournaments WHERE id = @tournamentId",
            new { tournamentId });

        if (!string.IsNullOrEmpty(existingJobId))
            BackgroundJob.Delete(existingJobId);

        var jobId = BackgroundJob.Schedule<TournamentCheckinDeadlineJob>(
            j => j.ExecuteAsync(tournamentId, CancellationToken.None),
            deadline.ToUniversalTime());

        await conn.ExecuteAsync(
            "UPDATE tournaments SET checkin_job_id = @jobId WHERE id = @tournamentId",
            new { jobId, tournamentId });
    }

    public async Task CancelTournamentCheckinDeadlineAsync(
        Guid tournamentId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var existingJobId = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT checkin_job_id FROM tournaments WHERE id = @tournamentId",
            new { tournamentId });

        if (!string.IsNullOrEmpty(existingJobId))
        {
            BackgroundJob.Delete(existingJobId);
            await conn.ExecuteAsync(
                "UPDATE tournaments SET checkin_job_id = NULL WHERE id = @tournamentId",
                new { tournamentId });
        }
    }

    public async Task ScheduleMatchWalkoverAsync(
        Guid matchId, DateTime scheduledTime, int checkinWindowMinutes = 15, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var existingJobId = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT walkover_job_id FROM brkt_matches WHERE id = @matchId",
            new { matchId });

        if (!string.IsNullOrEmpty(existingJobId))
            BackgroundJob.Delete(existingJobId);

        // Walkover fires when check-in window closes (= at scheduled_time)
        var fireAt = scheduledTime.ToUniversalTime();

        var jobId = BackgroundJob.Schedule<MatchWalkoverJob>(
            j => j.ExecuteAsync(matchId, CancellationToken.None),
            fireAt);

        await conn.ExecuteAsync(
            "UPDATE brkt_matches SET walkover_job_id = @jobId WHERE id = @matchId",
            new { jobId, matchId });
    }

    public async Task CancelMatchWalkoverAsync(Guid matchId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var existingJobId = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT walkover_job_id FROM brkt_matches WHERE id = @matchId",
            new { matchId });

        if (!string.IsNullOrEmpty(existingJobId))
        {
            BackgroundJob.Delete(existingJobId);
            await conn.ExecuteAsync(
                "UPDATE brkt_matches SET walkover_job_id = NULL WHERE id = @matchId",
                new { matchId });
        }
    }

    public static void EnqueueDiscordDm(Guid notificationId)
    {
        BackgroundJob.Enqueue<DiscordDmJob>(
            j => j.ExecuteAsync(notificationId, CancellationToken.None));
    }

}
