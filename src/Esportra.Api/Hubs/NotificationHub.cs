using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time notifications — each user auto-joins their own group on connect.
/// Groups: user:{userId}
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(IDbConnectionFactory db, ILogger<NotificationHub> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
            _logger.LogDebug("Client {Conn} auto-joined user:{UserId}", Context.ConnectionId, userId);
        }

        await base.OnConnectedAsync();
    }

    // ── Client-callable methods ───────────────────────────────────────────────

    /// <summary>Mark a single notification as read in DB, then broadcast to user's other tabs.</summary>
    public async Task MarkRead(string notificationId)
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE notifications SET read = TRUE, read_at = NOW() WHERE id = @Id AND user_id = @UserId",
            new { Id = notificationId, UserId = userId });

        // Broadcast to all other connections of this user (multi-tab support)
        await Clients.OthersInGroup(UserGroup(userId))
            .SendAsync(NotificationHubEvents.NotificationRead, notificationId);
    }

    /// <summary>Mark all unread notifications as read for this user.</summary>
    public async Task MarkAllRead()
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE notifications SET read = TRUE, read_at = NOW() WHERE user_id = @UserId AND read = FALSE",
            new { UserId = userId });

        await Clients.OthersInGroup(UserGroup(userId))
            .SendAsync(NotificationHubEvents.AllRead);
    }

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string UserGroup(string userId) => $"user:{userId}";
}

/// <summary>Events broadcast to notification group clients.</summary>
public static class NotificationHubEvents
{
    /// <summary>A new notification was created for this user.</summary>
    public const string NewNotification    = "NewNotification";

    /// <summary>A specific notification was marked as read (from another tab).</summary>
    public const string NotificationRead   = "NotificationRead";

    /// <summary>All notifications were marked as read (from another tab).</summary>
    public const string AllRead            = "AllRead";

    /// <summary>Team invite received.</summary>
    public const string TeamInvite         = "TeamInvite";

    /// <summary>Tournament status changed for a tournament the user is in.</summary>
    public const string TournamentUpdate   = "TournamentUpdate";
}
