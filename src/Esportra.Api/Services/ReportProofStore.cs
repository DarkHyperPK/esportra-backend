using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Esportra.Api.Services;

/// <summary>Private storage for Squad Up report proof screenshots (service key only; viewed via signed URLs).</summary>
public interface IReportProofStore
{
    Task<bool> UploadAsync(string path, byte[] data, string contentType, CancellationToken ct);

    /// <summary>Best effort: failures are logged, never thrown.</summary>
    Task DeleteAsync(IReadOnlyCollection<string> paths, CancellationToken ct);

    /// <summary>A time-limited URL for one stored image, or null when it cannot be created.</summary>
    Task<string?> CreateSignedUrlAsync(string path, TimeSpan lifetime, CancellationToken ct);
}

public sealed class ReportProofStore(IHttpClientFactory httpFactory, IConfiguration config, ILogger<ReportProofStore> logger)
    : IReportProofStore
{
    public const string Bucket = "squad-up.report-proof";

    public static string BuildPath(Guid reportId, string extension) => $"reports/{reportId}/{Guid.NewGuid()}{extension}";

    public async Task<bool> UploadAsync(string path, byte[] data, string contentType, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, $"{SupabaseUrl()}/storage/v1/object/{Bucket}/{path}");
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await httpFactory.CreateClient().SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return true;
        logger.LogError("Report proof upload failed: {Status}", (int)response.StatusCode);
        return false;
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) return;
        try
        {
            using var request = Authorized(HttpMethod.Delete, $"{SupabaseUrl()}/storage/v1/object/{Bucket}");
            request.Content = new StringContent(JsonSerializer.Serialize(new { prefixes = paths }), Encoding.UTF8, "application/json");
            using var response = await httpFactory.CreateClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Report proof cleanup failed: {Status}", (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Report proof cleanup failed");
        }
    }

    public async Task<string?> CreateSignedUrlAsync(string path, TimeSpan lifetime, CancellationToken ct)
    {
        try
        {
            using var request = Authorized(HttpMethod.Post, $"{SupabaseUrl()}/storage/v1/object/sign/{Bucket}/{path}");
            request.Content = new StringContent($$"""{"expiresIn": {{(int)lifetime.TotalSeconds}}}""", Encoding.UTF8, "application/json");
            using var response = await httpFactory.CreateClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var signed = doc.RootElement.GetProperty("signedURL").GetString();
            return signed is null ? null : $"{SupabaseUrl()}/storage/v1{signed}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Report proof signed URL failed");
            return null;
        }
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
