using System.Net.Http.Headers;
using System.Text.Json;

namespace Esportra.Api.Services;

public sealed class MfaFactorCleanupService(
    HttpClient http,
    IConfiguration configuration)
{
    private readonly string _supabaseUrl = configuration["Supabase:Url"]?.TrimEnd('/')
        ?? throw new InvalidOperationException("Supabase:Url is required.");
    private readonly string _serviceKey = configuration["Supabase:ServiceKey"]
        ?? throw new InvalidOperationException("Supabase:ServiceKey is required.");

    public async Task<IReadOnlyList<string>> ListFactorIdsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/users/{userId}/factors");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        return document.RootElement.EnumerateArray()
            .Where(factor => factor.TryGetProperty("id", out _))
            .Select(factor => factor.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();
    }

    public async Task DeleteFactorAsync(
        string userId,
        string factorId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"/users/{userId}/factors/{factorId}");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{_supabaseUrl}/auth/v1/admin{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceKey);
        request.Headers.Add("apikey", _serviceKey);
        return request;
    }
}