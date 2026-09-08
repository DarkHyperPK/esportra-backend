using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Esportra.Core.Notifications;
using Hangfire;
using StackExchange.Redis;

namespace Esportra.Api.ScheduledJobs;

[Queue("notifications")]
public sealed class ChatUnreadDmJob(
    IDbConnectionFactory db,
    DiscordNotificationService discord,
    IConnectionMultiplexer redis,
    IConfiguration config,
    ILogger<ChatUnreadDmJob> logger)
{
    public async Task ExecuteAsync(Guid matchId, Guid recipientUserId, CancellationToken ct)
    {
        try
        {
            var redisDb = redis.GetDatabase();
            var frontendUrl = (config["FrontendUrl"] ?? "https://esportra.com").TrimEnd('/');

            if (await redisDb.KeyExistsAsync($"chat-panel-open:{matchId}:{recipientUserId}"))
            {
                logger.LogDebug("Chat DM suppressed — panel open for match {MatchId}, recipient {RecipientId}", matchId, recipientUserId);
                return;
            }

            using var conn = db.CreateConnection();

            var unreadCount = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)::int
                FROM match_messages mm
                LEFT JOIN match_chat_reads mcr
                    ON mcr.match_id = mm.match_id AND mcr.user_id = @RecipientUserId
                WHERE mm.match_id = @MatchId
                  AND mm.sender_id != @RecipientUserId
                  AND mm.message_type = 'user'
                  AND mm.created_at > COALESCE(mcr.last_read_at, '1970-01-01'::timestamptz)
                """,
                new { MatchId = matchId, RecipientUserId = recipientUserId });

            if (unreadCount == 0)
            {
                logger.LogDebug("Chat DM suppressed — no unread messages for match {MatchId}, recipient {RecipientId}", matchId, recipientUserId);
                return;
            }

            var senderNames = (await conn.QueryAsync<string>(
                """
                SELECT DISTINCT mm.sender_name
                FROM match_messages mm
                LEFT JOIN match_chat_reads mcr
                    ON mcr.match_id = mm.match_id AND mcr.user_id = @RecipientUserId
                WHERE mm.match_id = @MatchId
                  AND mm.sender_id != @RecipientUserId
                  AND mm.message_type = 'user'
                  AND mm.created_at > COALESCE(mcr.last_read_at, '1970-01-01'::timestamptz)
                ORDER BY mm.sender_name
                """,
                new { MatchId = matchId, RecipientUserId = recipientUserId })).ToArray();

            var matchPath = await CaptainMatchLinkBuilder.BuildAsync(conn, matchId);
            if (matchPath == "/tournaments")
            {
                logger.LogWarning("ChatUnreadDmJob skipped — no tournament slug for match {MatchId}", matchId);
                return;
            }

            var matchRoomUrl = $"{frontendUrl}{matchPath}";

            var senders = FormatSenders(senderNames);
            var description = $"You have **{unreadCount}** unread message{(unreadCount == 1 ? "" : "s")} from {senders} in your match. [View match room]({matchRoomUrl})";

            var sent = await discord.TrySendDmAsync(recipientUserId, "match_chat_message", "Unread Match Chat Messages", description);

            if (sent)
                await redisDb.StringSetAsync($"chat-dm-cooldown:{matchId}:{recipientUserId}", 1, TimeSpan.FromMinutes(20));
            await redisDb.KeyDeleteAsync($"chat-dm-pending:{matchId}:{recipientUserId}");

            logger.LogDebug("Chat DM sent for match {MatchId}, recipient {RecipientId} — {Count} unread from {SenderCount} senders", matchId, recipientUserId, unreadCount, senderNames.Length);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ChatUnreadDmJob failed for match {MatchId}, recipient {RecipientId}", matchId, recipientUserId);
        }
    }

    private static string FormatSenders(string[] names) => names.Length switch
    {
        0 => "an unknown sender",
        1 => $"**{names[0]}**",
        2 => $"**{names[0]}** and **{names[1]}**",
        3 => $"**{names[0]}**, **{names[1]}**, and **{names[2]}**",
        _ => $"**{names[0]}**, **{names[1]}**, **{names[2]}**, and {names.Length - 3} others"
    };
}
