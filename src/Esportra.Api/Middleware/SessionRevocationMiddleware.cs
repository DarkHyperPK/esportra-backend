using System.Security.Claims;
using Dapper;
using Esportra.Infrastructure.Database;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Middleware;

public sealed class SessionRevocationMiddleware(RequestDelegate next, HybridCache cache, ILogger<SessionRevocationMiddleware> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        var userIdClaim = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? ctx.User.FindFirstValue("sub");

        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            await next(ctx);
            return;
        }

        var isRevoked = await cache.GetOrCreateAsync(
            $"session-revoked:{userId}",
            async cancel =>
            {
                try
                {
                    using var conn = db.CreateConnection();
                    var count = await conn.ExecuteScalarAsync<int>(
                        """
                        SELECT COUNT(*) FROM revoked_sessions
                        WHERE user_id = @userId::uuid AND expires_at > NOW()
                        """,
                        new { userId });
                    return count > 0;
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
                {
                    // Table doesn't exist yet - not revoked
                    return false;
                }
            },
            new HybridCacheEntryOptions { Expiration = CacheDuration });

        if (isRevoked)
        {
            logger.LogInformation("Blocked request from revoked session for user {UserId}", userId);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Session has been revoked",
                code = "SESSION_REVOKED"
            });
            return;
        }

        await next(ctx);
    }
}

public static class SessionRevocationMiddlewareExtensions
{
    public static IApplicationBuilder UseSessionRevocation(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SessionRevocationMiddleware>();
    }
}
