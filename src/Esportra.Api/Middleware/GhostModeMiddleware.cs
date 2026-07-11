using System.Security.Cryptography;
using System.Text;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Middleware;

public sealed record GhostSessionInfo(
    Guid SessionId,
    Guid AdminId,
    Guid TargetUserId,
    string TargetEmail,
    string[] TargetRoles,
    string[] TargetAdminRoles,
    string[] TargetPermissions,
    DateTimeOffset ExpiresAt);

public sealed class GhostModeMiddleware(
    RequestDelegate next,
    HybridCache cache,
    ILogger<GhostModeMiddleware> logger)
{
    private static readonly string[] BlockedPrefixes =
    [
        "/api/admin",
        "/api/billing",
        "/api/payments",
        "/api/wallets",
        "/api/gdpr",
        "/api/stripe"
    ];

    private static readonly string[] AllowedAdminRoutes =
    [
        "/api/admin/ghost/end",
        "/api/admin/ghost/current",
        "/api/admin/ghost/view",
        "/api/admin/ghost/field"
    ];

    public async Task InvokeAsync(HttpContext context, IDbConnectionFactory db)
    {
        var ghostToken = context.Request.Headers["X-Ghost-Token"].ToString();

        if (!string.IsNullOrEmpty(ghostToken) && context.User.Identity?.IsAuthenticated == true)
        {
            var tokenHash = HashToken(ghostToken);

            var session = await cache.GetOrCreateAsync(
                $"ghost-session:{tokenHash}",
                async ct => await ValidateGhostSessionAsync(db, tokenHash, ct),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) });

            if (session is not null)
            {
                if (session.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Ghost session has expired",
                        code = "GHOST_SESSION_EXPIRED"
                    }, context.RequestAborted);
                    return;
                }

                context.Items["GhostMode"] = session;
                context.Items["GhostModeActive"] = true;

                if (IsBlockedDuringImpersonation(context.Request.Path))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Ghost Mode cannot access admin, billing, payment, wallet, GDPR, or critical PII routes."
                    }, context.RequestAborted);
                    return;
                }

                var targetUserContext = new UserContext
                {
                    UserId = session.TargetUserId.ToString(),
                    Email = session.TargetEmail,
                    Roles = session.TargetRoles,
                    AdminRoles = session.TargetAdminRoles,
                    Permissions = session.TargetPermissions,
                };

                context.Items["UserContext"] = targetUserContext;
                context.Items["OriginalAdminId"] = session.AdminId;

                logger.LogDebug(
                    "[GhostMode] Admin {AdminId} viewing as {TargetUserId} on {Path}",
                    session.AdminId, session.TargetUserId, context.Request.Path);
            }
        }

        await next(context);
    }

    private static async Task<GhostSessionInfo?> ValidateGhostSessionAsync(
        IDbConnectionFactory db, string tokenHash, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT gs.id, gs.admin_id, gs.target_user_id, gs.expires_at,
                   p.email AS target_email
            FROM ghost_sessions gs
            JOIN profiles p ON p.id = gs.target_user_id
            WHERE gs.token_hash = @tokenHash
              AND gs.ended_at IS NULL
              AND gs.expires_at > NOW()
            """,
            new { tokenHash });

        if (row is null)
            return null;

        var targetUserId = (Guid)row.target_user_id;

        var roles = (await conn.QueryAsync<string>(
            "SELECT role FROM user_roles WHERE user_id = @userId AND is_active = TRUE",
            new { userId = targetUserId })).ToArray();

        string[] adminRoles;
        string[] permissions;
        try
        {
            adminRoles = (await conn.QueryAsync<string>("""
                SELECT DISTINCT ar.key
                FROM admin_user_roles aur
                JOIN admin_roles ar ON ar.id = aur.role_id
                WHERE aur.user_id = @userId
                """, new { userId = targetUserId })).ToArray();

            permissions = (await conn.QueryAsync<string>("""
                SELECT DISTINCT ap.name
                FROM admin_user_roles aur
                JOIN admin_role_permissions arp ON arp.role_id = aur.role_id
                JOIN admin_permissions ap ON ap.id = arp.permission_id
                WHERE aur.user_id = @userId
                """, new { userId = targetUserId })).ToArray();
        }
        catch
        {
            adminRoles = [];
            permissions = [];
        }

        return new GhostSessionInfo(
            SessionId: (Guid)row.id,
            AdminId: (Guid)row.admin_id,
            TargetUserId: targetUserId,
            TargetEmail: (string?)row.target_email ?? "",
            TargetRoles: roles,
            TargetAdminRoles: adminRoles,
            TargetPermissions: permissions,
            ExpiresAt: (DateTimeOffset)row.expires_at);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsBlockedDuringImpersonation(PathString path)
    {
        var pathStr = path.Value ?? "";

        if (AllowedAdminRoutes.Any(r => pathStr.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            return false;

        return BlockedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

public static class GhostModeMiddlewareExtensions
{
    public static IApplicationBuilder UseGhostMode(this IApplicationBuilder app)
        => app.UseMiddleware<GhostModeMiddleware>();
}
