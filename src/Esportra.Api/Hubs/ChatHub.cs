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

        using var conn = _db.CreateConnection();

        // Fetch username from profiles
        var username = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT username FROM profiles WHERE id = @Id", new { Id = userId });

        const string sql = """
            INSERT INTO match_messages (match_id, user_id, username, content, created_at)
            VALUES (@MatchId, @UserId, @Username, @Content, NOW())
            RETURNING id, match_id, user_id, username, content, created_at;
            """;

        var message = await conn.QuerySingleAsync<MessageDto>(sql, new
        {
            MatchId  = matchId,
            UserId   = userId,
            Username = username ?? "Unknown",
            Content  = content.Trim(),
        });

        await Clients.Group(ChatGroup(matchId))
            .SendAsync(ChatHubEvents.MessageReceived, message);
    }

    /// <summary>Broadcast typing indicator to other members of the chat group.</summary>
    public async Task SendTyping(string matchId, bool isTyping)
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;

        using var conn = _db.CreateConnection();
        var username = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT username FROM profiles WHERE id = @Id", new { Id = userId });

        await Clients.OthersInGroup(ChatGroup(matchId))
            .SendAsync(
                isTyping ? ChatHubEvents.TypingStart : ChatHubEvents.TypingStop,
                userId,
                username ?? "Unknown");
    }

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string ChatGroup(string matchId) => $"chat:{matchId}";
}

// ── DTO ───────────────────────────────────────────────────────────────────────

public sealed record MessageDto(
    string   Id,
    string   MatchId,
    string   UserId,
    string   Username,
    string   Content,
    DateTime CreatedAt);

/// <summary>Events broadcast to chat group clients.</summary>
public static class ChatHubEvents
{
    public const string MessageReceived = "MessageReceived";
    public const string TypingStart     = "TypingStart";
    public const string TypingStop      = "TypingStop";
    public const string Error           = "Error";
}
