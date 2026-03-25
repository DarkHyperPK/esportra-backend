using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

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

    public ChatHub(IDbConnectionFactory db, ILogger<ChatHub> logger)
    {
        _db     = db;
        _logger = logger;
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
        _logger.LogDebug("Client {Conn} joined chat:{MatchId}", Context.ConnectionId, matchId);
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

            // Fetch username and team_id for this match
            var userInfo = await conn.QuerySingleOrDefaultAsync<dynamic>("""
                SELECT p.username,
                       (SELECT tm.team_id FROM team_members tm
                        JOIN brkt_matches bm ON tm.team_id IN (bm.team1_id, bm.team2_id)
                        WHERE bm.id = @MatchId AND tm.user_id = @UserId
                        LIMIT 1) AS team_id
                FROM profiles p WHERE p.id = @UserId
                """, new { MatchId = Guid.Parse(matchId), UserId = Guid.Parse(userId) });

            var username = (string?)(userInfo?.username) ?? "Unknown";
            var teamId   = (Guid?)(userInfo?.team_id);

            const string sql = """
                INSERT INTO match_messages (match_id, sender_id, sender_name, team_id, content, message_type, created_at)
                VALUES (@MatchId, @SenderId, @SenderName, @TeamId, @Content, 'user', NOW())
                RETURNING id::text, match_id::text, sender_id::text, sender_name, team_id::text, content, message_type, created_at;
                """;

            var message = await conn.QuerySingleAsync<MessageDto>(sql, new
            {
                MatchId    = Guid.Parse(matchId),
                SenderId   = Guid.Parse(userId),
                SenderName = username,
                TeamId     = teamId,
                Content    = content.Trim(),
            });

            await Clients.Group(ChatGroup(matchId))
                .SendAsync(ChatHubEvents.MessageReceived, message);
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

    // ── Membership check ──────────────────────────────────────────────────────

    /// <summary>
    /// Checks if the user is a member of one of the teams in the match,
    /// or the tournament organizer.
    /// </summary>
    private async Task<bool> IsMatchParticipantAsync(string userId, string matchId)
    {
        using var conn = _db.CreateConnection();
        var isParticipant = await conn.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM brkt_matches bm
                JOIN team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
                WHERE bm.id = @matchId AND tm.user_id = @userId
                UNION ALL
                SELECT 1 FROM brkt_matches bm
                JOIN brkt_versions bv ON bv.id = bm.version_id
                JOIN tournament_stages ts ON ts.id = bv.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE bm.id = @matchId AND t.organizer_id = @userId
            )
            """,
            new { matchId = Guid.Parse(matchId), userId = Guid.Parse(userId) });
        return isParticipant;
    }
}

// ── DTO ───────────────────────────────────────────────────────────────────────

public sealed record MessageDto(
    string   Id,
    string   MatchId,
    string   SenderId,
    string   SenderName,
    string?  TeamId,
    string   Content,
    string   MessageType,
    DateTime CreatedAt);

/// <summary>Events broadcast to chat group clients.</summary>
public static class ChatHubEvents
{
    public const string MessageReceived = "MessageReceived";
    public const string TypingStart     = "TypingStart";
    public const string TypingStop      = "TypingStop";
    public const string Error           = "Error";
}
