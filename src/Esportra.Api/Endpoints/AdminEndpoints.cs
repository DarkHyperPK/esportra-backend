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

        // ── GET /api/sponsors/{id}/stats ──────────────────────────────────────
        app.MapGet("/api/sponsors/{id}/stats", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var stats = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT
                    COUNT(*) FILTER (WHERE event_type = 'view')         AS views,
                    COUNT(*) FILTER (WHERE event_type = 'click')        AS clicks,
                    COUNT(*) FILTER (WHERE event_type = 'impression')   AS impressions,
                    COUNT(DISTINCT visitor_id)                          AS unique_visitors
                FROM sponsor_impressions
                WHERE sponsor_id = @id
                """, new { id });
            return Results.Ok(stats ?? new { views = 0, clicks = 0, impressions = 0, unique_visitors = 0 });
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
                    (SELECT COUNT(*) FROM tournaments WHERE status IN ('open', 'check_in', 'ongoing')) AS active_tournaments
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
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            await conn.ExecuteAsync(
                """
                UPDATE sponsors
                SET name             = COALESCE(((@j)::jsonb->>'name')::text,             name),
                    tagline          = COALESCE(((@j)::jsonb->>'tagline')::text,           tagline),
                    description      = COALESCE(((@j)::jsonb->>'description')::text,      description),
                    website_url      = COALESCE(((@j)::jsonb->>'website_url')::text,       website_url),
                    logo_url         = COALESCE(((@j)::jsonb->>'logo_url')::text,          logo_url),
                    banner_image_url = COALESCE(((@j)::jsonb->>'banner_image_url')::text,  banner_image_url),
                    accent_color     = COALESCE(((@j)::jsonb->>'accent_color')::text,      accent_color),
                    tier             = COALESCE(((@j)::jsonb->>'tier')::text,               tier),
                    placement        = COALESCE((SELECT array_agg(e::text) FROM jsonb_array_elements_text(((@j)::jsonb->'placement')) e), placement),
                    cta_text         = COALESCE(((@j)::jsonb->>'cta_text')::text,          cta_text),
                    discount_text    = COALESCE(((@j)::jsonb->>'discount_text')::text,     discount_text),
                    is_active        = COALESCE(((@j)::jsonb->>'is_active')::boolean,      is_active),
                    priority         = COALESCE(((@j)::jsonb->>'priority')::int,            priority),
                    gallery_images   = COALESCE((SELECT array_agg(e::text) FROM jsonb_array_elements_text(((@j)::jsonb->'gallery_images')) e), gallery_images)
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

            // Allow self-service transactional emails for authenticated users
            var selfServiceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "TournamentRegistration", "CheckinReminder", "MatchCheckinReminder", "Welcome" };
            bool isSelfService = selfServiceTypes.Contains(req.Type ?? "");

            if (!isSelfService &&
                !userCtx.Permissions.Contains(Permissions.UsersEdit) &&
                !userCtx.AdminRoles.Any())
                return Results.Forbid();

            if (!Enum.TryParse<EmailType>(req.Type, ignoreCase: true, out var emailType))
                return Results.BadRequest(new { error = $"Unknown email type: {req.Type}" });

            try
            {
                await email.SendAsync(req.Email, emailType, req.Data, ct);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/admin/user-roles ──────────────────────────────────────────
        // Returns all user→role assignments
        app.MapGet("/api/admin/user-roles", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT ur.user_id, ur.role, ur.is_active,
                       p.username, p.email, p.avatar_url
                FROM user_roles ur
                JOIN profiles p ON p.id = ur.user_id
                ORDER BY p.username ASC
                """);
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/user-roles ─────────────────────────────────────────
        app.MapPost("/api/admin/user-roles", async (
            [FromBody] AssignRoleRequest req,
            HttpContext                  ctx,
            IDbConnectionFactory         db,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO user_roles (user_id, role, is_active)
                VALUES (@userId, @role, TRUE)
                ON CONFLICT (user_id, role) DO UPDATE SET is_active = TRUE
                """,
                new { userId = req.UserId, role = req.Role });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/user-roles ───────────────────────────────────────
        app.MapDelete("/api/admin/user-roles", async (
            Guid                 userId,
            string               role,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM user_roles WHERE user_id = @userId AND role = @role",
                new { userId, role });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/admin-user-roles ─────────────────────────────────────
        // Supports optional ?user_id= filter; joins admin_roles for name/key
        app.MapGet("/api/admin/admin-user-roles", async (
            Guid?                userId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = userId.HasValue
                ? await conn.QueryAsync<dynamic>(
                    """
                    SELECT aur.user_id, aur.role_id, ar.name AS role_name, ar.key AS role_key,
                           p.username, p.email, p.avatar_url
                    FROM admin_user_roles aur
                    JOIN profiles p ON p.id = aur.user_id
                    JOIN admin_roles ar ON ar.id = aur.role_id
                    WHERE aur.user_id = @userId
                    ORDER BY ar.name ASC
                    """,
                    new { userId })
                : await conn.QueryAsync<dynamic>(
                    """
                    SELECT aur.user_id, aur.role_id, ar.name AS role_name, ar.key AS role_key,
                           p.username, p.email, p.avatar_url
                    FROM admin_user_roles aur
                    JOIN profiles p ON p.id = aur.user_id
                    JOIN admin_roles ar ON ar.id = aur.role_id
                    ORDER BY p.username ASC
                    """);
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/admin-user-roles ─────────────────────────────────────
        app.MapPost("/api/admin/admin-user-roles", async (
            [FromBody] AdminUserRoleAssignRequest req,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO admin_user_roles (user_id, role_id)
                VALUES (@UserId, @RoleId)
                ON CONFLICT DO NOTHING
                """,
                req);
            return Results.Created($"/api/admin/admin-user-roles?user_id={req.UserId}", new { success = true });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/admin-user-roles ───────────────────────────────────
        app.MapDelete("/api/admin/admin-user-roles", async (
            Guid                 userId,
            Guid                 roleId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM admin_user_roles WHERE user_id = @userId AND role_id = @roleId",
                new { userId, roleId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/roles ──────────────────────────────────────────────
        // Returns the admin_roles catalog. Supports optional ?q= filter.
        app.MapGet("/api/admin/roles", async (
            string?              q,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, name, key FROM admin_roles
                WHERE (@q IS NULL OR name ILIKE '%' || @q || '%' OR key ILIKE '%' || @q || '%')
                ORDER BY name ASC
                """,
                new { q });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/users/{userId}/roles ────────────────────────────────
        app.MapGet("/api/admin/users/{userId}/roles", async (
            Guid                 userId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var roles = await conn.QueryAsync<dynamic>(
                "SELECT role, is_active FROM user_roles WHERE user_id = @userId",
                new { userId });
            var verifiedRoles = await conn.QueryAsync<dynamic>(
                "SELECT role, status, is_active FROM verified_roles WHERE user_id = @userId",
                new { userId });
            return Results.Ok(new { userRoles = roles, verifiedRoles });
        }).RequireAuthorization("Admin");

        // ── PATCH /api/admin/users/{userId}/roles ──────────────────────────────
        app.MapPatch("/api/admin/users/{userId}/roles", async (
            Guid                      userId,
            [FromBody] UpdateRoleRequest req,
            HttpContext               ctx,
            IDbConnectionFactory      db,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM public.user_roles WHERE user_id = @id", new { id = userId });
            foreach (var role in (req.Roles ?? []))
            {
                await conn.ExecuteAsync(
                    "INSERT INTO public.user_roles (user_id, role) VALUES (@id, @role) ON CONFLICT DO NOTHING",
                    new { id = userId, role });
            }
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/users/{userId} ─────────────────────────────────────
        // Updates user's is_admin flag and/or replaces their admin_roles assignments.
        app.MapPut("/api/admin/users/{userId}", async (
            Guid                            userId,
            [FromBody] AdminUpdateUserRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory            db,
            CancellationToken               ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();

            if (req.IsAdmin.HasValue)
                await conn.ExecuteAsync(
                    "UPDATE profiles SET is_admin = @isAdmin WHERE id = @userId",
                    new { userId, isAdmin = req.IsAdmin.Value });

            if (req.AdminRoles is not null)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM admin_user_roles WHERE user_id = @userId",
                    new { userId });
                foreach (var roleId in req.AdminRoles)
                    await conn.ExecuteAsync(
                        "INSERT INTO admin_user_roles (user_id, role_id) VALUES (@userId, @roleId) ON CONFLICT DO NOTHING",
                        new { userId, roleId });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/verified-roles ──────────────────────────────────────
        app.MapGet("/api/admin/verified-roles", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT vr.user_id, vr.role, vr.status, vr.is_active, vr.verified_at,
                       p.username, p.email, p.avatar_url
                FROM verified_roles vr
                JOIN profiles p ON p.id = vr.user_id
                ORDER BY vr.verified_at DESC
                """);
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/users/{userId}/verified-roles ───────────────────────
        app.MapGet("/api/admin/users/{userId}/verified-roles", async (
            Guid                 userId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT role, status, is_active, verified_at FROM verified_roles WHERE user_id = @userId",
                new { userId });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/tournaments ─────────────────────────────────────────
        app.MapGet("/api/admin/tournaments", async (
            string?              status,
            int                  page   = 1,
            int                  limit  = 50,
            HttpContext          ctx    = default!,
            IDbConnectionFactory db     = default!,
            CancellationToken    ct     = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT t.*, p.username AS organizer_name
                FROM tournaments t
                LEFT JOIN profiles p ON p.id = t.organizer_id
                WHERE (@status IS NULL OR t.status::text = @status)
                ORDER BY t.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { status, limit, offset = (page - 1) * limit });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/tournaments/{id} ───────────────────────────────────
        // Updates tournament status and/or is_featured flag from admin panel.
        app.MapPut("/api/admin/tournaments/{id}", async (
            Guid                                id,
            [FromBody] AdminUpdateTournamentRequest req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.TournamentsEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE tournaments SET
                    status      = CASE WHEN @Status IS NOT NULL THEN @Status::tournament_status ELSE status END,
                    is_featured = COALESCE(@IsFeatured, is_featured),
                    updated_at  = now()
                WHERE id = @id
                """,
                new { id, req.Status, req.IsFeatured });

            return affected == 0 ? Results.NotFound() : Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/disputes ────────────────────────────────────────────
        app.MapGet("/api/admin/disputes", async (
            string?              status,
            int                  page   = 1,
            int                  limit  = 50,
            HttpContext          ctx    = default!,
            IDbConnectionFactory db     = default!,
            CancellationToken    ct     = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT td.*, t.name AS tournament_name, p.username AS raised_by_name
                FROM tournament_disputes td
                LEFT JOIN tournaments t ON t.id = td.tournament_id
                LEFT JOIN profiles p ON p.id = td.raised_by_user_id
                WHERE (@status IS NULL OR td.status = @status)
                ORDER BY td.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { status, limit, offset = (page - 1) * limit });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/disputes/{disputeId} ────────────────────────────────
        app.MapGet("/api/admin/disputes/{disputeId}", async (
            Guid                 disputeId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var dispute = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT td.*, t.name AS tournament_name, p.username AS raised_by_name
                FROM tournament_disputes td
                LEFT JOIN tournaments t ON t.id = td.tournament_id
                LEFT JOIN profiles p ON p.id = td.raised_by_user_id
                WHERE td.id = @disputeId
                """, new { disputeId });
            if (dispute is null) return Results.NotFound();

            var comments = await conn.QueryAsync<dynamic>(
                """
                SELECT dc.*, prof.username AS author_name
                FROM dispute_comments dc
                LEFT JOIN profiles prof ON prof.id = dc.user_id
                WHERE dc.dispute_id = @disputeId
                ORDER BY dc.created_at ASC
                """, new { disputeId });

            return Results.Ok(new { dispute, comments });
        }).RequireAuthorization("Admin");

        // ── PATCH /api/admin/disputes/{disputeId} ──────────────────────────────
        app.MapPatch("/api/admin/disputes/{disputeId}", async (
            Guid                              disputeId,
            [FromBody] AdminUpdateDisputeRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE tournament_disputes
                SET status           = COALESCE(@status, status),
                    resolution_notes = COALESCE(@notes, resolution_notes),
                    assigned_to_user_id = COALESCE(@assignedTo, assigned_to_user_id),
                    updated_at       = NOW()
                WHERE id = @disputeId
                """,
                new { disputeId, status = req.Status, notes = req.ResolutionNotes, assignedTo = req.AssignedToUserId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/disputes/{disputeId}/comments ──────────────────────
        app.MapPost("/api/admin/disputes/{disputeId}/comments", async (
            Guid                              disputeId,
            [FromBody] AddDisputeCommentRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO dispute_comments (dispute_id, user_id, comment, is_internal, attachment_url)
                VALUES (@disputeId, @userId, @comment, @isInternal, @attachmentUrl)
                """,
                new { disputeId, userId = userCtx.UserIdGuid, comment = req.Comment ?? "", isInternal = req.IsInternal, attachmentUrl = req.AttachmentUrl });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/audit-logs ──────────────────────────────────────────
        app.MapGet("/api/admin/audit-logs", async (
            Guid?                organizationId,
            int                  page  = 1,
            int                  limit = 50,
            HttpContext          ctx   = default!,
            IDbConnectionFactory db    = default!,
            CancellationToken    ct    = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT sal.*, p.username AS actor_name
                FROM staff_audit_log sal
                LEFT JOIN profiles p ON p.id = sal.actor_id
                WHERE (@organizationId IS NULL OR sal.organization_id = @organizationId)
                ORDER BY sal.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { organizationId, limit, offset = (page - 1) * limit });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/system-settings ────────────────────────────────────
        app.MapGet("/api/admin/system-settings", (HttpContext ctx) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            // Placeholder: no system_settings table yet — return empty config
            return Results.Ok(new { maintenanceMode = false, registrationsEnabled = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/verification-requests ──────────────────────────────
        app.MapGet("/api/admin/verification-requests", async (
            string?              status,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            // Use verified_roles as the source for pending/approved verification requests
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT vr.user_id, vr.role, vr.status, vr.is_active, vr.verified_at,
                       p.username, p.email, p.avatar_url, p.full_name
                FROM verified_roles vr
                JOIN profiles p ON p.id = vr.user_id
                WHERE (@status IS NULL OR vr.status = @status)
                ORDER BY vr.verified_at DESC NULLS LAST
                """,
                new { status });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── PATCH /api/admin/verification-requests/{userId} ───────────────────
        app.MapPatch("/api/admin/verification-requests/{userId}", async (
            Guid                              userId,
            [FromBody] ApproveVerificationRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE verified_roles
                SET status     = @status,
                    is_active  = @isActive,
                    verified_at = CASE WHEN @status = 'approved' THEN NOW() ELSE verified_at END
                WHERE user_id = @userId AND role = @role
                """,
                new { userId, status = req.Status, isActive = req.Status == "approved", role = req.Role });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/company-profiles ───────────────────────────────────
        app.MapGet("/api/admin/company-profiles", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            // Return sponsor_accounts as the closest match to company profiles
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT sa.*, p.username, p.email, p.avatar_url
                FROM sponsor_accounts sa
                JOIN profiles p ON p.id = sa.user_id
                ORDER BY sa.created_at DESC
                """);
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── GET /api/users/{userId}/role ───────────────────────────────────────
        app.MapGet("/api/users/{userId}/role", async (
            Guid                 userId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var roles = (await conn.QueryAsync<string>(
                "SELECT role FROM user_roles WHERE user_id = @userId AND is_active = TRUE",
                new { userId })).ToList();
            return Results.Ok(new { userId, roles });
        });

        // ── PUT /api/users/{userId}/role ───────────────────────────────────────
        app.MapPut("/api/users/{userId}/role", async (
            Guid                      userId,
            [FromBody] SetRoleRequest req,
            HttpContext               ctx,
            IDbConnectionFactory      db,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM public.user_roles WHERE user_id = @id", new { id = userId });
            if (!string.IsNullOrWhiteSpace(req.Role))
                await conn.ExecuteAsync(
                    "INSERT INTO public.user_roles (user_id, role) VALUES (@id, @role)",
                    new { id = userId, role = req.Role });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/sponsors/applications ────────────────────────────────────
        app.MapGet("/api/sponsors/applications", async (
            string?              status,
            int                  page  = 1,
            int                  limit = 50,
            HttpContext          ctx   = default!,
            IDbConnectionFactory db    = default!,
            CancellationToken    ct    = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT pa.*
                FROM partner_applications pa
                WHERE (@status IS NULL OR pa.status = @status)
                ORDER BY pa.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { status, limit, offset = (page - 1) * limit });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── GET /api/sponsors/applications/{id} ───────────────────────────────
        app.MapGet("/api/sponsors/applications/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM partner_applications WHERE id = @id", new { id });
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Admin");

        // ── PATCH /api/sponsors/applications/{id} ─────────────────────────────
        app.MapPatch("/api/sponsors/applications/{id}", async (
            Guid                                  id,
            [FromBody] UpdateApplicationRequest   req,
            HttpContext                           ctx,
            IDbConnectionFactory                  db,
            CancellationToken                     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE partner_applications
                SET status     = COALESCE(@status, status),
                    admin_notes      = COALESCE(@notes, admin_notes),
                    updated_at = NOW()
                WHERE id = @id
                """,
                new { id, status = req.Status, notes = req.Notes });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");
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

// ── Admin request records ─────────────────────────────────────────────────────
public sealed record AssignRoleRequest(Guid UserId, string Role);
public sealed record UpdateRoleRequest(string[]? Roles = null);
public sealed record SetRoleRequest(string Role);
public sealed record AdminUpdateDisputeRequest(string? Status = null, string? ResolutionNotes = null, Guid? AssignedToUserId = null);
public sealed record ApproveVerificationRequest(string Status, string Role);
public sealed record UpdateApplicationRequest(string? Status = null, string? Notes = null);
public sealed record AdminUserRoleAssignRequest(Guid UserId, Guid RoleId);
public sealed record AdminUpdateTournamentRequest(string? Status = null, bool? IsFeatured = null);
public sealed record AdminUpdateUserRequest(bool? IsAdmin = null, Guid[]? AdminRoles = null);
