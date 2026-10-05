using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Esportra.Infrastructure.Integrations;

/// <summary>
/// Authenticated proxy to the Riot API with endpoint allowlisting.
/// Ported from supabase/functions/riot-match-proxy/index.ts.
/// Config: Riot:ApiKey
/// </summary>
public sealed class RiotApiClient(HttpClient http, IConfiguration config)
{
    private readonly string _apiKey = config["Riot:ApiKey"] ?? string.Empty;

    private static readonly string[] AllowedRegions =
    [
        "na", "eu", "ap", "kr", "br", "latam",
        "americas", "europe", "asia", "esports",
    ];

    private static readonly string[] AllowedPrefixes =
    [
        "/riot/account/v1/accounts/",
        "/riot/account/v1/active-shards/",
        "/val/match/v1/matches/",
        "/val/match/v1/matchlists/",
        "/val/content/v1/contents",
        "/val/ranked/v1/leaderboards",
    ];

    /// <summary>
    /// Proxy an allowlisted Riot API endpoint.
    /// Region examples: na, eu, ap, kr.
    /// </summary>
    public async Task<(int StatusCode, string Body)> ProxyAsync(
        string region, string endpoint, CancellationToken ct = default)
    {
        if (!AllowedRegions.Contains(region, StringComparer.OrdinalIgnoreCase))
            return (403, """{"error":"Region not allowlisted"}""");

        if (!AllowedPrefixes.Any(p => endpoint.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return (403, """{"error":"Endpoint not allowlisted"}""");

        var url = $"https://{region}.api.riotgames.com{endpoint}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Riot-Token", _apiKey);

        var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        return ((int)res.StatusCode, body);
    }

    /// <summary>
    /// Fetch a Valorant match by Riot match ID for post-game cross-validation.
    /// Returns (StatusCode, Body). Callers handle 200, 404, 429 separately.
    /// Region defaults to "na" when null or empty.
    /// </summary>
    public async Task<(int StatusCode, string Body)> GetMatchAsync(
        string riotMatchId, string? region, CancellationToken ct = default)
    {
        var resolvedRegion = string.IsNullOrWhiteSpace(region) ? "na" : region.ToLowerInvariant();
        return await ProxyAsync(resolvedRegion, $"/val/match/v1/matches/{riotMatchId}", ct);
    }

    /// <summary>
    /// Fetch the current gameName and tagLine for a saved PUUID.
    /// Uses the server API key (X-Riot-Token), not the user's OAuth token.
    /// Returns null on any non-200 response or parse failure.
    /// </summary>
    public async Task<(string GameName, string TagLine)?> GetAccountByPuuidAsync(
        string puuid, string? region, CancellationToken ct = default)
    {
        var routing = ResolveRoutingRegion(region);
        var (status, body) = await ProxyAsync(
            routing, $"/riot/account/v1/accounts/by-puuid/{Uri.EscapeDataString(puuid)}", ct);
        if (status != 200) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var gameName = root.GetProperty("gameName").GetString();
            var tagLine = root.GetProperty("tagLine").GetString();
            return gameName is null || tagLine is null ? null : (gameName, tagLine);
        }
        catch
        {
            return null;
        }
    }

    // Maps platform regions and routing values to the three Account API routing hosts.
    private static string ResolveRoutingRegion(string? region) =>
        region?.ToLowerInvariant() switch
        {
            "na" or "br" or "latam" or "americas" => "americas",
            "eu" or "europe" => "europe",
            "ap" or "kr" or "asia" => "asia",
            _ => "americas",
        };
}
