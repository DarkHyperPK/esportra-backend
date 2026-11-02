using System.Data;
using System.Text.Json;
using Dapper;
using Hangfire;
using Esportra.Api.ScheduledJobs;

namespace Esportra.Api.Services;

/// <summary>
/// Schedules (or reschedules) the reminder, check-in window and no-show timeout jobs for a scrim.
/// Called when a time is first agreed and again if the captains agree a different one.
/// </summary>
public static class ScrimJobScheduler
{
    public static async Task ScheduleAsync(
        IDbConnection conn, IBackgroundJobClient jobs, Guid sessionId, DateTime scheduledAtUtc)
    {
        await DeleteExistingAsync(conn, jobs, sessionId);

        var times = ScrimScheduling.JobTimes(scheduledAtUtc);
        var reminder = jobs.Schedule<ScrimReminderJob>(j => j.RunAsync(sessionId, CancellationToken.None), Utc(times.Reminder));
        var window = jobs.Schedule<ScrimCheckinWindowJob>(j => j.RunAsync(sessionId, CancellationToken.None), Utc(times.Window));
        var timeout = jobs.Schedule<ScrimCheckinTimeoutJob>(j => j.RunAsync(sessionId, CancellationToken.None), Utc(times.Timeout));

        var jobIds = JsonSerializer.Serialize(new { reminder, window, timeout });
        await conn.ExecuteAsync(
            "UPDATE public.scrim_sessions SET hangfire_job_ids = @jobIds::jsonb WHERE id = @sessionId",
            new { jobIds, sessionId });
    }

    /// <summary>Drops the scheduled jobs of a scrim whose time was cleared.</summary>
    public static async Task CancelAsync(IDbConnection conn, IBackgroundJobClient jobs, Guid sessionId)
    {
        await DeleteExistingAsync(conn, jobs, sessionId);
        await conn.ExecuteAsync(
            "UPDATE public.scrim_sessions SET hangfire_job_ids = NULL WHERE id = @sessionId", new { sessionId });
    }

    private static async Task DeleteExistingAsync(IDbConnection conn, IBackgroundJobClient jobs, Guid sessionId)
    {
        var existing = await conn.ExecuteScalarAsync<string?>(
            "SELECT hangfire_job_ids::text FROM public.scrim_sessions WHERE id = @sessionId", new { sessionId });
        if (string.IsNullOrEmpty(existing)) return;
        try
        {
            using var doc = JsonDocument.Parse(existing);
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String) jobs.Delete(prop.Value.GetString()!);
        }
        catch (JsonException)
        {
            // Unreadable ids: the jobs are idempotent and no-op for a rescheduled session.
        }
    }

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
}
