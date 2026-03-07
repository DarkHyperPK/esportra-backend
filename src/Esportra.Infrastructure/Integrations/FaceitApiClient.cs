using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Esportra.Infrastructure.Integrations;

/// <summary>
/// Authenticated proxy + OAuth client for Faceit API.
/// Ported from supabase/functions/faceit-match-proxy and faceit-oauth.
/// Config: Faceit:ApiKey, Faceit:ClientId, Faceit:ClientSecret
/// </summary>
public sealed class FaceitApiClient(HttpClient http, IConfiguration config)
{
    private readonly string _apiKey       = config["Faceit:ApiKey"]       ?? string.Empty;
    private readonly string _clientId     = config["Faceit:ClientId"]     ?? string.Empty;
    private readonly string _clientSecret = config["Faceit:ClientSecret"] ?? string.Empty;

    private static readonly string[] AllowedPathPrefixes =
    [
        "/players",
        "/matches/",
        "/games",
    ];

    // ── Proxy ────────────────────────────────────────────────────────────────

    public async Task<(int StatusCode, string Body)> ProxyAsync(
        string endpoint, CancellationToken ct = default)
    {
        // Strip query string before allowlist check
        var path = endpoint.Split('?')[0].TrimEnd('/');

        if (!AllowedPathPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return (403, """{"error":"Endpoint not allowlisted"}""");

        var url = $"https://open.faceit.com/data/v4{endpoint}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        return ((int)res.StatusCode, body);
    }

    // ── OAuth token exchange ─────────────────────────────────────────────────

    public async Task<FaceitTokenResponse?> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.faceit.com/auth/v1/oauth/token");
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}")));

        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"]  = redirectUri,
        });

        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;

        return await res.Content.ReadFromJsonAsync<FaceitTokenResponse>(ct);
    }
}

public sealed record FaceitTokenResponse(
    string access_token,
    string refresh_token,
    int    expires_in);
