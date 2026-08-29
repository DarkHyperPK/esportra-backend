using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Recurring fallback that picks up any DM-eligible notifications that weren't
/// dispatched by inline EnqueueDiscordDm calls. Runs every 30 seconds.
/// This ensures no DMs are missed even when notification inserts haven't been
/// migrated to use the inline enqueue pattern yet.
/// </summary>
[Queue("notifications")]
[DisableConcurrentExecution(timeoutInSeconds: 30)]
public sealed class DiscordDmPollJob(
    IDbConnectionFactory db,
    DiscordNotificationService discord,
    ILogger<DiscordDmPollJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!discord.IsConfigured)
            return;

        using var conn = db.CreateConnection();

        var pending = (await conn.QueryAsync<Guid>(
            """
            SELECT n.id
            FROM notifications n
            INNER JOIN profiles p ON p.id = n.user_id
            INNER JOIN auth.identities ai ON ai.user_id = p.id AND ai.provider = 'discord'
            WHERE n.created_at > NOW() - INTERVAL '2 minutes'
              AND COALESCE((p.settings->>'discord_dm_enabled')::boolean, TRUE) = TRUE
              AND COALESCE((n.data->>'discord_dm_sent')::boolean, FALSE) = FALSE
              AND n.type IN (
                  'match_ready', 'result_reported', 'result_disputed',
                  'dispute_resolved', 'tournament_registered',
                  'tournament_announcement', 'result_accepted', 'match_completed'
              )
            ORDER BY n.created_at ASC
            LIMIT 20
            """)).AsList();

        if (pending.Count == 0) return;

        logger.LogDebug("[DiscordDmPoll] Found {Count} pending DMs, enqueuing.", pending.Count);

        foreach (var notificationId in pending)
        {
            BackgroundJob.Enqueue<DiscordDmJob>(
                j => j.ExecuteAsync(notificationId, CancellationToken.None));
        }
    }
}
