using Esportra.Core.Match;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time map veto — replaces useMapVetoMachine Supabase subscription.
/// Groups: veto:{matchId}
/// </summary>
[Authorize]
public sealed class VetoHub : Hub
{
    private readonly VetoDbService _veto;
    private readonly ILogger<VetoHub> _logger;

    public VetoHub(VetoDbService veto, ILogger<VetoHub> logger)
    {
        _veto   = veto;
        _logger = logger;
    }

    // ── Client-callable methods ───────────────────────────────────────────────

    public async Task JoinVeto(string matchId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, VetoGroup(matchId));
        _logger.LogDebug("Client {Conn} joined veto:{MatchId}", Context.ConnectionId, matchId);

        // Send current state immediately so late-joiner catches up
        var current = await _veto.GetAsync(Guid.Parse(matchId));
        if (current is not null)
            await Clients.Caller.SendAsync(VetoHubEvents.StateSync, current);
    }

    public async Task LeaveVeto(string matchId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, VetoGroup(matchId));

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string VetoGroup(string matchId) => $"veto:{matchId}";
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
}
