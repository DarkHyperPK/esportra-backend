using System.Collections.Concurrent;
using Esportra.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Services;

/// <summary>
/// Singleton service that tracks which venues have a Local Hub currently connected
/// via the <see cref="VenueSyncHub"/>.  Used by <see cref="LiveHub"/>
/// to relay station commands to the correct SignalR connection.
/// </summary>
public sealed class VenueConnectionTracker
{
    private readonly ILogger<VenueConnectionTracker> _logger;

    // venueId → connectionId  (one hub per venue)
    private readonly ConcurrentDictionary<string, ConnectionInfo> _byVenue = new(StringComparer.OrdinalIgnoreCase);

    // connectionId → venueId  (reverse lookup for disconnect cleanup)
    private readonly ConcurrentDictionary<string, string> _byConnection = new(StringComparer.OrdinalIgnoreCase);

    public VenueConnectionTracker(ILogger<VenueConnectionTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>Whether a Local Hub is connected for the given venue.</summary>
    public bool IsVenueConnected(string venueId)
        => _byVenue.ContainsKey(venueId);

    /// <summary>Get the SignalR connection ID for a venue's Local Hub, or null if offline.</summary>
    public string? GetConnectionId(string venueId)
        => _byVenue.TryGetValue(venueId, out var info) ? info.ConnectionId : null;

    /// <summary>Get full connection info for a venue, or null if offline.</summary>
    public ConnectionInfo? GetConnectionInfo(string venueId)
        => _byVenue.TryGetValue(venueId, out var info) ? info : null;

    /// <summary>Register a Local Hub connection for a venue. Replaces any existing connection.</summary>
    public void TrackConnection(string venueId, string connectionId)
    {
        var info = new ConnectionInfo(connectionId, DateTime.UtcNow);

        // If this venue already had a connection, remove the old reverse mapping
        if (_byVenue.TryGetValue(venueId, out var old))
            _byConnection.TryRemove(old.ConnectionId, out _);

        _byVenue[venueId] = info;
        _byConnection[connectionId] = venueId;
    }

    /// <summary>
    /// Remove a connection by its connection ID (called on disconnect).
    /// Returns the venue ID that was disconnected, or null.
    /// </summary>
    public string? RemoveConnection(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var venueId))
            return null;

        // Only remove from the venue map if the stored connection matches
        // (another connection may have replaced it during reconnect race)
        if (_byVenue.TryGetValue(venueId, out var info) && info.ConnectionId == connectionId)
            _byVenue.TryRemove(venueId, out _);

        return venueId;
    }

    /// <summary>Get all currently connected venue IDs.</summary>
    public IReadOnlyCollection<string> GetConnectedVenueIds()
        => _byVenue.Keys.ToList().AsReadOnly();

    /// <summary>Total number of connected venues.</summary>
    public int ConnectedCount => _byVenue.Count;

    // ═══════════════════════════════════════════════════════════════════════════
    //  RELAY HELPERS — for backend services to send commands to Local Hubs
    //  These bypass LiveHub (which is client-facing) and go directly through
    //  IHubContext<VenueSyncHub> to the Local Hub connection.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Send an online booking to a venue's Local Hub for acceptance.
    /// Called by backend services (e.g. booking endpoints), NOT by web clients.
    /// </summary>
    public async Task SendIncomingBookingAsync(IHubContext<VenueSyncHub> syncHub, string venueId, object booking)
    {
        var connectionId = GetConnectionId(venueId);
        if (connectionId is null)
        {
            _logger.LogWarning("[VenueConnectionTracker] Cannot send booking — Local Hub offline for venue {VenueId}", venueId);
            return;
        }

        await syncHub.Clients.Client(connectionId)
            .SendAsync("IncomingBooking", booking);

        _logger.LogInformation("[VenueConnectionTracker] IncomingBooking relayed to venue {VenueId}", venueId);
    }

    /// <summary>
    /// Revoke a device session on a venue's Local Hub.
    /// Called by backend services (e.g. admin endpoints), NOT by web clients.
    /// </summary>
    public async Task SendDeviceRevokedAsync(IHubContext<VenueSyncHub> syncHub, string venueId, string deviceId, string reason)
    {
        var connectionId = GetConnectionId(venueId);
        if (connectionId is null)
        {
            _logger.LogWarning("[VenueConnectionTracker] Cannot revoke device — Local Hub offline for venue {VenueId}", venueId);
            return;
        }

        await syncHub.Clients.Client(connectionId)
            .SendAsync("DeviceRevoked", deviceId, reason);

        _logger.LogInformation("[VenueConnectionTracker] DeviceRevoked relayed to venue {VenueId} device={DeviceId}", venueId, deviceId);
    }

    public sealed record ConnectionInfo(string ConnectionId, DateTime ConnectedAt);
}
