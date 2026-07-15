using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time DM/group messaging — replaces useMessaging.ts Supabase subscription.
/// Groups: conversation:{conversationId}
/// </summary>
[Authorize]
public sealed class ConversationHub : Hub
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<ConversationHub> _logger;

    public ConversationHub(IDbConnectionFactory db, ILogger<ConversationHub> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Client-callable methods ───────────────────────────────────────────────

    /// <summary>Join a conversation group to receive real-time messages.</summary>
    public async Task JoinConversation(string conversationId)
    {
        var userId = Context.UserIdentifier;
        if (userId is null)
        {
            await Clients.Caller.SendAsync(ConversationHubEvents.Error, "Not authenticated.");
            return;
        }

        // Verify user is a participant
        if (!await IsConversationParticipantAsync(userId, conversationId))
        {
            await Clients.Caller.SendAsync(ConversationHubEvents.Error, "You are not a participant in this conversation.");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
        _logger.LogDebug("Client {Conn} joined conversation:{ConvId}", Context.ConnectionId, conversationId);
    }

    /// <summary>Leave a conversation group.</summary>
    public async Task LeaveConversation(string conversationId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));

    /// <summary>Join multiple conversations at once (for initial load).</summary>
    public async Task JoinConversations(string[] conversationIds)
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;

        // Cap to prevent DoS via unbounded array
        var capped = conversationIds.Length > 50 ? conversationIds[..50] : conversationIds;

        foreach (var convId in capped)
        {
            if (await IsConversationParticipantAsync(userId, convId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(convId));
            }
        }
        _logger.LogDebug("Client {Conn} joined {Count} conversations", Context.ConnectionId, capped.Length);
    }

    /// <summary>Broadcast typing indicator to other members of the conversation.</summary>
    public async Task SendTyping(string conversationId, bool isTyping)
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;

        // Cache username to avoid DB hit on every keystroke
        if (Context.Items.TryGetValue("username", out var cached))
        {
            var un = cached as string ?? "Unknown";
            await Clients.OthersInGroup(ConversationGroup(conversationId))
                .SendAsync(
                    isTyping ? ConversationHubEvents.TypingStart : ConversationHubEvents.TypingStop,
                    new { userId, username = un });
            return;
        }

        using var conn = _db.CreateConnection();
        var username = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT username FROM profiles WHERE id = @Id", new { Id = Guid.Parse(userId) });
        Context.Items["username"] = username ?? "Unknown";

        await Clients.OthersInGroup(ConversationGroup(conversationId))
            .SendAsync(
                isTyping ? ConversationHubEvents.TypingStart : ConversationHubEvents.TypingStop,
                new { userId, username = username ?? "Unknown" });
    }

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string ConversationGroup(string conversationId) => $"conversation:{conversationId}";

    // ── Membership check ──────────────────────────────────────────────────────

    private async Task<bool> IsConversationParticipantAsync(string userId, string conversationId)
    {
        using var conn = _db.CreateConnection();
        var isParticipant = await conn.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM conversation_participants
                WHERE conversation_id = @conversationId 
                  AND user_id = @userId 
                  AND is_active = TRUE
            )
            """,
            new { conversationId = Guid.Parse(conversationId), userId = Guid.Parse(userId) });
        return isParticipant;
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>Message payload sent via SignalR.</summary>
public sealed record ConversationMessageDto(
    string Id,
    string ConversationId,
    string SenderId,
    string Content,
    string MessageType,
    object? Attachments,
    bool IsEdited,
    DateTime CreatedAt,
    ConversationSenderDto? Sender);

public sealed record ConversationSenderDto(
    string Id,
    string Username,
    string? FullName,
    string? AvatarUrl);

/// <summary>Events broadcast to conversation group clients.</summary>
public static class ConversationHubEvents
{
    /// <summary>A new message was sent to the conversation.</summary>
    public const string MessageReceived = "MessageReceived";

    /// <summary>A message was edited.</summary>
    public const string MessageEdited = "MessageEdited";

    /// <summary>A message was deleted.</summary>
    public const string MessageDeleted = "MessageDeleted";

    /// <summary>User started typing.</summary>
    public const string TypingStart = "TypingStart";

    /// <summary>User stopped typing.</summary>
    public const string TypingStop = "TypingStop";

    /// <summary>Error occurred.</summary>
    public const string Error = "Error";
}
