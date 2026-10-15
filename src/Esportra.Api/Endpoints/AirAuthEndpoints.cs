using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Esportra.Api.Middleware;

namespace Esportra.Api.Endpoints;

public static class AirAuthEndpoints
{
    public static void MapAirAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/air/auth/refresh", Refresh).WithMetadata(new RateLimitPolicyMetadata("auth"));
    }
    private static async Task<IResult> Refresh(AirRefreshRequest input, IConfiguration configuration, IHttpClientFactory clients, HttpContext ctx)
    {
        if (string.IsNullOrWhiteSpace(input.RefreshToken) || input.RefreshToken.Length > 8192) return Results.BadRequest(new { error = "Sign in again to renew the account session." });
        var url = configuration["Supabase:Url"]?.TrimEnd('/'); var key = configuration["Supabase:AnonKey"] ?? configuration["Supabase:ServiceKey"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return Results.StatusCode(503);
        using var request = new HttpRequestMessage(HttpMethod.Post, url + "/auth/v1/token?grant_type=refresh_token") { Content = JsonContent.Create(new { refresh_token = input.RefreshToken }) };
        request.Headers.Add("apikey", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var response = await clients.CreateClient().SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return Results.Unauthorized();
            var tokens = await response.Content.ReadFromJsonAsync<AirAuthTokens>(cancellationToken: timeout.Token);
            return tokens is { AccessToken.Length: > 0, RefreshToken.Length: > 0, ExpiresIn: > 0 } ? Results.Ok(tokens) : Results.Unauthorized();
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return Results.StatusCode(503); }
    }
}
public sealed record AirRefreshRequest(string RefreshToken);
public sealed record AirAuthTokens([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("refresh_token")] string RefreshToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);
