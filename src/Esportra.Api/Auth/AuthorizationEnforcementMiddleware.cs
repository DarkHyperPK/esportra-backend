using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Auth;

/// <summary>
/// Single enforcement point for route-level authorization.
/// Reads from <see cref="RoutePermissionManifest"/> and evaluates access.
///
/// Operates in two modes controlled by config "Authorization:EnforceMode":
///   false (default) = AUDIT MODE — logs violations but allows requests through
///   true            = ENFORCE MODE — blocks unauthorized requests with 401/403
/// </summary>
public sealed class AuthorizationEnforcementMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthorizationEnforcementMiddleware> _logger;
    private readonly bool _enforceMode;

    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AuthorizationEnforcementMiddleware(
        RequestDelegate next,
        ILogger<AuthorizationEnforcementMiddleware> logger,
        IConfiguration config)
    {
        _next = next;
        _logger = logger;
        _enforceMode = config.GetValue("Authorization:EnforceMode", false);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var method = ctx.Request.Method;

        if (ShouldSkip(path))
        {
            await _next(ctx);
            return;
        }

        var rule = RoutePermissionManifest.Resolve(method, path);
        if (rule is null)
        {
            _logger.LogWarning(
                "MANIFEST-GAP: No rule for {Method} {Path} — {Mode}",
                method, path, _enforceMode ? "BLOCKING" : "PASSING (audit)");

            if (_enforceMode)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(
                    new { error = "Access denied", code = "no_manifest_rule" }, _jsonOpts);
                return;
            }

            await _next(ctx);
            return;
        }

        var result = await EvaluateAsync(ctx, rule);
        if (!result.Allowed)
        {
            var userId = (ctx.Items["UserContext"] as UserContext)?.UserId ?? "anonymous";

            _logger.LogInformation(
                "AUTH-{Action}: {UserId} → {Method} {Path} | Level={Level} | Reason={Reason}",
                _enforceMode ? "DENIED" : "WOULD-DENY",
                userId, method, path, rule.Level, result.Reason);

            if (_enforceMode)
            {
                ctx.Response.StatusCode = result.StatusCode;
                await ctx.Response.WriteAsJsonAsync(
                    new { error = result.Reason, code = "access_denied" }, _jsonOpts);
                return;
            }
        }

        await _next(ctx);
    }

    private async Task<AuthResult> EvaluateAsync(HttpContext ctx, RouteAuthRule rule)
    {
        if (rule.Level == AuthLevel.Public)
            return AuthResult.Allow();

        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null)
            return AuthResult.Deny(401, "Authentication required");

        if (rule.Level == AuthLevel.Authenticated)
            return AuthResult.Allow();

        if (rule.Level == AuthLevel.VerifiedRole)
            return await EvaluateVerifiedRoleAsync(ctx, userCtx, rule);

        if (rule.Level == AuthLevel.AdminAny)
        {
            return userCtx.AdminRoles.Length > 0
                ? AuthResult.Allow()
                : AuthResult.Deny(403, "Admin role required");
        }

        if (rule.Level == AuthLevel.AdminPermission)
        {
            if (userCtx.IsSuperAdmin) return AuthResult.Allow();

            var hasAny = rule.RequiredPermissions?.Any(p =>
                userCtx.Permissions.Contains(p, StringComparer.OrdinalIgnoreCase)) ?? false;

            return hasAny
                ? AuthResult.Allow()
                : AuthResult.Deny(403, $"Permission required: {string.Join(" | ", rule.RequiredPermissions ?? [])}");
        }

        return AuthResult.Deny(403, "Unknown auth level");
    }

    private async Task<AuthResult> EvaluateVerifiedRoleAsync(
        HttpContext ctx, UserContext userCtx, RouteAuthRule rule)
    {
        if (userCtx.IsSuperAdmin) return AuthResult.Allow();

        if (!userCtx.Roles.Contains(rule.RequiredRole!, StringComparer.OrdinalIgnoreCase))
            return AuthResult.Deny(403, $"Role '{rule.RequiredRole}' required");

        if (rule.RequiresVerification)
        {
            var db = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>();
            using var conn = db.CreateConnection();
            var verified = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM verified_roles
                    WHERE user_id = @userId AND role = @role::app_role
                      AND status = 'approved' AND is_active = TRUE
                )
                """,
                new { userId = userCtx.UserIdGuid, role = rule.RequiredRole });

            if (!verified)
                return AuthResult.Deny(403, $"Verified '{rule.RequiredRole}' status required");
        }

        if (rule.RequiresOrganization)
        {
            var db = ctx.RequestServices.GetRequiredService<IDbConnectionFactory>();
            using var conn = db.CreateConnection();
            var hasOrg = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM organizations WHERE owner_id = @userId)",
                new { userId = userCtx.UserIdGuid });

            if (!hasOrg)
                return AuthResult.Deny(403, "Organization ownership required");
        }

        return AuthResult.Allow();
    }

    private static bool ShouldSkip(string path) =>
        path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Result of an authorization evaluation.</summary>
public readonly record struct AuthResult(bool Allowed, int StatusCode, string? Reason)
{
    public static AuthResult Allow() => new(true, 200, null);
    public static AuthResult Deny(int status, string reason) => new(false, status, reason);
}

public static class AuthorizationEnforcementExtensions
{
    public static IApplicationBuilder UseAuthorizationEnforcement(this IApplicationBuilder app)
        => app.UseMiddleware<AuthorizationEnforcementMiddleware>();
}
