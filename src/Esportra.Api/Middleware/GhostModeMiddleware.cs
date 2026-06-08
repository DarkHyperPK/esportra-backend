using Esportra.Api.Services;
using Esportra.Contracts.Auth;

namespace Esportra.Api.Middleware;

public sealed class GhostModeMiddleware(
    RequestDelegate next,
    GhostModeTokenService ghostTokens,
    OperationsAuditService audit)
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

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            context.User.HasClaim(c => c.Type == "impersonated_by"))
        {
            var ghost = await ghostTokens.ValidateAsync(context, context.RequestAborted);
            if (ghost is null)
            {
                await next(context);
                return;
            }

            context.Items["GhostMode"] = ghost;

            if (IsBlockedDuringImpersonation(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Ghost Mode cannot access admin, billing, payment, wallet, GDPR, or critical PII routes."
                }, context.RequestAborted);
                return;
            }

            var adminCtx = new UserContext
            {
                UserId = ghost.AdminId.ToString(),
                Email = "ghost-mode-admin",
                AdminRoles = [AdminRoles.SuperAdmin],
                Permissions = [Permissions.ImpersonationAudit]
            };

            await audit.WriteFromHttpAsync(
                context,
                adminCtx,
                "impersonation.request",
                "http_request",
                context.Request.Path,
                new
                {
                    before = new { executed = false },
                    after = new
                    {
                        executed = true,
                        target_user_id = ghost.TargetUserId,
                        session_id = ghost.SessionId,
                        expires_at = ghost.ExpiresAt,
                        scopes = ghost.Scopes
                    }
                },
                "critical",
                context.RequestAborted);
        }

        await next(context);
    }

    private static bool IsBlockedDuringImpersonation(PathString path) =>
        BlockedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}

public static class GhostModeMiddlewareExtensions
{
    public static IApplicationBuilder UseGhostMode(this IApplicationBuilder app)
        => app.UseMiddleware<GhostModeMiddleware>();
}
