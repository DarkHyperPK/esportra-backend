using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Fires 10 minutes after a veto session is created for a scrim.
/// If the veto is still not completed (both teams abandoned it),
/// transitions the scrim session to 'cancelled' and notifies both teams.
/// Idempotency guard: UPDATE WHERE status = 'in_progress' AND veto_session_id = @vetoSessionId.
/// Enqueued by ScrimVetoService after veto creation; deleted by CancelSessionAsync.
/// </summary>
[Queue("notifications")]
public sealed class ScrimVetoAbandonJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> hub,
    ILogger<ScrimVetoAbandonJob> logger)
{
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid sessionId, Guid vetoSessionId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        // Skip cancel if the veto was completed — teams finished the pick/ban normally.
        var vetoStatus = await conn.ExecuteScalarAsync<string?>(
            "SELECT status FROM public.public_veto_sessions WHERE id = @vetoSessionId",
            new { vetoSessionId });

        if (vetoStatus == "completed")
        {
            logger.LogInformation(
                "[ScrimVetoAbandonJob] Veto {VetoId} for session {SessionId} completed; skipping.",
                vetoSessionId, sessionId);
            return;
        }

        // Idempotency: only cancel if session is still in_progress with this veto attached.
        var cancelled = await conn.QuerySingleOrDefaultAsync<ScrimSessionJobRef>(
            """
            UPDATE public.scrim_sessions
            SET status = 'cancelled'::scrim_session_status, updated_at = now()
            WHERE id = @sessionId
              AND status = 'in_progress'
              AND veto_session_id = @vetoSessionId
            RETURNING id, team1_id, team2_id, status, scheduled_at, game_slug
            """,
            new { sessionId, vetoSessionId });

        if (cancelled is null)
        {
            logger.LogInformation(
                "[ScrimVetoAbandonJob] Session {SessionId}: not in_progress with this veto or already processed; skipping.",
                sessionId);
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

        const string message =
            "Your scrim session was cancelled because the map veto was not completed within 10 minutes.";

        foreach (var staffId in staffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, is_read)
                VALUES (@userId, 'scrim_timeout_cancelled'::notification_type,
                        'Scrim Cancelled — Veto Abandoned', @message, FALSE)
                """,
                new { userId = staffId, message });
        }

        await TeamNotifications.PushAsync(hub, staffIds, "scrim_timeout_cancelled",
            "Scrim Cancelled — Veto Abandoned", message);

        logger.LogInformation(
            "[ScrimVetoAbandonJob] Cancelled session {SessionId} due to veto abandon.",
            sessionId);
    }
}
