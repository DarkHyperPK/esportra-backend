using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Esportra.Infrastructure.Integrations;

/// <summary>
/// Fetches game artwork/cover/video assets from IGDB (via Twitch OAuth).
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

    // Known IGDB game IDs for exact matches (avoids fuzzy search returning wrong game).
    // Verified IDs from IGDB — use `where id = X` for precise results.
    private static readonly Dictionary<string, int> KnownGameIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Valorant"]           = 126459,
        ["Counter-Strike 2"]   = 227155,
        ["League of Legends"]  = 115,
        ["Dota 2"]             = 1942,
        ["Fortnite"]           = 1905,
        ["Apex Legends"]       = 114795,
        ["PUBG"]               = 131572, // PUBG: Battlegrounds
        ["Rocket League"]      = 7346,
        ["Tekken 8"]           = 216716,
        ["EA FC"]              = 265036, // EA Sports FC 25
    };

    // Alternate game names that map to the same IGDB IDs
    private static readonly Dictionary<string, string> GameNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CS2"]                          = "Counter-Strike 2",
        ["CSGO"]                         = "Counter-Strike 2",
        ["Counter-Strike: Global Offensive"] = "Counter-Strike 2",
        ["LoL"]                          = "League of Legends",
        ["PUBG: Battlegrounds"]          = "PUBG",
        ["PlayerUnknown's Battlegrounds"]= "PUBG",
        ["EA Sports FC"]                 = "EA FC",
        ["EA Sports FC 25"]              = "EA FC",
        ["FIFA"]                         = "EA FC",
    };

    /// <summary>Search for a game and return all artwork, screenshot, and video assets.</summary>
    public async Task<IgdbGameAssets?> GetGameAssetsAsync(string gameName, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);

        // Resolve aliases to canonical name
        var canonicalName = GameNameAliases.TryGetValue(gameName.Trim(), out var alias) ? alias : gameName.Trim();

        string body;
        if (KnownGameIds.TryGetValue(canonicalName, out var knownId))
        {
            body = $"""
                where id = {knownId};
                fields name, artworks.image_id, cover.image_id, screenshots.image_id, videos.video_id, videos.name;
                limit 1;
                """;
        }
        else
        {
            body = $"""
                search "{EscapeIgdb(canonicalName)}";
                fields name, artworks.image_id, cover.image_id, screenshots.image_id, videos.video_id, videos.name;
                limit 1;
                """;
        }

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
        var banners = new List<string>();
        string? cover = null;
        var videos = new List<IgdbVideo>();

        // Artworks — cinematic, wide images (best for banners)
        if (game.TryGetProperty("artworks", out var artworks))
        {
            foreach (var art in artworks.EnumerateArray())
            {
                var imageId = art.GetProperty("image_id").GetString();
                if (imageId is not null)
                    banners.Add($"https://images.igdb.com/igdb/image/upload/t_1080p/{imageId}.jpg");
            }
        }

        // Screenshots — in-game captures (fallback / extra carousel images)
        if (game.TryGetProperty("screenshots", out var screenshots))
        {
            foreach (var ss in screenshots.EnumerateArray())
            {
                var imageId = ss.GetProperty("image_id").GetString();
                if (imageId is not null)
                    banners.Add($"https://images.igdb.com/igdb/image/upload/t_1080p/{imageId}.jpg");
            }
        }

        // Cover
        if (game.TryGetProperty("cover", out var coverProp))
        {
            var imageId = coverProp.GetProperty("image_id").GetString();
            if (imageId is not null)
                cover = $"https://images.igdb.com/igdb/image/upload/t_cover_big/{imageId}.jpg";
        }

        // Videos — YouTube trailer IDs
        if (game.TryGetProperty("videos", out var vids))
        {
            foreach (var v in vids.EnumerateArray())
            {
                var videoId = v.GetProperty("video_id").GetString();
                var name = v.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (videoId is not null)
                    videos.Add(new IgdbVideo(videoId, name));
            }
        }

        if (banners.Count == 0 && cover is null) return null;

        // Extract matched game name from IGDB response
        var matchedName = game.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

        return new IgdbGameAssets(
            banners.Count > 0 ? banners : (cover is not null ? [cover] : []),
            cover,
            videos,
            matchedName
        );
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
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
    }

    private static string EscapeIgdb(string s) => s.Replace("\"", "\\\"");
}

public sealed record IgdbVideo(string VideoId, string? Name);
public sealed record IgdbGameAssets(List<string> Banners, string? Cover, List<IgdbVideo> Videos, string? MatchedName = null);
