using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.DeveloperApi;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Middleware;

/// <summary>
/// Handles API key authentication for /api/v1/* requests.
/// If no X-Api-Key header is present, passes through unchanged (JWT path).
/// On successful auth, populates ApiKeyContext and a synthetic UserContext in HttpContext.Items.
/// </summary>
public sealed class ApiKeyAuthMiddleware(
    RequestDelegate next,
    HybridCache cache,
    IDbConnectionFactory db,
    ILogger<ApiKeyAuthMiddleware> logger)
{
    private static readonly Regex s_keyFormat =
        new(@"^ek_(live|sand)_[0-9a-f]{64}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public async Task InvokeAsync(HttpContext context)
    {
        var rawKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(rawKey))
        {
            await next(context);
            return;
        }

        if (!s_keyFormat.IsMatch(rawKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_api_key" });
            return;
        }

        var keyHash = HashKey(rawKey);
        var record = await cache.GetOrCreateAsync(
            $"api-key-ctx:{keyHash}",
            async ct => await LookupKeyAsync(keyHash, ct),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) });

        var validationResult = ValidateRecord(record);
        if (validationResult is not null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = validationResult });
            return;
        }

        var apiKeyCtx = BuildApiKeyContext(record!);
        SetHttpContextItems(context, apiKeyCtx);

        RegisterAuditCallback(context, apiKeyCtx, logger);

        await next(context);
    }

    private async Task<ApiKeyRecord?> LookupKeyAsync(string keyHash, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var svc = new ApiKeyValidationService(db);
        return await svc.FindByHashAsync(keyHash, ct);
    }

    private static string? ValidateRecord(ApiKeyRecord? record)
    {
        if (record is null) return "invalid_api_key";
        if (record.Status == "revoked") return "api_key_revoked";
        if (record.GracePeriodUntil is not null && record.GracePeriodUntil <= DateTimeOffset.UtcNow)
            return "api_key_expired";
        return null;
    }

    private static ApiKeyContext BuildApiKeyContext(ApiKeyRecord record) =>
        new()
        {
            KeyId = record.Id,
            OrgId = record.OrganizationId,
            OwnerId = record.OwnerId,
            Scopes = record.Scopes,
            Environment = record.Environment,
            RateLimitPerMin = record.RateLimitPerMin,
            GracePeriodUntil = record.GracePeriodUntil,
            KeyPrefix = record.KeyPrefix,
        };

    private static void SetHttpContextItems(HttpContext context, ApiKeyContext apiKeyCtx)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, apiKeyCtx.OwnerId.ToString()),
            new Claim("api_key_id", apiKeyCtx.KeyId.ToString()),
            new Claim("org_id", apiKeyCtx.OrgId.ToString()),
        ], "ApiKey");

        context.User = new ClaimsPrincipal(identity);

        var userCtx = new UserContext
        {
            UserId = apiKeyCtx.OwnerId.ToString(),
            Email = string.Empty,
            Roles = [],
            AdminRoles = [],
            Permissions = [],
        };

        context.Items["ApiKeyContext"] = apiKeyCtx;
        context.Items["UserContext"] = userCtx;
        context.Items["ApiKeyAuthenticated"] = true;
        context.Items["ApiKeyId"] = apiKeyCtx.KeyId.ToString();
        context.Items["DeveloperApiRateLimit"] = apiKeyCtx.RateLimitPerMin;
    }

    private static void RegisterAuditCallback(
        HttpContext context,
        ApiKeyContext apiKeyCtx,
        ILogger logger)
    {
        var startTime = Stopwatch.GetTimestamp();
        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? string.Empty;
        var ip = context.Connection.RemoteIpAddress?.ToString();

        context.Response.OnCompleted(async () =>
        {
            try
            {
                var elapsedMs = (int)Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
                var statusCode = context.Response.StatusCode;
                var auditSvc = context.RequestServices.GetRequiredService<IDeveloperApiAuditService>();
                await auditSvc.RecordAsync(new ApiKeyAuditEntry(
                    apiKeyCtx.KeyId,
                    apiKeyCtx.OrgId,
                    method,
                    path,
                    statusCode,
                    elapsedMs,
                    ip,
                    statusCode == 429));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[ApiKeyAudit] Failed to record audit entry for key {KeyId}", apiKeyCtx.KeyId);
            }
        });
    }

    internal static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public static class ApiKeyAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app)
        => app.UseMiddleware<ApiKeyAuthMiddleware>();
}
