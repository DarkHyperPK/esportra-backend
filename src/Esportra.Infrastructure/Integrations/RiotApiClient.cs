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
        if (!AllowedPrefixes.Any(p => endpoint.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return (403, """{"error":"Endpoint not allowlisted"}""");

        var url = $"https://{region}.api.riotgames.com{endpoint}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Riot-Token", _apiKey);

        var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        return ((int)res.StatusCode, body);
    }
}
