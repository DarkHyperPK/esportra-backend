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
        Context.Items[$"auth:{matchId}"] = true;
        var joined = Context.Items.TryGetValue("joinedMatches", out var existing)
            ? (List<string>)existing!
            : new List<string>();
        joined.Add(matchId);
        Context.Items["joinedMatches"] = joined;

        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroup(matchId));
        _ = TryMarkReadInternalAsync(matchId, userId);
        try
        {
            await SetPresenceAsync(matchId, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set chat presence for match {MatchId}, user {UserId} — continuing without presence", matchId, userId);
        }
        _logger.LogDebug("Client {Conn} joined chat:{MatchId}", Context.ConnectionId, matchId);
    }

    /// <summary>Refreshes the presence key — called by the client every ~90 s while the chat is open.</summary>
    public async Task Heartbeat(string matchId)
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;
        if (!await IsMatchParticipantAsync(userId, matchId)) return;
        Context.Items[$"auth:{matchId}"] = true;
        await SetPresenceAsync(matchId, userId);
        _ = TryMarkReadInternalAsync(matchId, userId);
    }

    public async Task LeaveChat(string matchId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatGroup(matchId));
        var userId = Context.UserIdentifier;
        if (userId is not null)
            await _redis.KeyDeleteAsync($"chat-active:{matchId}:{userId}");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier;
        if (userId is not null && Context.Items.TryGetValue("joinedMatches", out var raw) && raw is List<string> matches)
        {
            foreach (var matchId in matches)
                await _redis.KeyDeleteAsync($"chat-active:{matchId}:{userId}");
        }
        await base.OnDisconnectedAsync(exception);
    }

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

        // Verify user is a participant in this match — use cached result from JoinChat/Heartbeat
        if (!Context.Items.ContainsKey($"auth:{matchId}") && !await IsMatchParticipantAsync(userId, matchId))
        {
            await Clients.Caller.SendAsync(ChatHubEvents.Error, "You are not a participant in this match.");
            return;
        }

        try
        {
            var userIdGuid = Guid.Parse(userId);
            var matchIdGuid = Guid.Parse(matchId);

            using var conn = _db.CreateConnection();

            var competitorId = await BracketCompetitorResolver.GetUserCompetitorIdInMatchAsync(
                conn, userIdGuid, matchIdGuid);

            var username = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT username FROM profiles WHERE id = @UserId",
                new { UserId = userIdGuid }) ?? "Unknown";
            var userCtx = HubAuthHelper.GetUserContext(Context);
            var isOrganizer = userCtx is not null && await StaffAuthHelper.IsMatchOrganizerOrStaffAsync(
                conn, userCtx.UserIdGuid, matchIdGuid, userCtx);

            const string sql = """
                INSERT INTO match_messages (match_id, sender_id, sender_name, team_id, content, message_type, created_at)
                VALUES (@MatchId, @SenderId, @SenderName, @TeamId, @Content, 'user', NOW())
                RETURNING id::text, match_id::text, sender_id::text, sender_name, team_id::text, content, message_type, created_at, @IsOrganizer AS is_organizer;
                """;

            var message = await conn.QuerySingleAsync<MessageDto>(sql, new
            {
                MatchId = matchIdGuid,
                SenderId = userIdGuid,
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
        if (!Context.Items.ContainsKey($"auth:{matchId}") && !await IsMatchParticipantAsync(userId, matchId)) return;

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

    /// <summary>Marks all chat messages in the match as read by the current user.</summary>
    public async Task MarkRead(string matchId)
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;

        if (!Context.Items.ContainsKey($"auth:{matchId}") && !await IsMatchParticipantAsync(userId, matchId)) return;

        await TryMarkReadInternalAsync(matchId, userId);
    }

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string ChatGroup(string matchId) => $"chat:{matchId}";

    // ── Presence ──────────────────────────────────────────────────────────────

    private Task SetPresenceAsync(string matchId, string userId) =>
        _redis.StringSetAsync(
            $"chat-active:{matchId}:{userId}",
            1,
            TimeSpan.FromSeconds(180));

    private async Task TryMarkReadInternalAsync(string matchId, string userId)
    {
        try
        {
            var userIdGuid = Guid.Parse(userId);
            var matchIdGuid = Guid.Parse(matchId);

            var userCtx = HubAuthHelper.GetUserContext(Context);
            if (userCtx is null) return;

            using var conn = _db.CreateConnection();

            if (await StaffAuthHelper.IsMatchOrganizerOrStaffAsync(conn, userIdGuid, matchIdGuid, userCtx))
            {
                _logger.LogDebug("TryMarkReadInternalAsync: organizer {UserId} skipped for match {MatchId}", userId, matchId);
                return;
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO match_chat_reads (match_id, user_id, last_read_at)
                VALUES (@MatchId, @UserId, NOW())
                ON CONFLICT (match_id, user_id)
                DO UPDATE SET last_read_at = NOW()
                """,
                new { MatchId = matchIdGuid, UserId = userIdGuid });

            await Clients.OthersInGroup(ChatGroup(matchId))
                .SendAsync(ChatHubEvents.MessagesSeen,
                    new MessageSeenPayload(matchId, userId, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryMarkReadInternalAsync failed for match {MatchId}, user {UserId}", matchId, userId);
        }
    }

    // ── Email dispatch ────────────────────────────────────────────────────────

    private async Task DispatchChatEmailsAsync(
        string matchId, string senderUserId, Guid senderCompetitorId,
        string messageContent, string senderName)
    {
        try
        {
            var matchIdGuid = Guid.Parse(matchId);
            var senderUserIdGuid = Guid.Parse(senderUserId);
            var oppUserIdValue = Guid.Empty;
            var senderTeamName = senderName;
            string? email = null;
            var matchRoomUrl = string.Empty;
            var unreadCount = 1;

            using (var conn = _db.CreateConnection())
            {
                // 1. Get opposing competitor slot (works for both solo and team)
                var oppCompetitorId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    "SELECT CASE WHEN team1_id = @c THEN team2_id ELSE team1_id END FROM brkt_matches WHERE id = @matchId",
                    new { c = senderCompetitorId, matchId = matchIdGuid });
                if (oppCompetitorId is null) return;

                // 2. Get opposing captain user ID — needed for Redis key before heavier queries
                var oppUserId = await BracketCompetitorResolver.GetPrimaryUserIdForCompetitorAsync(conn, oppCompetitorId.Value);
                if (oppUserId is null || oppUserId == senderUserIdGuid) return;
                oppUserIdValue = oppUserId.Value;

                var recipientId = oppUserIdValue.ToString();
                // 3. Gate early: skip if recipient is in the chat room
                if (await _redis.KeyExistsAsync($"chat-active:{matchId}:{recipientId}"))
                {
                    _logger.LogDebug("Chat notification suppressed — recipient {RecipientId} is active in match {MatchId}", recipientId, matchId);
                    return;
                }
                // 4. Gate early: skip if already notified within cooldown window (5 min), atomic
                if (!await _redis.StringSetAsync($"chat-email-sent:{matchId}:{recipientId}", 1, TimeSpan.FromSeconds(300), When.NotExists))
                {
                    _logger.LogDebug("Chat notification suppressed — cooldown active for recipient {RecipientId} in match {MatchId}", recipientId, matchId);
                    return;
                }

                // 4b. Count unread messages for notification copy — fall back to 1 on failure (AC-206)
                try
                {
                    unreadCount = await conn.ExecuteScalarAsync<int>(
                        """
                        SELECT COUNT(*)::int
                        FROM match_messages
                        WHERE match_id = @MatchId
                          AND sender_id != @RecipientId
                          AND message_type = 'user'
                          AND created_at > COALESCE(
                              (SELECT last_read_at FROM match_chat_reads
                               WHERE match_id = @MatchId AND user_id = @RecipientId),
                              '1970-01-01'::timestamptz
                          )
                        """,
                        new { MatchId = matchIdGuid, RecipientId = oppUserIdValue });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Chat unread COUNT failed for match {MatchId}, recipient {RecipientId} — defaulting to 1",
                        matchId, oppUserIdValue);
                }

                // 5. Fetch display name, email, and URL only after passing both gates
                senderTeamName = await BracketCompetitorResolver.GetCompetitorDisplayNameAsync(conn, senderCompetitorId) ?? senderName;
                email = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT email FROM profiles WHERE id = @userId AND email IS NOT NULL",
                    new { userId = oppUserId });
                var matchPath = await CaptainMatchLinkBuilder.BuildAsync(conn, matchIdGuid);
                matchRoomUrl = $"{_frontendUrl}{matchPath}";
            }
            // Connection returned to pool here — HTTP calls follow outside the using block

            if (oppUserIdValue == Guid.Empty) return;

            var preview = messageContent.Length > 120 ? messageContent[..120] + "…" : messageContent;
            await SendChatNotificationsAsync(matchId, oppUserIdValue, email, senderTeamName, preview, matchRoomUrl, unreadCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DispatchChatEmails failed for match {MatchId}", matchId);
        }
    }

    private async Task SendChatNotificationsAsync(
        string matchId, Guid recipientUserId, string? email,
        string senderTeamName, string preview, string matchRoomUrl, int unreadCount)
    {
        if (email is not null)
        {
            try
            {
                await _email.SendAsync(email, EmailType.MatchChatMessage, new
                {
                    senderTeamName,
                    messagePreview = preview,
                    matchRoomUrl,
                    unreadCount,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chat email failed for recipient in match {MatchId}", matchId);
            }
        }

        await _discord.TrySendDmAsync(
            recipientUserId,
            "match_chat_message",
            $"You have {unreadCount} unread message{(unreadCount == 1 ? "" : "s")} from {senderTeamName}",
            matchRoomUrl);
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

public sealed record MessageSeenPayload(string MatchId, string UserId, DateTime LastReadAt);

/// <summary>Events broadcast to chat group clients.</summary>
public static class ChatHubEvents
{
    public const string MessageReceived = "MessageReceived";
    public const string TypingStart = "TypingStart";
    public const string TypingStop = "TypingStop";
    public const string Error = "Error";
    public const string MessagesSeen = "MessagesSeen";
}
