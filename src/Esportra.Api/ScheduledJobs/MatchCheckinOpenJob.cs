using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Fires at the start of a match's check-in window to notify both captains.
/// Idempotent: exits early if the match is no longer in pending status.
/// </summary>
public sealed class MatchCheckinOpenJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> notifHub,
    DiscordNotificationService discord,
    ILogger<MatchCheckinOpenJob> logger)
{
    public async Task ExecuteAsync(Guid matchId, CancellationToken ct)
    {
        try
        {
            using var conn = db.CreateConnection();

            var matchRow = await conn.QuerySingleOrDefaultAsync<(string Status, DateTime? ScheduledTime, DateTime? CheckInDeadline)>(
                """
                SELECT status AS Status, scheduled_time AS ScheduledTime,
                       check_in_deadline AS CheckInDeadline
                FROM brkt_matches WHERE id = @matchId
                """,
                new { matchId });

            // Only notify if the match is still pending and scheduled
            if (matchRow == default || matchRow.Status != "pending" || matchRow.ScheduledTime is null)
                return;

            var captainUserIds = (await conn.QueryAsync<Guid>(
                """
                SELECT DISTINCT user_id FROM (
                    SELECT tp.user_id
                    FROM tournament_participants tp
                    JOIN brkt_matches bm ON tp.id IN (bm.team1_id, bm.team2_id)
                    WHERE bm.id = @matchId AND tp.user_id IS NOT NULL

                    UNION

                    SELECT tm.user_id
                    FROM team_members tm
                    JOIN brkt_matches bm ON tm.team_id IN (bm.team1_id, bm.team2_id)
                    WHERE bm.id = @matchId AND tm.role = 'captain' AND tm.is_active = TRUE
                ) captains
                WHERE user_id IS NOT NULL
                """,
                new { matchId })).AsList();

            var scheduledAt = matchRow.ScheduledTime.Value.ToUniversalTime();
            var formattedTime = scheduledAt.ToString("MMM d 'at' h:mm tt UTC");
            const string title = "Check-in is now open";
            var message = $"Your match check-in window is open. Match starts at {formattedTime}. Check in now to avoid a walkover.";

            foreach (var userId in captainUserIds)
            {
                try
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO notifications (user_id, type, title, message, is_read)
                        VALUES (@userId, 'checkin_open'::notification_type, @title, @message, FALSE)
                        ON CONFLICT DO NOTHING
                        """,
                        new { userId, title, message });
                    await notifHub.Clients
                        .Group(NotificationHub.UserGroup(userId.ToString()))
                        .SendAsync(NotificationHubEvents.NewNotification,
                            new { type = "checkin_open", title, message }, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[CheckinOpen] Failed to notify user {UserId} for match {MatchId}", userId, matchId);
                }
                await discord.TrySendDmAsync(userId, "checkin_open", title, message);
            }

            logger.LogInformation("[CheckinOpen] Notified {Count} captain(s) for match {MatchId}", captainUserIds.Count, matchId);

            var deadline = (matchRow.CheckInDeadline ?? matchRow.ScheduledTime)?.ToUniversalTime();
            if (deadline.HasValue)
            {
                var reminderFireAt = deadline.Value - TimeSpan.FromMinutes(15);
                if (reminderFireAt > DateTime.UtcNow)
                {
                    BackgroundJob.Schedule<CheckinReminderJob>(
                        j => j.ExecuteAsync(matchId, CancellationToken.None),
                        reminderFireAt);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[CheckinOpen] Failed to process match {MatchId}", matchId);
            throw;
        }
    }
}
