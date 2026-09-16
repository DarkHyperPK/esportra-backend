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
        var db = context.RequestServices.GetRequiredService<IDbConnectionFactory>();

        using var conn = db.CreateConnection();

        // Parse userId as Guid so Dapper sends uuid type (not text) to PostgreSQL.
        if (!Guid.TryParse(userId, out var userGuid))
        {
            logger.LogWarning("[RoleEnrichment] Invalid userId format: {UserId}", userId);
            return new UserContext { UserId = userId, Email = string.Empty };
        }

        // Query platform roles (only active — revoked licenses set is_active = FALSE)
        var roles = (await Dapper.SqlMapper.QueryAsync<string>(conn,
            "SELECT role FROM public.user_roles WHERE user_id = @userId AND is_active = TRUE",
            new { userId = userGuid })).ToArray();

        // Query admin roles from the normalized admin_user_roles table (single source of truth).
        string[] adminRoles;
        string[] permissions;
        try
        {
            adminRoles = (await Dapper.SqlMapper.QueryAsync<string>(conn, """
                SELECT DISTINCT ar.key
                FROM public.admin_user_roles aur
                JOIN public.admin_roles ar ON ar.id = aur.role_id
                WHERE aur.user_id = @userId
                """, new { userId = userGuid })).ToArray();

            // Resolve permissions from DB via role → permission mappings
            permissions = (await Dapper.SqlMapper.QueryAsync<string>(conn, """
                SELECT DISTINCT ap.name
                FROM public.admin_user_roles aur
                JOIN public.admin_role_permissions arp ON arp.role_id = aur.role_id
                JOIN public.admin_permissions ap ON ap.id = arp.permission_id
                WHERE aur.user_id = @userId
                """, new { userId = userGuid })).ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RoleEnrichment] Admin roles query failed for {UserId}, defaulting to empty", userId);
            adminRoles = [];
            permissions = [];
        }

        if (adminRoles.Contains(AdminRoles.SuperAdmin, StringComparer.OrdinalIgnoreCase))
        {
            permissions = typeof(Permissions)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral)
                .Select(f => (string)f.GetRawConstantValue()!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var email = context.User.FindFirstValue(ClaimTypes.Email)
                 ?? context.User.FindFirstValue("email")
                 ?? string.Empty;

        logger.LogDebug("[RoleEnrichment] userId={UserId} roles=[{Roles}] adminRoles=[{AdminRoles}]",
            userId, string.Join(",", roles), string.Join(",", adminRoles));

        return new UserContext
        {
            UserId = userId,
            Email = email,
            Roles = roles,
            AdminRoles = adminRoles,
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
