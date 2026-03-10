using System.Security.Claims;
using Microsoft.Extensions.Caching.Distributed;

namespace Esportra.Api.Middleware;

/// <summary>
/// Sliding-window rate limiter backed by Redis (IDistributedCache).
/// Applies per-IP for anonymous requests and per-user for authenticated ones.
/// Returns 429 Too Many Requests when the limit is exceeded.
///
/// Defaults: 100 requests per 60-second window (configurable via appsettings).
/// </summary>
public sealed class RateLimitMiddleware(
    RequestDelegate next,
    IDistributedCache cache,
    IConfiguration config,
    ILogger<RateLimitMiddleware> logger)
{
    private readonly int _maxRequests = config.GetValue("RateLimit:MaxRequests", 100);
    private readonly int _windowSeconds = config.GetValue("RateLimit:WindowSeconds", 60);

    // Paths that are exempt from rate limiting
    private static readonly HashSet<string> ExemptPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/api/analytics/events", // fire-and-forget telemetry
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip rate limiting for exempt paths and SignalR negotiation
        if (ExemptPaths.Contains(path) || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var clientKey = GetClientKey(context);
        var cacheKey = $"ratelimit:{clientKey}";

        try
        {
            var counterBytes = await cache.GetAsync(cacheKey, context.RequestAborted);
            var count = counterBytes is not null ? BitConverter.ToInt32(counterBytes, 0) : 0;

            if (count >= _maxRequests)
            {
                logger.LogWarning("Rate limit exceeded for {ClientKey} ({Count}/{Max})", clientKey, count, _maxRequests);

                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = _windowSeconds.ToString();
                context.Response.Headers["X-RateLimit-Limit"] = _maxRequests.ToString();
                context.Response.Headers["X-RateLimit-Remaining"] = "0";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Too many requests. Please try again later.",
                    retryAfterSeconds = _windowSeconds,
                });
                return;
            }

            // Increment counter
            var newCount = count + 1;
            var newBytes = BitConverter.GetBytes(newCount);
            await cache.SetAsync(cacheKey, newBytes, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_windowSeconds),
            }, context.RequestAborted);

            // Add rate limit headers
            context.Response.Headers["X-RateLimit-Limit"] = _maxRequests.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, _maxRequests - newCount).ToString();
        }
        catch (Exception ex)
        {
            // If Redis is down, allow the request through (fail-open)
            logger.LogError(ex, "Rate limiter Redis error for {ClientKey}", clientKey);
        }

        await next(context);
    }

    private static string GetClientKey(HttpContext context)
    {
        // Authenticated: rate limit per user
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? context.User.FindFirstValue("sub");
            if (!string.IsNullOrEmpty(userId))
                return $"user:{userId}";
        }

        // Anonymous: rate limit per IP
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }
}

public static class RateLimitMiddlewareExtensions
{
    public static IApplicationBuilder UseRateLimit(this IApplicationBuilder app)
        => app.UseMiddleware<RateLimitMiddleware>();
}
