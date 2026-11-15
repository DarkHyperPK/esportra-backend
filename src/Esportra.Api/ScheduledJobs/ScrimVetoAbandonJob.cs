using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Email;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>What the abandon check found.</summary>
internal enum VetoAbandonKind { Skipped, StillActive, Cancelled }

internal sealed record VetoAbandonOutcome(VetoAbandonKind Kind, TimeSpan Remaining, ScrimSessionJobRef? Session);

/// <summary>The decision, kept apart from scheduling and notifications so it can be tested against the database.</summary>
internal static class ScrimVetoAbandon
{
    /// <summary>
    /// Cancels the scrim only when its veto has had no action for <paramref name="inactivity"/>. The idle time is read
    /// from the veto row (every toss, ban, pick, side and host reset bumps it) using the database clock, and the cancel
    /// re-checks it in the same statement, so a veto that finishes or moves while this runs is never cancelled.
    /// </summary>
    public static async Task<VetoAbandonOutcome> EvaluateAsync(
        System.Data.IDbConnection conn, Guid sessionId, Guid vetoSessionId, TimeSpan inactivity)
    {
        var veto = await conn.QuerySingleOrDefaultAsync<(string Status, double IdleSeconds)?>(
            "SELECT status, EXTRACT(EPOCH FROM (now() - updated_at))::double precision FROM public.public_veto_sessions WHERE id = @vetoSessionId",
            new { vetoSessionId });
        if (veto is null || veto.Value.Status is "completed" or "cancelled")
            return new VetoAbandonOutcome(VetoAbandonKind.Skipped, TimeSpan.Zero, null);

        var idle = TimeSpan.FromSeconds(Math.Max(0, veto.Value.IdleSeconds));
        if (idle < inactivity)
            return new VetoAbandonOutcome(VetoAbandonKind.StillActive, inactivity - idle, null);

        var cancelled = await conn.QuerySingleOrDefaultAsync<ScrimSessionJobRef>(
            """
            UPDATE public.scrim_sessions s
            SET status = 'cancelled'::scrim_session_status, cancelled_reason = @reason, updated_at = now()
            WHERE s.id = @sessionId
              AND s.status = 'in_progress'
              AND s.veto_session_id = @vetoSessionId
              AND NOT EXISTS (
                  SELECT 1 FROM public.public_veto_sessions v
                  WHERE v.id = @vetoSessionId
                    AND (v.status IN ('completed', 'cancelled') OR v.updated_at > now() - make_interval(secs => @windowSeconds)))
            RETURNING s.id AS Id, s.team1_id AS Team1Id, s.team2_id AS Team2Id, s.status::text AS Status, s.scheduled_at AS ScheduledAt, s.game_slug AS GameSlug
            """,
            new { sessionId, vetoSessionId, reason = Esportra.Contracts.Competitive.ScrimCancelReasons.VetoAbandoned, windowSeconds = inactivity.TotalSeconds });
        return cancelled is null
            ? new VetoAbandonOutcome(VetoAbandonKind.Skipped, TimeSpan.Zero, null)
            : new VetoAbandonOutcome(VetoAbandonKind.Cancelled, TimeSpan.Zero, cancelled);
    }
}

/// <summary>
/// Checks a scrim's veto after the inactivity window. A veto that is still being acted on re-arms the check; one
/// nobody touched for the whole window cancels the scrim as abandoned (reason veto_abandoned) and tells both teams' staff.
/// Enqueued by ScrimVetoService after veto creation; deleted by CancelSessionAsync.
/// </summary>
[Queue("notifications")]
public sealed class ScrimVetoAbandonJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> hub,
    IBackgroundJobClient jobs,
    Microsoft.Extensions.Options.IOptions<Esportra.Api.Services.ScrimVetoOptions> options,
    CompetitiveEmailService emails,
    ILogger<ScrimVetoAbandonJob> logger)
{
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid sessionId, Guid vetoSessionId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var inactivity = TimeSpan.FromMinutes(Math.Max(1, options.Value.VetoInactivityMinutes));
        var outcome = await ScrimVetoAbandon.EvaluateAsync(conn, sessionId, vetoSessionId, inactivity);

        if (outcome.Kind == VetoAbandonKind.StillActive)
        {
            await RearmAsync(conn, sessionId, vetoSessionId, outcome.Remaining);
            return;
        }
        if (outcome.Kind == VetoAbandonKind.Skipped || outcome.Session is not { } cancelled)
        {
            logger.LogInformation(
                "[ScrimVetoAbandonJob] Session {SessionId}: veto finished, or session no longer in_progress with it; skipping.", sessionId);
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

        var message =
            $"Your scrim session was cancelled because nobody acted on the map veto for {(int)inactivity.TotalMinutes} minutes.";

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

        // S9 variant — email all staff that the session was cancelled due to veto abandon
        var sessionUrl = $"{emails.FrontendUrl}/competitive/scrims/sessions/{sessionId}";
        foreach (var staffId in staffIds)
            await emails.SendScrimAsync(staffId, EmailType.ScrimTimeoutCancelled, new { sessionUrl }, ct);

        logger.LogInformation(
            "[ScrimVetoAbandonJob] Cancelled session {SessionId} due to veto abandon.",
            sessionId);
    }

    private async Task RearmAsync(System.Data.IDbConnection conn, Guid sessionId, Guid vetoSessionId, TimeSpan remaining)
    {
        var delay = remaining < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : remaining;
        var jobId = jobs.Schedule<ScrimVetoAbandonJob>(j => j.RunAsync(sessionId, vetoSessionId, CancellationToken.None), delay);
        await conn.ExecuteAsync(
            """
            UPDATE public.scrim_sessions
            SET hangfire_job_ids = COALESCE(hangfire_job_ids, '{}'::jsonb) || jsonb_build_object('vetoAbandon', @jobId::text)
            WHERE id = @sessionId
            """,
            new { sessionId, jobId });
        logger.LogInformation("[ScrimVetoAbandonJob] Veto {VetoId} is still being used; re-checking in {Delay}.", vetoSessionId, delay);
    }
}
