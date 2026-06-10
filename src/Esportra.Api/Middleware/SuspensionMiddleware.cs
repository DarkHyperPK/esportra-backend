using System.Data;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Middleware;

/// <summary>
/// Blocks suspended users from authenticated API routes except an allowlist
/// (e.g. /api/profiles/me so the client can read suspension status).
/// Auto-clears expired timed suspensions.
/// </summary>
public sealed class SuspensionMiddleware(
    RequestDelegate next,
    HybridCache cache,
    ILogger<SuspensionMiddleware> logger)
{
    private static readonly string[] AllowlistedExactPaths =
    [
        "/api/profiles/me",
    ];

    private static readonly string[] AllowlistedPrefixes =
    [
        "/health",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (context.User.Identity?.IsAuthenticated != true || IsAllowlisted(path))
        {
            await next(context);
            return;
        }

        var userCtx = context.Items["UserContext"] as UserContext;
        if (userCtx is null || !Guid.TryParse(userCtx.UserId, out var userId))
        {
            await next(context);
            return;
        }

        var status = await cache.GetOrCreateAsync(
            $"user-suspension:{userId}",
            async ct => await FetchSuspensionStatusAsync(context, userId, ct),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) });

        if (status.IsActive)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = status.Reason ?? "Your account is suspended.",
                code = "account_suspended",
            });
            return;
        }

        await next(context);
    }

    private static bool IsAllowlisted(string path)
    {
        if (AllowlistedExactPaths.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase)))
            return true;

        return AllowlistedPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<SuspensionStatus> FetchSuspensionStatusAsync(
        HttpContext context,
        Guid userId,
        CancellationToken ct)
    {
        var db = context.RequestServices.GetRequiredService<IDbConnectionFactory>();
        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<SuspensionRow>(
            """
            SELECT is_suspended, suspension_reason, suspension_until
            FROM public.profiles
            WHERE id = @userId
            """,
            new { userId });

        if (row is null || !row.IsSuspended)
            return SuspensionStatus.Inactive;

        if (row.SuspensionUntil is not null && row.SuspensionUntil <= DateTime.UtcNow)
        {
            await conn.ExecuteAsync(
                """
                UPDATE public.profiles
                SET is_suspended = false,
                    suspension_reason = null,
                    suspension_type = null,
                    suspension_until = null,
                    updated_at = NOW()
                WHERE id = @userId
                """,
                new { userId });

            try
            {
                await cache.RemoveAsync($"user-suspension:{userId}", ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Suspension] Failed to evict cache for expired suspension {UserId}", userId);
            }

            return SuspensionStatus.Inactive;
        }

        return new SuspensionStatus(true, row.SuspensionReason);
    }

    private sealed record SuspensionRow(bool IsSuspended, string? SuspensionReason, DateTime? SuspensionUntil);

    private sealed record SuspensionStatus(bool IsActive, string? Reason = null)
    {
        public static SuspensionStatus Inactive { get; } = new(false);
    }
}

public static class SuspensionMiddlewareExtensions
{
    public static IApplicationBuilder UseSuspensionGate(this IApplicationBuilder app)
        => app.UseMiddleware<SuspensionMiddleware>();
}
