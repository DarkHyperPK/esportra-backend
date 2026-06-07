using System.Net.Http.Headers;

namespace Esportra.Api.Services;

public sealed class GameCatalogAssetService(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<GameCatalogAssetService> logger)
{
    public const string BucketName = "game-assets";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg",
    };

    private const long MaxFileSizeBytes = 5 * 1024 * 1024;

    public async Task<string> UploadGameLogoAsync(string slug, IFormFile file, CancellationToken ct = default)
    {
        if (file.Length == 0)
            throw new GameCatalogValidationException("No file provided.");

        if (file.Length > MaxFileSizeBytes)
            throw new GameCatalogValidationException("Logo file exceeds 5MB limit.");

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            throw new GameCatalogValidationException("Logo must be jpg, png, gif, webp, or svg.");

        var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Supabase:Url not configured");
        var serviceKey = config["Supabase:ServiceKey"]
            ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

        var safeSlug = slug.Trim().ToLowerInvariant().Replace(' ', '-');
        var storagePath = $"logos/{safeSlug}{ext.ToLowerInvariant()}";

        var client = httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
        client.DefaultRequestHeaders.Add("apikey", serviceKey);
        client.DefaultRequestHeaders.Add("x-upsert", "true");

        await using var stream = file.OpenReadStream();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

        var uploadUrl = $"{supabaseUrl}/storage/v1/object/{BucketName}/{storagePath}";
        var response = await client.PostAsync(uploadUrl, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Game logo upload failed: {Url} {Status} {Body}", uploadUrl, response.StatusCode, errorBody);
            throw new GameCatalogValidationException("Logo upload failed.");
        }

        return $"{supabaseUrl}/storage/v1/object/public/{BucketName}/{storagePath}";
    }
}
