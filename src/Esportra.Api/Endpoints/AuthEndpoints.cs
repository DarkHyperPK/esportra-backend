using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Esportra.Api.Auth;
using Esportra.Api.Middleware;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Api.Hubs;
using Esportra.Api.ScheduledJobs;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/recovery", RequestRecoveryAsync)
            .WithMetadata(new RateLimitPolicyMetadata("auth"));

        app.MapPost("/api/auth/password-reset-completed", CompletePasswordResetAsync)
            .RequireAuthorization("Authenticated")
            .WithMetadata(new RateLimitPolicyMetadata("auth"));

        app.MapPost("/api/auth/set-password", () => Results.Json(
            new { error = "This password reset flow is no longer supported." },
            statusCode: StatusCodes.Status410Gone));
    }

    internal static async Task<IResult> CompletePasswordResetAsync(
        HttpContext context,
        AccountSecurityService accountSecurity,
        IHubContext<NotificationHub> notificationHub,
        CancellationToken cancellationToken)
    {
        var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Results.Unauthorized();
        }

        var state = await accountSecurity.RevokeAllAsync(
            userId,
            "password_reset",
            cancellationToken);

        try
        {
            await notificationHub.Clients
                .Group(NotificationHub.UserGroup(userId.ToString()))
                .SendAsync(
                    NotificationHubEvents.ForceLogout,
                    new { reason = "password_reset" },
                    cancellationToken);
        }
        catch
        {
            // Epoch revocation is authoritative; realtime notification is best effort.
        }

        return Results.Ok(new
        {
            completed = true,
            revocationVersion = state.RevocationVersion,
        });
    }

    internal static async Task<IResult> RequestRecoveryAsync(
        PasswordRecoveryRequest request,
        PasswordRecoveryService passwordRecovery,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!TryParseRequest(request, out var email, out var portal))
        {
            return Results.BadRequest(new { error = "Invalid recovery request." });
        }

        try
        {
            await passwordRecovery.RequestAsync(email, portal, cancellationToken);
        }
        catch
        {
            // Preserve the generic response for provider failures.
        }
        return Results.Json(
            new PasswordRecoveryResponse(PasswordRecoveryService.GenericMessage),
            statusCode: StatusCodes.Status202Accepted,
            contentType: "application/json");
    }

    private static bool TryParseRequest(
        PasswordRecoveryRequest request,
        out string email,
        out RecoveryPortal portal)
    {
        email = request.Email?.Trim() ?? string.Empty;
        portal = default;

        return email.Length is > 0 and <= 254
            && new EmailAddressAttribute().IsValid(email)
            && Enum.TryParse(request.Portal, ignoreCase: true, out portal)
            && Enum.IsDefined(portal);
    }
}
