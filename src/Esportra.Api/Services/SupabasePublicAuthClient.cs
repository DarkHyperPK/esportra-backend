using System.Net.Http.Json;
using Esportra.Api.Auth;
using Microsoft.Extensions.Options;

namespace Esportra.Api.Services;

public interface ISupabasePublicAuthClient
{
    Task RequestPasswordRecoveryAsync(string email, RecoveryPortal portal, CancellationToken cancellationToken);
}

public sealed class SupabasePublicAuthClient(
    HttpClient http,
    IConfiguration configuration,
    IOptions<RecoveryOptions> recoveryOptions,
    ILogger<SupabasePublicAuthClient> logger) : ISupabasePublicAuthClient
{
    private readonly string _supabaseUrl = configuration["Supabase:Url"]?.TrimEnd('/')
        ?? throw new InvalidOperationException("Supabase:Url is required.");
    private readonly string _anonKey = configuration["Supabase:AnonKey"]
        ?? throw new InvalidOperationException("Supabase:AnonKey is required.");

    public async Task RequestPasswordRecoveryAsync(
        string email,
        RecoveryPortal portal,
        CancellationToken cancellationToken)
    {
        var redirectTo = portal == RecoveryPortal.Main
            ? recoveryOptions.Value.MainRedirectUrl
            : recoveryOptions.Value.PartnerRedirectUrl;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/auth/v1/recover")
        {
            Content = JsonContent.Create(new { email, redirect_to = redirectTo }),
        };
        request.Headers.Add("apikey", _anonKey);

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Supabase password recovery request failed with status {StatusCode}", (int)response.StatusCode);
        }
    }
}