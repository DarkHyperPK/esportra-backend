using Dapper;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Email;

namespace Esportra.Api.BackgroundJobs;

/// <summary>
/// Replaces: automated-reminders Edge Function (cron).
/// Runs every 5 minutes. Sends check-in reminder emails to tournament participants
/// whose check-in window opens in the next 25–35 minutes.
/// </summary>
public sealed class AutomatedRemindersJob(
    IDbConnectionFactory db,
    IEmailService        email,
    ILogger<AutomatedRemindersJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[Reminders] Background job started — first run in 30s");

        // Wait for the container's DNS resolver and Postgres to be fully reachable
        // before the first run. Avoids a spurious DNS error in startup logs.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // Graceful shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Reminders] Job iteration failed — will retry in 5 minutes");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // Graceful shutdown
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        // Find tournaments with check-in window opening in 25–35 minutes
        var tournaments = (await conn.QueryAsync("""
            SELECT id, name, slug
            FROM public.tournaments
            WHERE check_in_required = true
              AND check_in_reminder_sent = false
              AND status = 'published'
              AND start_date BETWEEN NOW() + INTERVAL '25 minutes'
                                AND NOW() + INTERVAL '35 minutes'
            """)).AsList();

        if (tournaments.Count == 0) return;

        logger.LogInformation("[Reminders] {Count} tournaments need check-in reminders", tournaments.Count);

        foreach (var tournament in tournaments)
        {
            string tournamentId   = tournament.id;
            string tournamentName = tournament.name;
            string slug           = tournament.slug;
            var    checkInUrl     = $"https://esportra.com/tournaments/{slug}";

            // Get participants who haven't checked in
            var participants = (await conn.QueryAsync("""
                SELECT
                    p.user_id,
                    p.team_id,
                    pr.email,
                    pr.username,
                    p.participant_type
                FROM public.tournament_participants p
                JOIN public.profiles pr ON pr.id = p.user_id
                WHERE p.tournament_id = @tournamentId
                  AND p.status = 'registered'
                  AND p.checked_in = false
                  AND pr.email IS NOT NULL
                """, new { tournamentId })).AsList();

            foreach (var participant in participants)
            {
                try
                {
                    await email.SendAsync(
                        (string)participant.email,
                        EmailType.CheckinReminder,
                        new
                        {
                            username       = participant.username ?? "Competitor",
                            tournamentName = tournamentName,
                            checkInUrl     = checkInUrl,
                        },
                        ct);
                }
                catch (Exception ex)
                {
                    string participantEmail = participant.email;
                    logger.LogWarning(ex, "[Reminders] Failed to send reminder to {Email}", participantEmail);
                }
            }

            // Mark as sent
            await conn.ExecuteAsync(
                "UPDATE public.tournaments SET check_in_reminder_sent = true WHERE id = @id",
                new { id = tournamentId });

            logger.LogInformation("[Reminders] Sent {Count} reminders for {Tournament}",
                participants.Count, tournamentName);
        }
    }
}
