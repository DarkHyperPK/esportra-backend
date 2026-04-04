using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Supabase;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Hybrid;
using Esportra.Api.Hubs;

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
        // Actions: "delete-user", "update-role", "assign_role", "revoke_role"
        app.MapPost("/api/admin/users/{userId}/action", async (
            Guid                     userId,
            [FromBody] ManageUserRequest req,
            IDbConnectionFactory     db,
            ISupabaseAdminClient     supabase,
            HttpContext              ctx,
            CancellationToken        ct) =>
        {
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
                "assign_role" => await AssignRoleToUserAsync(userId, req, conn, ct),
                "revoke_role" => await RevokeRoleFromUserAsync(userId, req, conn, ct),
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
                LIMIT 200
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
                    COUNT(*) FILTER (WHERE event_type = 'impression')   AS impressions,
                    COUNT(*) FILTER (WHERE event_type = 'click')        AS clicks,
                    COUNT(DISTINCT visitor_id)                          AS unique_visitors
                FROM sponsor_impressions
                WHERE sponsor_id = @id
                """, new { id });

            long impressions = stats?.impressions ?? 0;
            long clicks = stats?.clicks ?? 0;
            var ctr = impressions > 0
                ? $"{Math.Round((double)clicks / impressions * 100, 2)}%"
                : "0%";

            return Results.Ok(new
            {
                impressions,
                clicks,
                unique_visitors = (long)(stats?.unique_visitors ?? 0),
                ctr,
            });
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

            if (!Guid.TryParse(req.SponsorId, out var sponsorId))
                return Results.BadRequest(new { error = "Invalid SponsorId." });

            using var conn = db.CreateConnection();

            // Get sponsor name
            var sponsorName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM public.sponsors WHERE id = @id", new { id = sponsorId });

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
                    """, new { userId = Guid.Parse(sponsorUserId), sponsorId });

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
                    new { sponsor_id = sponsorId.ToString() }, ct);
                sponsorUserId = newUser.Id;

                await conn.ExecuteAsync("""
                    INSERT INTO public.sponsor_accounts (user_id, sponsor_id, role, onboarding_meta)
                    VALUES (@userId, @sponsorId, 'owner', '{}')
                    ON CONFLICT DO NOTHING
                    """, new { userId = Guid.Parse(sponsorUserId), sponsorId });

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
                if (Guid.TryParse(req.ApplicationId, out var appId))
                {
                    await conn.ExecuteAsync(
                        "UPDATE public.partner_applications SET status = 'approved' WHERE id = @id",
                        new { id = appId });
                }
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
            [FromQuery] string?  role   = null,
            CancellationToken    ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
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

            // Role filtering via JOIN
            if (!string.IsNullOrWhiteSpace(role))
            {
                if (role == "admin")
                    conditions.Add("EXISTS (SELECT 1 FROM admin_user_roles aur WHERE aur.user_id = p.id)");
                else if (role == "casual")
                    conditions.Add("NOT EXISTS (SELECT 1 FROM user_roles ur2 WHERE ur2.user_id = p.id AND ur2.is_active = TRUE) AND NOT EXISTS (SELECT 1 FROM admin_user_roles aur2 WHERE aur2.user_id = p.id)");
                else
                    conditions.Add("EXISTS (SELECT 1 FROM user_roles ur2 WHERE ur2.user_id = p.id AND ur2.role = @role AND ur2.is_active = TRUE)");
            }

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
                    p.created_at
                FROM profiles p
                {where}
                ORDER BY p.created_at DESC
                LIMIT @limit OFFSET @offset
                """;

            var users = await conn.QueryAsync<dynamic>(sql,
                new { search = $"%{search}%", limit, offset, role });

            // Fetch roles for these users in a single query
            var userIds = users.Select(u => (Guid)u.id).ToList();
            var rolesMap = new Dictionary<Guid, List<string>>();
            if (userIds.Count > 0)
            {
                var roleRows = await conn.QueryAsync<dynamic>(
                    "SELECT user_id, role FROM user_roles WHERE user_id = ANY(@ids) AND is_active = TRUE",
                    new { ids = userIds.ToArray() });
                foreach (var r in roleRows)
                {
                    var uid = (Guid)r.user_id;
                    if (!rolesMap.ContainsKey(uid)) rolesMap[uid] = new List<string>();
                    rolesMap[uid].Add((string)r.role);
                }
            }

            var enriched = users.Select(u => {
                var uid = (Guid)u.id;
                return new {
                    u.id, u.username, u.email, u.full_name,
                    u.avatar_url, u.is_suspended, u.created_at,
                    roles = rolesMap.ContainsKey(uid) ? rolesMap[uid].ToArray() : Array.Empty<string>()
                };
            });

            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM profiles p {where}",
                new { search = $"%{search}%", role });

            // Role breakdown counts (unfiltered — always reflects full platform)
            var roleCountRows = await conn.QueryAsync<dynamic>(
                "SELECT role, COUNT(DISTINCT user_id)::int AS count FROM user_roles WHERE is_active = TRUE GROUP BY role");
            var roleCounts = new Dictionary<string, int>();
            foreach (var r in roleCountRows)
                roleCounts[(string)r.role] = (int)r.count;

            var adminCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(DISTINCT user_id) FROM admin_user_roles");

            return Results.Ok(new { users = enriched, total, roleCounts, adminCount });
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
                return Results.BadRequest(new { error = "Event type must be 'impression' or 'click'." });

            if (!Guid.TryParse(req.SponsorId, out var sponsorId))
                return Results.BadRequest(new { error = "Invalid sponsor ID." });

            using var conn = db.CreateConnection();

            // Verify sponsor exists before inserting (avoids FK violation)
            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM sponsors WHERE id = @sponsorId)",
                new { sponsorId });

            if (!exists)
                return Results.Ok(new { success = true, tracked = false });

            // Visitor ID: authenticated user ID, or hash of IP+UA for anonymous
            var userCtx = ctx.Items["UserContext"] as UserContext;
            string? visitorId = userCtx?.UserId;
            if (string.IsNullOrEmpty(visitorId))
            {
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var forwarded = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
                if (!string.IsNullOrEmpty(forwarded))
                    ip = forwarded.Split(',')[0].Trim();
                var ua = ctx.Request.Headers.UserAgent.FirstOrDefault() ?? "";
                var raw = $"{ip}:{ua}";
                var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
                visitorId = $"anon_{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
            }

            // Build metadata from request headers + profile country
            var clientIp = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                       ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = ctx.Request.Headers.UserAgent.FirstOrDefault() ?? "";
            var referer = ctx.Request.Headers.Referer.FirstOrDefault() ?? "";

            // Look up country from profile if authenticated
            string country = "unknown";
            if (userCtx is not null)
            {
                country = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT country_code FROM profiles WHERE id = @userId",
                    new { userId = userCtx.UserIdGuid }) ?? "unknown";
            }

            var metadata = System.Text.Json.JsonSerializer.Serialize(new
            {
                ip = clientIp,
                user_agent = userAgent,
                referer,
                country,
            });

            Guid? tournamentId = null;
            if (!string.IsNullOrEmpty(req.TournamentId) && Guid.TryParse(req.TournamentId, out var tid))
                tournamentId = tid;

            await conn.ExecuteAsync(
                """
                INSERT INTO sponsor_impressions (sponsor_id, event_type, page_url, visitor_id, metadata, tournament_id)
                VALUES (@sponsorId, @eventType, @pageUrl, @visitorId, @metadata::jsonb, @tournamentId)
                """,
                new
                {
                    sponsorId,
                    eventType = req.EventType,
                    pageUrl   = req.PageUrl,
                    visitorId,
                    metadata,
                    tournamentId,
                });

            return Results.Ok(new { success = true, tracked = true });
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
                    (SELECT COUNT(*) FROM tournaments WHERE status IN ('open', 'check_in', 'ongoing')) AS active_tournaments,
                    (SELECT COUNT(*) FROM verification_requests WHERE status = 'pending') AS pending_verifications,
                    (SELECT COUNT(*) FROM profiles WHERE created_at >= NOW() - INTERVAL '1 day') AS new_users_today,
                    (SELECT COUNT(*) FROM venues WHERE status = 'pending_review' AND deleted_at IS NULL) AS pending_venues,
                    (SELECT COUNT(*) FROM licenses WHERE status = 'pending') AS pending_licenses
                """);
            return Results.Ok(new {
                totalUsers = (long)row.total_users,
                activeVenues = (long)row.active_venues,
                activeTournaments = (long)row.active_tournaments,
                totalRevenue = 0,
                pendingVerifications = (long)row.pending_verifications,
                totalBookings = 0,
                newUsersToday = (long)row.new_users_today,
                pendingPartners = 0,
                pendingVenues = (long)row.pending_venues,
                pendingLicenses = (long)row.pending_licenses
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
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            var j = System.Text.Json.JsonDocument.Parse(json).RootElement;

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO sponsors
                    (name, tagline, description, website_url, logo_url, banner_image_url,
                     accent_color, tier, placement, cta_text, discount_text,
                     is_active, priority, gallery_images)
                VALUES
                    (@name, @tagline, @description, @websiteUrl, @logoUrl, @bannerImageUrl,
                     @accentColor, @tier, @placement::text[], @ctaText, @discountText,
                     @isActive, @priority, @galleryImages::text[])
                RETURNING id, name, tagline, description, website_url, logo_url, banner_image_url,
                         accent_color, tier, placement, cta_text, discount_text,
                         is_active, priority, gallery_images, created_at
                """,
                new
                {
                    name           = j.TryGetProperty("name", out var n) ? n.GetString() : null,
                    tagline        = j.TryGetProperty("tagline", out var tl) ? tl.GetString() : null,
                    description    = j.TryGetProperty("description", out var d) ? d.GetString() : null,
                    websiteUrl     = j.TryGetProperty("website_url", out var wu) ? wu.GetString() : null,
                    logoUrl        = j.TryGetProperty("logo_url", out var lu) ? lu.GetString() : null,
                    bannerImageUrl = j.TryGetProperty("banner_image_url", out var bi) ? bi.GetString() : null,
                    accentColor    = j.TryGetProperty("accent_color", out var ac) ? ac.GetString() : "#8b5cf6",
                    tier           = j.TryGetProperty("tier", out var ti) ? ti.GetString() : "standard",
                    placement      = j.TryGetProperty("placement", out var pl)
                        ? "{" + string.Join(",", pl.EnumerateArray().Select(e => e.GetString())) + "}"
                        : "{banner}",
                    ctaText        = j.TryGetProperty("cta_text", out var ct2) ? ct2.GetString() : "Learn More",
                    discountText   = j.TryGetProperty("discount_text", out var dt) ? dt.GetString() : null,
                    isActive       = !j.TryGetProperty("is_active", out var ia) || ia.GetBoolean(),
                    priority       = j.TryGetProperty("priority", out var pr) ? pr.GetInt32() : 0,
                    galleryImages  = j.TryGetProperty("gallery_images", out var gi)
                        ? "{" + string.Join(",", gi.EnumerateArray().Select(e => e.GetString())) + "}"
                        : "{}",
                });
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
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
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
            if (!userCtx.Permissions.Contains(Permissions.UsersBan)) return Results.Forbid();
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE profiles
                SET is_suspended = true,
                    suspension_reason = @reason,
                    updated_at = NOW()
                WHERE id = @userId
                """,
                new { userId, reason = req.Reason });
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
            if (!userCtx.Permissions.Contains(Permissions.UsersBan)) return Results.Forbid();
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE profiles
                SET is_suspended = false,
                    suspension_reason = null,
                    suspension_type = null,
                    suspension_until = null,
                    updated_at = NOW()
                WHERE id = @userId
                """,
                new { userId });
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
                { "TournamentRegistration", "Welcome", "TeamInvite" };
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
            catch (Exception)
            {
                return Results.Json(new { error = "We couldn't send the email. Please try again." }, statusCode: 502);
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
                LIMIT 500
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
                    LIMIT 50
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
                    LIMIT 500
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
                LIMIT 100
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
            using var txn = conn.BeginTransaction();
            await conn.ExecuteAsync(
                "DELETE FROM public.user_roles WHERE user_id = @id", new { id = userId }, txn);
            foreach (var role in (req.Roles ?? []))
            {
                await conn.ExecuteAsync(
                    "INSERT INTO public.user_roles (user_id, role) VALUES (@id, @role) ON CONFLICT DO NOTHING",
                    new { id = userId, role }, txn);
            }
            txn.Commit();
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
            using var txn = conn.BeginTransaction();

            if (req.IsAdmin.HasValue)
                await conn.ExecuteAsync(
                    "UPDATE profiles SET is_admin = @isAdmin WHERE id = @userId",
                    new { userId, isAdmin = req.IsAdmin.Value }, txn);

            if (req.AdminRoles is not null)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM admin_user_roles WHERE user_id = @userId",
                    new { userId }, txn);
                foreach (var roleId in req.AdminRoles)
                    await conn.ExecuteAsync(
                        "INSERT INTO admin_user_roles (user_id, role_id) VALUES (@userId, @roleId) ON CONFLICT DO NOTHING",
                        new { userId, roleId }, txn);
            }

            txn.Commit();

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
                LIMIT 500
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
            limit = Math.Clamp(limit, 1, 100);
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
            limit = Math.Clamp(limit, 1, 100);
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
        // Also mapped as PUT for frontend compatibility
        async Task<IResult> AdminUpdateDispute(
            Guid                              disputeId,
            [FromBody] AdminUpdateDisputeRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct)
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
        }
        app.MapPatch("/api/admin/disputes/{disputeId}", AdminUpdateDispute).RequireAuthorization("Admin");
        app.MapPut("/api/admin/disputes/{disputeId}", AdminUpdateDispute).RequireAuthorization("Admin");

        // ── GET /api/admin/disputes/{disputeId}/comments ──────────────────────
        app.MapGet("/api/admin/disputes/{disputeId}/comments", async (
            Guid                 disputeId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT dc.id, dc.dispute_id, dc.user_id, dc.comment, dc.is_internal,
                       dc.attachment_url, dc.created_at,
                       COALESCE(p.full_name, p.username, 'Unknown') AS user_name,
                       p.avatar_url AS user_avatar
                FROM dispute_comments dc
                LEFT JOIN profiles p ON p.id = dc.user_id
                WHERE dc.dispute_id = @disputeId
                ORDER BY dc.created_at ASC
                """,
                new { disputeId });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/disputes/{disputeId}/comments ──────────────────────
        app.MapPost("/api/admin/disputes/{disputeId}/comments", async (
            Guid                              disputeId,
            [FromBody] AddDisputeCommentRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            IHubContext<MatchHub>             matchHub,
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

            // Scope broadcast to the match group instead of all clients
            var matchId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT match_id FROM tournament_disputes WHERE id = @disputeId", new { disputeId });
            if (matchId is not null)
                await matchHub.Clients.Group(MatchHub.MatchGroup(matchId.Value.ToString()))
                    .SendAsync(MatchHubEvents.DisputeCommentAdded,
                        new { disputeId, userId = userCtx.UserIdGuid.ToString() }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/tournaments/{tournamentId}/bans/{banId} ──────────
        app.MapDelete("/api/admin/tournaments/{tournamentId}/bans/{banId}", async (
            Guid                 tournamentId,
            Guid                 banId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var ban = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE tournament_bans
                SET is_active = FALSE, lifted_by = @liftedBy, lifted_at = NOW()
                WHERE id = @banId AND tournament_id = @tournamentId AND is_active = TRUE
                RETURNING participant_id
                """, new { banId, tournamentId, liftedBy = userCtx.UserIdGuid });

            if (ban is null)
                return Results.NotFound(new { error = "Ban not found or already lifted." });

            // Restore participant if disqualified and no other active bans
            if (ban.participant_id is not null)
            {
                var otherBans = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM tournament_bans WHERE participant_id = @pid AND is_active = TRUE",
                    new { pid = (Guid)ban.participant_id });

                if (otherBans == 0)
                {
                    await conn.ExecuteAsync(
                        "UPDATE tournament_participants SET status = 'approved' WHERE id = @pid AND status = 'disqualified'",
                        new { pid = (Guid)ban.participant_id });
                }
            }

            return Results.Ok(new { success = true, participantRestored = ban.participant_id is not null });
        }).RequireAuthorization("Admin");

        // ── PATCH /api/admin/tournaments/{tournamentId}/participants/{pid}/restore ──
        app.MapPatch("/api/admin/tournaments/{tournamentId}/participants/{pid}/restore", async (
            Guid                 tournamentId,
            Guid                 pid,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify participant belongs to this tournament
            var affected = await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET status = 'approved'
                WHERE id = @pid AND tournament_id = @tournamentId
                  AND status IN ('disqualified', 'rejected', 'cancelled')
                """, new { pid, tournamentId });

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Participant not found or already active." });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/disputes/{disputeId}/lift-ban ─────────────────────
        app.MapPost("/api/admin/disputes/{disputeId}/lift-ban", async (
            Guid                 disputeId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Get dispute details
            var dispute = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT tournament_id, raised_by_user_id, team_id, dispute_reason FROM tournament_disputes WHERE id = @disputeId",
                new { disputeId });

            if (dispute is null)
                return Results.NotFound(new { error = "Dispute not found." });

            if ((string)dispute.dispute_reason != "ban_appeal")
                return Results.BadRequest(new { error = "This action is only available for ban appeal disputes." });

            // Find the active ban matching this dispute
            Guid? tournamentId = dispute.tournament_id;
            Guid? userId = dispute.raised_by_user_id;
            Guid? teamId = dispute.team_id;

            if (tournamentId is null)
                return Results.BadRequest(new { error = "Dispute has no associated tournament." });

            var ban = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, participant_id FROM tournament_bans
                WHERE tournament_id = @tournamentId AND is_active = TRUE
                  AND (user_id = @userId OR team_id = @teamId)
                LIMIT 1
                """, new { tournamentId, userId, teamId });

            if (ban is null)
                return Results.NotFound(new { error = "No active ban found for this dispute." });

            // Lift the ban
            await conn.ExecuteAsync(
                "UPDATE tournament_bans SET is_active = FALSE, lifted_by = @liftedBy, lifted_at = NOW() WHERE id = @banId",
                new { banId = (Guid)ban.id, liftedBy = userCtx.UserIdGuid });

            // Restore participant if disqualified
            bool restored = false;
            if (ban.participant_id is not null)
            {
                var otherBans = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM tournament_bans WHERE participant_id = @pid AND is_active = TRUE",
                    new { pid = (Guid)ban.participant_id });

                if (otherBans == 0)
                {
                    var rows = await conn.ExecuteAsync(
                        "UPDATE tournament_participants SET status = 'approved' WHERE id = @pid AND status = 'disqualified'",
                        new { pid = (Guid)ban.participant_id });
                    restored = rows > 0;
                }
            }

            // Auto-resolve the dispute
            await conn.ExecuteAsync(
                """
                UPDATE tournament_disputes
                SET status = 'resolved', resolution_notes = 'Ban lifted by admin.', updated_at = NOW()
                WHERE id = @disputeId AND status != 'resolved'
                """, new { disputeId });

            return Results.Ok(new { success = true, banLifted = true, participantRestored = restored });
        }).RequireAuthorization("Admin");
        app.MapGet("/api/admin/audit-logs", async (
            Guid?                organizationId,
            [FromQuery] string?  search      = null,
            [FromQuery] string?  target_type = null,
            [FromQuery] string?  from        = null,
            [FromQuery] string?  to          = null,
            int                  page        = 1,
            int                  limit       = 50,
            HttpContext          ctx          = default!,
            IDbConnectionFactory db           = default!,
            CancellationToken    ct           = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            conditions.Add("(@organizationId IS NULL OR sal.organization_id = @organizationId)");

            if (!string.IsNullOrWhiteSpace(search))
                conditions.Add("(p.username ILIKE @search OR sal.action ILIKE @search OR sal.target_type ILIKE @search)");
            if (!string.IsNullOrWhiteSpace(target_type))
                conditions.Add("sal.target_type = @target_type");

            DateTimeOffset? fromDate = null;
            DateTimeOffset? toDate = null;
            if (!string.IsNullOrWhiteSpace(from) && DateTimeOffset.TryParse(from, out var fd))
            {
                fromDate = fd;
                conditions.Add("sal.created_at >= @fromDate");
            }
            if (!string.IsNullOrWhiteSpace(to) && DateTimeOffset.TryParse(to, out var td))
            {
                toDate = td;
                conditions.Add("sal.created_at <= @toDate");
            }

            var where = "WHERE " + string.Join(" AND ", conditions);

            var sql = $"""
                SELECT sal.*, p.username AS actor_name
                FROM staff_audit_log sal
                LEFT JOIN profiles p ON p.id = sal.actor_id
                {where}
                ORDER BY sal.created_at DESC
                LIMIT @limit OFFSET @offset
                """;

            var rows = await conn.QueryAsync<dynamic>(sql,
                new { organizationId, search = $"%{search}%", target_type, fromDate, toDate, limit, offset = (page - 1) * limit });

            var countSql = $"SELECT COUNT(*) FROM staff_audit_log sal LEFT JOIN profiles p ON p.id = sal.actor_id {where}";
            var total = await conn.ExecuteScalarAsync<int>(countSql,
                new { organizationId, search = $"%{search}%", target_type, fromDate, toDate });

            return Results.Ok(new { data = rows, count = total });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/audit-logs ─────────────────────────────────────────
        app.MapPost("/api/admin/audit-logs", async (
            [FromBody] CreateAuditLogRequest req,
            HttpContext                      ctx,
            IDbConnectionFactory             db,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var details = new
            {
                admin_name  = req.AdminName,
                target_name = req.TargetName,
                severity    = req.Severity,
                user_agent  = req.UserAgent,
                extra       = req.Details,
            };

            await conn.ExecuteAsync(
                """
                INSERT INTO staff_audit_log (organization_id, actor_id, action, target_type, target_id, details)
                VALUES (@orgId, @actorId, @action, @targetType, @targetId, @details::jsonb)
                """,
                new
                {
                    orgId      = (Guid?)null,
                    actorId    = userCtx.UserIdGuid,
                    action     = req.ActionType,
                    targetType = req.TargetType,
                    targetId   = Guid.TryParse(req.TargetId, out var tid) ? tid : (Guid?)null,
                    details    = System.Text.Json.JsonSerializer.Serialize(details),
                });

            return Results.Ok(new { success = true });
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
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT vr.id, vr.user_id, vr.requested_role, vr.status,
                       vr.first_name, vr.last_name, vr.business_name, vr.business_type,
                       vr.business_description, vr.email AS contact_email,
                       vr.cnic_front_url, vr.cnic_back_url,
                       vr.verification_notes AS notes,
                       vr.created_at, vr.updated_at,
                       p.username  AS profile_username,
                       p.full_name AS profile_full_name,
                       p.email     AS profile_email
                FROM verification_requests vr
                JOIN profiles p ON p.id = vr.user_id
                WHERE (@status IS NULL OR vr.status = @status)
                ORDER BY vr.created_at DESC
                LIMIT 200
                """,
                new { status });
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── PATCH/PUT /api/admin/verification-requests/{requestId} ────────────
        async Task<IResult> AdminUpdateVerificationRequest(
            Guid                              requestId,
            [FromBody] UpdateVerificationRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            IEmailService                     email,
            IConfiguration                    config,
            CancellationToken                 ct)
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();
            if (req.Status is not ("approved" or "rejected"))
                return Results.BadRequest(new { error = "Status must be approved or rejected." });

            using var conn = db.CreateConnection();
            var vr = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT user_id, requested_role FROM verification_requests WHERE id = @requestId",
                new { requestId });
            if (vr is null) return Results.NotFound(new { error = "Verification request not found" });

            await conn.ExecuteAsync(
                """
                UPDATE verification_requests
                SET status     = @status,
                    updated_at = NOW()
                WHERE id = @requestId
                """,
                new { requestId, status = req.Status });

            // Keep role/license sync here so UI only needs approve/reject.
            var role = ((string)vr.requested_role).Trim().ToLowerInvariant();
            var userId = (Guid)vr.user_id;
            if (req.Status == "approved")
            {
                var expiresAt = DateTime.UtcNow.AddYears(1);
                await conn.ExecuteAsync(
                    "UPDATE user_roles SET is_active = TRUE WHERE user_id = @userId AND role = @role",
                    new { userId, role });
                await conn.ExecuteAsync(
                    """
                    INSERT INTO user_roles (user_id, role, is_active)
                    SELECT @userId, @role, TRUE
                    WHERE NOT EXISTS (SELECT 1 FROM user_roles WHERE user_id = @userId AND role = @role)
                    """,
                    new { userId, role });

                if (role is "organizer" or "venue_owner")
                {
                    await conn.ExecuteAsync(
                        "UPDATE verified_roles SET status = 'approved', is_active = TRUE, verified_at = NOW() WHERE user_id = @userId AND role::text = @role",
                        new { userId, role });
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO verified_roles (user_id, role, status, is_active, verified_at)
                        SELECT @userId, @role::app_role, 'approved', TRUE, NOW()
                        WHERE NOT EXISTS (SELECT 1 FROM verified_roles WHERE user_id = @userId AND role::text = @role)
                        """,
                        new { userId, role });
                    await conn.ExecuteAsync(
                        "UPDATE profiles SET role = @role::app_role WHERE id = @userId AND role = 'casual'::app_role",
                        new { userId, role });
                }

                var existingLicenseId = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT license_id FROM licenses WHERE user_id = @userId AND license_type = @licenseType LIMIT 1",
                    new { userId, licenseType = role });
                var issuedAt = DateTime.UtcNow;
                if (existingLicenseId is not null)
                {
                    await conn.ExecuteAsync(
                        "UPDATE licenses SET status = 'active', issued_at = @issuedAt, expires_at = @expiresAt WHERE user_id = @userId AND license_type = @licenseType",
                        new { userId, licenseType = role, issuedAt, expiresAt });
                }
                else
                {
                    var prefix = role switch
                    {
                        "organizer" => "ESP-OR",
                        "venue_owner" => "ESP-VO",
                        "broadcaster" => "ESP-BR",
                        _ => "ESP-XX"
                    };
                    existingLicenseId = $"{prefix}-{Random.Shared.Next(100000, 999999)}";
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO licenses (user_id, license_id, license_type, status, issued_at, expires_at)
                        VALUES (@userId, @licenseId, @licenseType, 'active', @issuedAt, @expiresAt)
                        """,
                        new { userId, licenseId = existingLicenseId, licenseType = role, issuedAt, expiresAt });
                }
            }
            else
            {
                await conn.ExecuteAsync(
                    "UPDATE user_roles SET is_active = FALSE WHERE user_id = @userId AND role = @role",
                    new { userId, role });
                if (role is "organizer" or "venue_owner")
                {
                    await conn.ExecuteAsync(
                        "UPDATE verified_roles SET status = 'rejected', is_active = FALSE WHERE user_id = @userId AND role::text = @role",
                        new { userId, role });
                    await conn.ExecuteAsync(
                        "UPDATE profiles SET role = 'casual'::app_role WHERE id = @userId AND role::text = @role",
                        new { userId, role });
                }
            }

            var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT email, username FROM profiles WHERE id = @id",
                new { id = userId });
            if (profile?.email is not null)
            {
                var frontendUrl = config["FrontendUrl"] ?? "https://esportra.com";
                if (req.Status == "approved")
                {
                    var lic = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        "SELECT license_id, issued_at, expires_at FROM licenses WHERE user_id = @userId AND license_type = @licenseType ORDER BY issued_at DESC NULLS LAST LIMIT 1",
                        new { userId, licenseType = role });
                    await email.SendAsync(
                        (string)profile.email,
                        EmailType.LicenseApproved,
                        new
                        {
                            username = (string?)profile.username ?? "there",
                            licenseType = role,
                            licenseId = (string?)lic?.license_id ?? string.Empty,
                            issuedAt = ((DateTime?)lic?.issued_at ?? DateTime.UtcNow).ToString("MMM dd, yyyy"),
                            expiresAt = ((DateTime?)lic?.expires_at ?? DateTime.UtcNow.AddYears(1)).ToString("MMM dd, yyyy"),
                            dashboardUrl = $"{frontendUrl}/verification-status",
                        },
                        ct);
                }
                else
                {
                    await email.SendAsync(
                        (string)profile.email,
                        EmailType.LicenseRejected,
                        new
                        {
                            username = (string?)profile.username ?? "there",
                            licenseType = role,
                            dashboardUrl = $"{frontendUrl}/verification-status",
                        },
                        ct);
                }
            }
            return Results.Ok(new { success = true });
        }
        app.MapPatch("/api/admin/verification-requests/{requestId}", AdminUpdateVerificationRequest)
           .RequireAuthorization("Admin");
        app.MapPut("/api/admin/verification-requests/{requestId}", AdminUpdateVerificationRequest)
           .RequireAuthorization("Admin");

        // ── POST /api/admin/verified-roles ────────────────────────────────────
        app.MapPost("/api/admin/verified-roles", async (
            [FromBody] CreateVerifiedRoleRequest req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            IEmailService                       email,
            IConfiguration                      config,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO verified_roles (user_id, role, status, is_active, verified_at)
                VALUES (@userId, @role, @status, @isActive, NOW())
                ON CONFLICT (user_id, role) DO UPDATE
                SET status = @status, is_active = @isActive, verified_at = NOW()
                """,
                new { userId = req.UserId, role = req.Role, status = req.Status, isActive = req.IsActive });

            // Create/update license record and send approval email
            if (string.Equals(req.Status, "approved", StringComparison.OrdinalIgnoreCase) && req.IsActive)
            {
                try
                {
                    var issuedAt  = DateTime.UtcNow;
                    var expiresAt = issuedAt.AddYears(1);

                    // Check if license record already exists
                    var existingLicenseId = await conn.QuerySingleOrDefaultAsync<string>(
                        "SELECT license_id FROM licenses WHERE user_id = @userId AND license_type = @licenseType LIMIT 1",
                        new { userId = req.UserId, licenseType = req.Role });

                    if (existingLicenseId is not null)
                    {
                        // Renew existing license
                        await conn.ExecuteAsync(
                            """
                            UPDATE licenses SET status = 'active', issued_at = @issuedAt, expires_at = @expiresAt
                            WHERE user_id = @userId AND license_type = @licenseType
                            """,
                            new { userId = req.UserId, licenseType = req.Role, issuedAt, expiresAt });
                    }
                    else
                    {
                        // Generate new license_id
                        var prefix = req.Role switch
                        {
                            "organizer"   => "ESP-OR",
                            "venue_owner" => "ESP-VO",
                            "broadcaster" => "ESP-BR",
                            _             => "ESP-XX"
                        };
                        existingLicenseId = $"{prefix}-{Random.Shared.Next(100000, 999999)}";

                        await conn.ExecuteAsync(
                            """
                            INSERT INTO licenses (user_id, license_id, license_type, status, issued_at, expires_at)
                            VALUES (@userId, @licenseId, @licenseType, 'active', @issuedAt, @expiresAt)
                            """,
                            new { userId = req.UserId, licenseId = existingLicenseId, licenseType = req.Role, issuedAt, expiresAt });
                    }

                    var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        "SELECT email, username FROM profiles WHERE id = @id",
                        new { id = req.UserId });
                    if (profile?.email is not null)
                    {
                        var frontendUrl = config["FrontendUrl"] ?? "https://esportra.com";
                        await email.SendAsync(
                            (string)profile.email,
                            EmailType.LicenseApproved,
                            new
                            {
                                username     = (string?)profile.username ?? "there",
                                licenseType  = req.Role,
                                licenseId    = existingLicenseId,
                                issuedAt     = issuedAt.ToString("MMM dd, yyyy"),
                                expiresAt    = expiresAt.ToString("MMM dd, yyyy"),
                                dashboardUrl = $"{frontendUrl}/verification-status",
                            },
                            ct);
                    }
                }
                catch { /* email/license failure should not block approval */ }
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/licenses ───────────────────────────────────────────
        // List all licenses with user info, supports ?status=, ?type=, ?q= search
        app.MapGet("/api/admin/licenses", async (
            string?              status,
            string?              type,
            string?              q,
            int?                 limit,
            int?                 offset,
            HttpContext          ctx,
            IDbConnectionFactory db,
            ILogger<Program>     logger,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                using var conn = db.CreateConnection();
                var where = new List<string>();
                var p = new Dapper.DynamicParameters();

                if (!string.IsNullOrWhiteSpace(status)) { where.Add("l.status = @status"); p.Add("status", status); }
                if (!string.IsNullOrWhiteSpace(type))   { where.Add("l.license_type = @type"); p.Add("type", type); }
                if (!string.IsNullOrWhiteSpace(q))       { where.Add("(p.username ILIKE @q OR p.email ILIKE @q OR l.license_id ILIKE @q)"); p.Add("q", $"%{q}%"); }

                var lim = Math.Min(limit ?? 50, 200);
                var off = offset ?? 0;
                p.Add("lim", lim);
                p.Add("off", off);

                var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
                var sql = $"SELECT l.id, l.user_id, l.license_id, l.license_type, l.status, l.issued_at, l.expires_at, l.notes, l.created_at, p.username, p.email, p.avatar_url, p.full_name FROM licenses l JOIN profiles p ON p.id = l.user_id {whereClause} ORDER BY l.created_at DESC LIMIT @lim OFFSET @off";
                var rows = await conn.QueryAsync<dynamic>(sql, p);

                var countP = new Dapper.DynamicParameters();
                if (!string.IsNullOrWhiteSpace(status)) countP.Add("status", status);
                if (!string.IsNullOrWhiteSpace(type))   countP.Add("type", type);
                if (!string.IsNullOrWhiteSpace(q))       countP.Add("q", $"%{q}%");

                var countSql = $"SELECT COUNT(*) FROM licenses l JOIN profiles p ON p.id = l.user_id {whereClause}";
                var total = await conn.ExecuteScalarAsync<int>(countSql, countP);

                return Results.Ok(new { items = rows, total });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GET /api/admin/licenses failed");
                return Results.Json(new { error = "We couldn't load the licenses. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/users/{userId}/detail ──────────────────────────────
        // Full user profile: profile + licenses + roles + verified_roles + orgs
        app.MapGet("/api/admin/users/{userId}/detail", async (
            Guid                 userId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, username, email, full_name, avatar_url, is_admin, admin_roles, created_at FROM profiles WHERE id = @userId",
                new { userId });
            if (profile is null) return Results.NotFound(new { error = "User not found" });

            var licenses = await conn.QueryAsync<dynamic>(
                "SELECT id, license_id, license_type, status, issued_at, expires_at, notes FROM licenses WHERE user_id = @userId ORDER BY issued_at DESC",
                new { userId });
            var userRoles = await conn.QueryAsync<dynamic>(
                "SELECT role, is_active FROM user_roles WHERE user_id = @userId",
                new { userId });
            var verifiedRoles = await conn.QueryAsync<dynamic>(
                "SELECT role, status, is_active, verified_at FROM verified_roles WHERE user_id = @userId",
                new { userId });
            var organizations = await conn.QueryAsync<dynamic>(
                "SELECT id, name, slug, logo_url FROM organizations WHERE owner_id = @userId",
                new { userId });
            var venues = await conn.QueryAsync<dynamic>(
                "SELECT id, name, city, country, status FROM venues WHERE owner_id = @userId",
                new { userId });
            var tournaments = await conn.QueryAsync<dynamic>(
                "SELECT id, name, game, status::text AS status FROM tournaments WHERE organizer_id = @userId ORDER BY created_at DESC LIMIT 20",
                new { userId });

            return Results.Ok(new
            {
                profile,
                licenses,
                user_roles = userRoles,
                verified_roles = verifiedRoles,
                organizations,
                venues,
                tournaments
            });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/licenses ──────────────────────────────────────────
        // Manually assign a license to a user (super admin)
        app.MapPost("/api/admin/licenses", async (
            [FromBody] AdminCreateLicenseRequest req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();
            var licenseType = (req.LicenseType ?? string.Empty).Trim().ToLowerInvariant();
            if (licenseType is not ("organizer" or "venue_owner" or "broadcaster"))
                return Results.BadRequest(new { error = "Invalid license type." });
            var syncsVerifiedRoles = licenseType is "organizer" or "venue_owner";

            using var conn = db.CreateConnection();

            // Check if license already exists for this user+type
            var existingId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT license_id FROM licenses WHERE user_id = @userId AND license_type = @licenseType LIMIT 1",
                new { userId = req.UserId, licenseType });

            string licenseId;
            var issuedAt  = DateTime.UtcNow;
            var expiresAt = issuedAt.AddYears(1);

            if (existingId is not null)
            {
                licenseId = existingId;
                await conn.ExecuteAsync(
                    "UPDATE licenses SET status = 'active', issued_at = @issuedAt, expires_at = @expiresAt WHERE user_id = @userId AND license_type = @licenseType",
                    new { userId = req.UserId, licenseType, issuedAt, expiresAt });
            }
            else
            {
                var prefix = licenseType switch
                {
                    "organizer"   => "ESP-OR",
                    "venue_owner" => "ESP-VO",
                    "broadcaster" => "ESP-BR",
                    _             => "ESP-XX"
                };
                licenseId = $"{prefix}-{Random.Shared.Next(100000, 999999)}";

                await conn.ExecuteAsync(
                    """
                    INSERT INTO licenses (user_id, license_id, license_type, status, issued_at, expires_at)
                    VALUES (@userId, @licenseId, @licenseType, 'active', @issuedAt, @expiresAt)
                    """,
                    new { userId = req.UserId, licenseId, licenseType, issuedAt, expiresAt });
            }

            // verified_roles.role is app_role enum (broadcaster is not in this enum)
            if (syncsVerifiedRoles)
            {
                var vrRows = await conn.ExecuteAsync(
                    "UPDATE verified_roles SET status = 'approved', is_active = TRUE, verified_at = NOW() WHERE user_id = @userId AND role::text = @role",
                    new { userId = req.UserId, role = licenseType });
                if (vrRows == 0)
                    await conn.ExecuteAsync(
                        "INSERT INTO verified_roles (user_id, role, status, is_active, verified_at) VALUES (@userId, @role::app_role, 'approved', TRUE, NOW())",
                        new { userId = req.UserId, role = licenseType });
            }

            // Upsert user_roles: update first, insert if missing
            var urRows = await conn.ExecuteAsync(
                "UPDATE user_roles SET is_active = TRUE WHERE user_id = @userId AND role = @role",
                new { userId = req.UserId, role = licenseType });
            if (urRows == 0)
                await conn.ExecuteAsync(
                    "INSERT INTO user_roles (user_id, role, is_active) VALUES (@userId, @role, TRUE)",
                    new { userId = req.UserId, role = licenseType });

            return Results.Ok(new { success = true, license_id = licenseId });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/licenses/{userId}/{licenseType}/revoke ─────────────
        app.MapPut("/api/admin/licenses/{userId}/{licenseType}/revoke", async (
            Guid                 userId,
            string               licenseType,
            HttpContext          ctx,
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();
            var normalizedType = (licenseType ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedType is not ("organizer" or "venue_owner" or "broadcaster"))
                return Results.BadRequest(new { success = false, error = "Invalid license type." });
            var syncsVerifiedRoles = normalizedType is "organizer" or "venue_owner";

            using var conn = db.CreateConnection();

            try
            {
                await conn.ExecuteAsync(
                    "UPDATE licenses SET status = 'revoked' WHERE user_id = @userId AND license_type = @licenseType",
                    new { userId, licenseType = normalizedType });

                if (syncsVerifiedRoles)
                    await conn.ExecuteAsync(
                        "UPDATE verified_roles SET is_active = FALSE WHERE user_id = @userId AND role::text = @licenseType",
                        new { userId, licenseType = normalizedType });

                await conn.ExecuteAsync(
                    "UPDATE user_roles SET is_active = FALSE WHERE user_id = @userId AND role = @licenseType",
                    new { userId, licenseType = normalizedType });

                // Reset profiles.role to 'casual' if the revoked type matches their current role
                if (syncsVerifiedRoles)
                    await conn.ExecuteAsync(
                        "UPDATE profiles SET role = 'casual'::app_role WHERE id = @userId AND role::text = @licenseType",
                        new { userId, licenseType = normalizedType });
            }
            catch (Exception)
            {
                return Results.Json(new { success = false, error = "License operation failed." }, statusCode: 500);
            }

            // Evict cached UserContext so the role change takes effect immediately
            try { await cache.RemoveAsync($"user-ctx:{userId}"); } catch { /* best effort */ }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/licenses/{userId}/{licenseType}/reinstate ──────────
        app.MapPut("/api/admin/licenses/{userId}/{licenseType}/reinstate", async (
            Guid                 userId,
            string               licenseType,
            HttpContext          ctx,
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();
            var normalizedType = (licenseType ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedType is not ("organizer" or "venue_owner" or "broadcaster"))
                return Results.BadRequest(new { success = false, error = "Invalid license type." });
            var syncsVerifiedRoles = normalizedType is "organizer" or "venue_owner";

            using var conn = db.CreateConnection();

            try
            {
                var expiresAt = DateTime.UtcNow.AddYears(1);
                await conn.ExecuteAsync(
                    "UPDATE licenses SET status = 'active', expires_at = @expiresAt WHERE user_id = @userId AND license_type = @licenseType",
                    new { userId, licenseType = normalizedType, expiresAt });

                if (syncsVerifiedRoles)
                {
                    var vrUpdated = await conn.ExecuteAsync(
                        "UPDATE verified_roles SET status = 'approved', is_active = TRUE, verified_at = NOW() WHERE user_id = @userId AND role::text = @licenseType",
                        new { userId, licenseType = normalizedType });
                    if (vrUpdated == 0)
                        await conn.ExecuteAsync(
                            "INSERT INTO verified_roles (user_id, role, status, is_active, verified_at) VALUES (@userId, @licenseType::app_role, 'approved', TRUE, NOW())",
                            new { userId, licenseType = normalizedType });
                }

                // Upsert user_roles: update first, insert if missing
                var urUpdated = await conn.ExecuteAsync(
                    "UPDATE user_roles SET is_active = TRUE WHERE user_id = @userId AND role = @licenseType",
                    new { userId, licenseType = normalizedType });
                if (urUpdated == 0)
                    await conn.ExecuteAsync(
                        "INSERT INTO user_roles (user_id, role, is_active) VALUES (@userId, @licenseType, TRUE)",
                        new { userId, licenseType = normalizedType });

                // Restore profiles.role if currently casual
                if (syncsVerifiedRoles)
                    await conn.ExecuteAsync(
                        "UPDATE profiles SET role = @licenseType::app_role WHERE id = @userId AND role = 'casual'::app_role",
                        new { userId, licenseType = normalizedType });
            }
            catch (Exception)
            {
                return Results.Json(new { success = false, error = "License operation failed." }, statusCode: 500);
            }

            // Evict cached UserContext so the role change takes effect immediately
            try { await cache.RemoveAsync($"user-ctx:{userId}"); } catch { /* best effort */ }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/licenses/backfill ─────────────────────────────────
        app.MapPost("/api/admin/licenses/backfill", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersEdit)) return Results.Forbid();

            AdminBackfillLicensesRequest? req = null;
            try { req = await ctx.Request.ReadFromJsonAsync<AdminBackfillLicensesRequest>(ct); } catch { }
            var licenseType = (req?.LicenseType ?? "organizer").Trim().ToLowerInvariant();
            if (licenseType is not ("organizer" or "venue_owner" or "broadcaster"))
                return Results.BadRequest(new { success = false, error = "Invalid license type." });
            var syncsVerifiedRoles = licenseType is "organizer" or "venue_owner";
            var prefix = licenseType switch
            {
                "organizer"   => "ESP-OR",
                "venue_owner" => "ESP-VO",
                "broadcaster" => "ESP-BR",
                _             => "ESP-XX"
            };

            using var conn = db.CreateConnection();

            // Find users with the role who don't have an active license of that type
            // p.role is a custom enum (app_role) — cast to text for comparison
            var unlicensed = await conn.QueryAsync<Guid>(
                """
                SELECT DISTINCT p.id
                FROM profiles p
                LEFT JOIN licenses l ON l.user_id = p.id AND l.license_type = @licenseType AND l.status = 'active'
                WHERE l.id IS NULL
                  AND (
                    p.role::text = @licenseType
                    OR EXISTS (SELECT 1 FROM user_roles ur WHERE ur.user_id = p.id AND ur.role = @licenseType AND ur.is_active = TRUE)
                  )
                """,
                new { licenseType });

            var userIds = unlicensed.ToList();
            var issuedAt  = DateTime.UtcNow;
            var expiresAt = issuedAt.AddYears(1);
            var issued = 0;

            foreach (var userId in userIds)
            {
                var licenseId = $"{prefix}-{Random.Shared.Next(100000, 999999)}";

                // Check if a revoked/suspended license already exists for this user+type
                var existingId = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT license_id FROM licenses WHERE user_id = @userId AND license_type = @licenseType LIMIT 1",
                    new { userId, licenseType });

                if (existingId is not null)
                {
                    await conn.ExecuteAsync(
                        "UPDATE licenses SET status = 'active', issued_at = @issuedAt, expires_at = @expiresAt WHERE user_id = @userId AND license_type = @licenseType",
                        new { userId, licenseType, issuedAt, expiresAt });
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO licenses (user_id, license_id, license_type, status, issued_at, expires_at)
                        VALUES (@userId, @licenseId, @licenseType, 'active', @issuedAt, @expiresAt)
                        """,
                        new { userId, licenseId, licenseType, issuedAt, expiresAt });
                }

                if (syncsVerifiedRoles)
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO verified_roles (user_id, role, status, is_active, verified_at)
                        VALUES (@userId, @role::app_role, 'approved', TRUE, NOW())
                        ON CONFLICT (user_id, role) DO UPDATE SET status = 'approved', is_active = TRUE, verified_at = NOW()
                        """,
                        new { userId, role = licenseType });

                await conn.ExecuteAsync(
                    """
                    INSERT INTO user_roles (user_id, role, is_active)
                    VALUES (@userId, @role, TRUE)
                    ON CONFLICT (user_id, role) DO UPDATE SET is_active = TRUE
                    """,
                    new { userId, role = licenseType });

                issued++;
            }

            return Results.Ok(new { issued });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/verification-requests/{requestId} ───────────────
        app.MapDelete("/api/admin/verification-requests/{requestId}", async (
            Guid                 requestId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM verification_requests WHERE id = @id",
                new { id = requestId });

            return deleted > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Verification request not found" });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/licenses/{licenseId} ────────────────────────────
        app.MapDelete("/api/admin/licenses/{licenseId}", async (
            Guid                 licenseId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM licenses WHERE id = @id",
                new { id = licenseId });

            return deleted > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "License not found" });
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
                LIMIT 500
                """);
            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── GET /api/users/{userId}/role ───────────────────────────────────────
        app.MapGet("/api/users/{userId}/role", async (
            Guid                 userId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Only the user themselves or an admin can query roles
            if (userCtx.UserIdGuid != userId && !userCtx.Roles.Contains("admin"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var roles = (await conn.QueryAsync<string>(
                "SELECT role FROM user_roles WHERE user_id = @userId AND is_active = TRUE",
                new { userId })).ToList();
            return Results.Ok(new { userId, roles });
        }).RequireAuthorization("Authenticated");

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
            limit = Math.Clamp(limit, 1, 100);
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

        // ── POST /api/sponsors/applications/{id}/approve ──────────────────────
        // One-click approval: creates sponsor from application + links user account
        app.MapPost("/api/sponsors/applications/{id}/approve", async (
            Guid                    id,
            HttpContext             ctx,
            IDbConnectionFactory    db,
            ISupabaseAdminClient    supabase,
            IEmailService           email,
            IConfiguration          config,
            CancellationToken       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Fetch application
            var app2 = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM partner_applications WHERE id = @id", new { id });
            if (app2 is null)
                return Results.NotFound(new { error = "Application not found." });

            var dict = (IDictionary<string, object?>)app2;
            var status = dict["status"]?.ToString();
            if (status == "approved")
                return Results.BadRequest(new { error = "Application already approved." });

            var companyName     = dict["company_name"]?.ToString() ?? "Unknown";
            var companyWebsite  = dict["company_website"]?.ToString() ?? "";
            var contactEmail    = dict["contact_email"]?.ToString();
            var partnershipTier = dict["partnership_tier"]?.ToString() ?? "diamond";
            var message         = dict["message"]?.ToString();

            if (string.IsNullOrWhiteSpace(contactEmail))
                return Results.BadRequest(new { error = "Application has no contact email." });

            // 2. Create sponsor record
            var sponsorId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO sponsors (name, website_url, tier, description, is_active, placement, priority, accent_color)
                VALUES (@name, @website, @tier, @description, true, ARRAY['banner'], 0, '#f43f5e')
                RETURNING id
                """,
                new { name = companyName, website = companyWebsite, tier = partnershipTier, description = message });

            // 3. Link user account
            var partnerUrl = config["PartnerUrl"] ?? "https://partner.esportra.com";
            var existingUser = await supabase.GetUserByEmailAsync(contactEmail, ct);
            bool isNewUser;
            string userId;

            if (existingUser is not null)
            {
                isNewUser = false;
                userId = existingUser.Id;

                await conn.ExecuteAsync(
                    """
                    INSERT INTO sponsor_accounts (user_id, sponsor_id, role, onboarding_meta)
                    VALUES (@userId, @sponsorId, 'owner', '{"completed":false,"current_step":0,"steps":{}}')
                    ON CONFLICT (user_id, sponsor_id) DO NOTHING
                    """,
                    new { userId = Guid.Parse(userId), sponsorId });

                try
                {
                    await email.SendAsync(contactEmail, EmailType.PartnerWelcome, new
                    {
                        sponsorName = companyName,
                        portalUrl   = partnerUrl,
                    }, ct);
                }
                catch { /* Email is best-effort */ }
            }
            else
            {
                isNewUser = true;

                var newUser = await supabase.CreateUserAsync(contactEmail,
                    new { sponsor_id = sponsorId.ToString() }, ct);
                userId = newUser.Id;

                await conn.ExecuteAsync(
                    """
                    INSERT INTO sponsor_accounts (user_id, sponsor_id, role, onboarding_meta)
                    VALUES (@userId, @sponsorId, 'owner', '{"completed":false,"current_step":0,"steps":{}}')
                    ON CONFLICT DO NOTHING
                    """,
                    new { userId = Guid.Parse(userId), sponsorId });

                string? setupUrl = null;
                try
                {
                    var link = await supabase.GenerateRecoveryLinkAsync(contactEmail, ct);
                    setupUrl = $"{partnerUrl}/set-password?token_hash={link.TokenHash}&type=recovery";

                    await email.SendAsync(contactEmail, EmailType.PartnerInvite, new
                    {
                        sponsorName = companyName,
                        setupUrl,
                    }, ct);
                }
                catch { /* Email is best-effort */ }
            }

            // 4. Mark application as approved
            await conn.ExecuteAsync(
                "UPDATE partner_applications SET status = 'approved', updated_at = NOW() WHERE id = @id",
                new { id });

            return Results.Ok(new
            {
                success    = true,
                sponsorId,
                isNewUser,
                userId,
                companyName,
                contactEmail,
            });
        }).RequireAuthorization("Admin");

        // ══════════════════════════════════════════════════════════════════════
        // TEAM MANAGEMENT (super_admin only)
        // ══════════════════════════════════════════════════════════════════════

        // ── GET /api/admin/teams — paginated list with search + stats ────────
        app.MapGet("/api/admin/teams", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct,
            string?  search = null,
            string?  game   = null,
            int      limit  = 20,
            int      offset = 0) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Contains("super_admin")) return Results.Forbid();

            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            using var conn = db.CreateConnection();

            var conditions = new List<string> { "t.is_solo = FALSE" };
            if (!string.IsNullOrWhiteSpace(search))
                conditions.Add("(t.name ILIKE @search OR t.tag ILIKE @search)");
            if (!string.IsNullOrWhiteSpace(game))
                conditions.Add("t.game ILIKE @game");

            var where = "WHERE " + string.Join(" AND ", conditions);
            var searchParam = search is not null ? $"%{search}%" : null;
            var gameParam = game is not null ? $"%{game}%" : null;

            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM teams t {where}",
                new { search = searchParam, game = gameParam });

            var teams = await conn.QueryAsync<dynamic>(
                $"""
                SELECT t.id, t.name, t.tag, t.game, t.logo_url, t.owner_id, t.is_active,
                       t.country_code, t.created_at,
                       (SELECT COUNT(*) FROM team_members tm WHERE tm.team_id = t.id AND tm.is_active = TRUE) AS member_count,
                       (SELECT COUNT(*) FROM tournament_participants tp WHERE tp.team_id = t.id) AS tournament_count,
                       (SELECT COUNT(*) FROM tournaments tr WHERE tr.winner_id = t.id AND tr.status = 'completed') AS wins,
                       p.username AS owner_username, p.full_name AS owner_name, p.avatar_url AS owner_avatar
                FROM teams t
                LEFT JOIN profiles p ON p.id = t.owner_id
                {where}
                ORDER BY t.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { search = searchParam, game = gameParam, limit, offset });

            // Aggregate stats
            var stats = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    COUNT(*) FILTER (WHERE is_solo = FALSE) AS total_teams,
                    COUNT(*) FILTER (WHERE is_solo = FALSE AND is_active = TRUE) AS active_teams,
                    ROUND(AVG(mc)::numeric, 1) AS avg_members
                FROM teams t
                LEFT JOIN LATERAL (
                    SELECT COUNT(*) AS mc FROM team_members tm WHERE tm.team_id = t.id AND tm.is_active = TRUE
                ) m ON TRUE
                WHERE t.is_solo = FALSE
                """);

            return Results.Ok(new { teams, total, stats });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/teams/{id} — full detail ─────────────────────────
        app.MapGet("/api/admin/teams/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Contains("super_admin")) return Results.Forbid();

            using var conn = db.CreateConnection();

            var team = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT t.*, p.username AS owner_username, p.full_name AS owner_name, p.avatar_url AS owner_avatar
                FROM teams t
                LEFT JOIN profiles p ON p.id = t.owner_id
                WHERE t.id = @id
                """, new { id });
            if (team is null) return Results.NotFound();

            var members = await conn.QueryAsync<dynamic>(
                """
                SELECT tm.user_id, tm.role, tm.is_active, tm.joined_at, tm.display_order,
                       p.username, p.full_name, p.avatar_url, p.email
                FROM team_members tm
                LEFT JOIN profiles p ON p.id = tm.user_id
                WHERE tm.team_id = @id
                ORDER BY tm.display_order ASC, tm.joined_at ASC
                """, new { id });

            var tournaments = await conn.QueryAsync<dynamic>(
                """
                SELECT tp.tournament_id, tp.status, tp.registered_at,
                       t.name AS tournament_name, t.game, t.status AS tournament_status,
                       t.start_date, t.prize_pool
                FROM tournament_participants tp
                JOIN tournaments t ON t.id = tp.tournament_id
                WHERE tp.team_id = @id
                ORDER BY t.start_date DESC
                """, new { id });

            var invites = await conn.QueryAsync<dynamic>(
                """
                SELECT ti.id, ti.invited_user_id, ti.status, ti.created_at, ti.responded_at,
                       p.username AS invited_username, p.avatar_url AS invited_avatar
                FROM team_invitations ti
                LEFT JOIN profiles p ON p.id = ti.invited_user_id
                WHERE ti.team_id = @id
                ORDER BY ti.created_at DESC
                LIMIT 20
                """, new { id });

            // Leaderboard rank (RP calculation)
            var leaderboard = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                WITH team_stats AS (
                    SELECT
                        COALESCE(SUM(CASE WHEN m.winner_id = @id THEN 1 ELSE 0 END), 0) AS wins,
                        COALESCE(SUM(CASE
                            WHEN m.status = 'completed' AND m.winner_id IS NOT NULL AND m.winner_id != @id
                            THEN 1 ELSE 0
                        END), 0) AS losses,
                        COALESCE((SELECT COUNT(*) FROM tournaments tr WHERE tr.winner_id = @id AND tr.status = 'completed'), 0) AS tournament_wins
                    FROM brkt_matches m
                    WHERE (m.team1_id = @id OR m.team2_id = @id) AND m.status = 'completed'
                )
                SELECT wins, losses, (wins + losses) AS matches_played,
                       CASE WHEN (wins + losses) > 0 THEN ROUND(wins * 100.0 / (wins + losses), 1) ELSE 0 END AS win_rate,
                       tournament_wins AS tournaments_won,
                       (wins * 50 + tournament_wins * 500 - losses * 10) AS rp
                FROM team_stats
                """, new { id });

            return Results.Ok(new { team, members, tournaments, invites, leaderboard });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/teams/{id} — disband team ─────────────────────
        app.MapDelete("/api/admin/teams/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Contains("super_admin")) return Results.Forbid();

            using var conn = db.CreateConnection();

            var team = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, name FROM teams WHERE id = @id", new { id });
            if (team is null) return Results.NotFound();

            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync("DELETE FROM team_roster_members WHERE roster_id IN (SELECT id FROM team_rosters WHERE team_id = @id)", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM team_rosters WHERE team_id = @id", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM team_invitations WHERE team_id = @id", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM team_members WHERE team_id = @id",     new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM tournament_participants WHERE team_id = @id", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM teams WHERE id = @id",                 new { id }, tx);
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }

            return Results.Ok(new { success = true, disbanded = (string)team.name });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/teams/{id}/members/{userId} — remove member ───
        app.MapDelete("/api/admin/teams/{id}/members/{userId}", async (
            Guid                 id,
            Guid                 userId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Contains("super_admin")) return Results.Forbid();

            using var conn = db.CreateConnection();

            // Prevent removing the captain — must transfer first
            var member = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT role FROM team_members WHERE team_id = @id AND user_id = @userId",
                new { id, userId });
            if (member is null) return Results.NotFound(new { error = "Member not found." });
            if ((string)member.role == "captain")
                return Results.BadRequest(new { error = "Cannot remove captain. Transfer captaincy first." });

            await conn.ExecuteAsync(
                "DELETE FROM team_members WHERE team_id = @id AND user_id = @userId",
                new { id, userId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/teams/{id}/transfer-captain ─────────────────────
        app.MapPost("/api/admin/teams/{id}/transfer-captain", async (
            Guid                              id,
            [FromBody] AdminTransferCaptainReq req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Contains("super_admin")) return Results.Forbid();

            if (!Guid.TryParse(req.NewCaptainId, out var newCaptainId))
                return Results.BadRequest(new { error = "Invalid user ID." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                // Demote current captain
                await conn.ExecuteAsync(
                    "UPDATE team_members SET role = 'member' WHERE team_id = @id AND role = 'captain'",
                    new { id }, tx);

                // Promote new captain
                var affected = await conn.ExecuteAsync(
                    "UPDATE team_members SET role = 'captain' WHERE team_id = @id AND user_id = @newCaptainId",
                    new { id, newCaptainId }, tx);
                if (affected == 0) { tx.Rollback(); return Results.BadRequest(new { error = "User is not a team member." }); }

                // Transfer ownership
                await conn.ExecuteAsync(
                    "UPDATE teams SET owner_id = @newCaptainId, updated_at = NOW() WHERE id = @id",
                    new { id, newCaptainId }, tx);

                await conn.ExecuteAsync(
                    "UPDATE tournament_participants SET team_captain_id = @newCaptainId WHERE team_id = @id",
                    new { id, newCaptainId }, tx);

                tx.Commit();
            }
            catch { tx.Rollback(); throw; }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/teams/{id} — edit team details ───────────────────
        app.MapPut("/api/admin/teams/{id}", async (
            Guid                          id,
            [FromBody] AdminEditTeamReq   req,
            HttpContext                    ctx,
            IDbConnectionFactory          db,
            CancellationToken             ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Contains("super_admin")) return Results.Forbid();

            using var conn = db.CreateConnection();

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE teams SET
                    name        = COALESCE(@name, name),
                    tag         = COALESCE(@tag, tag),
                    description = COALESCE(@description, description),
                    game        = COALESCE(@game, game),
                    updated_at  = NOW()
                WHERE id = @id
                RETURNING id, name, tag, game
                """,
                new { id, name = req.Name, tag = req.Tag, description = req.Description, game = req.Game });

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization("Admin");
    }

    private sealed record AdminTransferCaptainReq(string NewCaptainId);
    private sealed record AdminEditTeamReq(string? Name = null, string? Tag = null, string? Description = null, string? Game = null);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<IResult> DeleteUserAsync(
        Guid userId,
        System.Data.IDbConnection conn,
        ISupabaseAdminClient supabase,
        CancellationToken ct)
    {
        // Delete in dependency order to avoid FK constraint errors.
        // Wrap in transaction so a mid-way failure doesn't leave partial data.
        using var txn = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(
                "DELETE FROM public.match_result_reports WHERE reported_by = @id", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.match_messages WHERE sender_id = @id", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.notifications WHERE user_id = @id", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.staff_tournament_assignments WHERE organization_staff_id IN (SELECT id FROM organization_staff WHERE user_id = @id)", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.organization_staff WHERE user_id = @id", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.sponsor_accounts WHERE user_id = @id", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.tournament_participants WHERE user_id = @id", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.team_members WHERE user_id = @id", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.tournaments WHERE organizer_id = @id", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.user_roles WHERE user_id = @id", new { id = userId }, txn);
            await conn.ExecuteAsync(
                "DELETE FROM public.profiles WHERE id = @id", new { id = userId }, txn);

            txn.Commit();
        }
        catch
        {
            txn.Rollback();
            throw;
        }

        // Finally delete from Supabase Auth (outside transaction — can't rollback external service)
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

    private static async Task<IResult> AssignRoleToUserAsync(
        Guid userId,
        ManageUserRequest req,
        System.Data.IDbConnection conn,
        CancellationToken ct)
    {
        var role = req.RoleKey ?? req.Role;
        if (string.IsNullOrWhiteSpace(role))
            return Results.BadRequest(new { error = "Role is required for assign_role action." });

        var isAdmin = string.Equals(req.RoleType, "admin", StringComparison.OrdinalIgnoreCase);

        if (isAdmin)
        {
            // Resolve admin role id from admin_roles table by key
            var roleId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM admin_roles WHERE key = @role LIMIT 1", new { role });
            if (roleId is null)
                return Results.BadRequest(new { error = $"Admin role '{role}' not found." });

            await conn.ExecuteAsync(
                """
                INSERT INTO admin_user_roles (user_id, role_id)
                VALUES (@userId, @roleId)
                ON CONFLICT DO NOTHING
                """,
                new { userId, roleId });
        }
        else
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO user_roles (user_id, role, is_active)
                VALUES (@userId, @role, TRUE)
                ON CONFLICT (user_id, role) DO UPDATE SET is_active = TRUE
                """,
                new { userId, role });
        }

        return Results.Ok(new { success = true, role });
    }

    private static async Task<IResult> RevokeRoleFromUserAsync(
        Guid userId,
        ManageUserRequest req,
        System.Data.IDbConnection conn,
        CancellationToken ct)
    {
        var role = req.RoleKey ?? req.Role;
        if (string.IsNullOrWhiteSpace(role))
            return Results.BadRequest(new { error = "Role is required for revoke_role action." });

        var isAdmin = string.Equals(req.RoleType, "admin", StringComparison.OrdinalIgnoreCase);

        if (isAdmin)
        {
            var roleId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM admin_roles WHERE key = @role LIMIT 1", new { role });
            if (roleId is not null)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM admin_user_roles WHERE user_id = @userId AND role_id = @roleId",
                    new { userId, roleId });
            }
        }
        else
        {
            await conn.ExecuteAsync(
                "UPDATE user_roles SET is_active = FALSE WHERE user_id = @userId AND role = @role",
                new { userId, role });
        }

        return Results.Ok(new { success = true, role });
    }
}

// ── Admin request records ─────────────────────────────────────────────────────
public sealed record AssignRoleRequest(
    [property: JsonPropertyName("user_id")] Guid UserId,
    string Role);
public sealed record AdminCreateLicenseRequest(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("license_type")] string LicenseType);
public sealed record AdminBackfillLicensesRequest(
    [property: JsonPropertyName("license_type")] string? LicenseType = "organizer");
public sealed record UpdateRoleRequest(string[]? Roles = null);
public sealed record SetRoleRequest(string Role);
public sealed record AdminUpdateDisputeRequest(string? Status = null, string? ResolutionNotes = null, Guid? AssignedToUserId = null);
public sealed record ApproveVerificationRequest(string Status, string Role);
public sealed record UpdateVerificationRequest(string Status);
public sealed record CreateVerifiedRoleRequest(
    [property: JsonPropertyName("user_id")] Guid UserId,
    string Role,
    string Status,
    [property: JsonPropertyName("is_active")] bool IsActive);
public sealed record UpdateApplicationRequest(string? Status = null, string? Notes = null);
public sealed record AdminUserRoleAssignRequest(Guid UserId, Guid RoleId);
public sealed record AdminUpdateTournamentRequest(string? Status = null, bool? IsFeatured = null);
public sealed record AdminUpdateUserRequest(bool? IsAdmin = null, Guid[]? AdminRoles = null);
