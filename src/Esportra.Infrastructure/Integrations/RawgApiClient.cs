using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace Esportra.Infrastructure.Integrations;

/// <summary>
/// Proxies requests to the RAWG API.
/// Ported from supabase/functions/rawg-proxy/index.ts.
/// Config: Rawg:ApiKey
/// </summary>
public sealed class RawgApiClient(HttpClient http, IConfiguration config)
{
    private readonly string _apiKey = config["Rawg:ApiKey"] ?? string.Empty;

    /// <summary>Search games by name. Returns raw JSON string from RAWG.</summary>
    public async Task<string> SearchGamesAsync(string query, CancellationToken ct = default)
    {
        var url = $"https://api.rawg.io/api/games?key={_apiKey}&search={Uri.EscapeDataString(query)}&page_size=10";
        var res = await http.GetAsync(url, ct);
        return await res.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Get screenshots for a game by RAWG game ID. Returns raw JSON string.</summary>
    public async Task<string> GetScreenshotsAsync(int gameId, CancellationToken ct = default)
    {
        var url = $"https://api.rawg.io/api/games/{gameId}/screenshots?key={_apiKey}";
        var res = await http.GetAsync(url, ct);
        return await res.Content.ReadAsStringAsync(ct);
    }
}
