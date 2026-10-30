using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Fires at T+15 min (30 minutes after check-in window opens at T-15).
/// If one or both teams have not checked in and the session is still 'scheduled',
/// transitions the session to 'cancelled' (reason: checkin_timeout) and notifies both teams.
/// Single-row UPDATE RETURNING guards against double-processing.
/// Enqueued by AcceptOfferAsync when scheduled_at is set; deleted on cancel.
/// </summary>
[Queue("notifications")]
public sealed class ScrimCheckinTimeoutJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> hub,
    ILogger<ScrimCheckinTimeoutJob> logger)
{
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid sessionId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        // Idempotency guard: only cancel if still 'scheduled' AND at least one team has not checked in.
        var cancelled = await conn.QuerySingleOrDefaultAsync<ScrimSessionJobRef>(
            """
            UPDATE public.scrim_sessions
            SET status     = 'cancelled'::scrim_session_status,
                updated_at = now()
            WHERE id = @sessionId
              AND status = 'scheduled'
              AND (team1_checked_in_at IS NULL OR team2_checked_in_at IS NULL)
            RETURNING id, team1_id, team2_id, status, scheduled_at, game_slug
            """,
            new { sessionId });

        if (cancelled is null)
        {
            logger.LogInformation("[ScrimCheckinTimeoutJob] Session {SessionId}: already in_progress/cancelled or both checked in; skipping.", sessionId);
            return;
        }

        var staffIds = (await conn.QueryAsync<Guid>(
            """
            SELECT user_id FROM public.team_members
            WHERE team_id IN (@t1, @t2)
              AND role IN ('captain', 'manager')
              AND is_active = TRUE
            """,
            new { t1 = cancelled.Team1Id, t2 = cancelled.Team2Id })).ToList();

        const string message = "Your scrim session was cancelled because not both teams checked in within the 30-minute window.";

        foreach (var staffId in staffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, is_read)
                VALUES (@userId, 'scrim_timeout_cancelled'::notification_type,
                        'Scrim Cancelled — Check-in Timeout', @message, FALSE)
                """,
                new { userId = staffId, message });
        }

        await TeamNotifications.PushAsync(hub, staffIds, "scrim_timeout_cancelled",
            "Scrim Cancelled — Check-in Timeout", message);

        logger.LogInformation("[ScrimCheckinTimeoutJob] Cancelled session {SessionId} due to check-in timeout.", sessionId);
    }
}

/// <summary>Shared projection for scrim session job handlers.</summary>
internal sealed record ScrimSessionJobRef(
    Guid Id, Guid Team1Id, Guid Team2Id, string Status,
    DateTime? ScheduledAt, string? GameSlug);
