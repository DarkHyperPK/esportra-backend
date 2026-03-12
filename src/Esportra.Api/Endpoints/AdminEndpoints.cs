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
            Guid                     userId,
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

        // ── POST /api/admin/users/cleanup ─────────────────────────────────────
        // Deletes profiles with no matching auth.users entry (orphaned rows).
        app.MapPost("/api/admin/users/cleanup", async (
            IDbConnectionFactory     db,
            HttpContext              ctx,
            CancellationToken        ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersDelete))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var orphanIds = (await conn.QueryAsync<Guid>("""
                DELETE FROM public.profiles
                WHERE id NOT IN (SELECT id FROM auth.users)
                RETURNING id
                """)).ToList();

            return Results.Ok(new { deleted = orphanIds.Count, orphanIds });
        }).RequireAuthorization("Admin");

        // ── GET /api/sponsors ─────────────────────────────────────────────────
        // Returns active sponsors (optionally filtered by placement).
        app.MapGet("/api/sponsors", async (
            bool?                active,
            string?              placement,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var sponsors = await conn.QueryAsync<dynamic>(
                """
                SELECT id, name, tagline, description, website_url, logo_url,
                       banner_image_url, accent_color, tier, placement, cta_text,
                       discount_text, is_active, priority, start_date, end_date,
                       created_at, gallery_images, tier_features
                FROM sponsors
                WHERE (@active IS NULL OR is_active = @active)
                  AND (@placement IS NULL OR @placement = ANY(placement))
                ORDER BY priority DESC, created_at DESC
                """,
                new { active, placement });
            return Results.Ok(sponsors);
        });

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
                    """, new { userId = Guid.Parse(sponsorUserId), sponsorId = req.SponsorId });

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
                    """, new { userId = Guid.Parse(sponsorUserId), sponsorId = req.SponsorId });

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

        // ── GET /api/admin/users ─────────────────────────────────────────────
        // Paginated user list with search for admin dashboard
        app.MapGet("/api/admin/users", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            [FromQuery] int      limit  = 20,
            [FromQuery] int      offset = 0,
            [FromQuery] string?  search = null,
            [FromQuery] string?  status = null,
            CancellationToken    ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
                conditions.Add("(p.username ILIKE @search OR p.email ILIKE @search OR p.full_name ILIKE @search)");
            if (status == "suspended")
                conditions.Add("p.is_suspended = TRUE");
            else if (status == "active")
                conditions.Add("(p.is_suspended IS NULL OR p.is_suspended = FALSE)");

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var sql = $"""
                SELECT
                    p.id,
                    p.username,
                    p.email,
                    p.full_name,
                    p.avatar_url,
                    p.is_suspended,
                    p.created_at,
                    COALESCE(
                        (SELECT jsonb_agg(ur.role) FROM user_roles ur WHERE ur.user_id = p.id AND ur.is_active = TRUE),
                        '[]'::jsonb
                    ) AS roles
                FROM profiles p
                {where}
                ORDER BY p.created_at DESC
                LIMIT @limit OFFSET @offset
                """;

            var users = await conn.QueryAsync<dynamic>(sql,
                new { search = $"%{search}%", limit, offset });

            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM profiles p {where}",
                new { search = $"%{search}%" });

            return Results.Ok(new { users, total });
        }).RequireAuthorization("Admin");

        // ── POST /api/sponsors/track ────────────────────────────────────────
        // Replaces: record-metric Edge Function (ad impression/click tracking)
        // Accepts sponsor_id, event_type (impression|click), page_url
        app.MapPost("/api/sponsors/track", async (
            [FromBody] SponsorTrackRequest req,
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            if (req.EventType is not ("impression" or "click"))
                return Results.BadRequest(new { error = "EventType must be 'impression' or 'click'" });

            if (!Guid.TryParse(req.SponsorId, out var sponsorId))
                return Results.BadRequest(new { error = "Invalid SponsorId" });

            using var conn = db.CreateConnection();

            var visitorId = (ctx.Items["UserContext"] as UserContext)?.UserId;

            await conn.ExecuteAsync(
                """
                INSERT INTO sponsor_impressions (sponsor_id, event_type, page_url, visitor_id)
                VALUES (@sponsorId, @eventType, @pageUrl, @visitorId)
                """,
                new
                {
                    sponsorId,
                    eventType = req.EventType,
                    pageUrl   = req.PageUrl,
                    visitorId
                });

            return Results.Ok(new { success = true });
        });

        // ── GET /api/admin/stats ──────────────────────────────────────────────
        // Dashboard summary stats
        app.MapGet("/api/admin/stats", async (
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    (SELECT COUNT(*) FROM profiles) AS total_users,
                    (SELECT COUNT(*) FROM venues) AS active_venues,
                    (SELECT COUNT(*) FROM tournaments WHERE status IN ('upcoming', 'ongoing')) AS active_tournaments
                """);
            return Results.Ok(new {
                totalUsers = (long)row.total_users,
                activeVenues = (long)row.active_venues,
                activeTournaments = (long)row.active_tournaments,
                totalRevenue = 0
            });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/analytics ────────────────────────────────────────────
        // Replaces 8 parallel supabase count queries
        app.MapGet("/api/admin/analytics", async (
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var stats = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    (SELECT COUNT(*) FROM profiles) AS total_users,
                    (SELECT COUNT(*) FROM tournaments) AS total_tournaments,
                    (SELECT COUNT(*) FROM venues) AS total_venues,
                    (SELECT COALESCE(SUM(COALESCE(prize_pool, 0)), 0) FROM tournaments) AS total_prize_pool,
                    (SELECT COUNT(*) FROM profiles WHERE created_at >= NOW() - INTERVAL '7 days') AS new_users_this_week,
                    (SELECT COUNT(*) FROM tournaments WHERE created_at >= NOW() - INTERVAL '7 days') AS new_tournaments_this_week,
                    (SELECT COUNT(*) FROM venue_bookings) AS total_bookings,
                    (SELECT COUNT(*) FROM tournaments WHERE status = 'completed') AS completed_tournaments
                """);
            return Results.Ok(stats);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/system-stats ─────────────────────────────────────────
        // Replaces supabase.rpc('get_system_stats') + fallback count queries
        app.MapGet("/api/admin/system-stats", async (
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var stats = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    (SELECT COUNT(*) FROM profiles) AS total_users,
                    (SELECT COUNT(*) FROM tournaments) AS total_tournaments,
                    (SELECT COUNT(*) FROM venues) AS total_venues,
                    (SELECT COALESCE(SUM(COALESCE(prize_pool, 0)), 0) FROM tournaments) AS total_prize_pool
                """);
            return Results.Ok(stats);
        }).RequireAuthorization("Admin");

        // ── CRUD: Sponsors ──────────────────────────────────────────────────────
        app.MapPost("/api/sponsors", async (
            [FromBody] object    payload,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO sponsors SELECT * FROM jsonb_populate_record(NULL::sponsors, @json::jsonb)
                RETURNING *
                """,
                new { json });
            return Results.Ok(row);
        }).RequireAuthorization("Admin");

        app.MapPut("/api/sponsors/{id}", async (
            Guid                 id,
            [FromBody] object    payload,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            // Dynamic update: serialize payload to JSON, use jsonb_each to set fields
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            await conn.ExecuteAsync(
                """
                UPDATE sponsors
                SET name        = COALESCE(((@j)::jsonb->>'name')::text,        name),
                    is_active   = COALESCE(((@j)::jsonb->>'is_active')::boolean, is_active),
                    updated_at  = NOW()
                WHERE id = @id
                """,
                new { id, j = json });
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM sponsors WHERE id = @id", new { id });
            return Results.Ok(row);
        }).RequireAuthorization("Admin");

        app.MapDelete("/api/sponsors/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync("DELETE FROM sponsors WHERE id = @id", new { id });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/users/{userId}/suspend ──────────────────────────────
        app.MapPost("/api/admin/users/{userId}/suspend", async (
            Guid                 userId,
            [FromBody] SuspendUserRequest req,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "SELECT admin_suspend_user(@p_user_id, @p_reason, @p_admin_id)",
                new { p_user_id = userId, p_reason = req.Reason, p_admin_id = userCtx.UserIdGuid });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/users/{userId}/unsuspend ────────────────────────────
        app.MapPost("/api/admin/users/{userId}/unsuspend", async (
            Guid                 userId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "SELECT admin_unsuspend_user(@p_user_id, @p_admin_id)",
                new { p_user_id = userId, p_admin_id = userCtx.UserIdGuid });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

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

            // Only admins can send arbitrary emails
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit) &&
                !userCtx.AdminRoles.Any())
                return Results.Forbid();

            if (!Enum.TryParse<EmailType>(req.Type, ignoreCase: true, out var emailType))
                return Results.BadRequest(new { error = $"Unknown email type: {req.Type}" });

            await email.SendAsync(req.Email, emailType, req.Data, ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<IResult> DeleteUserAsync(
        Guid userId,
        System.Data.IDbConnection conn,
        ISupabaseAdminClient supabase,
        CancellationToken ct)
    {
        // Delete in dependency order to avoid FK constraint errors.
        // Child/junction tables first, then parent tables, then auth.
        await conn.ExecuteAsync(
            "DELETE FROM public.match_result_reports WHERE reported_by = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.match_messages WHERE sender_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.notifications WHERE user_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.tournament_staff WHERE user_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.organization_staff WHERE user_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.sponsor_accounts WHERE user_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.tournament_participants WHERE user_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.team_members WHERE user_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.tournaments WHERE organizer_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.user_roles WHERE user_id = @id", new { id = userId });
        await conn.ExecuteAsync(
            "DELETE FROM public.profiles WHERE id = @id", new { id = userId });

        // Finally delete from Supabase Auth
        await supabase.DeleteUserAsync(userId.ToString(), ct);
        return Results.Ok(new { success = true });
    }

    private static async Task<IResult> UpdateUserRoleAsync(
        Guid userId,
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
