using System.Net.Http.Headers;

namespace Esportra.Api.ScoreboardOcr;

/// <summary>
/// Stores parsed scoreboard screenshots in Supabase Storage (tournaments.results) with the service key,
/// under a server-chosen path so the evidence the opponent sees is exactly the image that was read.
/// </summary>
public sealed class ScoreboardScreenshotStore(IHttpClientFactory httpFactory, IConfiguration config, ILogger<ScoreboardScreenshotStore> logger)
{
    public const string Bucket = "tournaments.results";

    public static string BuildPath(Guid matchId, Guid parseId, string extension) =>
        $"matches/{matchId}/ocr/{parseId}{extension}";

    public string PublicUrl(string path) => $"{SupabaseUrl()}/storage/v1/object/public/{Bucket}/{path}";

    /// <summary>Uploads the image; returns false (and logs) when storage rejects it.</summary>
    public async Task<bool> UploadAsync(string path, byte[] data, string contentType, CancellationToken ct)
    {
        var serviceKey = config["Supabase:ServiceKey"]
            ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");
        var client = httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SupabaseUrl()}/storage/v1/object/{Bucket}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
        request.Headers.Add("apikey", serviceKey);
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return true;
        logger.LogError("Scoreboard screenshot upload failed: {Status}", (int)response.StatusCode);
        return false;
    }

    private string SupabaseUrl() =>
        config["Supabase:Url"]?.TrimEnd('/') ?? throw new InvalidOperationException("Supabase:Url not configured");
}
