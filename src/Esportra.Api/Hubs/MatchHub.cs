using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time match lifecycle — replaces match result / dispute Supabase subscriptions.
/// Groups: match:{matchId}
/// </summary>
[Authorize]
public sealed class MatchHub : Hub
{
    private readonly ILogger<MatchHub> _logger;

    public MatchHub(ILogger<MatchHub> logger) => _logger = logger;

    // ── Client-callable methods ───────────────────────────────────────────────

    public async Task JoinMatch(string matchId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, MatchGroup(matchId));
        _logger.LogDebug("Client {Conn} joined match:{MatchId}", Context.ConnectionId, matchId);
    }

    public async Task LeaveMatch(string matchId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, MatchGroup(matchId));

    // ── Group name helper ─────────────────────────────────────────────────────

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

    /// <summary>Match check-in status changed.</summary>
    public const string CheckInUpdated = "CheckInUpdated";

    /// <summary>Match status changed (scheduled → in_progress → completed).</summary>
    public const string StatusChanged = "StatusChanged";
}
