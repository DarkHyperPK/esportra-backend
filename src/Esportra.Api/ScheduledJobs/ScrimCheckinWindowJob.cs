using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Email;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Fires at T-15 min before a scrim session.
/// Opens the check-in window by setting checkin_window_opened_at.
/// Single-row UPDATE RETURNING guards against double-processing during rolling deploys.
/// Enqueued by AcceptOfferAsync when scheduled_at is set; deleted on cancel.
/// </summary>
[Queue("notifications")]
public sealed class ScrimCheckinWindowJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> hub,
    CompetitiveEmailService emails,
    ILogger<ScrimCheckinWindowJob> logger)
{
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid sessionId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        // Idempotency guard: only set checkin_window_opened_at if still NULL + scheduled.
        var opened = await conn.QuerySingleOrDefaultAsync<ScrimSessionJobRef>(
            """
            UPDATE public.scrim_sessions
            SET checkin_window_opened_at = now(), updated_at = now()
            WHERE id = @sessionId
              AND status = 'scheduled'
              AND checkin_window_opened_at IS NULL
            RETURNING id, team1_id, team2_id, status, scheduled_at, game_slug
            """,
            new { sessionId });

        if (opened is null)
        {
            logger.LogInformation("[ScrimCheckinWindowJob] Session {SessionId}: window already open or not in 'scheduled' state; skipping.", sessionId);
            return;
        }

        var staffIds = (await conn.QueryAsync<Guid>(
            """
            SELECT user_id FROM public.team_members
            WHERE team_id IN (@t1, @t2)
              AND role IN ('captain', 'manager')
              AND is_active = TRUE
            """,
            new { t1 = opened.Team1Id, t2 = opened.Team2Id })).ToList();

        var scheduledStr = opened.ScheduledAt?.ToString("HH:mm UTC") ?? "now";
        var message = $"Check-in is now open for your scrim at {scheduledStr}. You have 30 minutes.";

        foreach (var staffId in staffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, is_read)
                VALUES (@userId, 'scrim_checkin_window_open'::notification_type,
                        'Check-in Window Open', @message, FALSE)
                """,
                new { userId = staffId, message });
        }

        await TeamNotifications.PushAsync(hub, staffIds, "scrim_checkin_window_open",
            "Check-in Window Open", message);

        // S8 — email both teams' staff that check-in is open (opponent differs per team)
        var teamNames = (await conn.QueryAsync<(Guid Id, string Name)>(
            "SELECT id, name FROM public.teams WHERE id IN (@t1, @t2)",
            new { t1 = opened.Team1Id, t2 = opened.Team2Id })).ToList();
        var t1Name = teamNames.FirstOrDefault(t => t.Id == opened.Team1Id).Name ?? "the other team";
        var t2Name = teamNames.FirstOrDefault(t => t.Id == opened.Team2Id).Name ?? "the other team";
        var sessionUrl = $"{emails.FrontendUrl}/competitive/scrims/sessions/{sessionId}";
        var scheduledAtStr = opened.ScheduledAt?.ToString("ddd d MMM yyyy, HH:mm 'UTC'") ?? "now";
        var t1StaffIds = await CompetitiveEmailService.GetTeamStaffIdsAsync(conn, opened.Team1Id);
        var t2StaffIds = await CompetitiveEmailService.GetTeamStaffIdsAsync(conn, opened.Team2Id);
        foreach (var staffId in t1StaffIds)
            await emails.SendScrimAsync(staffId, EmailType.ScrimCheckinOpen, new { opponentName = t2Name, scheduledAt = scheduledAtStr, sessionUrl }, ct);
        foreach (var staffId in t2StaffIds)
            await emails.SendScrimAsync(staffId, EmailType.ScrimCheckinOpen, new { opponentName = t1Name, scheduledAt = scheduledAtStr, sessionUrl }, ct);

        logger.LogInformation("[ScrimCheckinWindowJob] Opened check-in window for session {SessionId}.", sessionId);
    }
}
