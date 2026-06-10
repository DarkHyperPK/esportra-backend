using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time match lifecycle — replaces match result / dispute Supabase subscriptions.
/// Groups: match:{matchId}
/// </summary>
[Authorize]
public sealed class MatchHub(IDbConnectionFactory db, ILogger<MatchHub> logger) : Hub
{
    public async Task JoinMatch(string matchId)
    {
        if (!await HubAuthHelper.EnsureMatchRoomAccessAsync(Context, db, matchId))
            throw new HubException("You don't have access to this match room.");

        await Groups.AddToGroupAsync(Context.ConnectionId, MatchGroup(matchId));
        logger.LogDebug("Client {Conn} joined match:{MatchId}", Context.ConnectionId, matchId);
    }

    public async Task LeaveMatch(string matchId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, MatchGroup(matchId));

    public static string MatchGroup(string matchId) => $"match:{matchId}";
}

/// <summary>
/// Events broadcast to match group clients.
/// </summary>
public static class MatchHubEvents
{
    /// <summary>A team captain submitted their result report.</summary>
    public const string ReportSubmitted = "ReportSubmitted";

    /// <summary>The opposing captain accepted the submitted report.</summary>
    public const string ReportAccepted = "ReportAccepted";

    /// <summary>The opposing captain disputed the submitted report.</summary>
    public const string ReportDisputed = "ReportDisputed";

    /// <summary>An admin/organizer resolved the dispute.</summary>
    public const string DisputeResolved = "DisputeResolved";

    /// <summary>A new comment was added to a dispute thread.</summary>
    public const string DisputeCommentAdded = "DisputeCommentAdded";

    /// <summary>Match check-in status changed.</summary>
    public const string CheckInUpdated = "CheckInUpdated";

    /// <summary>Match status changed (scheduled → in_progress → completed).</summary>
    public const string StatusChanged = "StatusChanged";
}
