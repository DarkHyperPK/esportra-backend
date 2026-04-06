using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Esportra.Api.Services;

/// <summary>
/// Client for calling the esportra-venue-hub internal API.
/// Routes online bookings to venue local hubs via the venue-hub service.
/// </summary>
public sealed class VenueHubService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _venueHubUrl;
    private readonly string _internalSecret;
    private readonly ILogger<VenueHubService> _logger;

    public VenueHubService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<VenueHubService> logger)
    {
        _httpFactory = httpFactory;
        _venueHubUrl = config["VenueHub:Url"]?.TrimEnd('/') ?? "";
        _internalSecret = config["VenueHub:InternalSecret"] ?? "";
        _logger = logger;

        if (IsConfigured && string.IsNullOrEmpty(_internalSecret))
            _logger.LogWarning("VenueHub:Url is set but VenueHub:InternalSecret is missing — authenticated calls will fail");
    }

    /// <summary>
    /// Check if the venue-hub service is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_venueHubUrl);

    /// <summary>
    /// Check if a venue's local hub is currently online.
    /// </summary>
    public async Task<bool> IsVenueOnlineAsync(string venueId)
    {
        if (!IsConfigured) return false;

        try
        {
            using var http = CreateClient();
            var response = await http.GetFromJsonAsync<VenueOnlineResponse>(
                $"{_venueHubUrl}/api/venues/{venueId}/online");
            return response?.IsOnline ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to check venue online status: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Route an online booking to the venue's local hub via the venue-hub.
    /// Returns the local hub's accept/reject response.
    /// </summary>
    public async Task<BookingRouteResult> RouteBookingAsync(
        string bookingId, string venueId, string? stationId,
        string userId, string displayName, int durationMinutes,
        string bookingCode, string? startTime)
    {
        if (!IsConfigured)
            return new BookingRouteResult(false, null, "Venue hub service not configured");

        try
        {
            using var http = CreateAuthenticatedClient();
            var payload = new
            {
                BookingId = bookingId,
                VenueId = venueId,
                StationId = stationId,
                UserId = userId,
                DisplayName = displayName,
                DurationMinutes = durationMinutes,
                BookingCode = bookingCode,
                StartTime = startTime
            };

            var response = await http.PostAsJsonAsync(
                $"{_venueHubUrl}/api/internal/bookings/route", payload);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Venue hub booking route failed ({Status}): {Body}",
                    response.StatusCode, body);
                return new BookingRouteResult(false, null, $"Venue hub error: {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<BookingRouteResult>();
            return result ?? new BookingRouteResult(false, null, "Empty response from venue hub");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to route booking {BookingId} to venue {VenueId}",
                bookingId, venueId);
            return new BookingRouteResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Get current seat availability for a venue.
    /// </summary>
    public async Task<VenueSeatsResponse?> GetVenueSeatsAsync(string venueId)
    {
        if (!IsConfigured) return null;

        try
        {
            using var http = CreateClient();
            return await http.GetFromJsonAsync<VenueSeatsResponse>(
                $"{_venueHubUrl}/api/venues/{venueId}/seats");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to get venue seats: {Message}", ex.Message);
            return null;
        }
    }

    private HttpClient CreateClient()
    {
        return _httpFactory.CreateClient("VenueHub");
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var http = _httpFactory.CreateClient("VenueHub");
        var token = GenerateInternalJwt();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private string GenerateInternalJwt()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_internalSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "esportra-backend",
            audience: "esportra-venue-hub",
            claims: [new Claim("service", "esportra-backend")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

// Response models
public sealed record VenueOnlineResponse(bool IsOnline);
public sealed record BookingRouteResult(bool Accepted, string? StationId, string? Reason);
public sealed record VenueSeatsResponse(
    string VenueId,
    bool IsOnline,
    List<SeatStatusDto> Stations,
    DateTime UpdatedAt);
public sealed record SeatStatusDto(
    string StationId,
    string VenueId,
    string Status,
    string? SessionType,
    string? DisplayName,
    string? ExpiresAt,
    DateTime UpdatedAt);
