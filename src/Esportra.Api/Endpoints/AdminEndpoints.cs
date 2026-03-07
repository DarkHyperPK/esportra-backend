using System.Security.Claims;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Supabase;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: manage-users and invite-sponsor Edge Functions.
/// Also exposes POST /api/emails for internal use (replaces send-email Edge Function).
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        // ── POST /api/admin/users/{userId}/action ─────────────────────────────
        // Replaces: manage-users Edge Function
        // Actions: "delete-user", "update-role"
        app.MapPost("/api/admin/users/{userId}/action", async (
            string                   userId,
            [FromBody] ManageUserRequest req,
            IDbConnectionFactory     db,
            ISupabaseAdminClient     supabase,
            HttpContext              ctx,
            CancellationToken        ct) =>
        {
            // Verify caller has users:delete or users:edit permission
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var requiredPerm = req.Action == "delete-user"
                ? Permissions.UsersDelete
                : Permissions.UsersEdit;

            if (!userCtx.Permissions.Contains(requiredPerm))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            return req.Action switch
            {
                "delete-user" => await DeleteUserAsync(userId, conn, supabase, ct),
                "update-role" => await UpdateUserRoleAsync(userId, req.Role, conn, ct),
                _ => Results.BadRequest(new { error = $"Unknown action: {req.Action}" })
            };
        }).RequireAuthorization("Authenticated");

        // ── POST /api/sponsors/invite ─────────────────────────────────────────
        // Replaces: invite-sponsor Edge Function
        app.MapPost("/api/sponsors/invite", async (
            [FromBody] InviteSponsorRequest req,
            IDbConnectionFactory     db,
            ISupabaseAdminClient     supabase,
            IEmailService            email,
            IConfiguration           config,
            HttpContext              ctx,
            CancellationToken        ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.SponsorsCreate))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            // Get sponsor name
            var sponsorName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM public.sponsors WHERE id = @id", new { id = req.SponsorId });

            if (sponsorName is null)
                return Results.NotFound(new { error = "Sponsor not found." });

            var partnerUrl = config["PartnerUrl"] ?? "https://partner.esportra.com";

            // Check if user already exists
            var existingUser = await supabase.GetUserByEmailAsync(req.Email, ct);
            bool isNewUser;
            string sponsorUserId;

            if (existingUser is not null)
            {
                isNewUser     = false;
                sponsorUserId = existingUser.Id;

                // Link existing user to sponsor
                await conn.ExecuteAsync("""
                    INSERT INTO public.sponsor_accounts (user_id, sponsor_id, role, onboarding_meta)
                    VALUES (@userId, @sponsorId, 'owner', '{}')
                    ON CONFLICT (user_id, sponsor_id) DO NOTHING
                    """, new { userId = sponsorUserId, sponsorId = req.SponsorId });

                // Send portal access email
                await email.SendAsync(req.Email, EmailType.PartnerWelcome, new
                {
                    sponsorName,
                    portalUrl = partnerUrl,
                }, ct);
            }
            else
            {
                isNewUser = true;

                // Create new user
                var newUser = await supabase.CreateUserAsync(req.Email,
                    new { sponsor_id = req.SponsorId }, ct);
                sponsorUserId = newUser.Id;

                await conn.ExecuteAsync("""
                    INSERT INTO public.sponsor_accounts (user_id, sponsor_id, role, onboarding_meta)
                    VALUES (@userId, @sponsorId, 'owner', '{}')
                    ON CONFLICT DO NOTHING
                    """, new { userId = sponsorUserId, sponsorId = req.SponsorId });

                // Generate recovery link for password setup
                var link    = await supabase.GenerateRecoveryLinkAsync(req.Email, ct);
                var setupUrl = $"{partnerUrl}/set-password?token_hash={link.TokenHash}&type=recovery";

                await email.SendAsync(req.Email, EmailType.PartnerInvite, new
                {
                    sponsorName,
                    setupUrl,
                }, ct);
            }

            // Mark partner application as approved if provided
            if (!string.IsNullOrWhiteSpace(req.ApplicationId))
            {
                await conn.ExecuteAsync(
                    "UPDATE public.partner_applications SET status = 'approved' WHERE id = @id",
                    new { id = req.ApplicationId });
            }

            return Results.Ok(new { success = true, isNewUser, userId = sponsorUserId });

        }).RequireAuthorization(Permissions.SponsorsCreate);

        // ── POST /api/emails ──────────────────────────────────────────────────
        // Replaces: send-email Edge Function (internal use only)
        // Requires authenticated admin or service call.
        app.MapPost("/api/emails", async (
            [FromBody] SendEmailRequest req,
            IEmailService         email,
            HttpContext           ctx,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!Enum.TryParse<EmailType>(req.Type, ignoreCase: true, out var emailType))
                return Results.BadRequest(new { error = $"Unknown email type: {req.Type}" });

            await email.SendAsync(req.Email, emailType, req.Data, ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<IResult> DeleteUserAsync(
        string userId,
        System.Data.IDbConnection conn,
        ISupabaseAdminClient supabase,
        CancellationToken ct)
    {
        // Mirror manage-users edge function: delete in order to avoid FK errors
        await conn.ExecuteAsync(
            "DELETE FROM public.tournaments WHERE organizer_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.user_roles WHERE user_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.profiles WHERE id = @id", new { id = userId });

        // Finally delete from Auth
        await supabase.DeleteUserAsync(userId, ct);
        return Results.Ok(new { success = true });
    }

    private static async Task<IResult> UpdateUserRoleAsync(
        string userId,
        string? role,
        System.Data.IDbConnection conn,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(role))
            return Results.BadRequest(new { error = "Role is required for update-role action." });

        await conn.ExecuteAsync(
            "DELETE FROM public.user_roles WHERE user_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "INSERT INTO public.user_roles (user_id, role) VALUES (@id, @role)",
            new { id = userId, role });

        return Results.Ok(new { success = true, role });
    }
}
