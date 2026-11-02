using System.Data;
using Dapper;
using Hangfire;

namespace Esportra.Api.Services;

/// <summary>
/// Clears a scrim's agreed time: no time, no check-in window, no scheduled jobs, and any pending
/// proposal is dropped. The captains then agree a time again.
/// </summary>
public static class ScrimTimeReset
{
    public static async Task ApplyAsync(IDbConnection conn, IBackgroundJobClient jobs, Guid sessionId, bool clearCheckins)
    {
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(
            "UPDATE public.scrim_time_proposals SET status = 'superseded', responded_at = now() WHERE session_id = @sessionId AND status = 'pending'",
            new { sessionId }, tx);
        await conn.ExecuteAsync(
            "UPDATE public.scrim_time_clear_requests SET status = 'superseded', responded_at = now() WHERE session_id = @sessionId AND status = 'pending'",
            new { sessionId }, tx);
        await conn.ExecuteAsync(
            """
            UPDATE public.scrim_sessions
            SET scheduled_at = NULL,
                checkin_window_opened_at = NULL,
                status = 'scheduled'::scrim_session_status,
                team1_checked_in_at = CASE WHEN @clear THEN NULL ELSE team1_checked_in_at END,
                team2_checked_in_at = CASE WHEN @clear THEN NULL ELSE team2_checked_in_at END,
                updated_at = now()
            WHERE id = @sessionId
            """,
            new { sessionId, clear = clearCheckins }, tx);
        tx.Commit();
        await ScrimJobScheduler.CancelAsync(conn, jobs, sessionId);
    }
}
