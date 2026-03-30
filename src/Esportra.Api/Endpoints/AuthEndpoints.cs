using System.Security.Claims;
using Esportra.Contracts.Requests;
using Esportra.Infrastructure.Supabase;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: set-password Edge Function. Password reset is now handled by GoTrue directly.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // ── POST /api/auth/set-password ───────────────────────────────────────
        // Replaces: set-password Edge Function
        // Two paths: token_hash (stateless recovery) OR existing JWT session.
        app.MapPost("/api/auth/set-password", async (
            [FromBody] SetPasswordRequest  req,
            HttpContext                    ctx,
            ISupabaseAdminClient           supabase,
            CancellationToken              ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
                return Results.BadRequest(new { error = "Password must be at least 8 characters." });

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
                // Path 2: existing authenticated session (GoTrue PASSWORD_RECOVERY flow)
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
