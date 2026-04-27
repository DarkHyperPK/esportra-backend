using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Esportra.Infrastructure.Integrations;

public interface IDatHostService
{
    Task<DatHostServer> CreateServerAsync(DatHostCreateRequest request, CancellationToken ct = default);
    Task<DatHostServer> GetServerAsync(string serverId, CancellationToken ct = default);
    Task StartServerAsync(string serverId, CancellationToken ct = default);
    Task StopServerAsync(string serverId, CancellationToken ct = default);
    Task DeleteServerAsync(string serverId, CancellationToken ct = default);
    Task SendConsoleCommandAsync(string serverId, string command, CancellationToken ct = default);
}

public class DatHostService : IDatHostService
{
    private readonly HttpClient _http;
    private readonly ILogger<DatHostService> _logger;
    private const string BaseUrl = "https://dathost.net/api/0.1";

    public DatHostService(HttpClient http, IConfiguration config, ILogger<DatHostService> logger)
    {
        _http = http;
        _logger = logger;

        var email = config["DatHost:Email"] ?? throw new InvalidOperationException("DatHost:Email not configured");
        var password = config["DatHost:Password"] ?? throw new InvalidOperationException("DatHost:Password not configured");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    public async Task<DatHostServer> CreateServerAsync(DatHostCreateRequest request, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["name"] = request.Name,
            ["game"] = "csgo",
            ["location"] = request.Location,
            ["csgo_settings.rcon"] = request.RconPassword,
            ["csgo_settings.steam_game_server_login_token"] = request.Gslt,
            ["csgo_settings.slots"] = request.Slots.ToString(),
            ["csgo_settings.tickrate"] = request.Tickrate.ToString(),
            ["csgo_settings.mapgroup_start_map"] = request.StartMap,
            ["csgo_settings.game_mode"] = "classic_competitive",
            ["csgo_settings.enable_gotv"] = request.EnableGotv ? "true" : "false",
            ["csgo_settings.password"] = request.ServerPassword ?? "",
            ["autostop"] = "true",
            ["autostop_minutes"] = "30",
        };

        if (request.EnableSourceMod)
            form["csgo_settings.enable_sourcemod"] = "true";

        var content = new FormUrlEncodedContent(form);
        var response = await _http.PostAsync($"{BaseUrl}/game-servers", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("DatHost CreateServer failed: {Status} {Error}", response.StatusCode, error);
            throw new DatHostException($"Failed to create server: {response.StatusCode} - {error}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var server = JsonSerializer.Deserialize<DatHostServer>(json, JsonOpts)
            ?? throw new DatHostException("Failed to deserialize server response");

        _logger.LogInformation("DatHost server created: {Id} at {Location} ({Ip}:{Port})",
            server.Id, server.Location, server.Ip, server.Ports?.Game);

        return server;
    }

    public async Task<DatHostServer> GetServerAsync(string serverId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{BaseUrl}/game-servers/{serverId}", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<DatHostServer>(json, JsonOpts)
            ?? throw new DatHostException("Failed to deserialize server response");
    }

    public async Task StartServerAsync(string serverId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"{BaseUrl}/game-servers/{serverId}/start", null, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("DatHost StartServer failed for {Id}: {Status} {Error}", serverId, response.StatusCode, error);
            throw new DatHostException($"Failed to start server: {response.StatusCode}");
        }
        _logger.LogInformation("DatHost server started: {Id}", serverId);
    }

    public async Task StopServerAsync(string serverId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"{BaseUrl}/game-servers/{serverId}/stop", null, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("DatHost StopServer failed for {Id}: {Status} {Error}", serverId, response.StatusCode, error);
        }
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"{BaseUrl}/game-servers/{serverId}", ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("DatHost DeleteServer failed for {Id}: {Status} {Error}", serverId, response.StatusCode, error);
        }
        else
        {
            _logger.LogInformation("DatHost server deleted: {Id}", serverId);
        }
    }

    public async Task SendConsoleCommandAsync(string serverId, string command, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string> { ["line"] = command };
        var content = new FormUrlEncodedContent(form);
        var response = await _http.PostAsync($"{BaseUrl}/game-servers/{serverId}/console", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("DatHost console command failed for {Id}: {Command} - {Error}", serverId, command, error);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// -- Request / Response models --

public record DatHostCreateRequest
{
    public required string Name { get; init; }
    public required string Location { get; init; }
    public required string RconPassword { get; init; }
    public required string Gslt { get; init; }
    public required string StartMap { get; init; }
    public int Slots { get; init; } = 12;
    public int Tickrate { get; init; } = 128;
    public bool EnableGotv { get; init; } = true;
    public bool EnableSourceMod { get; init; } = false;
    public string? ServerPassword { get; init; }
}

public class DatHostServer
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("game")]
    public string? Game { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("raw_ip")]
    public string? RawIp { get; set; }

    [JsonPropertyName("on")]
    public bool On { get; set; }

    [JsonPropertyName("booting")]
    public bool Booting { get; set; }

    [JsonPropertyName("server_error")]
    public string? ServerError { get; set; }

    [JsonPropertyName("players_online")]
    public int PlayersOnline { get; set; }

    [JsonPropertyName("ports")]
    public DatHostPorts? Ports { get; set; }

    [JsonPropertyName("cost_per_hour")]
    public decimal CostPerHour { get; set; }

    [JsonPropertyName("status")]
    public List<string>? Status { get; set; }
}

public class DatHostPorts
{
    [JsonPropertyName("game")]
    public int Game { get; set; }

    [JsonPropertyName("gotv")]
    public int? Gotv { get; set; }

    [JsonPropertyName("gotv_secondary")]
    public int? GotvSecondary { get; set; }
}

public class DatHostException : Exception
{
    public DatHostException(string message) : base(message) { }
}

// -- Region definitions --

public static class DatHostRegions
{
    public static readonly DatHostRegion[] All =
    [
        // Europe
        new("amsterdam", "Amsterdam", "EU", "Netherlands"),
        new("dusseldorf", "Frankfurt", "EU", "Germany"),
        new("stockholm", "Stockholm", "EU", "Sweden"),
        new("strasbourg", "Paris", "EU", "France"),
        new("london", "London", "EU", "United Kingdom"),
        new("madrid", "Madrid", "EU", "Spain"),
        new("warsaw", "Warsaw", "EU", "Poland"),
        // Middle East
        new("dubai", "Dubai", "ME", "UAE"),
        // North America
        new("chicago", "Chicago", "NA", "USA"),
        new("dallas", "Dallas", "NA", "USA"),
        new("los_angeles", "Los Angeles", "NA", "USA"),
        new("new_york", "New York", "NA", "USA"),
        // South America
        new("sao_paulo", "São Paulo", "SA", "Brazil"),
        // Asia Pacific
        new("sydney", "Sydney", "OCE", "Australia"),
        new("singapore", "Singapore", "APAC", "Singapore"),
        new("tokyo", "Tokyo", "APAC", "Japan"),
        // Africa
        new("johannesburg", "Johannesburg", "AF", "South Africa"),
    ];

    public static DatHostRegion? GetByLocationId(string locationId)
        => All.FirstOrDefault(r => r.LocationId.Equals(locationId, StringComparison.OrdinalIgnoreCase));

    public static DatHostRegion[] GetByContinent(string continent)
        => All.Where(r => r.Continent.Equals(continent, StringComparison.OrdinalIgnoreCase)).ToArray();
}

public record DatHostRegion(string LocationId, string City, string Continent, string Country);
