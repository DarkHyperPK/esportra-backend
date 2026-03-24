using System.Security.Claims;
using Esportra.Contracts.Requests;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Supabase;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: set-password and send-recovery-email Edge Functions.
/// </summary>
public static class AuthEndpoints
{
    private static readonly string[] AllowedRedirectHosts =
    [
        "esportra.com", "www.esportra.com", "staging.esportra.com",
        "frontend-staging.esportra.com", "partner.esportra.com", "localhost",
    ];

    public static void MapAuthEndpoints(this WebApplication app)
    {
        // ── POST /api/auth/recovery ───────────────────────────────────────────
        // Replaces: send-recovery-email Edge Function
        // Public — no JWT required.
        app.MapPost("/api/auth/recovery", async (
            SendRecoveryEmailRequest req,
            ISupabaseAdminClient     supabase,
            IEmailService            email,
            IConfiguration           config,
            CancellationToken        ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email))
                return Results.BadRequest(new { error = "Email is required." });

            var frontendUrl = config["FrontendUrl"] ?? "https://esportra.com";

            // Validate redirect base against allowed hosts
            string redirectBase = frontendUrl;
            if (!string.IsNullOrWhiteSpace(req.RedirectBase))
            {
                if (Uri.TryCreate(req.RedirectBase, UriKind.Absolute, out var uri) &&
                    AllowedRedirectHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
                {
                    redirectBase = req.RedirectBase.TrimEnd('/');
                }
            }

            try
            {
                var link = await supabase.GenerateRecoveryLinkAsync(req.Email, ct);
                var resetUrl = $"{redirectBase}/auth/reset-password?token_hash={link.TokenHash}&type=recovery";

                await email.SendAsync(req.Email, EmailType.PasswordReset, new { resetUrl }, ct);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                // Don't reveal whether email exists — always return success
                Console.WriteLine($"[Auth] Recovery email error: {ex.Message}");
                return Results.Ok(new { success = true });
            }
        });

        // ── POST /api/auth/set-password ───────────────────────────────────────
        // Replaces: set-password Edge Function
        // Two paths: token_hash (stateless recovery) OR existing JWT session.
        app.MapPost("/api/auth/set-password", async (
            [FromBody] SetPasswordRequest  req,
            HttpContext                    ctx,
            ISupabaseAdminClient           supabase,
            CancellationToken              ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
                return Results.BadRequest(new { error = "Password must be at least 6 characters." });

            string? userId;
            string? userEmail;

            // Path 1: token_hash recovery (stateless — no existing session)
            if (!string.IsNullOrWhiteSpace(req.TokenHash))
            {
                var user = await supabase.VerifyOtpAsync(
                    req.TokenHash, req.Type ?? "recovery", ct);

                if (user is null)
                    return Results.BadRequest(new { error = "Invalid or expired recovery token." });

                userId    = user.Id;
                userEmail = user.Email;
            }
            else
            {
                // Path 2: existing authenticated session
                userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");

                if (string.IsNullOrWhiteSpace(userId))
                    return Results.Unauthorized();

                userEmail = ctx.User.FindFirstValue(ClaimTypes.Email)
                         ?? ctx.User.FindFirstValue("email");
            }

            await supabase.SetPasswordAsync(userId!, req.Password, ct);
            return Results.Ok(new { success = true, email = userEmail });
        });
    }
}
