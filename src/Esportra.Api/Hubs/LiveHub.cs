using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time venue live status — replaces useVenueLiveStatus Supabase subscription.
/// Groups: venue:{venueId}
/// </summary>
[Authorize]
public sealed class LiveHub : Hub
{
    private readonly ILogger<LiveHub> _logger;

    public LiveHub(ILogger<LiveHub> logger) => _logger = logger;

    // ── Client-callable methods ───────────────────────────────────────────────

    public async Task JoinVenue(string venueId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, VenueGroup(venueId));
        _logger.LogDebug("Client {Conn} joined venue:{VenueId}", Context.ConnectionId, venueId);
    }

    public async Task LeaveVenue(string venueId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, VenueGroup(venueId));

    // ── Group name helper ─────────────────────────────────────────────────────

    public static string VenueGroup(string venueId) => $"venue:{venueId}";
}

/// <summary>Events broadcast to venue group clients.</summary>
public static class LiveHubEvents
{
    /// <summary>
    /// A station/seat changed state (available, occupied, locked, offline).
    /// Payload: { stationId, venueId, status, occupiedBy?, sessionStart? }
    /// </summary>
    public const string SeatUpdate = "SeatUpdate";

    /// <summary>
    /// Venue-level status changed (open, closed, full, maintenance).
    /// Payload: { venueId, status, updatedAt }
    /// </summary>
    public const string StatusChange = "StatusChange";

    /// <summary>
    /// A booking was created or cancelled — refresh availability calendar.
    /// Payload: { venueId, date, slotsAffected }
    /// </summary>
    public const string BookingChanged = "BookingChanged";
}
