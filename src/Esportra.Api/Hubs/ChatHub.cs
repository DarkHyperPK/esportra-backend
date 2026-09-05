using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Services;
using Esportra.Core.Notifications;
using Esportra.Core.Tournaments;
using Esportra.Infrastructure.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time match chat — replaces useMatchChat Supabase subscription.
/// Groups: chat:{matchId}
/// </summary>
[Authorize]
public sealed class ChatHub : Hub
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<ChatHub> _logger;
    private readonly IEmailService _email;
    private readonly IDatabase _redis;
    private readonly string _frontendUrl;
    private readonly DiscordNotificationService _discord;

    public ChatHub(
        IDbConnectionFactory db,
        ILogger<ChatHub> logger,
        IEmailService email,
        IConnectionMultiplexer redis,
        IConfiguration config,
        DiscordNotificationService discord)
    {
        _db = db;
        _logger = logger;
        _email = email;
        _redis = redis.GetDatabase();
        _frontendUrl = (config["FrontendUrl"] ?? "https://esportra.com").TrimEnd('/');
        _discord = discord;
    }

    // ── Client-callable methods ───────────────────────────────────────────────

    public async Task JoinChat(string matchId)
    {
        var userId = Context.UserIdentifier;
        if (userId is null)
        {
            await Clients.Caller.SendAsync(ChatHubEvents.Error, "Not authenticated.");
            return;
        }

        // Verify user is a participant or organizer of this match
        if (!await IsMatchParticipantAsync(userId, matchId))
        {
            await Clients.Caller.SendAsync(ChatHubEvents.Error, "You are not a participant in this match.");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroup(matchId));
        await SetPresenceAsync(matchId, userId);
        _logger.LogDebug("Client {Conn} joined chat:{MatchId}", Context.ConnectionId, matchId);
    }

    /// <summary>Refreshes the presence key — called by the client every ~90 s while the chat is open.</summary>
    public async Task Heartbeat(string matchId)
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;
        await SetPresenceAsync(matchId, userId);
    }

    public async Task LeaveChat(string matchId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatGroup(matchId));

    /// <summary>
    /// Send a chat message: persists to match_messages then broadcasts to the group.
    /// </summary>
    public async Task SendMessage(string matchId, string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 1000)
        {
            await Clients.Caller.SendAsync(ChatHubEvents.Error, "Message must be 1–1000 characters.");
            return;
        }

        var userId = Context.UserIdentifier;
        if (userId is null)
        {
            await Clients.Caller.SendAsync(ChatHubEvents.Error, "Not authenticated.");
            return;
        }

        // Verify user is a participant in this match
        if (!await IsMatchParticipantAsync(userId, matchId))
        {
            await Clients.Caller.SendAsync(ChatHubEvents.Error, "You are not a participant in this match.");
            return;
        }

        try
        {
            using var conn = _db.CreateConnection();

            var competitorId = await BracketCompetitorResolver.GetUserCompetitorIdInMatchAsync(
                conn, Guid.Parse(userId), Guid.Parse(matchId));

            var userInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT username FROM profiles p WHERE p.id = @UserId",
                new { UserId = Guid.Parse(userId) });

            var username = (string?)(userInfo?.username) ?? "Unknown";
            var userCtx = HubAuthHelper.GetUserContext(Context);
            var isOrganizer = userCtx is not null && await StaffAuthHelper.IsMatchOrganizerOrStaffAsync(
                conn, userCtx.UserIdGuid, Guid.Parse(matchId), userCtx);

            const string sql = """
                INSERT INTO match_messages (match_id, sender_id, sender_name, team_id, content, message_type, created_at)
                VALUES (@MatchId, @SenderId, @SenderName, @TeamId, @Content, 'user', NOW())
                RETURNING id::text, match_id::text, sender_id::text, sender_name, team_id::text, content, message_type, created_at, @IsOrganizer AS is_organizer;
                """;

            var message = await conn.QuerySingleAsync<MessageDto>(sql, new
            {
                MatchId = Guid.Parse(matchId),
                SenderId = Guid.Parse(userId),
                SenderName = username,
                TeamId = competitorId,
                Content = content.Trim(),
                IsOrganizer = isOrganizer,
            });

            await Clients.Group(ChatGroup(matchId))
                .SendAsync(ChatHubEvents.MessageReceived, message);

            // Fire-and-forget: notify offline opposing team members via email
            if (competitorId.HasValue)
            {
                _ = DispatchChatEmailsAsync(
                    matchId, userId, competitorId.Value, message.Content, message.SenderName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendMessage failed for match {MatchId}, user {UserId}", matchId, userId);
            await Clients.Caller.SendAsync(ChatHubEvents.Error, "Failed to send message. Please try again.");
        }
    }

    /// <summary>Broadcast typing indicator to other members of the chat group.</summary>
    public async Task SendTyping(string matchId, bool isTyping)
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;

        // Cache username to avoid DB hit on every keystroke
        if (Context.Items.TryGetValue("username", out var cached))
        {
            await Clients.OthersInGroup(ChatGroup(matchId))
                .SendAsync(
                    isTyping ? ChatHubEvents.TypingStart : ChatHubEvents.TypingStop,
                    userId,
                    cached as string ?? "Unknown");
            return;
        }

        using var conn = _db.CreateConnection();
        var username = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT username FROM profiles WHERE id = @Id", new { Id = Guid.Parse(userId) });
        Context.Items["username"] = username ?? "Unknown";

        await Clients.OthersInGroup(ChatGroup(matchId))
            .SendAsync(
                isTyping ? ChatHubEvents.TypingStart : ChatHubEvents.TypingStop,
                userId,
                username ?? "Unknown");
    }

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string ChatGroup(string matchId) => $"chat:{matchId}";

    // ── Presence ──────────────────────────────────────────────────────────────

    private Task SetPresenceAsync(string matchId, string userId) =>
        _redis.StringSetAsync(
            $"chat-active:{matchId}:{userId}",
            1,
            TimeSpan.FromSeconds(180));

    // ── Email dispatch ────────────────────────────────────────────────────────

    private async Task DispatchChatEmailsAsync(
        string matchId, string senderUserId, Guid senderCompetitorId,
        string messageContent, string senderName)
    {
        try
        {
            using var conn = _db.CreateConnection();

            var recipients = await conn.QueryAsync<(string UserId, string Email, string SenderTeamName)>(
                """
                SELECT
                    opp_p.id::text        AS UserId,
                    opp_p.email           AS Email,
                    sender_tp.team_name   AS SenderTeamName
                FROM brkt_matches bm
                JOIN tournament_participants sender_tp
                  ON sender_tp.id = @SenderCompetitorId
                JOIN tournament_participants opp_tp
                  ON opp_tp.id IN (bm.team1_id, bm.team2_id)
                 AND opp_tp.id != @SenderCompetitorId
                JOIN profiles opp_p ON opp_p.id = opp_tp.user_id
                WHERE bm.id = @MatchId
                  AND opp_p.email IS NOT NULL
                  AND opp_p.id != @SenderUserId
                """,
                new
                {
                    SenderCompetitorId = senderCompetitorId,
                    MatchId = Guid.Parse(matchId),
                    SenderUserId = Guid.Parse(senderUserId),
                });

            var matchPath = await CaptainMatchLinkBuilder.BuildAsync(conn, Guid.Parse(matchId));
            var matchRoomUrl = $"{_frontendUrl}{matchPath}";
            var preview = messageContent.Length > 120
                ? messageContent[..120] + "…"
                : messageContent;

            foreach (var (recipientUserId, email, senderTeamName) in recipients)
            {
                var activeKey = $"chat-active:{matchId}:{recipientUserId}";
                var cooldownKey = $"chat-email-sent:{matchId}:{recipientUserId}";

                // Skip if the recipient is currently active in this chat room
                if (await _redis.KeyExistsAsync(activeKey)) continue;

                // Skip if already notified within the cooldown window (5 min)
                if (!await _redis.StringSetAsync(cooldownKey, 1, TimeSpan.FromSeconds(300), When.NotExists))
                    continue;

                try
                {
                    await _email.SendAsync(email, EmailType.MatchChatMessage, new
                    {
                        senderTeamName = senderTeamName ?? senderName,
                        messagePreview = preview,
                        matchRoomUrl,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Chat email failed for recipient {UserId} in match {MatchId}",
                        recipientUserId, matchId);
                }

                if (Guid.TryParse(recipientUserId, out var recipientGuid))
                {
                    var dmPreview = messageContent.Length > 120
                        ? string.Concat(messageContent.AsSpan(0, 120), "…")
                        : messageContent;
                    await _discord.TrySendDmAsync(recipientGuid, "match_chat_message",
                        $"New message from {senderName}",
                        $"\"{dmPreview}\" — {matchRoomUrl}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DispatchChatEmails failed for match {MatchId}", matchId);
        }
    }

    // ── Membership check ──────────────────────────────────────────────────────

    /// <summary>
    /// Checks if the user is a member of one of the teams in the match,
    /// or the tournament organizer.
    /// </summary>
    private async Task<bool> IsMatchParticipantAsync(string userId, string matchId)
    {
        var userCtx = HubAuthHelper.GetUserContext(Context);
        if (userCtx is null) return false;

        using var conn = _db.CreateConnection();
        return await StaffAuthHelper.CanAccessMatchRoomAsync(
            conn, userCtx.UserIdGuid, Guid.Parse(matchId), userCtx);
    }
}

// ── DTO ───────────────────────────────────────────────────────────────────────

public sealed record MessageDto(
    string Id,
    string MatchId,
    string SenderId,
    string SenderName,
    string? TeamId,
    string Content,
    string MessageType,
    DateTime CreatedAt,
    bool IsOrganizer);

/// <summary>Events broadcast to chat group clients.</summary>
public static class ChatHubEvents
{
    public const string MessageReceived = "MessageReceived";
    public const string TypingStart = "TypingStart";
    public const string TypingStop = "TypingStop";
    public const string Error = "Error";
}
