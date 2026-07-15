using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Esportra.Api.Services;

namespace Esportra.Api.Middleware;

public sealed class SessionRevocationMiddleware(RequestDelegate next, ILogger<SessionRevocationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, AccountSecurityService accountSecurity)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");
        var issuedAtClaim = context.User.FindFirstValue(JwtRegisteredClaimNames.Iat)
            ?? context.User.FindFirstValue("iat");

        if (!Guid.TryParse(userIdClaim, out var userId)
            || !long.TryParse(issuedAtClaim, out var issuedAt)
            || issuedAt < 0)
        {
            await WriteRejectionAsync(context, "Invalid session", "INVALID_SESSION");
            return;
        }

        AccountSecurityState state;
        try
        {
            state = await accountSecurity.GetAsync(userId, context.RequestAborted);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Account security lookup failed for user {UserId}", userId);
            await WriteRejectionAsync(context, "Session validation unavailable", "SESSION_VALIDATION_FAILED");
            return;
        }

        if (!AccountSecurityService.IsTokenValid(issuedAt, state.SessionsValidAfter))
        {
            logger.LogInformation(
                "Blocked revoked session for user {UserId}, version {Version}",
                userId,
                state.RevocationVersion);
            await WriteRejectionAsync(context, "Session has been revoked", "SESSION_REVOKED");
            return;
        }

        var lifecycleCode = state.AccountStatus switch
        {
            "active" => null,
            "suspended" => "account_suspended",
            "deletion_pending" => "deletion_pending",
            "deleted" => "account_deleted",
            _ => "account_state_invalid",
        };

        if (lifecycleCode is not null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Account access is restricted",
                code = lifecycleCode,
            });
            return;
        }

        await next(context);
    }

    private static async Task WriteRejectionAsync(HttpContext context, string error, string code)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error, code });
    }
}

public static class SessionRevocationMiddlewareExtensions
{
    public static IApplicationBuilder UseSessionRevocation(this IApplicationBuilder app)
        => app.UseMiddleware<SessionRevocationMiddleware>();
}
