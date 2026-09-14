using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

[Queue("notifications")]
public sealed class DiscordDmJob(
    IDbConnectionFactory db,
    DiscordNotificationService discord,
    ILogger<DiscordDmJob> logger)
{
    public static bool IsDmEligibleType(string type) =>
        DiscordNotificationTypes.DmEligibleTypes.Contains(type);

    public async Task ExecuteAsync(Guid notificationId, CancellationToken ct)
    {
        if (!discord.IsConfigured)
            return;

        using var conn = db.CreateConnection();

        var dm = await conn.QuerySingleOrDefaultAsync<PendingDm>(
            """
            SELECT n.id AS Id,
                   n.user_id AS UserId,
                   n.type AS Type,
                   n.title AS Title,
                   n.message AS Message,
                   n.data->>'game_slug' AS GameSlug
            FROM notifications n
            INNER JOIN profiles p ON p.id = n.user_id
            INNER JOIN auth.identities ai ON ai.user_id = p.id AND ai.provider = 'discord'
            WHERE n.id = @notificationId
              AND COALESCE((p.settings->>'discord_dm_enabled')::boolean, TRUE) = TRUE
              AND COALESCE((n.data->>'discord_dm_sent')::boolean, FALSE) = FALSE
              AND n.type::text = ANY(@eligibleTypes)
            """,
            new { notificationId, eligibleTypes = DiscordNotificationTypes.DmEligibleTypes.ToArray() });

        if (dm is null)
            return;

        // Flag before send — at-most-once delivery; if send fails, job retries from scratch
        await conn.ExecuteAsync(
            """
            UPDATE notifications
            SET data = COALESCE(data, '{}'::jsonb) || '{"discord_dm_sent": true}'::jsonb
            WHERE id = @notificationId
            """,
            new { notificationId });

        await discord.TrySendDmAsync(dm.UserId, dm.Type, dm.Title ?? "", dm.Message ?? "", dm.GameSlug);

        logger.LogDebug("[DiscordDm] Sent DM for notification {Id} (type={Type}).", notificationId, dm.Type);
    }

    private sealed record PendingDm(Guid Id, Guid UserId, string Type, string? Title, string? Message, string? GameSlug);
}
