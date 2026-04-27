using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Cloud-side hub that LOCAL HUBS connect to (not web browsers).
/// Authenticates via per-venue API key (<c>X-Venue-ApiKey</c> header),
/// tracks connected hubs, receives state pushes, and relays commands.
///
/// Route: <c>/hubs/venue-sync</c>
/// </summary>
public sealed class VenueSyncHub : Hub
{
    private readonly IDbConnectionFactory _db;
    private readonly VenueConnectionTracker _tracker;
    private readonly IHubContext<LiveHub> _liveHub;
    private readonly ILogger<VenueSyncHub> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public VenueSyncHub(
        IDbConnectionFactory db,
        VenueConnectionTracker tracker,
        IHubContext<LiveHub> liveHub,
        ILogger<VenueSyncHub> logger)
    {
        _db = db;
        _tracker = tracker;
        _liveHub = liveHub;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  CONNECTION LIFECYCLE — API-key authentication
    // ═══════════════════════════════════════════════════════════════════════════

    public override async Task OnConnectedAsync()
    {
        var httpCtx = Context.GetHttpContext();
        if (httpCtx is null)
        {
            _logger.LogWarning("[VenueSyncHub] Connection rejected — no HTTP context");
            Context.Abort();
            return;
        }

        var venueId = httpCtx.Request.Query["venueId"].ToString();
        var apiKey = httpCtx.Request.Headers["X-Venue-ApiKey"].ToString();

        if (string.IsNullOrEmpty(venueId) || string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("[VenueSyncHub] Connection rejected — missing venueId or API key");
            Context.Abort();
            return;
        }

        // Validate the API key against the bcrypt hash stored in the database
        if (!await ValidateApiKeyAsync(venueId, apiKey))
        {
            _logger.LogWarning("[VenueSyncHub] Connection rejected — invalid API key for venue {VenueId}", venueId);
            Context.Abort();
            return;
        }

        // Store venueId on the connection for later use
        Context.Items["VenueId"] = venueId;

        // Track the connection and join the venue group
        _tracker.TrackConnection(venueId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, VenueGroup(venueId));

        _logger.LogInformation(
            "[VenueSyncHub] Local Hub connected — venue={VenueId} connId={ConnId}",
            venueId, Context.ConnectionId);

        // Notify web dashboard clients that the hub is now online
        await _liveHub.Clients.Group(LiveHub.VenueGroup(venueId))
            .SendAsync("HubStatusChanged", new { venue_id = venueId, is_connected = true, connected_at = DateTime.UtcNow });

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var venueId = _tracker.RemoveConnection(Context.ConnectionId);

        if (venueId is not null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, VenueGroup(venueId));

            _logger.LogInformation(
                "[VenueSyncHub] Local Hub disconnected — venue={VenueId} reason={Reason}",
                venueId, exception?.Message ?? "clean close");

            // Notify web dashboard clients that the hub went offline
            await _liveHub.Clients.Group(LiveHub.VenueGroup(venueId))
                .SendAsync("HubStatusChanged", new { venue_id = venueId, is_connected = false, disconnected_at = DateTime.UtcNow });
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  METHODS CALLED BY THE LOCAL HUB (state pushes)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Local Hub pushes a single station's state change.
    /// Forwarded to web dashboard clients watching this venue.
    /// </summary>
    public async Task SyncStationState(JsonElement stationState)
    {
        var venueId = GetVenueId();
        if (venueId is null) return;

        _logger.LogDebug("[VenueSyncHub] SyncStationState from venue {VenueId}", venueId);

        // Forward to web dashboard clients via LiveHub
        await _liveHub.Clients.Group(LiveHub.VenueGroup(venueId))
            .SendAsync(LiveHubEvents.SeatUpdate, stationState);
    }

    /// <summary>
    /// Local Hub pushes a full state snapshot (all stations).
    /// Typically called on connect/reconnect.
    /// </summary>
    public async Task SyncFullState(JsonElement fullState)
    {
        var venueId = GetVenueId();
        if (venueId is null) return;

        _logger.LogInformation("[VenueSyncHub] SyncFullState from venue {VenueId}", venueId);

        // Forward full snapshot to web dashboard
        await _liveHub.Clients.Group(LiveHub.VenueGroup(venueId))
            .SendAsync("FullStateSync", fullState);
    }

    /// <summary>
    /// Local Hub responds to an online booking request.
    /// </summary>
    public async Task RespondToBooking(string bookingId, JsonElement response)
    {
        var venueId = GetVenueId();
        if (venueId is null) return;

        _logger.LogInformation(
            "[VenueSyncHub] Booking response from venue {VenueId} — bookingId={BookingId}",
            venueId, bookingId);

        // Forward booking acknowledgement to web dashboard
        await _liveHub.Clients.Group(LiveHub.VenueGroup(venueId))
            .SendAsync("BookingResponse", new { booking_id = bookingId, response });
    }

    /// <summary>
    /// Local Hub pushes health/heartbeat data.
    /// </summary>
    public async Task PushHealth(JsonElement healthData)
    {
        var venueId = GetVenueId();
        if (venueId is null) return;

        _logger.LogDebug("[VenueSyncHub] Health data from venue {VenueId}", venueId);

        // Forward to web dashboard
        await _liveHub.Clients.Group(LiveHub.VenueGroup(venueId))
            .SendAsync("HubHealth", healthData);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  METHODS CALLED BY THE CLOUD TO RELAY TO LOCAL HUBS
    //  (invoked from LiveHub via IHubContext<VenueSyncHub>)
    // ═══════════════════════════════════════════════════════════════════════════

    // These are not hub methods callable by the Local Hub.
    // Instead, LiveHub uses IHubContext<VenueSyncHub>.Clients.Client(connectionId)
    // to send these events to the specific Local Hub connection:
    //
    //   • IncomingBooking(booking)         — new online booking to route
    //   • DeviceRevoked(deviceId, reason)  — revoke a device session
    //   • LockStation(stationId)           — lock a station
    //   • UnlockStation(stationId, session)— unlock with session config
    //   • RestartStation(stationId)        — restart station PC
    //   • ForceShutdown(stationId)         — force shutdown station PC
    //   • SendMessage(stationId, message)  — display message on station
    //   • SetStationMaintenance(stationId, maintenance) — toggle maintenance
    //   • EndSession(stationId)            — end current session
    //   • ExtendSession(stationId, additionalMinutes) — extend session
    //   • CreateSession(stationId, sessionConfig) — create new session

    // ═══════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    private string? GetVenueId()
    {
        if (Context.Items.TryGetValue("VenueId", out var vid) && vid is string venueId)
            return venueId;

        _logger.LogWarning("[VenueSyncHub] No VenueId on connection {ConnId}", Context.ConnectionId);
        return null;
    }

    private static string VenueGroup(string venueId) => $"venue-sync:{venueId}";

    /// <summary>
    /// Validate a plaintext API key against the bcrypt hash in the venues table.
    /// Uses pgcrypto's crypt() for server-side comparison (no BCrypt.Net needed).
    /// </summary>
    private async Task<bool> ValidateApiKeyAsync(string venueId, string apiKey)
    {
        try
        {
            using var conn = _db.CreateConnection();
            var isValid = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM   venues
                    WHERE  id = @VenueId::uuid
                      AND  hub_api_key_hash IS NOT NULL
                      AND  hub_api_key_hash = extensions.crypt(@ApiKey, hub_api_key_hash)
                      AND  deleted_at IS NULL
                )
                """,
                new { VenueId = venueId, ApiKey = apiKey });

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VenueSyncHub] API key validation failed for venue {VenueId}", venueId);
            return false;
        }
    }
}
