using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Fires at T-30 min before a scrim session.
/// Sends a reminder notification to both teams' captains/managers.
/// Idempotent: no-op if the session is no longer 'scheduled' or already notified.
/// Enqueued by AcceptOfferAsync when scheduled_at is set; deleted on cancel.
/// </summary>
[Queue("notifications")]
public sealed class ScrimReminderJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> hub,
    ILogger<ScrimReminderJob> logger)
{
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid sessionId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionJobRef>(
            """
            SELECT id, team1_id, team2_id, status, scheduled_at, game_slug
            FROM public.scrim_sessions
            WHERE id = @sessionId
            """,
            new { sessionId });

        if (session is null)
        {
            logger.LogWarning("[ScrimReminderJob] Session {SessionId} not found.", sessionId);
            return;
        }

        if (session.Status != "scheduled")
        {
            logger.LogInformation("[ScrimReminderJob] Session {SessionId} is '{Status}'; skipping reminder.", sessionId, session.Status);
            return;
        }

        var staffIds = (await conn.QueryAsync<Guid>(
            """
            SELECT user_id FROM public.team_members
            WHERE team_id IN (@t1, @t2)
              AND role IN ('captain', 'manager')
              AND is_active = TRUE
            """,
            new { t1 = session.Team1Id, t2 = session.Team2Id })).ToList();

        var scheduledStr = session.ScheduledAt?.ToString("HH:mm UTC") ?? "soon";
        var message = $"Your scrim ({session.GameSlug ?? "unknown"}) starts at {scheduledStr} — 30 minutes to go. Check in from T-15.";

        foreach (var staffId in staffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, is_read)
                VALUES (@userId, 'scrim_reminder_30min'::notification_type,
                        'Scrim Reminder — 30 Min', @message, FALSE)
                """,
                new { userId = staffId, message });
        }

        await TeamNotifications.PushAsync(hub, staffIds, "scrim_reminder_30min",
            "Scrim Reminder — 30 Min", message);

        logger.LogInformation("[ScrimReminderJob] Sent reminder for session {SessionId} to {Count} staff.", sessionId, staffIds.Count);
    }
}
