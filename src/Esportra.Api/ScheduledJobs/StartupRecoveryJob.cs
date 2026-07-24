using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;

namespace Esportra.Api.ScheduledJobs;

public sealed class StartupRecoveryJob(
    IDbConnectionFactory db,
    JobSchedulingService scheduler,
    ILogger<StartupRecoveryJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        // 1. Process missed tournament check-in deadlines
        var missedTournaments = await conn.QueryAsync<Guid>(
            """
            SELECT id FROM tournaments
            WHERE check_in_required = true
              AND auto_remove_unchecked = true
              AND check_in_deadline IS NOT NULL
              AND check_in_deadline < NOW()
              AND checkin_job_id IS NULL
              AND status IN ('open', 'published', 'closed')
              AND EXISTS (
                  SELECT 1 FROM tournament_participants tp
                  WHERE tp.tournament_id = tournaments.id
                    AND tp.checked_in_at IS NULL
                    AND tp.status IN ('pending', 'approved')
              )
            """);

        var missedList = missedTournaments.AsList();
        if (missedList.Count > 0)
        {
            logger.LogInformation("[StartupRecovery] Found {Count} missed tournament deadline(s). Processing now.", missedList.Count);
            foreach (var tid in missedList)
            {
                Hangfire.BackgroundJob.Enqueue<TournamentCheckinDeadlineJob>(
                    j => j.ExecuteAsync(tid, CancellationToken.None));
            }
        }

        // 2. Process missed match walkovers
        var missedMatches = await conn.QueryAsync<Guid>(
            """
            SELECT m.id
            FROM brkt_matches m
            WHERE LOWER(COALESCE(m.status, 'pending')) = 'pending'
              AND m.team1_id IS NOT NULL
              AND m.team2_id IS NOT NULL
              AND m.walkover_job_id IS NULL
              AND (
                  m.scheduled_time IS NOT NULL AND m.scheduled_time < NOW()
                  OR EXISTS (
                      SELECT 1 FROM match_time_proposals p
                      WHERE p.match_id = m.id AND p.status = 'accepted'
                        AND p.proposed_time < NOW()
                  )
              )
            """);

        var missedMatchList = missedMatches.AsList();
        if (missedMatchList.Count > 0)
        {
            logger.LogInformation("[StartupRecovery] Found {Count} missed match walkover(s). Processing now.", missedMatchList.Count);
            foreach (var mid in missedMatchList)
            {
                Hangfire.BackgroundJob.Enqueue<MatchWalkoverJob>(
                    j => j.ExecuteAsync(mid, CancellationToken.None));
            }
        }

        // 3. Schedule future tournaments that have no job yet
        var futureTournaments = await conn.QueryAsync<(Guid Id, DateTime Deadline)>(
            """
            SELECT id, check_in_deadline
            FROM tournaments
            WHERE check_in_required = true
              AND auto_remove_unchecked = true
              AND check_in_deadline IS NOT NULL
              AND check_in_deadline > NOW()
              AND checkin_job_id IS NULL
              AND status IN ('open', 'published', 'closed')
            """);

        foreach (var (id, deadline) in futureTournaments)
            await scheduler.ScheduleTournamentCheckinDeadlineAsync(id, deadline, ct);

        // 4. Schedule future match walkovers that have no job yet
        var futureMatches = await conn.QueryAsync<(Guid Id, DateTime ScheduledTime)>(
            """
            SELECT m.id, COALESCE(m.scheduled_time, p.proposed_time) AS scheduled_time
            FROM brkt_matches m
            LEFT JOIN LATERAL (
                SELECT proposed_time FROM match_time_proposals
                WHERE match_id = m.id AND status = 'accepted'
                ORDER BY responded_at DESC NULLS LAST, created_at DESC
                LIMIT 1
            ) p ON true
            WHERE LOWER(COALESCE(m.status, 'pending')) = 'pending'
              AND m.team1_id IS NOT NULL
              AND m.team2_id IS NOT NULL
              AND m.walkover_job_id IS NULL
              AND COALESCE(m.scheduled_time, p.proposed_time) > NOW()
            """);

        foreach (var (id, scheduledTime) in futureMatches)
            await scheduler.ScheduleMatchWalkoverAsync(id, scheduledTime, ct: ct);

        logger.LogInformation("[StartupRecovery] Recovery complete.");
    }
}
