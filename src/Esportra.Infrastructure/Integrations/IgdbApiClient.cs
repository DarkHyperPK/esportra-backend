using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Esportra.Infrastructure.Integrations;

/// <summary>
/// Fetches game artwork/cover images from IGDB (via Twitch OAuth).
/// Config: Igdb:ClientId, Igdb:ClientSecret
/// </summary>
public sealed class IgdbApiClient
{
    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public IgdbApiClient(HttpClient http, IConfiguration config)
    {
        _http = http;
        _clientId = config["Igdb:ClientId"] ?? string.Empty;
        _clientSecret = config["Igdb:ClientSecret"] ?? string.Empty;
    }

    /// <summary>Search for a game and return its artwork/cover URLs.</summary>
    public async Task<IgdbGameImages?> GetGameImagesAsync(string gameName, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);

        // Search for the game and get artworks + cover in one call
        var body = $"""
            search "{EscapeIgdb(gameName)}";
            fields name, artworks.image_id, cover.image_id, screenshots.image_id;
            limit 1;
            """;

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("Client-ID", _clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var res = await _http.SendAsync(request, ct);
        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.GetArrayLength() == 0) return null;

        var game = root[0];
        string? banner = null;
        string? cover = null;

        // Prefer artworks for banners (wider, more cinematic)
        if (game.TryGetProperty("artworks", out var artworks) && artworks.GetArrayLength() > 0)
        {
            var imageId = artworks[0].GetProperty("image_id").GetString();
            banner = $"https://images.igdb.com/igdb/image/upload/t_1080p/{imageId}.jpg";
        }

        // Fallback to screenshots
        if (banner is null && game.TryGetProperty("screenshots", out var screenshots) && screenshots.GetArrayLength() > 0)
        {
            var imageId = screenshots[0].GetProperty("image_id").GetString();
            banner = $"https://images.igdb.com/igdb/image/upload/t_1080p/{imageId}.jpg";
        }

        // Cover
        if (game.TryGetProperty("cover", out var coverProp))
        {
            var imageId = coverProp.GetProperty("image_id").GetString();
            cover = $"https://images.igdb.com/igdb/image/upload/t_cover_big/{imageId}.jpg";
        }

        // If no artwork or screenshots, use cover as banner too
        banner ??= cover;

        return banner is null ? null : new IgdbGameImages(banner, cover);
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTime.UtcNow < _tokenExpiry) return;

        var url = $"https://id.twitch.tv/oauth2/token?client_id={_clientId}&client_secret={_clientSecret}&grant_type=client_credentials";
        var res = await _http.PostAsync(url, null, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60); // Refresh 1 min early
    }

    private static string EscapeIgdb(string s) => s.Replace("\"", "\\\"");
}

public sealed record IgdbGameImages(string Banner, string? Cover);
