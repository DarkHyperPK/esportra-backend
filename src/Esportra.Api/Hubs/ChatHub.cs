using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.ScheduledJobs;
using Esportra.Core.Tournaments;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
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
    private readonly IDatabase _redis;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly IBackgroundJobClient _bgJobs;

    public ChatHub(
        IDbConnectionFactory db,
        ILogger<ChatHub> logger,
        IConnectionMultiplexer redis,
        IHubContext<ChatHub> hubContext,
        IBackgroundJobClient backgroundJobClient)
    {
        _db = db;
        _logger = logger;
        _redis = redis.GetDatabase();
        _hubContext = hubContext;
        _bgJobs = backgroundJobClient;
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

    /// <summary>
    /// Refreshes the presence key — called by the client every ~90 s while the chat is open.
    /// isChatOpen: true when the chat panel is visible; triggers a MarkRead upsert so the
    /// Seen indicator stays current during long sessions where the panel stays open.
    /// </summary>
    public async Task Heartbeat(string matchId, bool isChatOpen = false)
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;
        // Capture ConnectionId before any await — Context is invalid after hub disposal.
        var connId = Context.ConnectionId;
        if (!await IsMatchParticipantAsync(userId, matchId)) return;
        Context.Items[$"auth:{matchId}"] = true;
        await SetPresenceAsync(matchId, userId);
        if (isChatOpen)
        {
            _ = TryMarkReadInternalAsync(matchId, userId, connId);
        }
    }

    public async Task LeaveChat(string matchId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatGroup(matchId));
        var userId = Context.UserIdentifier;
        if (userId is not null)
        {
            await _redis.KeyDeleteAsync($"chat-active:{matchId}:{userId}");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier;
        if (userId is not null && Context.Items.TryGetValue("joinedMatches", out var raw) && raw is List<string> matches)
        {
            foreach (var matchId in matches)
            {
                await _redis.KeyDeleteAsync($"chat-active:{matchId}:{userId}");
            }
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

            // AC-009: skip notification scheduling for organizer messages (no competitorId)
            if (competitorId.HasValue)
            {
                _ = ScheduleNotificationJobAsync(matchIdGuid, userIdGuid, competitorId.Value);
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
        var connId = Context.ConnectionId;

        if (!Context.Items.ContainsKey($"auth:{matchId}") && !await IsMatchParticipantAsync(userId, matchId)) return;

        await TryMarkReadInternalAsync(matchId, userId, connId);
    }

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string ChatGroup(string matchId) => $"chat:{matchId}";

    // ── Presence ──────────────────────────────────────────────────────────────

    private Task SetPresenceAsync(string matchId, string userId) =>
        _redis.StringSetAsync(
            $"chat-active:{matchId}:{userId}",
            1,
            TimeSpan.FromSeconds(180));

    // ── Notification scheduling ───────────────────────────────────────────────

    /// <summary>
    /// Cancels any pending notification job for the recipient and schedules a fresh
    /// 2-minute sliding-window job. Fire-and-forget — failure is logged but non-fatal.
    /// </summary>
    private async Task ScheduleNotificationJobAsync(Guid matchId, Guid senderUserId, Guid senderCompetitorId)
    {
        try
        {
            using var conn = _db.CreateConnection();

            var recipientCompId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT CASE WHEN team1_id = @c THEN team2_id ELSE team1_id END FROM brkt_matches WHERE id = @matchId",
                new { c = senderCompetitorId, matchId });
            if (recipientCompId is null) return;

            var recipientUserId = await BracketCompetitorResolver.GetPrimaryUserIdForCompetitorAsync(
                conn, recipientCompId.Value);
            if (recipientUserId is null || recipientUserId == senderUserId) return;

            var recipientUserIdGuid = recipientUserId.Value;

            var existingJobId = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT notification_job_id FROM match_chat_reads WHERE match_id = @matchId AND user_id = @recipientUserIdGuid",
                new { matchId, recipientUserIdGuid });

            if (existingJobId is not null)
                _bgJobs.Delete(existingJobId);

            var newJobId = _bgJobs.Schedule<ChatNotificationJob>(
                job => job.ExecuteAsync(matchId, recipientUserIdGuid),
                TimeSpan.FromMinutes(2));

            await conn.ExecuteAsync(
                """
                INSERT INTO match_chat_reads (match_id, user_id, notification_job_id)
                VALUES (@matchId, @recipientUserIdGuid, @newJobId)
                ON CONFLICT (match_id, user_id) DO UPDATE SET notification_job_id = @newJobId
                """,
                new { matchId, recipientUserIdGuid, newJobId });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScheduleNotificationJob failed for match {MatchId}", matchId);
        }
    }

    private async Task TryMarkReadInternalAsync(string matchId, string userId, string callerConnectionId)
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

            // Read current job id before clearing it so we can cancel it
            var pendingJobId = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT notification_job_id FROM match_chat_reads WHERE match_id = @matchIdGuid AND user_id = @userIdGuid",
                new { matchIdGuid, userIdGuid });

            await conn.ExecuteAsync(
                """
                INSERT INTO match_chat_reads (match_id, user_id, last_read_at, notification_job_id)
                VALUES (@MatchId, @UserId, NOW(), NULL)
                ON CONFLICT (match_id, user_id)
                DO UPDATE SET last_read_at = NOW(), notification_job_id = NULL
                """,
                new { MatchId = matchIdGuid, UserId = userIdGuid });

            if (pendingJobId is not null)
                _bgJobs.Delete(pendingJobId);

            // Use IHubContext (singleton) + GroupExcept rather than this.Clients.OthersInGroup
            // (hub-lifetime) so this method is safe from fire-and-forget tasks that outlive
            // the hub instance. callerConnectionId is captured before any await in the caller.
            await _hubContext.Clients.GroupExcept(ChatGroup(matchId), callerConnectionId)
                .SendAsync(ChatHubEvents.MessagesSeen,
                    new MessageSeenPayload(matchId, userId, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryMarkReadInternalAsync failed for match {MatchId}, user {UserId}", matchId, userId);
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
