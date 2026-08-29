using Esportra.Core.Match;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time map veto — replaces useMapVetoMachine Supabase subscription.
/// Groups: veto:{matchId}
/// Anonymous read-only subscribe is allowed so token-link captains receive live updates.
/// </summary>
[AllowAnonymous]
public sealed class VetoHub : Hub
{
    private readonly VetoDbService _veto;
    private readonly ILogger<VetoHub> _logger;

    public VetoHub(VetoDbService veto, ILogger<VetoHub> logger)
    {
        _veto = veto;
        _logger = logger;
    }

    // ── Client-callable methods ───────────────────────────────────────────────

    public async Task JoinVeto(string matchId)
    {
        if (Context.User?.Identity?.IsAuthenticated != true)
        {
            await Clients.Caller.SendAsync("Error", "Authentication required to watch veto.");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, VetoGroup(matchId));
        _logger.LogDebug("Client {Conn} joined veto:{MatchId}", Context.ConnectionId, matchId);

        // Send current state immediately so late-joiner catches up
        var current = await _veto.GetAsync(Guid.Parse(matchId));
        if (current is not null)
            await Clients.Caller.SendAsync(VetoHubEvents.StateSync, current);
    }

    public async Task LeaveVeto(string matchId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, VetoGroup(matchId));

    public async Task JoinPublicToolVeto(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, PublicToolVetoGroup(sessionId));
        _logger.LogDebug("Client {Conn} joined public-tool-veto:{SessionId}", Context.ConnectionId, sessionId);
    }

    public async Task LeavePublicToolVeto(string sessionId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, PublicToolVetoGroup(sessionId));

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string VetoGroup(string matchId) => $"veto:{matchId}";

    public static string PublicToolVetoGroup(string sessionId) => $"public-tool-veto:{sessionId}";
}

/// <summary>
/// Events broadcast to veto group clients.
/// </summary>
public static class VetoHubEvents
{
    /// <summary>
    /// A veto action (ban/pick/side) was applied.
    /// Payload: the full updated MatchMapVeto record.
    /// </summary>
    public const string VetoAction = "VetoAction";

    /// <summary>
    /// Veto is complete. Payload: array of PickedMap objects.
    /// </summary>
    public const string VetoComplete = "VetoComplete";

    /// <summary>
    /// State sync sent to a late-joining client. Payload: MatchMapVeto.
    /// </summary>
    public const string StateSync = "StateSync";

    /// <summary>Veto was reset by organizer.</summary>
    public const string VetoReset = "VetoReset";

    /// <summary>Veto action history updated. Payload: array of VetoActionHistory.</summary>
    public const string VetoHistoryUpdated = "VetoHistoryUpdated";

    /// <summary>Public tool veto session changed. Payload: { sessionId }.</summary>
    public const string PublicVetoUpdated = "PublicVetoUpdated";

    /// <summary>Public tool veto session reset. Payload: { sessionId }.</summary>
    public const string PublicVetoReset = "PublicVetoReset";
}
