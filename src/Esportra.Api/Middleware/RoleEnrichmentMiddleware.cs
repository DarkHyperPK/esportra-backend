using System.Security.Claims;
using Esportra.Contracts.Auth;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Middleware;

/// <summary>
/// Runs after JWT validation. For authenticated requests:
///   1. Reads userId + email from the Supabase JWT claims.
///   2. Fetches the user's platform roles, admin roles, and resolved permissions
///      from the database — cached in Redis under user-ctx:{userId} for 60s.
///   3. Stores a UserContext in HttpContext.Items["UserContext"].
///
/// This makes enriched user data available to all downstream middleware
/// and endpoint handlers without additional DB queries per request.
/// </summary>
public sealed class RoleEnrichmentMiddleware(
    RequestDelegate next,
    HybridCache cache,
    ILogger<RoleEnrichmentMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? context.User.FindFirstValue("sub");

            if (!string.IsNullOrWhiteSpace(userId))
            {
                var userCtx = await cache.GetOrCreateAsync(
                    $"user-ctx:{userId}",
                    async ct => await FetchUserContextAsync(context, userId, ct),
                    new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) });

                context.Items["UserContext"] = userCtx;
            }
        }

        await next(context);
    }

    private async Task<UserContext> FetchUserContextAsync(
        HttpContext context, string userId, CancellationToken ct)
    {
        // Resolve DB service from the request scope
        var db = context.RequestServices.GetRequiredService<Esportra.Infrastructure.Database.IDbConnectionFactory>();

        using var conn = db.CreateConnection();

        // Query platform roles
        var roles = (await Dapper.SqlMapper.QueryAsync<string>(conn,
            "SELECT role FROM public.user_roles WHERE user_id = @userId",
            new { userId })).ToArray();

        // Query admin roles assigned to this user (handles both profiles.admin_roles array
        // and the normalized admin_user_roles join table)
        var adminRoles = (await Dapper.SqlMapper.QueryAsync<string>(conn, """
            SELECT DISTINCT ar.key
            FROM public.admin_user_roles aur
            JOIN public.admin_roles ar ON ar.id = aur.role_id
            WHERE aur.user_id = @userId
            UNION
            SELECT UNNEST(p.admin_roles)
            FROM public.profiles p
            WHERE p.id = @userId AND p.admin_roles IS NOT NULL
            """, new { userId })).ToArray();

        // Resolve permissions from admin role keys
        var permissions = adminRoles
            .SelectMany(r => AdminRoles.RolePermissions.TryGetValue(r, out var perms) ? perms : [])
            .Distinct()
            .ToArray();

        var email = context.User.FindFirstValue(ClaimTypes.Email)
                 ?? context.User.FindFirstValue("email")
                 ?? string.Empty;

        logger.LogDebug("[RoleEnrichment] userId={UserId} roles=[{Roles}] adminRoles=[{AdminRoles}]",
            userId, string.Join(",", roles), string.Join(",", adminRoles));

        return new UserContext
        {
            UserId      = userId,
            Email       = email,
            Roles       = roles,
            AdminRoles  = adminRoles,
            Permissions = permissions,
        };
    }
}

// Extension method for clean registration
public static class RoleEnrichmentMiddlewareExtensions
{
    public static IApplicationBuilder UseRoleEnrichment(this IApplicationBuilder app)
        => app.UseMiddleware<RoleEnrichmentMiddleware>();
}
