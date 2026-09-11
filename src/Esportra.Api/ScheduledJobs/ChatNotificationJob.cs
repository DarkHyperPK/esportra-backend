using Dapper;
using Esportra.Api.Services;
using Esportra.Core.Notifications;
using Esportra.Core.Tournaments;
using Esportra.Infrastructure.Email;
using Hangfire;
using System.Data;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Sliding-window Hangfire job: fires 2 minutes after the last message,
/// checks the read waterline, sends email + Discord DM if still unread.
/// </summary>
[Queue("notifications")]
public sealed class ChatNotificationJob(
    IDbConnectionFactory db,
    IEmailService emailService,
    DiscordNotificationService discordNotification,
    IConfiguration config,
    ILogger<ChatNotificationJob> logger)
{
    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "completed", "cancelled", "bye" };

    /// <summary>Returns true for match statuses that make chat notifications irrelevant.</summary>
    public static bool IsTerminalStatus(string? status) =>
        status is null || TerminalStatuses.Contains(status);

    public async Task ExecuteAsync(Guid matchId, Guid recipientUserId)
    {
        try
        {
            using var conn = db.CreateConnection();

            var unreadCount = await GetUnreadCountAsync(conn, matchId, recipientUserId);
            if (unreadCount == 0) return;

            var matchStatus = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT status FROM brkt_matches WHERE id = @matchId",
                new { matchId });
            if (IsTerminalStatus(matchStatus)) return;

            var recipientEmail = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT email FROM profiles WHERE id = @recipientUserId AND email IS NOT NULL",
                new { recipientUserId });
            if (recipientEmail is null)
            {
                logger.LogWarning("[ChatNotification] No email for recipient {RecipientId} in match {MatchId}",
                    recipientUserId, matchId);
                return;
            }

            var senderTeamName = await GetSenderTeamNameAsync(conn, matchId, recipientUserId);
            var preview = await GetMessagePreviewAsync(conn, matchId, recipientUserId);
            var matchRoomUrl = await BuildMatchRoomUrlAsync(conn, matchId);

            await SendEmailAsync(recipientEmail, senderTeamName, preview, matchRoomUrl, unreadCount);
            await SendDiscordDmAsync(recipientUserId, senderTeamName, preview, matchRoomUrl, unreadCount);

            logger.LogDebug("[ChatNotification] Sent for match {MatchId}, recipient {RecipientId}, {Count} unread",
                matchId, recipientUserId, unreadCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ChatNotification] Failed for match {MatchId}, recipient {RecipientId}",
                matchId, recipientUserId);
        }
    }

    private static async Task<int> GetUnreadCountAsync(IDbConnection conn, Guid matchId, Guid recipientUserId)
        => await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int
            FROM match_messages mm
            LEFT JOIN match_chat_reads mcr
                ON mcr.match_id = mm.match_id AND mcr.user_id = @recipientUserId
            WHERE mm.match_id = @matchId
              AND mm.sender_id != @recipientUserId
              AND mm.message_type = 'user'
              AND mm.created_at > COALESCE(mcr.last_read_at, '1970-01-01'::timestamptz)
            """,
            new { matchId, recipientUserId });

    private static async Task<string> GetSenderTeamNameAsync(
        IDbConnection conn, Guid matchId, Guid recipientUserId)
    {
        var recipientCompId = await BracketCompetitorResolver.GetUserCompetitorIdInMatchAsync(
            conn, recipientUserId, matchId);
        if (recipientCompId is null) return "Opponent";

        var senderCompId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT CASE WHEN team1_id = @c THEN team2_id ELSE team1_id END FROM brkt_matches WHERE id = @matchId",
            new { c = recipientCompId.Value, matchId });
        if (senderCompId is null) return "Opponent";

        return await BracketCompetitorResolver.GetCompetitorDisplayNameAsync(conn, senderCompId.Value)
            ?? "Opponent";
    }

    private static async Task<string> GetMessagePreviewAsync(
        IDbConnection conn, Guid matchId, Guid recipientUserId)
    {
        var content = await conn.QuerySingleOrDefaultAsync<string?>(
            """
            SELECT mm.content
            FROM match_messages mm
            LEFT JOIN match_chat_reads mcr
                ON mcr.match_id = mm.match_id AND mcr.user_id = @recipientUserId
            WHERE mm.match_id = @matchId
              AND mm.sender_id != @recipientUserId
              AND mm.message_type = 'user'
              AND mm.created_at > COALESCE(mcr.last_read_at, '1970-01-01'::timestamptz)
            ORDER BY mm.created_at DESC
            LIMIT 1
            """,
            new { matchId, recipientUserId });

        if (content is null) return "";
        return content.Length > 100 ? content[..100] + "…" : content;
    }

    private async Task<string> BuildMatchRoomUrlAsync(IDbConnection conn, Guid matchId)
    {
        var frontendUrl = (config["FrontendUrl"] ?? "https://esportra.com").TrimEnd('/');
        var matchPath = await CaptainMatchLinkBuilder.BuildAsync(conn, matchId);
        return $"{frontendUrl}{matchPath}";
    }

    private async Task SendEmailAsync(
        string recipientEmail, string senderTeamName, string preview,
        string matchRoomUrl, int unreadCount)
    {
        try
        {
            await emailService.SendAsync(recipientEmail, EmailType.MatchChatMessage, new
            {
                senderTeamName,
                messagePreview = preview,
                matchRoomUrl,
                unreadCount,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ChatNotification] Email send failed");
        }
    }

    private async Task SendDiscordDmAsync(
        Guid recipientUserId, string senderTeamName,
        string preview, string matchRoomUrl, int unreadCount)
    {
        var plural = unreadCount == 1 ? "" : "s";
        var description =
            $"You have **{unreadCount}** unread message{plural} from **{senderTeamName}** in your match. " +
            $"[View match room]({matchRoomUrl})";

        await discordNotification.TrySendDmAsync(
            recipientUserId, "match_chat_message", "Unread Match Chat Messages", description);
    }
}
