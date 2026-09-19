using System.Collections.Concurrent;
using System.Security.Claims;
using StackExchange.Redis;

namespace Esportra.Api.Middleware;

// ── Configuration models ────────────────────────────────────────────────────

public sealed record RateLimitPolicyConfig
{
    public int MaxRequests { get; init; } = 200;
    public int WindowSeconds { get; init; } = 60;
}

public sealed record RateLimitOptions
{
    public bool Enabled { get; init; } = true;

    public Dictionary<string, RateLimitPolicyConfig> Policies { get; init; } = new()
    {
        ["default"] = new() { MaxRequests = 200, WindowSeconds = 60 },
        ["relaxed"] = new() { MaxRequests = 500, WindowSeconds = 60 },
        ["public"] = new() { MaxRequests = 60, WindowSeconds = 60 },
        ["strict"] = new() { MaxRequests = 30, WindowSeconds = 60 },
        ["auth"] = new() { MaxRequests = 10, WindowSeconds = 60 },
        ["admin"] = new() { MaxRequests = 1000, WindowSeconds = 60 },
        ["sponsorAnalytics"] = new() { MaxRequests = 120, WindowSeconds = 60 },
    };

    public HashSet<string> ExemptPaths { get; init; } = ["/health", "/api/analytics/events"];
    public List<string> ExemptPrefixes { get; init; } = ["/hubs/"];

    /// <summary>
    /// Path prefix → policy name. First match wins.
    /// More specific prefixes should come before broader ones.
    /// </summary>
    public Dictionary<string, string> PathPolicies { get; init; } = new()
    {
        ["/api/admin/"] = "admin",
        ["/api/auth/"] = "auth",
    };
}

/// <summary>
/// Marker applied via <c>.WithMetadata(new RateLimitPolicyMetadata("strict"))</c>
/// on endpoint groups to select a named rate-limit policy.
/// </summary>
public sealed record RateLimitPolicyMetadata(string PolicyName);

// ── Middleware ───────────────────────────────────────────────────────────────

/// <summary>
/// Fixed-window rate limiter with tiered policies.
///
/// Primary: Atomic Redis Lua script (INCR + conditional EXPIRE).
/// Fallback: In-memory ConcurrentDictionary when Redis is unavailable.
///
/// Policies are resolved from endpoint metadata → HTTP method heuristic → "default".
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConnectionMultiplexer _redis;
    private readonly RateLimitOptions _options;
    private readonly ILogger<RateLimitMiddleware> _logger;

    // Lua script: atomic increment + conditional expire. Returns current count.
    private static readonly LuaScript _luaScript = LuaScript.Prepare(
        """
        local count = redis.call('INCR', @key)
        if count == 1 then
            redis.call('EXPIRE', @key, @ttl)
        end
        return count
        """);

    // In-memory fallback when Redis is down
    private static readonly ConcurrentDictionary<string, (int Count, long Bucket)> _memoryCounters = new();
    private static readonly Timer _cleanupTimer = new(_ =>
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var kvp in _memoryCounters)
        {
            // Remove entries from old buckets (> 2 windows stale)
            if (now - kvp.Value.Bucket > 180)
                _memoryCounters.TryRemove(kvp.Key, out var _unused);
        }
    }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

    public RateLimitMiddleware(
        RequestDelegate next,
        IConnectionMultiplexer redis,
        IConfiguration config,
        ILogger<RateLimitMiddleware> logger)
    {
        _next = next;
        _redis = redis;
        _logger = logger;

        _options = new RateLimitOptions();
        config.GetSection("RateLimit").Bind(_options);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";

        if (IsExempt(path))
        {
            await _next(context);
            return;
        }

        var policy = ResolvePolicy(context);
        var config = _options.Policies.GetValueOrDefault(policy)
                       ?? _options.Policies.GetValueOrDefault("default")
                       ?? new RateLimitPolicyConfig();
        var clientKey = GetClientKey(context);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bucket = now / config.WindowSeconds;
        var cacheKey = $"rl:{policy}:{clientKey}:{bucket}";

        // Seconds remaining until current window resets
        var windowEnd = (bucket + 1) * config.WindowSeconds;
        var retryAfter = (int)Math.Max(1, windowEnd - now);

        int count;
        try
        {
            var db = _redis.GetDatabase();
            var result = await db.ScriptEvaluateAsync(_luaScript, new { key = (RedisKey)cacheKey, ttl = config.WindowSeconds });
            count = (int)result;
        }
        catch (Exception ex)
        {
            // Redis unavailable — use in-memory fallback (not fail-open)
            _logger.LogWarning(ex, "Redis unavailable for rate limiting, using in-memory fallback");
            count = IncrementMemory(cacheKey, bucket);
        }

        // Always set rate limit headers
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-RateLimit-Limit"] = config.MaxRequests.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, config.MaxRequests - count).ToString();
            context.Response.Headers["X-RateLimit-Reset"] = windowEnd.ToString();
            context.Response.Headers["X-RateLimit-Policy"] = policy;
            return Task.CompletedTask;
        });

        if (count > config.MaxRequests)
        {
            _logger.LogWarning("Rate limit exceeded for {ClientKey} policy={Policy} ({Count}/{Max})",
                clientKey, policy, count, config.MaxRequests);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = retryAfter.ToString();
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Too many requests. Please try again later.",
                retryAfterSeconds = retryAfter,
                policy,
            });
            return;
        }

        await _next(context);
    }

    private bool IsExempt(string path)
    {
        if (_options.ExemptPaths.Contains(path)) return true;
        foreach (var prefix in _options.ExemptPrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Resolve the rate-limit policy name for this request.
    /// Priority: endpoint metadata → path prefix match → HTTP method heuristic → "default".
    /// </summary>
    private string ResolvePolicy(HttpContext context)
    {
        // 1. Explicit endpoint metadata tag
        var endpoint = context.GetEndpoint();
        var meta = endpoint?.Metadata.GetMetadata<RateLimitPolicyMetadata>();
        if (meta is not null) return meta.PolicyName;

        // 2. Path prefix match (configured in appsettings)
        var path = context.Request.Path.Value ?? "";
        foreach (var (prefix, policy) in _options.PathPolicies)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return policy;

        // 3. HTTP method heuristic
        return context.Request.Method.ToUpperInvariant() switch
        {
            "GET" or "HEAD" or "OPTIONS" => "relaxed",
            _ => "default",
        };
    }

    private static string GetClientKey(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? context.User.FindFirstValue("sub");
            if (!string.IsNullOrEmpty(userId))
                return $"u:{userId}";
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }

    private static int IncrementMemory(string key, long bucket)
    {
        var updated = _memoryCounters.AddOrUpdate(
            key,
            _ => (1, bucket),
            (_, existing) => existing.Bucket == bucket
                ? (existing.Count + 1, bucket)
                : (1, bucket));
        return updated.Count;
    }
}

public static class RateLimitMiddlewareExtensions
{
    public static IApplicationBuilder UseRateLimit(this IApplicationBuilder app)
        => app.UseMiddleware<RateLimitMiddleware>();
}
