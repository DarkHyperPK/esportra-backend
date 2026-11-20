using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Esportra.Api.Services;

/// <summary>Air game packs in private storage: one manifest per game and its files, read with the service key.</summary>
public interface IGamePackStore
{
    /// <summary>The game's manifest.json, or null when the game has no pack.</summary>
    Task<string?> ReadManifestAsync(string game, CancellationToken ct);

    /// <summary>Short-lived download URLs for the given object paths (path → URL); paths that failed are left out.</summary>
    Task<IReadOnlyDictionary<string, string>> SignAsync(IReadOnlyCollection<string> paths, TimeSpan lifetime, CancellationToken ct);
}

public sealed class GamePackStore(IHttpClientFactory httpFactory, IConfiguration config, IMemoryCache cache, ILogger<GamePackStore> logger)
    : IGamePackStore
{
    public const string Bucket = "air.game-packs";

    public async Task<string?> ReadManifestAsync(string game, CancellationToken ct)
    {
        var key = $"air-game-pack:{game}";
        if (cache.TryGetValue(key, out string? cached)) return cached;
        using var request = Authorized(HttpMethod.Get, $"{SupabaseUrl()}/storage/v1/object/{Bucket}/{game}/manifest.json");
        using var response = await httpFactory.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is not (400 or 404)) logger.LogWarning("Game pack manifest read failed: {Status}", (int)response.StatusCode);
            return null;
        }
        var json = await response.Content.ReadAsStringAsync(ct);
        cache.Set(key, json, TimeSpan.FromMinutes(5));
        return json;
    }

    public async Task<IReadOnlyDictionary<string, string>> SignAsync(IReadOnlyCollection<string> paths, TimeSpan lifetime, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (paths.Count == 0) return result;
        using var request = Authorized(HttpMethod.Post, $"{SupabaseUrl()}/storage/v1/object/sign/{Bucket}");
        request.Content = new StringContent(JsonSerializer.Serialize(new { expiresIn = (int)lifetime.TotalSeconds, paths }), Encoding.UTF8, "application/json");
        using var response = await httpFactory.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Game pack signing failed: {Status}", (int)response.StatusCode);
            return result;
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
            var signed = item.TryGetProperty("signedURL", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (path is not null && signed is not null) result[path] = $"{SupabaseUrl()}/storage/v1{signed}";
        }
        return result;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var serviceKey = config["Supabase:ServiceKey"] ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
        request.Headers.Add("apikey", serviceKey);
        return request;
    }

    private string SupabaseUrl() =>
        config["Supabase:Url"]?.TrimEnd('/') ?? throw new InvalidOperationException("Supabase:Url not configured");
}
