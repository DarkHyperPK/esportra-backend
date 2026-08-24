using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Supabase;
using Esportra.Core.Alerts;
using Esportra.Core.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Hybrid;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: manage-users and invite-sponsor Edge Functions.
/// Also exposes POST /api/emails for internal use (replaces send-email Edge Function).
/// </summary>
public static class AdminEndpoints
{
    private static string NormalizeSponsorTier(string? tier) => tier?.Trim().ToLowerInvariant() switch
    {
        "radiant" => "radiant",
        "ascendant" => "ascendant",
        "diamond" => "diamond",
        _ => "diamond",
    };

    private static string EscapeLike(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "%";
        var escaped = input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return $"%{escaped}%";
    }

    private static string CsvEscape(object? value)
    {
        if (value is null) return "";
        var s = value.ToString() ?? "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    private static async Task EvictUserContextAsync(
        HybridCache cache,
        Guid userId,
        HttpContext ctx,
        CancellationToken ct = default)
    {
        try
        {
            await cache.RemoveAsync($"user-ctx:{userId}", ct);
        }
        catch (Exception ex)
        {
            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Esportra.Api.Endpoints.AdminEndpoints");
            logger.LogWarning(ex, "UserContext cache eviction failed for {UserId}", userId);
        }
    }

    private static async Task EvictUsersWithAdminRoleAsync(
        HybridCache cache,
        System.Data.IDbConnection conn,
        Guid roleId,
        HttpContext ctx,
        CancellationToken ct = default)
    {
        var userIds = await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT user_id FROM public.admin_user_roles WHERE role_id = @roleId",
            new { roleId },
            cancellationToken: ct));

        foreach (var userId in userIds)
        {
            await EvictUserContextAsync(cache, userId, ctx, ct);
        }
    }

    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/sponsors/{sponsorId}/invitations", async (
            Guid sponsorId,
            [FromBody] CreatePartnerSponsorInvitationRequest request,
            HttpContext context,
            PartnerSponsorOnboardingService invitations,
            CancellationToken cancellationToken) =>
        {
            var userContext = context.Items["UserContext"] as UserContext;
            if (userContext is null) return Results.Unauthorized();
            if (!userContext.Permissions.Contains(Permissions.SponsorsCreate)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Email)
                || request.Email.Length > 254
                || !System.Net.Mail.MailAddress.TryCreate(request.Email.Trim(), out _))
                return Results.BadRequest(new { error = "A valid email address is required." });

            var invitation = await invitations.CreateInvitationAsync(
                sponsorId,
                request.Email,
                request.Role,
                userContext.UserIdGuid,
                cancellationToken);
            return invitation is null
                ? Results.BadRequest(new { error = "Unable to create invitation." })
                : !invitation.WasDelivered
                    ? Results.Json(new { error = "Invitation delivery failed. Please retry." }, statusCode: 502)
                : Results.Ok(new { invitation.InvitationId, invitation.RequiresPasswordSetup });
        }).RequireAuthorization(Permissions.SponsorsCreate);

        app.MapGet("/api/admin/sponsor-invitations", async (
            Guid? sponsorId,
            PartnerSponsorOnboardingService invitations,
            CancellationToken cancellationToken) =>
        {
            var results = await invitations.ListAsync(sponsorId, cancellationToken);
            return Results.Ok(results);
        }).RequireAuthorization(Permissions.SponsorsView);

        app.MapPost("/api/admin/sponsor-invitations/{invitationId}/resend", async (
            Guid invitationId,
            HttpContext context,
            PartnerSponsorOnboardingService invitations,
            CancellationToken cancellationToken) =>
        {
            var userContext = context.Items["UserContext"] as UserContext;
            if (userContext is null) return Results.Unauthorized();

            var result = await invitations.ResendAsync(
                invitationId,
                userContext.UserIdGuid,
                cancellationToken);
            return result is null
                ? Results.Conflict(new { error = "Invitation cannot be resent." })
                : !result.WasDelivered
                    ? Results.Json(new
                    {
                        error = "Invitation was recreated, but delivery failed.",
                        result.InvitationId,
                    }, statusCode: 502)
                    : Results.Ok(new { result.InvitationId, result.RequiresPasswordSetup });
        }).RequireAuthorization(Permissions.SponsorsCreate);

        app.MapPost("/api/admin/sponsor-invitations/{invitationId}/revoke", async (
            Guid invitationId,
            PartnerSponsorOnboardingService invitations,
            CancellationToken cancellationToken) =>
        {
            var wasRevoked = await invitations.RevokeAsync(invitationId, cancellationToken);
            return wasRevoked
                ? Results.Ok(new { revoked = true })
                : Results.Conflict(new { error = "Invitation cannot be revoked." });
        }).RequireAuthorization(Permissions.SponsorsCreate);

        app.MapPost("/api/admin/mfa/cleanup", async (
            HttpContext context,
            IHostEnvironment environment,
            ISupabaseAdminClient supabase,
            MfaFactorCleanupService mfaCleanup,
            AccountSecurityService accountSecurity,
            CancellationToken cancellationToken) =>
        {
            var userContext = context.Items["UserContext"] as UserContext;
            if (userContext is null) return Results.Unauthorized();
            if (!environment.IsStaging() || !userContext.IsSuperAdmin) return Results.NotFound();

            var deletedFactors = 0;
            var affectedUsers = 0;
            var page = 1;

            while (true)
            {
                var result = await supabase.ListUsersAsync(page, 100, cancellationToken);
                foreach (var user in result.Users)
                {
                    var factors = await mfaCleanup.ListFactorIdsAsync(user.Id, cancellationToken);
                    if (factors.Count == 0) continue;

                    foreach (var factor in factors)
                    {
                        await mfaCleanup.DeleteFactorAsync(user.Id, factor, cancellationToken);
                        deletedFactors++;
                    }

                    if (Guid.TryParse(user.Id, out var userId))
                    {
                        await accountSecurity.RevokeAllAsync(userId, "mfa_cleanup", cancellationToken);
                    }
                    await supabase.LogoutUserAsync(user.Id, cancellationToken);
                    affectedUsers++;
                }

                if (result.Users.Count < 100) break;
                page++;
            }

            return Results.Ok(new { deletedFactors, affectedUsers });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/users/{userId}/action ─────────────────────────────
        // Replaces: manage-users Edge Function
        // Actions: "delete-user", "update-role", "assign_role", "revoke_role"
        app.MapPost("/api/admin/users/{userId}/action", async (
            Guid userId,
            [FromBody] ManageUserRequest req,
            IDbConnectionFactory db,
            ISupabaseAdminClient supabase,
            AuditService audit,
            HybridCache cache,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var requiredPerm = req.Action == "delete-user"
                ? Permissions.UsersDelete
                : Permissions.UsersEdit;

            if (!userCtx.Permissions.Contains(requiredPerm))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            // For delete: fetch name before deletion, audit afterward
            if (req.Action == "delete-user")
            {
                var targetName = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT COALESCE(full_name, username, id::text) FROM profiles WHERE id = @userId", new { userId });
                var result = await DeleteUserAsync(userId, conn, supabase, ct);
                // AuditService uses its own connection — safe to call after conn operations
                await audit.LogAsync(
                    userCtx.UserIdGuid, userCtx.Email,
                    ActionType.Delete, TargetType.User,
                    userId, targetName ?? userId.ToString(), ct: ct);
                await EvictUserContextAsync(cache, userId, ctx, ct);
                return result;
            }

            var actionResult = req.Action switch
            {
                "update-role" => await UpdateUserRoleAsync(userId, req.Role, conn, ct),
                "assign_role" => await AssignRoleToUserAsync(userId, req, conn, userCtx, ct),
                "revoke_role" => await RevokeRoleFromUserAsync(userId, req, conn, userCtx, ct),
                _ => Results.BadRequest(new { error = $"Unknown action: {req.Action}" })
            };

            if (req.Action is "update-role" or "assign_role" or "revoke_role")
                await EvictUserContextAsync(cache, userId, ctx, ct);

            return actionResult;
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/users/cleanup ─────────────────────────────────────
        // Deletes profiles with no matching auth.users entry (orphaned rows).
        app.MapPost("/api/admin/users/cleanup", async (
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
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
            bool? active,
            string? placement,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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

        // Replaces: invite-sponsor Edge Function
        app.MapPost("/api/sponsors/invite", async (
            [FromBody] InviteSponsorRequest req,
            PartnerSponsorOnboardingService invitations,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.SponsorsCreate))
                return Results.Forbid();

            if (!Guid.TryParse(req.SponsorId, out var sponsorId))
                return Results.BadRequest(new { error = "Invalid SponsorId." });

            var invitation = await invitations.CreateInvitationAsync(
                sponsorId,
                req.Email,
                "owner",
                userCtx.UserIdGuid,
                ct);
            return invitation is null
                ? Results.BadRequest(new { error = "Unable to create invitation." })
                : !invitation.WasDelivered
                    ? Results.Json(new { error = "Invitation delivery failed. Please retry." }, statusCode: 502)
                : Results.Ok(new { success = true, invitation.InvitationId, invitation.RequiresPasswordSetup });

        }).RequireAuthorization(Permissions.SponsorsCreate);

        // ── GET /api/admin/users ─────────────────────────────────────────────
        // Paginated user list with search for admin dashboard
        app.MapGet("/api/admin/users", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0,
            [FromQuery] string? search = null,
            [FromQuery] string? status = null,
            [FromQuery] string? role = null,
            [FromQuery] string? country = null,
            [FromQuery] string? joined_from = null,
            [FromQuery] string? joined_to = null,
            [FromQuery] string? has_team = null,
            [FromQuery] string? verified = null,
            [FromQuery] string? sort_by = null,
            [FromQuery] string? sort_dir = null,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
                conditions.Add("(p.username ILIKE @search ESCAPE '\\' OR p.email ILIKE @search ESCAPE '\\' OR p.full_name ILIKE @search ESCAPE '\\')");
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

            if (!string.IsNullOrWhiteSpace(country))
                conditions.Add("p.country_code = @country");

            DateTimeOffset? joinedFrom = null;
            DateTimeOffset? joinedTo = null;
            if (!string.IsNullOrWhiteSpace(joined_from) && DateTimeOffset.TryParse(joined_from, out var jf))
            { joinedFrom = jf; conditions.Add("p.created_at >= @joinedFrom"); }
            if (!string.IsNullOrWhiteSpace(joined_to) && DateTimeOffset.TryParse(joined_to, out var jt))
            { joinedTo = jt.AddDays(1); conditions.Add("p.created_at < @joinedTo"); }

            if (has_team == "true")
                conditions.Add("EXISTS (SELECT 1 FROM team_members tm WHERE tm.user_id = p.id)");
            else if (has_team == "false")
                conditions.Add("NOT EXISTS (SELECT 1 FROM team_members tm WHERE tm.user_id = p.id)");

            // "Verified" = holds an approved, active licensed role (organizer / venue_owner / …).
            // NOT platform-wide verification — licenses are issued through the license system.
            if (verified == "true")
                conditions.Add("EXISTS (SELECT 1 FROM verified_roles vr WHERE vr.user_id = p.id AND vr.status = 'approved' AND vr.is_active = TRUE)");
            else if (verified == "false")
                conditions.Add("NOT EXISTS (SELECT 1 FROM verified_roles vr WHERE vr.user_id = p.id AND vr.status = 'approved' AND vr.is_active = TRUE)");

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            // Whitelist allowed sort columns
            var allowedSorts = new HashSet<string> { "created_at", "username", "updated_at" };
            var sortColumn = allowedSorts.Contains(sort_by ?? "") ? sort_by! : "created_at";
            var sortDirection = sort_dir?.ToLower() == "asc" ? "ASC" : "DESC";

            var sql = $"""
                SELECT
                    p.id,
                    p.username,
                    p.email,
                    p.full_name,
                    p.avatar_url,
                    p.is_suspended,
                    p.created_at,
                    p.country_code,
                    p.date_of_birth,
                    p.updated_at,
                    p.steam_tag,
                    p.riot_tag,
                    CASE
                        WHEN p.role = 'organizer' AND EXISTS (SELECT 1 FROM verified_roles vr WHERE vr.user_id = p.id AND vr.status = 'approved' AND vr.is_active = TRUE)   THEN 'verified_organizer'
                        WHEN p.role = 'venue_owner' AND EXISTS (SELECT 1 FROM verified_roles vr WHERE vr.user_id = p.id AND vr.status = 'approved' AND vr.is_active = TRUE) THEN 'verified_venue_owner'
                        WHEN EXISTS (SELECT 1 FROM auth.users au WHERE au.id = p.id AND au.email_confirmed_at IS NOT NULL)                                                   THEN 'email_verified'
                        ELSE 'unverified'
                    END AS verification_status
                FROM profiles p
                {where}
                ORDER BY p.{sortColumn} {sortDirection}
                LIMIT @limit OFFSET @offset
                """;

            var users = await conn.QueryAsync<dynamic>(sql,
                new { search = EscapeLike(search), limit, offset, role, country, joinedFrom, joinedTo });

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

            var enriched = users.Select(u =>
            {
                var uid = (Guid)u.id;
                return new
                {
                    u.id,
                    u.username,
                    u.email,
                    u.full_name,
                    u.avatar_url,
                    u.is_suspended,
                    u.created_at,
                    u.country_code,
                    u.date_of_birth,
                    u.verification_status,
                    u.updated_at,
                    roles = rolesMap.ContainsKey(uid) ? rolesMap[uid].ToArray() : Array.Empty<string>()
                };
            });

            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM profiles p {where}",
                new { search = EscapeLike(search), role, country, joinedFrom, joinedTo });

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



        // ── GET /api/admin/analytics ────────────────────────────────────────────
        // Replaces 8 parallel supabase count queries
        app.MapGet("/api/admin/analytics", async (
            IDbConnectionFactory db,
            CancellationToken ct) =>
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





        // ── CRUD: Sponsors ──────────────────────────────────────────────────────
        app.MapPost("/api/sponsors", async (
            [FromBody] object payload,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.SponsorsCreate))
                return Results.Forbid();
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
                    name = j.TryGetProperty("name", out var n) ? n.GetString() : null,
                    tagline = j.TryGetProperty("tagline", out var tl) ? tl.GetString() : null,
                    description = j.TryGetProperty("description", out var d) ? d.GetString() : null,
                    websiteUrl = j.TryGetProperty("website_url", out var wu) ? wu.GetString() : null,
                    logoUrl = j.TryGetProperty("logo_url", out var lu) ? lu.GetString() : null,
                    bannerImageUrl = j.TryGetProperty("banner_image_url", out var bi) ? bi.GetString() : null,
                    accentColor = j.TryGetProperty("accent_color", out var ac) ? ac.GetString() : "#8b5cf6",
                    tier = j.TryGetProperty("tier", out var ti) ? ti.GetString() : "standard",
                    placement = j.TryGetProperty("placement", out var pl)
                        ? "{" + string.Join(",", pl.EnumerateArray().Select(e => e.GetString())) + "}"
                        : "{banner}",
                    ctaText = j.TryGetProperty("cta_text", out var ct2) ? ct2.GetString() : "Learn More",
                    discountText = j.TryGetProperty("discount_text", out var dt) ? dt.GetString() : null,
                    isActive = !j.TryGetProperty("is_active", out var ia) || ia.GetBoolean(),
                    priority = j.TryGetProperty("priority", out var pr) ? pr.GetInt32() : 0,
                    galleryImages = j.TryGetProperty("gallery_images", out var gi)
                        ? "{" + string.Join(",", gi.EnumerateArray().Select(e => e.GetString())) + "}"
                        : "{}",
                });
            return Results.Ok(row);
        }).RequireAuthorization("Admin");

        app.MapPut("/api/sponsors/{id}", async (
            Guid id,
            [FromBody] object payload,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.SponsorsEdit))
                return Results.Forbid();
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
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            using var txn = conn.BeginTransaction();
            await conn.ExecuteAsync("DELETE FROM sponsor_placement_audit_log WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_asset_cleanup_jobs WHERE asset_id IN (SELECT sa.id FROM sponsor_placement_assets sa JOIN sponsor_placements sp ON sp.banner_asset_id = sa.id OR sp.logo_asset_id = sa.id WHERE sp.sponsor_id = @id)", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_placements WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_placement_assets WHERE uploaded_by IN (SELECT user_id FROM sponsor_accounts WHERE sponsor_id = @id)", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_accounts WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_daily_totals WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_placement_daily_stats WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_device_daily_stats WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_content_daily_stats WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_audience_daily_facts WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_audience_identities WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsor_analytics_events WHERE sponsor_id = @id", new { id }, txn);
            await conn.ExecuteAsync("DELETE FROM sponsors WHERE id = @id", new { id }, txn);
            txn.Commit();
            return Results.Ok(new { success = true });
        }).RequireAuthorization(Permissions.SponsorsDelete);

        // ── POST /api/admin/users/{userId}/suspend ──────────────────────────────
        app.MapPost("/api/admin/users/{userId}/suspend", async (
            Guid userId,
            [FromBody] SuspendUserRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            ISupabaseAdminClient supabase,
            HybridCache cache,
            AuditService audit,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersBan)) return Results.Forbid();
            using var conn = db.CreateConnection();
            var targetName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT COALESCE(full_name, username, id::text) FROM profiles WHERE id = @userId", new { userId });
            await conn.ExecuteAsync(
                """
                UPDATE profiles
                SET is_suspended = true,
                    suspension_reason = @reason,
                    suspension_type = @suspensionType,
                    suspension_until = @suspensionUntil,
                    updated_at = NOW()
                WHERE id = @userId
                """,
                new
                {
                    userId,
                    reason = req.Reason,
                    suspensionType = req.SuspensionType,
                    suspensionUntil = req.SuspensionUntil,
                });

            try
            {
                await supabase.LogoutUserAsync(userId.ToString(), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Supabase force-logout failed for suspended user {UserId}", userId);
            }

            try
            {
                await cache.RemoveAsync($"user-ctx:{userId}", ct);
                await cache.RemoveAsync($"user-suspension:{userId}", ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cache eviction failed for suspended user {UserId}", userId);
            }

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Suspend, TargetType.User,
                userId, targetName ?? userId.ToString(),
                new
                {
                    reason = req.Reason,
                    suspensionType = req.SuspensionType,
                    suspensionUntil = req.SuspensionUntil,
                },
                ct: ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/users/{userId}/unsuspend ────────────────────────────
        app.MapPost("/api/admin/users/{userId}/unsuspend", async (
            Guid userId,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersBan)) return Results.Forbid();
            using var conn = db.CreateConnection();
            var targetName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT COALESCE(full_name, username, id::text) FROM profiles WHERE id = @userId", new { userId });
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

            try
            {
                await cache.RemoveAsync($"user-ctx:{userId}", ct);
                await cache.RemoveAsync($"user-suspension:{userId}", ct);
            }
            catch
            {
                // Non-critical — suspension columns are cleared in DB
            }

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Unsuspend, TargetType.User,
                userId, targetName ?? userId.ToString(), ct: ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/users/bulk-action ─────────────────────────────────
        app.MapPost("/api/admin/users/bulk-action", async (
            [FromBody] BulkUserActionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            ISupabaseAdminClient supabase,
            HybridCache cache,
            AuditService audit,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.UserIds is not { Length: > 0 })
                return Results.BadRequest(new { error = "UserIds must not be empty." });
            if (req.UserIds.Length > 100)
                return Results.BadRequest(new { error = "Cannot process more than 100 users at once." });

            var requiredPerm = req.Action switch
            {
                "suspend" or "unsuspend" => Permissions.UsersBan,
                "delete" => Permissions.UsersDelete,
                _ => (string?)null
            };
            if (requiredPerm is null)
                return Results.BadRequest(new { error = $"Unknown action: {req.Action}" });
            if (!userCtx.Permissions.Contains(requiredPerm))
                return Results.Forbid();

            // Prevent admin from acting on themselves
            var safeIds = req.UserIds.Where(id => id != userCtx.UserIdGuid).ToArray();
            if (safeIds.Length == 0)
                return Results.BadRequest(new { error = "Cannot perform this action on yourself." });

            using var conn = db.CreateConnection();

            // Fetch target names for audit logging before any mutations
            var targetUsers = (await conn.QueryAsync<(Guid id, string name)>(
                "SELECT id, COALESCE(full_name, username, id::text) AS name FROM profiles WHERE id = ANY(@ids)",
                new { ids = safeIds })).ToDictionary(u => u.id, u => u.name);

            if (req.Action == "delete")
            {
                // All-or-nothing: single transaction wraps all cascade deletes
                using var txn = conn.BeginTransaction();
                try
                {
                    foreach (var userId in safeIds)
                        await DeleteUserCascadeAsync(userId, conn, txn);

                    txn.Commit();
                }
                catch
                {
                    txn.Rollback();
                    throw;
                }

                // Supabase auth cleanup after DB commit succeeds — external service calls
                // can't be rolled back, so we only attempt them after data is committed.
                // Each call is individually try-caught to prevent one failure from blocking the rest.
                var authFailures = 0;
                foreach (var userId in safeIds)
                {
                    try
                    {
                        await supabase.DeleteUserAsync(userId.ToString(), ct);
                    }
                    catch (Exception ex)
                    {
                        authFailures++;
                        logger.LogError(ex, "[BulkDelete] Failed to delete Supabase auth for user {UserId} — orphaned auth entry requires manual cleanup", userId);
                    }
                }

                // Audit each deletion
                foreach (var userId in safeIds)
                {
                    await audit.LogAsync(
                        userCtx.UserIdGuid, userCtx.Email,
                        ActionType.Delete, TargetType.User,
                        userId, targetUsers.GetValueOrDefault(userId, userId.ToString()),
                        new { bulk = true, batchSize = safeIds.Length }, ct: ct);
                }

                return Results.Ok(new { success = true, affected = safeIds.Length, authCleanupFailures = authFailures });
            }

            var actionType = req.Action == "suspend" ? ActionType.Suspend : ActionType.Unsuspend;

            var (sql, parameters) = req.Action switch
            {
                "suspend" => (
                    """
                    UPDATE profiles
                    SET is_suspended = true,
                        suspension_reason = @Reason,
                        updated_at = NOW()
                    WHERE id = ANY(@UserIds)
                    """,
                    (object)new { UserIds = safeIds, req.Reason }),
                "unsuspend" => (
                    """
                    UPDATE profiles
                    SET is_suspended = false,
                        suspension_reason = null,
                        suspension_type = null,
                        suspension_until = null,
                        updated_at = NOW()
                    WHERE id = ANY(@UserIds)
                    """,
                    (object)new { UserIds = safeIds }),
                _ => throw new InvalidOperationException()
            };

            var affected = await conn.ExecuteAsync(
                new CommandDefinition(sql, parameters, cancellationToken: ct));

            foreach (var userId in safeIds)
            {
                if (req.Action == "suspend")
                {
                    try
                    {
                        await supabase.LogoutUserAsync(userId.ToString(), ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Supabase force-logout failed for bulk-suspended user {UserId}", userId);
                    }
                }

                try
                {
                    await cache.RemoveAsync($"user-ctx:{userId}", ct);
                    await cache.RemoveAsync($"user-suspension:{userId}", ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Cache eviction failed for bulk action user {UserId}", userId);
                }
            }

            // Audit each affected user (email used as adminName — UserContext lacks display name)
            var auditDetails = req.Action == "suspend"
                ? (object)new { bulk = true, batchSize = safeIds.Length, reason = req.Reason }
                : new { bulk = true, batchSize = safeIds.Length };
            foreach (var userId in safeIds)
            {
                await audit.LogAsync(
                    userCtx.UserIdGuid, userCtx.Email,
                    actionType, TargetType.User,
                    userId, targetUsers.GetValueOrDefault(userId, userId.ToString()),
                    auditDetails, ct: ct);
            }

            return Results.Ok(new { success = true, affected });
        }).RequireAuthorization("Admin");

        // ── POST /api/emails ──────────────────────────────────────────────────
        // Replaces: send-email Edge Function (internal use only)
        // Requires authenticated admin or service call.
        app.MapPost("/api/emails", async (
            [FromBody] SendEmailRequest req,
            IEmailService email,
            HttpContext ctx,
            CancellationToken ct) =>
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
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid userId,
            string role,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid? userId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin &&
                !userCtx.Permissions.Contains(Permissions.AdminUsersView, StringComparer.OrdinalIgnoreCase))
                return Results.Forbid();

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
        }).RequireAuthorization(Permissions.AdminUsersView);

        // ── POST /api/admin/admin-user-roles ─────────────────────────────────────
        app.MapPost("/api/admin/admin-user-roles", async (
            [FromBody] AdminUserRoleAssignRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                """
                INSERT INTO admin_user_roles (user_id, role_id)
                VALUES (@UserId, @RoleId)
                ON CONFLICT DO NOTHING
                """,
                req);
            await EvictUserContextAsync(cache, req.UserId, ctx, ct);
            return Results.Created($"/api/admin/admin-user-roles?user_id={req.UserId}", new { success = true });
        }).RequireAuthorization(Permissions.AdminUsersAssignRole);

        // ── DELETE /api/admin/admin-user-roles ───────────────────────────────────
        app.MapDelete("/api/admin/admin-user-roles", async (
            Guid userId,
            Guid roleId,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                "DELETE FROM admin_user_roles WHERE user_id = @userId AND role_id = @roleId",
                new { userId, roleId });
            await EvictUserContextAsync(cache, userId, ctx, ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization(Permissions.AdminUsersRevokeRole);

        // ── GET /api/admin/my-context ─────────────────────────────────────────
        // Returns the current user's resolved admin roles and permissions from DB.
        // Used by the frontend AdminContext instead of hardcoded permission maps.
        app.MapGet("/api/admin/my-context", (HttpContext ctx) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            return Results.Ok(new
            {
                adminRoles = userCtx.AdminRoles,
                permissions = userCtx.Permissions,
                isSuperAdmin = userCtx.IsSuperAdmin,
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/admin/roles ──────────────────────────────────────────────
        // Returns the admin_roles catalog with permission/user counts. Supports optional ?q= filter.
        app.MapGet("/api/admin/roles", async (
            string? q,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin &&
                !userCtx.Permissions.Contains(Permissions.RbacView, StringComparer.OrdinalIgnoreCase))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
                """
                SELECT ar.id, ar.name, ar.key, ar.description, ar.created_at,
                       (SELECT COUNT(*) FROM admin_role_permissions WHERE role_id = ar.id) AS permission_count,
                       (SELECT COUNT(*) FROM admin_user_roles WHERE role_id = ar.id) AS user_count
                FROM admin_roles ar
                WHERE (@q IS NULL OR ar.name ILIKE @qp ESCAPE '\' OR ar.key ILIKE @qp ESCAPE '\')
                ORDER BY ar.name ASC
                LIMIT 100
                """,
                new { q, qp = string.IsNullOrWhiteSpace(q) ? null : EscapeLike(q) },
                cancellationToken: ct));
            return Results.Ok(rows);
        }).RequireAuthorization(Permissions.RbacView);

        // ── GET /api/admin/permissions ────────────────────────────────────────
        // Returns all available permissions grouped by resource.
        app.MapGet("/api/admin/permissions", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin &&
                !userCtx.Permissions.Contains(Permissions.RbacView, StringComparer.OrdinalIgnoreCase))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
                """
                SELECT id, name, description, resource, action,
                       label, category, risk_level, sort_order, is_system
                FROM admin_permissions
                ORDER BY category NULLS LAST, sort_order, resource, action
                """,
                cancellationToken: ct));
            return Results.Ok(rows);
        }).RequireAuthorization(Permissions.RbacView);

        // ── GET /api/admin/roles/{roleId} ─────────────────────────────────────
        // Returns a single role with its assigned permissions and user count.
        app.MapGet("/api/admin/roles/{roleId}", async (
            Guid roleId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin &&
                !userCtx.Permissions.Contains(Permissions.RbacView, StringComparer.OrdinalIgnoreCase))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var role = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                """
                SELECT ar.id, ar.name, ar.key, ar.description, ar.created_at,
                       (SELECT COUNT(*) FROM admin_user_roles WHERE role_id = ar.id) AS user_count
                FROM admin_roles ar
                WHERE ar.id = @roleId
                """,
                new { roleId },
                cancellationToken: ct));

            if (role is null) return Results.NotFound(new { error = "Role not found." });

            var permissions = await conn.QueryAsync<dynamic>(new CommandDefinition(
                """
                SELECT ap.id, ap.name, ap.description, ap.resource, ap.action,
                       ap.label, ap.category, ap.risk_level, ap.sort_order, ap.is_system
                FROM admin_permissions ap
                JOIN admin_role_permissions arp ON arp.permission_id = ap.id
                WHERE arp.role_id = @roleId
                ORDER BY ap.category NULLS LAST, ap.sort_order, ap.resource, ap.action
                """,
                new { roleId },
                cancellationToken: ct));

            return Results.Ok(new
            {
                role.id,
                role.name,
                role.key,
                role.description,
                role.created_at,
                role.user_count,
                permissions
            });
        }).RequireAuthorization(Permissions.RbacView);

        // ── POST /api/admin/roles ─────────────────────────────────────────────
        // Create a custom admin role with assigned permissions.
        app.MapPost("/api/admin/roles", async (
            [FromBody] CreateAdminRoleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            // Validate key format: lowercase, alphanumeric + underscores, 3-50 chars
            if (string.IsNullOrWhiteSpace(req.Key) || req.Key.Length < 3 || req.Key.Length > 50
                || !System.Text.RegularExpressions.Regex.IsMatch(req.Key, @"^[a-z0-9_]+$"))
                return Results.BadRequest(new { error = "Key must be 3-50 characters, lowercase alphanumeric and underscores only." });

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            if (req.Name.Length > 100)
                return Results.BadRequest(new { error = "Name must be 100 characters or fewer." });

            if ((req.Description?.Length ?? 0) > 500)
                return Results.BadRequest(new { error = "Description must be 500 characters or fewer." });

            if (req.PermissionIds is null || req.PermissionIds.Length == 0)
                return Results.BadRequest(new { error = "At least one permission is required." });

            using var conn = db.CreateConnection();

            // Validate all permissionIds exist
            var existingCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM admin_permissions WHERE id = ANY(@Ids)",
                new { Ids = req.PermissionIds },
                cancellationToken: ct));

            if (existingCount != req.PermissionIds.Length)
                return Results.BadRequest(new { error = "One or more permission IDs are invalid." });

            var roleId = Guid.NewGuid();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            using var txn = conn.BeginTransaction();

            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO admin_roles (id, name, key, description)
                    VALUES (@Id, @Name, @Key, @Description)
                    """,
                    new { Id = roleId, req.Name, req.Key, Description = req.Description ?? "" },
                    transaction: txn,
                    cancellationToken: ct));

                foreach (var permId in req.PermissionIds)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO admin_role_permissions (role_id, permission_id)
                        VALUES (@RoleId, @PermId)
                        ON CONFLICT DO NOTHING
                        """,
                        new { RoleId = roleId, PermId = permId },
                        transaction: txn,
                        cancellationToken: ct));
                }

                txn.Commit();
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Conflict(new { error = "A role with this name or key already exists." });
            }

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Create, TargetType.System,
                roleId, req.Name,
                new { role_key = req.Key, permission_count = req.PermissionIds.Length },
                ct: ct);

            return Results.Created($"/api/admin/roles/{roleId}", new
            {
                id = roleId,
                name = req.Name,
                key = req.Key,
                description = req.Description ?? "",
                permission_count = req.PermissionIds.Length
            });
        }).RequireAuthorization(Permissions.RbacCreateRole);

        // ── PUT /api/admin/roles/{roleId} ─────────────────────────────────────
        // Update an existing custom role's name, description, and permissions.
        app.MapPut("/api/admin/roles/{roleId}", async (
            Guid roleId,
            [FromBody] UpdateAdminRoleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var protectedRoleKeys = new HashSet<string> { "super_admin", "ops_admin", "moderator", "finance_admin", "support_admin" };

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            if (req.Name.Length > 100)
                return Results.BadRequest(new { error = "Name must be 100 characters or fewer." });

            if ((req.Description?.Length ?? 0) > 500)
                return Results.BadRequest(new { error = "Description must be 500 characters or fewer." });

            if (req.PermissionIds is null || req.PermissionIds.Length == 0)
                return Results.BadRequest(new { error = "At least one permission is required." });

            using var conn = db.CreateConnection();

            // Fetch existing role
            var existingRole = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT id, name, key, description FROM admin_roles WHERE id = @roleId",
                new { roleId },
                cancellationToken: ct));

            if (existingRole is null) return Results.NotFound(new { error = "Role not found." });

            // Validate all permissionIds exist
            var existingCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM admin_permissions WHERE id = ANY(@Ids)",
                new { Ids = req.PermissionIds },
                cancellationToken: ct));

            if (existingCount != req.PermissionIds.Length)
                return Results.BadRequest(new { error = "One or more permission IDs are invalid." });

            // Get old permissions for audit
            var oldPermIds = (await conn.QueryAsync<Guid>(new CommandDefinition(
                "SELECT permission_id FROM admin_role_permissions WHERE role_id = @roleId",
                new { roleId },
                cancellationToken: ct))).ToArray();

            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            using var txn = conn.BeginTransaction();

            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE admin_roles SET name = @Name, description = @Description
                    WHERE id = @roleId
                    """,
                    new { req.Name, Description = req.Description ?? "", roleId },
                    transaction: txn,
                    cancellationToken: ct));

                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM admin_role_permissions WHERE role_id = @roleId",
                    new { roleId },
                    transaction: txn,
                    cancellationToken: ct));

                foreach (var permId in req.PermissionIds)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO admin_role_permissions (role_id, permission_id)
                        VALUES (@RoleId, @PermId)
                        ON CONFLICT DO NOTHING
                        """,
                        new { RoleId = roleId, PermId = permId },
                        transaction: txn,
                        cancellationToken: ct));
                }

                txn.Commit();
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Conflict(new { error = "A role with this name already exists." });
            }

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Update, TargetType.System,
                roleId, req.Name,
                new
                {
                    old_name = (string)existingRole.name,
                    new_name = req.Name,
                    old_permissions = oldPermIds,
                    new_permissions = req.PermissionIds
                },
                ct: ct);

            await EvictUsersWithAdminRoleAsync(cache, conn, roleId, ctx, ct);

            return Results.Ok(new { success = true, id = roleId, name = req.Name });
        }).RequireAuthorization(Permissions.RbacEditRole);

        // ── DELETE /api/admin/roles/{roleId} ──────────────────────────────────
        // Delete a custom admin role. Built-in roles cannot be deleted.
        app.MapDelete("/api/admin/roles/{roleId}", async (
            Guid roleId,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var protectedRoleKeys = new HashSet<string> { "super_admin", "ops_admin", "moderator", "finance_admin", "support_admin" };

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            using var conn = db.CreateConnection();

            var existingRole = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT id, name, key FROM admin_roles WHERE id = @roleId",
                new { roleId },
                cancellationToken: ct));

            if (existingRole is null) return Results.NotFound(new { error = "Role not found." });

            // Check if any users are assigned
            var assignedCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM admin_user_roles WHERE role_id = @roleId",
                new { roleId },
                cancellationToken: ct));

            if (assignedCount > 0)
                return Results.Conflict(new { error = $"Cannot delete role: {assignedCount} user(s) are still assigned to it." });

            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            using var txn = conn.BeginTransaction();

            try
            {
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM admin_role_permissions WHERE role_id = @roleId",
                    new { roleId },
                    transaction: txn,
                    cancellationToken: ct));

                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM admin_roles WHERE id = @roleId",
                    new { roleId },
                    transaction: txn,
                    cancellationToken: ct));

                txn.Commit();
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23503")
            {
                return Results.Conflict(new { error = "Cannot delete role: users were assigned after your check." });
            }

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Delete, TargetType.System,
                roleId, (string)existingRole.name,
                new { role_key = (string)existingRole.key },
                ct: ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/users/{userId}/roles ────────────────────────────────
        app.MapGet("/api/admin/users/{userId}/roles", async (
            Guid userId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid userId,
            [FromBody] UpdateRoleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
        // Replaces admin_roles assignments. Super_admin only — use dedicated
        // POST/DELETE /api/admin/admin-user-roles for granular role changes.
        app.MapPut("/api/admin/users/{userId}", async (
            Guid userId,
            [FromBody] AdminUpdateUserRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

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

                // Sync profiles.admin_roles TEXT[] with the resolved role keys
                var roleKeys = (await conn.QueryAsync<string>(
                    "SELECT ar.key FROM admin_user_roles aur JOIN admin_roles ar ON ar.id = aur.role_id WHERE aur.user_id = @userId",
                    new { userId }, txn)).ToArray();

                await conn.ExecuteAsync(
                    "UPDATE profiles SET admin_roles = @roleKeys, is_admin = @isAdmin WHERE id = @userId",
                    new { userId, roleKeys, isAdmin = roleKeys.Length > 0 }, txn);
            }

            txn.Commit();
            await EvictUserContextAsync(cache, userId, ctx, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/verified-roles ──────────────────────────────────────
        app.MapGet("/api/admin/verified-roles", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.LicensesView)) return Results.Forbid();

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
            Guid userId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            [FromQuery] string? status = null,
            [FromQuery] string? search = null,
            [FromQuery] string? game = null,
            [FromQuery] string? format = null,
            [FromQuery] decimal? prize_min = null,
            [FromQuery] decimal? prize_max = null,
            [FromQuery] string? date_from = null,
            [FromQuery] string? date_to = null,
            [FromQuery] string? sort_by = null,
            [FromQuery] string? sort_dir = null,
            int page = 1,
            int limit = 50,
            HttpContext ctx = default!,
            IDbConnectionFactory db = default!,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            page = Math.Max(page, 1);
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(status))
                conditions.Add("t.status::text = @status");
            if (!string.IsNullOrWhiteSpace(search))
                conditions.Add("(t.name ILIKE @search ESCAPE '\\' OR p.username ILIKE @search ESCAPE '\\')");
            if (!string.IsNullOrWhiteSpace(game))
                conditions.Add("t.game ILIKE @game ESCAPE '\\'");
            if (!string.IsNullOrWhiteSpace(format))
                conditions.Add("t.format = @format");
            if (prize_min.HasValue)
                conditions.Add("COALESCE(t.prize_pool, 0) >= @prizeMin");
            if (prize_max.HasValue)
                conditions.Add("COALESCE(t.prize_pool, 0) <= @prizeMax");

            DateTimeOffset? dateFrom = null;
            DateTimeOffset? dateTo = null;
            if (!string.IsNullOrWhiteSpace(date_from) && DateTimeOffset.TryParse(date_from, out var df))
            { dateFrom = df; conditions.Add("t.created_at >= @dateFrom"); }
            if (!string.IsNullOrWhiteSpace(date_to) && DateTimeOffset.TryParse(date_to, out var dt2))
            { dateTo = dt2.AddDays(1); conditions.Add("t.created_at < @dateTo"); }

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var allowedSorts = new HashSet<string> { "created_at", "start_date", "prize_pool", "name" };
            var sortColumn = allowedSorts.Contains(sort_by ?? "") ? sort_by! : "created_at";
            var sortDirection = sort_dir?.ToLower() == "asc" ? "ASC" : "DESC";

            var sql = $"""
                SELECT t.*, p.username AS organizer_name
                FROM tournaments t
                LEFT JOIN profiles p ON p.id = t.organizer_id
                {where}
                ORDER BY t.{sortColumn} {sortDirection}
                LIMIT @limit OFFSET @offset
                """;

            var rows = await conn.QueryAsync<dynamic>(sql,
                new { status, search = EscapeLike(search), game = EscapeLike(game), format, prizeMin = prize_min, prizeMax = prize_max, dateFrom, dateTo, limit, offset = (page - 1) * limit });

            var needsJoin = !string.IsNullOrWhiteSpace(search);
            var countJoin = needsJoin ? "LEFT JOIN profiles p ON p.id = t.organizer_id" : "";
            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM tournaments t {countJoin} {where}",
                new { status, search = EscapeLike(search), game = EscapeLike(game), format, prizeMin = prize_min, prizeMax = prize_max, dateFrom, dateTo });

            var statusCounts = await conn.QueryAsync<dynamic>(
                "SELECT status::text AS status, COUNT(*)::int AS count FROM tournaments GROUP BY status");
            var statusCountDict = new Dictionary<string, int>();
            foreach (var sc in statusCounts)
                statusCountDict[(string)sc.status] = (int)sc.count;

            return Results.Ok(new { data = rows, total, statusCounts = statusCountDict });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/tournaments/{id} ───────────────────────────────────
        // Updates tournament status and/or is_featured flag from admin panel.
        app.MapPut("/api/admin/tournaments/{id}", async (
            Guid id,
            [FromBody] AdminUpdateTournamentRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            OperationsAuthorizationService opsAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var requiredPermissions = new List<string>();
            if (req.Name is not null || req.Game is not null || req.Format is not null ||
                req.PrizePool.HasValue || req.MaxTeams.HasValue || req.StartDate.HasValue)
                requiredPermissions.Add(Permissions.TournamentsEdit);
            if (req.Status is "approved")
                requiredPermissions.Add(Permissions.TournamentsApprove);
            if (req.Status is "cancelled")
                requiredPermissions.Add(Permissions.TournamentsCancel);
            if (req.Status is not null && req.Status is not "approved" and not "cancelled")
                requiredPermissions.Add(Permissions.TournamentsEdit);
            if (req.IsFeatured is true)
                requiredPermissions.Add(Permissions.TournamentsFeature);
            if (req.IsFeatured is false)
                requiredPermissions.Add(Permissions.TournamentsFeature);

            if (requiredPermissions.Count == 0)
                requiredPermissions.Add(Permissions.TournamentsEdit);

            foreach (var permission in requiredPermissions.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!await opsAuth.CanMutateTournamentAsync(userCtx, id, permission, ct))
                    return Results.Forbid();
            }

            using var conn = db.CreateConnection();
            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                new CommandDefinition(
                    """
                    SELECT id, name, game, format, status::text AS status, prize_pool, max_teams,
                           start_date, is_featured
                    FROM tournaments
                    WHERE id = @id
                    """,
                    new { id },
                    cancellationToken: ct));
            if (existing is null) return Results.NotFound();

            var affected = await conn.ExecuteAsync(
                new CommandDefinition(
                    """
                UPDATE tournaments SET
                    status      = CASE WHEN @Status IS NOT NULL THEN @Status::tournament_status ELSE status END,
                    is_featured = COALESCE(@IsFeatured, is_featured),
                    name        = COALESCE(@Name, name),
                    game        = COALESCE(@Game, game),
                    format      = COALESCE(@Format, format),
                    prize_pool  = COALESCE(@PrizePool, prize_pool),
                    max_teams   = COALESCE(@MaxTeams, max_teams),
                    start_date  = COALESCE(@StartDate, start_date),
                    updated_at  = now()
                WHERE id = @id
                """,
                    new
                    {
                        id,
                        req.Status,
                        req.IsFeatured,
                        req.Name,
                        req.Game,
                        req.Format,
                        req.PrizePool,
                        req.MaxTeams,
                        req.StartDate
                    },
                    cancellationToken: ct));

            var actionType = req.Status switch
            {
                "approved" => ActionType.Approve,
                "cancelled" => ActionType.Cancel,
                _ when req.IsFeatured is true => ActionType.Feature,
                _ when req.IsFeatured is false => ActionType.Unfeature,
                _ => ActionType.Update
            };

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                actionType, TargetType.Tournament,
                id, (string?)existing.name ?? id.ToString(),
                new
                {
                    before = new
                    {
                        name = existing.name,
                        game = existing.game,
                        format = existing.format,
                        status = existing.status,
                        prize_pool = existing.prize_pool,
                        max_teams = existing.max_teams,
                        start_date = existing.start_date,
                        is_featured = existing.is_featured
                    },
                    requested = req,
                    reason = req.Reason
                },
                ct: ct);

            return affected == 0 ? Results.NotFound() : Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/tournaments/bulk-action ───────────────────────────
        app.MapPost("/api/admin/tournaments/bulk-action", async (
            [FromBody] BulkTournamentActionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            OperationsAuthorizationService opsAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.TournamentIds is not { Length: > 0 })
                return Results.BadRequest(new { error = "TournamentIds must not be empty." });
            if (req.TournamentIds.Length > 100)
                return Results.BadRequest(new { error = "Cannot process more than 100 tournaments at once." });

            var requiredPermission = req.Action switch
            {
                "approve" => Permissions.TournamentsApprove,
                "cancel" => Permissions.TournamentsCancel,
                "feature" => Permissions.TournamentsFeature,
                "unfeature" => Permissions.TournamentsFeature,
                _ => Permissions.TournamentsEdit
            };

            foreach (var tournamentId in req.TournamentIds.Distinct())
            {
                if (!await opsAuth.CanMutateTournamentAsync(userCtx, tournamentId, requiredPermission, ct))
                    return Results.Forbid();
            }

            var sql = req.Action switch
            {
                "approve" => "UPDATE tournaments SET status = 'approved'::tournament_status, updated_at = now() WHERE id = ANY(@Ids) AND status IN ('draft', 'pending')",
                "cancel" => "UPDATE tournaments SET status = 'cancelled'::tournament_status, updated_at = now() WHERE id = ANY(@Ids) AND status NOT IN ('completed', 'cancelled')",
                "feature" => "UPDATE tournaments SET is_featured = true, updated_at = now() WHERE id = ANY(@Ids)",
                "unfeature" => "UPDATE tournaments SET is_featured = false, updated_at = now() WHERE id = ANY(@Ids)",
                _ => (string?)null
            };
            if (sql is null)
                return Results.BadRequest(new { error = $"Unknown action: {req.Action}" });

            // Map action string to audit ActionType
            var actionType = req.Action switch
            {
                "approve" => ActionType.Approve,
                "cancel" => ActionType.Cancel,
                "feature" => ActionType.Feature,
                "unfeature" => ActionType.Unfeature,
                _ => ActionType.Update
            };

            using var conn = db.CreateConnection();

            // Fetch target names for audit logging before mutation
            var targetTournaments = (await conn.QueryAsync<(Guid id, string name)>(
                "SELECT id, COALESCE(name, id::text) AS name FROM tournaments WHERE id = ANY(@ids)",
                new { ids = req.TournamentIds })).ToDictionary(t => t.id, t => t.name);

            var affected = await conn.ExecuteAsync(
                new CommandDefinition(sql, new { Ids = req.TournamentIds }, cancellationToken: ct));

            // Audit each affected tournament
            foreach (var id in req.TournamentIds)
            {
                await audit.LogAsync(
                    userCtx.UserIdGuid, userCtx.Email,
                    actionType, TargetType.Tournament,
                    id, targetTournaments.GetValueOrDefault(id, id.ToString()),
                    new { bulk = true, batchSize = req.TournamentIds.Length, action = req.Action }, ct: ct);
            }

            return Results.Ok(new { success = true, affected });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/disputes ────────────────────────────────────────────
        app.MapGet("/api/admin/disputes", async (
            string? status,
            int page = 1,
            int limit = 50,
            HttpContext ctx = default!,
            IDbConnectionFactory db = default!,
            CancellationToken ct = default) =>
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
            Guid disputeId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid disputeId,
            [FromBody] AdminUpdateDisputeRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct)
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
            Guid disputeId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid disputeId,
            [FromBody] AddDisputeCommentRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<MatchHub> matchHub,
            CancellationToken ct) =>
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
            Guid tournamentId,
            Guid banId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid tournamentId,
            Guid pid,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid disputeId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid? organizationId,
            [FromQuery] string? search = null,
            [FromQuery] string? target_type = null,
            [FromQuery] string? from = null,
            [FromQuery] string? to = null,
            int page = 1,
            int limit = 50,
            HttpContext ctx = default!,
            IDbConnectionFactory db = default!,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            conditions.Add("(@organizationId IS NULL OR sal.organization_id = @organizationId)");

            if (!string.IsNullOrWhiteSpace(search))
                conditions.Add("(p.username ILIKE @search ESCAPE '\\' OR sal.action ILIKE @search ESCAPE '\\' OR sal.target_type ILIKE @search ESCAPE '\\')");
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
                new { organizationId, search = EscapeLike(search), target_type, fromDate, toDate, limit, offset = (page - 1) * limit });

            var countSql = $"SELECT COUNT(*) FROM staff_audit_log sal LEFT JOIN profiles p ON p.id = sal.actor_id {where}";
            var total = await conn.ExecuteScalarAsync<int>(countSql,
                new { organizationId, search = EscapeLike(search), target_type, fromDate, toDate });

            return Results.Ok(new { data = rows, count = total });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/audit-logs ─────────────────────────────────────────
        app.MapPost("/api/admin/audit-logs", async (
            [FromBody] CreateAuditLogRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var details = new
            {
                admin_name = req.AdminName,
                target_name = req.TargetName,
                severity = req.Severity,
                user_agent = req.UserAgent,
                extra = req.Details,
            };

            await conn.ExecuteAsync(
                """
                INSERT INTO staff_audit_log (organization_id, actor_id, action, target_type, target_id, details)
                VALUES (@orgId, @actorId, @action, @targetType, @targetId, @details::jsonb)
                """,
                new
                {
                    orgId = (Guid?)null,
                    actorId = userCtx.UserIdGuid,
                    action = req.ActionType,
                    targetType = req.TargetType,
                    targetId = Guid.TryParse(req.TargetId, out var tid) ? tid : (Guid?)null,
                    details = System.Text.Json.JsonSerializer.Serialize(details),
                });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/system-settings ────────────────────────────────────
        app.MapGet("/api/admin/system-settings", async (
            string? category,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isSuperAdmin = userCtx.AdminRoles.Contains("super_admin");

            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
                """
                SELECT key, value, category, label, description,
                       data_type, is_sensitive, updated_at
                FROM system_settings
                WHERE (@category IS NULL OR category = @category)
                ORDER BY category, key
                """, new { category }, cancellationToken: ct));

            var result = rows.Select(r =>
            {
                bool sensitive = (bool)r.is_sensitive;
                return new
                {
                    key = (string)r.key,
                    value = sensitive && !isSuperAdmin ? "••••••••" : (string)r.value,
                    category = (string)r.category,
                    label = (string)r.label,
                    description = (string)(r.description ?? ""),
                    data_type = (string)r.data_type,
                    is_sensitive = sensitive,
                    updated_at = r.updated_at,
                };
            });

            return Results.Ok(result);
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/system-settings/{key} ─────────────────────────────
        app.MapGet("/api/admin/system-settings/{key}", async (
            string key,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                """
                SELECT key, value, category, label, description,
                       data_type, is_sensitive, updated_at
                FROM system_settings
                WHERE key = @key
                """, new { key }, cancellationToken: ct));

            if (row is null) return Results.NotFound(new { error = $"Setting '{key}' not found." });

            bool sensitive = (bool)row.is_sensitive;
            bool isSuperAdmin = userCtx.AdminRoles.Contains("super_admin");

            return Results.Ok(new
            {
                key = (string)row.key,
                value = sensitive && !isSuperAdmin ? "••••••••" : (string)row.value,
                category = (string)row.category,
                label = (string)row.label,
                description = (string)(row.description ?? ""),
                data_type = (string)row.data_type,
                is_sensitive = sensitive,
                updated_at = row.updated_at,
            });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/system-settings ────────────────────────────────────
        app.MapPut("/api/admin/system-settings", async (
            [FromBody] UpdateSystemSettingsRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.Settings is null || req.Settings.Length == 0)
                return Results.BadRequest(new { error = "At least one setting is required." });

            using var conn = db.CreateConnection();

            // Fetch existing settings for validation + audit diff
            var requestedKeys = req.Settings.Select(s => s.Key).ToArray();
            var existing = (await conn.QueryAsync<dynamic>(
                "SELECT key, value, data_type, is_sensitive FROM system_settings WHERE key = ANY(@keys)",
                new { keys = requestedKeys })).ToDictionary(r => (string)r.key, r => r);

            // Validate all keys exist
            var unknownKeys = requestedKeys.Where(k => !existing.ContainsKey(k)).ToArray();
            if (unknownKeys.Length > 0)
                return Results.BadRequest(new { error = $"Unknown setting keys: {string.Join(", ", unknownKeys)}" });

            // Validate values by data_type
            var errors = new List<string>();
            var isSuperAdmin = userCtx.AdminRoles.Contains("super_admin");

            foreach (var setting in req.Settings)
            {
                var meta = existing[setting.Key];
                string dataType = (string)meta.data_type;
                bool sensitive = (bool)meta.is_sensitive;

                // Sensitive settings require super_admin
                if (sensitive && !isSuperAdmin)
                {
                    errors.Add($"'{setting.Key}' requires super_admin role to modify.");
                    continue;
                }

                switch (dataType)
                {
                    case "boolean":
                        if (setting.Value != "true" && setting.Value != "false")
                            errors.Add($"'{setting.Key}' must be 'true' or 'false'.");
                        break;
                    case "number":
                        if (!double.TryParse(setting.Value, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out _))
                            errors.Add($"'{setting.Key}' must be a valid number.");
                        break;
                    case "email":
                        if (!string.IsNullOrEmpty(setting.Value) &&
                            !System.Net.Mail.MailAddress.TryCreate(setting.Value, out _))
                            errors.Add($"'{setting.Key}' must be a valid email address.");
                        break;
                    case "url":
                        if (!string.IsNullOrEmpty(setting.Value) &&
                            !(Uri.TryCreate(setting.Value, UriKind.Absolute, out var uri) &&
                              (uri.Scheme == "https" || uri.Scheme == "http")))
                            errors.Add($"'{setting.Key}' must be a valid HTTP(S) URL.");
                        break;
                }
            }

            if (errors.Count > 0)
                return Results.BadRequest(new { error = "Validation failed.", details = errors });

            // Build audit diff and apply updates in a transaction
            var changes = new List<object>();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();
            using var txn = conn.BeginTransaction();

            foreach (var setting in req.Settings)
            {
                var meta = existing[setting.Key];
                var oldValue = (string)meta.value;
                bool sensitive = (bool)meta.is_sensitive;

                await conn.ExecuteAsync(
                    """
                    UPDATE system_settings
                    SET value = @value, updated_by = @updatedBy, updated_at = NOW()
                    WHERE key = @key
                    """,
                    new { key = setting.Key, value = setting.Value, updatedBy = userCtx.UserIdGuid },
                    transaction: txn);

                if (oldValue != setting.Value)
                {
                    changes.Add(new
                    {
                        key = setting.Key,
                        old_value = sensitive ? "••••••••" : oldValue,
                        new_value = sensitive ? "••••••••" : setting.Value,
                    });
                }
            }

            txn.Commit();

            // Audit log
            if (changes.Count > 0)
            {
                await audit.LogAsync(
                    userCtx.UserIdGuid,
                    userCtx.Email,
                    ActionType.SettingsUpdate,
                    TargetType.System,
                    userCtx.UserIdGuid,           // targetId — system-level, use admin's own id
                    "system_settings",
                    new { updated_count = changes.Count, changes },
                    ct: ct);
            }

            return Results.Ok(new { success = true, updated = changes.Count });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/verification-requests ──────────────────────────────
        app.MapGet("/api/admin/verification-requests", async (
            string? status,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.VerificationView)) return Results.Forbid();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT vr.id, vr.user_id, vr.requested_role, vr.status,
                       vr.first_name, vr.last_name, vr.business_name, vr.business_type,
                       vr.business_description, vr.email AS contact_email,
                       vr.cnic_front_url, vr.cnic_back_url,
                       vr.verification_notes AS notes,
                       vr.created_at, vr.updated_at,
                       vr.experience_description, vr.website_url, vr.phone,
                       vr.date_of_birth, vr.organizer_data, vr.venue_data,
                       p.username  AS profile_username,
                       p.full_name AS profile_full_name,
                       p.email     AS profile_email,
                       p.avatar_url AS profile_avatar_url
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
            Guid requestId,
            [FromBody] UpdateVerificationRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService email,
            IConfiguration config,
            CancellationToken ct)
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
                    existingLicenseId = $"{prefix}-{Guid.NewGuid().ToString("N")[..10].ToUpper()}";
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
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService email,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.VerificationApprove)) return Results.Forbid();

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
                    var issuedAt = DateTime.UtcNow;
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
                            "organizer" => "ESP-OR",
                            "venue_owner" => "ESP-VO",
                            "broadcaster" => "ESP-BR",
                            _ => "ESP-XX"
                        };
                        existingLicenseId = $"{prefix}-{Guid.NewGuid().ToString("N")[..10].ToUpper()}";

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
                                username = (string?)profile.username ?? "there",
                                licenseType = req.Role,
                                licenseId = existingLicenseId,
                                issuedAt = issuedAt.ToString("MMM dd, yyyy"),
                                expiresAt = expiresAt.ToString("MMM dd, yyyy"),
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
            string? status,
            string? type,
            string? q,
            int? limit,
            int? offset,
            HttpContext ctx,
            IDbConnectionFactory db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.LicensesView)) return Results.Forbid();

            try
            {
                using var conn = db.CreateConnection();
                var where = new List<string>();
                var p = new Dapper.DynamicParameters();

                if (!string.IsNullOrWhiteSpace(status)) { where.Add("l.status = @status"); p.Add("status", status); }
                if (!string.IsNullOrWhiteSpace(type)) { where.Add("l.license_type = @type"); p.Add("type", type); }
                if (!string.IsNullOrWhiteSpace(q)) { where.Add("(p.username ILIKE @q OR p.email ILIKE @q OR l.license_id ILIKE @q)"); p.Add("q", $"%{q}%"); }

                var lim = Math.Min(limit ?? 50, 200);
                var off = offset ?? 0;
                p.Add("lim", lim);
                p.Add("off", off);

                var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
                var sql = $"""
                    SELECT l.id, l.user_id, l.license_id, l.license_type, l.status,
                           l.issued_at, l.expires_at, l.notes, l.created_at,
                           p.username, p.email, p.avatar_url, p.full_name,
                           vr.phone, vr.website_url, vr.business_name, vr.business_type
                    FROM licenses l
                    JOIN profiles p ON p.id = l.user_id
                    LEFT JOIN verification_requests vr ON vr.user_id = l.user_id
                        AND vr.requested_role::text = l.license_type
                        AND vr.status = 'approved'
                    {whereClause}
                    ORDER BY l.created_at DESC
                    LIMIT @lim OFFSET @off
                    """;
                var rows = await conn.QueryAsync<dynamic>(sql, p);

                var countP = new Dapper.DynamicParameters();
                if (!string.IsNullOrWhiteSpace(status)) countP.Add("status", status);
                if (!string.IsNullOrWhiteSpace(type)) countP.Add("type", type);
                if (!string.IsNullOrWhiteSpace(q)) countP.Add("q", $"%{q}%");

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
            Guid userId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Esportra.Api.Endpoints.AdminEndpoints");

            using var conn = db.CreateConnection();
            var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT p.id, p.username, p.email, p.full_name, p.avatar_url, p.bio, p.location, p.country_code,
                       p.date_of_birth, p.riot_tag, p.social_links, p.card_image_url,
                       p.banner_url, p.is_admin, p.admin_roles, p.is_suspended,
                       p.suspension_reason, p.suspension_type, p.suspension_until, p.settings,
                       p.created_at, p.updated_at,
                       CASE
                           WHEN p.role = 'organizer'   AND EXISTS (SELECT 1 FROM verified_roles vr WHERE vr.user_id = p.id AND vr.status = 'approved' AND vr.is_active = TRUE) THEN 'verified_organizer'
                           WHEN p.role = 'venue_owner' AND EXISTS (SELECT 1 FROM verified_roles vr WHERE vr.user_id = p.id AND vr.status = 'approved' AND vr.is_active = TRUE) THEN 'verified_venue_owner'
                           WHEN EXISTS (SELECT 1 FROM auth.users au WHERE au.id = p.id AND au.email_confirmed_at IS NOT NULL) THEN 'email_verified'
                           ELSE 'unverified'
                       END AS verification_status
                FROM profiles p WHERE p.id = @userId
                """,
                new { userId });
            if (profile is null) return Results.NotFound(new { error = "User not found" });

            async Task<IEnumerable<dynamic>> SafeQueryAsync(string segment, string sql, object? param = null)
            {
                try
                {
                    return await conn.QueryAsync<dynamic>(new CommandDefinition(sql, param, cancellationToken: ct));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Admin user detail segment {Segment} failed for {UserId}", segment, userId);
                    return Array.Empty<dynamic>();
                }
            }

            var licenses = await SafeQueryAsync(
                "licenses",
                "SELECT id, license_id, license_type, status, issued_at, expires_at, notes FROM licenses WHERE user_id = @userId ORDER BY issued_at DESC",
                new { userId });
            var userRoles = await SafeQueryAsync(
                "user_roles",
                "SELECT role, COALESCE(is_active, TRUE) AS is_active FROM user_roles WHERE user_id = @userId",
                new { userId });
            var verifiedRoles = await SafeQueryAsync(
                "verified_roles",
                "SELECT role, status, is_active, verified_at FROM verified_roles WHERE user_id = @userId",
                new { userId });
            var organizations = await SafeQueryAsync(
                "organizations",
                "SELECT id, name, slug, logo_url FROM organizations WHERE owner_id = @userId",
                new { userId });
            var venues = await SafeQueryAsync(
                "venues",
                "SELECT id, name, city, country, status FROM venues WHERE owner_id = @userId",
                new { userId });
            var tournaments = await SafeQueryAsync(
                "tournaments",
                "SELECT id, name, game, status::text AS status FROM tournaments WHERE organizer_id = @userId ORDER BY created_at DESC LIMIT 20",
                new { userId });
            var connectedAccounts = await SafeQueryAsync(
                "connected_accounts",
                "SELECT provider, provider_id, created_at, updated_at FROM auth.identities WHERE user_id = @userId",
                new { userId });
            var teams = await SafeQueryAsync(
                "teams",
                """
                SELECT t.id, t.name, t.tag, t.logo_url, tm.role
                FROM team_members tm
                JOIN teams t ON t.id = tm.team_id
                WHERE tm.user_id = @userId AND tm.is_active = TRUE
                """,
                new { userId });

            return Results.Ok(new
            {
                profile,
                licenses,
                user_roles = userRoles,
                verified_roles = verifiedRoles,
                organizations,
                venues,
                tournaments,
                connected_accounts = connectedAccounts,
                teams
            });
        }).RequireAuthorization(Permissions.UsersView);

        // ── POST /api/admin/licenses ──────────────────────────────────────────
        // Manually assign a license to a user (super admin)
        app.MapPost("/api/admin/licenses", async (
            [FromBody] AdminCreateLicenseRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.LicensesCreate)) return Results.Forbid();
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
            var issuedAt = DateTime.UtcNow;
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
                    "organizer" => "ESP-OR",
                    "venue_owner" => "ESP-VO",
                    "broadcaster" => "ESP-BR",
                    _ => "ESP-XX"
                };
                licenseId = $"{prefix}-{Guid.NewGuid().ToString("N")[..10].ToUpper()}";

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
            Guid userId,
            string licenseType,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.LicensesRevoke)) return Results.Forbid();
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
            Guid userId,
            string licenseType,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.LicensesReinstate)) return Results.Forbid();
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
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.LicensesCreate)) return Results.Forbid();

            AdminBackfillLicensesRequest? req = null;
            try { req = await ctx.Request.ReadFromJsonAsync<AdminBackfillLicensesRequest>(ct); } catch { }
            var licenseType = (req?.LicenseType ?? "organizer").Trim().ToLowerInvariant();
            if (licenseType is not ("organizer" or "venue_owner" or "broadcaster"))
                return Results.BadRequest(new { success = false, error = "Invalid license type." });
            var syncsVerifiedRoles = licenseType is "organizer" or "venue_owner";
            var prefix = licenseType switch
            {
                "organizer" => "ESP-OR",
                "venue_owner" => "ESP-VO",
                "broadcaster" => "ESP-BR",
                _ => "ESP-XX"
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
            var issuedAt = DateTime.UtcNow;
            var expiresAt = issuedAt.AddYears(1);
            var issued = 0;

            foreach (var userId in userIds)
            {
                var licenseId = $"{prefix}-{Guid.NewGuid().ToString("N")[..10].ToUpper()}";

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
            Guid requestId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.VerificationDelete)) return Results.Forbid();

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
            Guid licenseId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.LicensesDelete)) return Results.Forbid();

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
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid userId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Only the user themselves or an admin can query roles
            if (userCtx.UserIdGuid != userId && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var roles = (await conn.QueryAsync<string>(
                "SELECT role FROM user_roles WHERE user_id = @userId AND is_active = TRUE",
                new { userId })).ToList();
            return Results.Ok(new { userId, roles });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/users/{userId}/role ───────────────────────────────────────
        app.MapPut("/api/users/{userId}/role", async (
            Guid userId,
            [FromBody] SetRoleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            string? status,
            int page = 1,
            int limit = 50,
            HttpContext ctx = default!,
            IDbConnectionFactory db = default!,
            CancellationToken ct = default) =>
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
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid id,
            [FromBody] UpdateApplicationRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid id,
            [FromBody] PartnerApplicationApprovalRequest request,
            HttpContext ctx,
            IDbConnectionFactory db,
            PartnerSponsorOnboardingService invitations,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.SponsorsApproveApplication)) return Results.Forbid();

            using var conn = db.CreateConnection();
            using var transaction = conn.BeginTransaction();
            var application = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT *
                FROM partner_applications
                WHERE id = @id
                FOR UPDATE
                """,
                new { id }, transaction);
            if (application is null) return Results.NotFound();

            var dict = (IDictionary<string, object?>)application;
            var status = dict["status"]?.ToString();
            if (status == "approved" && dict["approved_sponsor_id"] is Guid approvedSponsorId)
            {
                var approvedCompanyName = dict["company_name"]?.ToString() ?? "Unknown";
                var approvedContactEmail = request.InvitationEmail?.Trim()
                    ?? dict["invitation_email"]?.ToString()?.Trim()
                    ?? dict["contact_email"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(approvedContactEmail)
                    || approvedContactEmail.Length > 254
                    || !System.Net.Mail.MailAddress.TryCreate(approvedContactEmail, out _))
                    return Results.BadRequest(new { error = "Application has no valid invitation email." });

                var existingInvitation = await conn.QuerySingleOrDefaultAsync<(Guid Id, bool RequiresPasswordSetup)>(
                    """
                    SELECT id AS Id, requires_password_setup AS RequiresPasswordSetup
                    FROM partner_sponsor_invitations
                    WHERE sponsor_id = @sponsorId
                      AND LOWER(email) = LOWER(@email)
                      AND (
                          status = 'accepted'
                          OR (status = 'pending' AND expires_at > NOW())
                      )
                    ORDER BY created_at DESC
                    LIMIT 1
                    """,
                    new { sponsorId = approvedSponsorId, email = approvedContactEmail }, transaction);
                transaction.Commit();

                if (existingInvitation != default)
                {
                    return Results.Ok(new
                    {
                        success = true,
                        sponsorId = approvedSponsorId,
                        companyName = approvedCompanyName,
                        contactEmail = approvedContactEmail,
                        invitationId = existingInvitation.Id,
                        existingInvitation.RequiresPasswordSetup,
                        alreadyApproved = true,
                    });
                }

                var replacementInvitation = await invitations.CreateInvitationAsync(
                    approvedSponsorId,
                    approvedContactEmail,
                    "owner",
                    userCtx.UserIdGuid,
                    ct);
                if (replacementInvitation is null)
                    return Results.Json(new { error = "Unable to create sponsor invitation." }, statusCode: 500);
                if (!replacementInvitation.WasDelivered)
                    return Results.Json(new
                    {
                        error = "Invitation delivery failed. Resend the invitation.",
                        sponsorId = approvedSponsorId,
                        replacementInvitation.InvitationId,
                    }, statusCode: 502);

                return Results.Ok(new
                {
                    success = true,
                    sponsorId = approvedSponsorId,
                    companyName = approvedCompanyName,
                    contactEmail = approvedContactEmail,
                    replacementInvitation.InvitationId,
                    replacementInvitation.RequiresPasswordSetup,
                    alreadyApproved = true,
                });
            }
            if (status is not ("pending" or "reviewed"))
                return Results.Conflict(new { error = "Application is not available for approval." });

            var companyName = dict["company_name"]?.ToString() ?? "Unknown";
            var companyWebsite = dict["company_website"]?.ToString() ?? "";
            var contactEmail = request.InvitationEmail?.Trim()
                ?? dict["contact_email"]?.ToString()?.Trim();
            var partnershipTier = NormalizeSponsorTier(request.Tier);
            var message = dict["message"]?.ToString();

            if (string.IsNullOrWhiteSpace(contactEmail)
                || contactEmail.Length > 254
                || !System.Net.Mail.MailAddress.TryCreate(contactEmail, out _))
                return Results.BadRequest(new { error = "Application has no contact email." });

            var sponsorId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO sponsors (name, website_url, tier, description, is_active, placement, priority, accent_color)
                VALUES (@name, @website, @tier, @description, false, ARRAY['banner'], 0, '#f43f5e')
                RETURNING id
                """,
                new { name = companyName, website = companyWebsite, tier = partnershipTier, description = message },
                transaction);

            await conn.ExecuteAsync(
                """
                UPDATE partner_applications
                SET status = 'approved',
                    invitation_email = @contactEmail,
                    approved_sponsor_id = @sponsorId,
                    approved_by = @approvedBy,
                    approved_at = NOW(),
                    updated_at = NOW()
                WHERE id = @id
                """,
                new { id, contactEmail = contactEmail.ToLowerInvariant(), sponsorId, approvedBy = userCtx.UserIdGuid },
                transaction);
            transaction.Commit();

            var invitation = await invitations.CreateInvitationAsync(
                sponsorId,
                contactEmail,
                "owner",
                userCtx.UserIdGuid,
                ct);
            if (invitation is null)
                return Results.Json(new { error = "Unable to create sponsor invitation." }, statusCode: 500);

            if (!invitation.WasDelivered)
                return Results.Json(new
                {
                    error = "Partner approved, but invitation delivery failed. Resend the invitation.",
                    sponsorId,
                    invitation.InvitationId,
                }, statusCode: 502);

            return Results.Ok(new
            {
                success = true,
                sponsorId,
                companyName,
                contactEmail,
                invitation.InvitationId,
                invitation.RequiresPasswordSetup,
            });
        }).RequireAuthorization(Permissions.SponsorsApproveApplication);

        // ══════════════════════════════════════════════════════════════════════
        // TEAM MANAGEMENT (super_admin only)
        // ══════════════════════════════════════════════════════════════════════

        // ── GET /api/admin/teams — paginated list with search + stats ────────
        app.MapGet("/api/admin/teams", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct,
            string? search = null,
            string? game = null,
            string? team_kind = null,
            int limit = 20,
            int offset = 0) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.TeamsView)) return Results.Forbid();

            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            if (string.Equals(team_kind, "solo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(team_kind, "mock", StringComparison.OrdinalIgnoreCase))
            {
                conditions.Add($"COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = @teamKindFilter");
            }
            else
            {
                conditions.Add(TeamKindSql.RealTeamWhere);
            }

            if (!string.IsNullOrWhiteSpace(search))
                conditions.Add("(t.name ILIKE @search OR t.tag ILIKE @search)");
            if (!string.IsNullOrWhiteSpace(game))
                conditions.Add("t.game ILIKE @game");

            var where = "WHERE " + string.Join(" AND ", conditions);
            var searchParam = search is not null ? $"%{search}%" : null;
            var gameParam = game is not null ? $"%{game}%" : null;
            var teamKindFilter = string.Equals(team_kind, "solo", StringComparison.OrdinalIgnoreCase)
                ? "solo"
                : string.Equals(team_kind, "mock", StringComparison.OrdinalIgnoreCase)
                    ? "mock"
                    : null;

            var total = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM teams t {where}",
                new { search = searchParam, game = gameParam, teamKindFilter });

            var teams = await conn.QueryAsync<dynamic>(
                $"""
                SELECT t.id, t.name, t.tag, t.game, t.logo_url, t.owner_id, t.is_active,
                       t.country_code, t.created_at,
                       COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) AS team_kind,
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
                new { search = searchParam, game = gameParam, teamKindFilter, limit, offset });

            // Aggregate stats
            var stats = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    COUNT(*) FILTER (WHERE COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'team') AS total_teams,
                    COUNT(*) FILTER (WHERE COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'team' AND t.is_active = TRUE) AS active_teams,
                    COUNT(*) FILTER (WHERE COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'solo') AS solo_adapters,
                    COUNT(*) FILTER (WHERE COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'mock') AS mock_teams,
                    COUNT(*) FILTER (
                        WHERE COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'mock'
                          AND NOT EXISTS (
                              SELECT 1 FROM tournament_participants tp
                              WHERE tp.team_id = t.id AND COALESCE(tp.is_mock, false) = true
                          )
                    ) AS orphan_mock_teams,
                    ROUND(AVG(mc) FILTER (WHERE COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) = 'team')::numeric, 1) AS avg_members
                FROM teams t
                LEFT JOIN LATERAL (
                    SELECT COUNT(*) AS mc FROM team_members tm WHERE tm.team_id = t.id AND tm.is_active = TRUE
                ) m ON TRUE
                """);

            return Results.Ok(new { teams, total, stats });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/teams/{id} — full detail ─────────────────────────
        app.MapGet("/api/admin/teams/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.TeamsView)) return Results.Forbid();

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
                SELECT tp.tournament_id, tp.status, tp.registration_date AS registered_at,
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
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.TeamsDisband)) return Results.Forbid();

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
                await conn.ExecuteAsync("DELETE FROM team_members WHERE team_id = @id", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM tournament_participants WHERE team_id = @id", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM teams WHERE id = @id", new { id }, tx);
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }

            return Results.Ok(new { success = true, disbanded = (string)team.name });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/teams/{id}/members/{userId} — remove member ───
        app.MapDelete("/api/admin/teams/{id}/members/{userId}", async (
            Guid id,
            Guid userId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.TeamsRemoveMember)) return Results.Forbid();

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
            Guid id,
            [FromBody] AdminTransferCaptainReq req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.TeamsTransferCaptain)) return Results.Forbid();

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
            Guid id,
            [FromBody] AdminEditTeamReq req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.TeamsEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, name, tag, game, description, logo_url FROM teams WHERE id = @id",
                new { id });
            if (existing is null) return Results.NotFound();

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE teams SET
                    name        = COALESCE(@name, name),
                    tag         = COALESCE(@tag, tag),
                    description = COALESCE(@description, description),
                    game        = COALESCE(@game, game),
                    logo_url    = CASE WHEN @removeLogo THEN NULL ELSE COALESCE(@logoUrl, logo_url) END,
                    updated_at  = NOW()
                WHERE id = @id
                RETURNING id, name, tag, game, description, logo_url
                """,
                new
                {
                    id,
                    name = req.Name,
                    tag = req.Tag,
                    description = req.Description,
                    game = req.Game,
                    logoUrl = req.LogoUrl,
                    removeLogo = req.RemoveLogo,
                });

            if (updated is null) return Results.NotFound();

            var changes = new Dictionary<string, object?>();
            if (req.Name is not null && (string?)existing.name != (string?)updated.name)
                changes["name"] = new { old = (string?)existing.name, @new = (string?)updated.name };
            if (req.Tag is not null && (string?)existing.tag != (string?)updated.tag)
                changes["tag"] = new { old = (string?)existing.tag, @new = (string?)updated.tag };
            if (req.Game is not null && (string?)existing.game != (string?)updated.game)
                changes["game"] = new { old = (string?)existing.game, @new = (string?)updated.game };
            if (req.Description is not null && (string?)existing.description != (string?)updated.description)
                changes["description"] = new { old = (string?)existing.description, @new = (string?)updated.description };
            if (req.RemoveLogo || req.LogoUrl is not null)
            {
                var previousLogo = (string?)existing.logo_url;
                var nextLogo = (string?)updated.logo_url;
                if (previousLogo != nextLogo)
                    changes["logo_url"] = new { old = previousLogo, @new = nextLogo };
            }

            if (changes.Count > 0)
            {
                await audit.LogAsync(
                    userCtx.UserIdGuid,
                    userCtx.Email,
                    ActionType.Update,
                    TargetType.Team,
                    id,
                    (string)updated.name,
                    new { changes },
                    ct: ct);
            }

            return Results.Ok(updated);
        }).RequireAuthorization("Admin");

        // ══════════════════════════════════════════════════════════════════════
        // CSV EXPORT ENDPOINTS
        // ══════════════════════════════════════════════════════════════════════

        // ── GET /api/admin/export/users ──────────────────────────────────────
        app.MapGet("/api/admin/export/users", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] string? search = null,
            [FromQuery] string? status = null,
            [FromQuery] string? role = null,
            [FromQuery] string? country = null,
            [FromQuery] string? joined_from = null,
            [FromQuery] string? joined_to = null,
            [FromQuery] string? has_team = null,
            [FromQuery] string? sort_by = null,
            [FromQuery] string? sort_dir = null,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.UsersView)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            var parameters = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("(p.username ILIKE @search ESCAPE '\\' OR p.email ILIKE @search ESCAPE '\\' OR p.full_name ILIKE @search ESCAPE '\\')");
                parameters.Add("search", EscapeLike(search));
            }

            if (status == "suspended")
                conditions.Add("p.is_suspended = TRUE");
            else if (status == "active")
                conditions.Add("(p.is_suspended IS NULL OR p.is_suspended = FALSE)");

            if (!string.IsNullOrWhiteSpace(role))
            {
                if (role == "admin")
                    conditions.Add("EXISTS (SELECT 1 FROM admin_user_roles aur WHERE aur.user_id = p.id)");
                else if (role == "casual")
                    conditions.Add("NOT EXISTS (SELECT 1 FROM user_roles ur2 WHERE ur2.user_id = p.id AND ur2.is_active = TRUE) AND NOT EXISTS (SELECT 1 FROM admin_user_roles aur2 WHERE aur2.user_id = p.id)");
                else
                {
                    conditions.Add("EXISTS (SELECT 1 FROM user_roles ur2 WHERE ur2.user_id = p.id AND ur2.role = @role AND ur2.is_active = TRUE)");
                    parameters.Add("role", role);
                }
            }

            if (!string.IsNullOrWhiteSpace(country))
            {
                conditions.Add("p.country_code = @country");
                parameters.Add("country", country);
            }

            if (!string.IsNullOrWhiteSpace(joined_from) && DateTimeOffset.TryParse(joined_from, out var jf))
            {
                conditions.Add("p.created_at >= @joinedFrom");
                parameters.Add("joinedFrom", jf);
            }
            if (!string.IsNullOrWhiteSpace(joined_to) && DateTimeOffset.TryParse(joined_to, out var jt))
            {
                conditions.Add("p.created_at < @joinedTo");
                parameters.Add("joinedTo", jt.AddDays(1));
            }

            if (has_team == "true")
                conditions.Add("EXISTS (SELECT 1 FROM team_members tm WHERE tm.user_id = p.id)");
            else if (has_team == "false")
                conditions.Add("NOT EXISTS (SELECT 1 FROM team_members tm WHERE tm.user_id = p.id)");

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var allowedSorts = new HashSet<string> { "created_at", "username", "updated_at" };
            var sortColumn = allowedSorts.Contains(sort_by ?? "") ? sort_by! : "created_at";
            var sortDirection = sort_dir?.ToLower() == "asc" ? "ASC" : "DESC";

            var sql = $"""
                SELECT p.id, p.full_name, p.username, p.email, p.country_code, p.date_of_birth, p.is_suspended, p.created_at,
                       COALESCE(array_agg(DISTINCT ur.role) FILTER (WHERE ur.role IS NOT NULL), ARRAY[]::text[]) AS roles
                FROM profiles p
                LEFT JOIN user_roles ur ON ur.user_id = p.id AND ur.is_active = TRUE
                {where}
                GROUP BY p.id
                ORDER BY p.{sortColumn} {sortDirection}
                LIMIT 10000
                """;

            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, parameters, cancellationToken: ct));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ID,Full Name,Username,Email,Country,Date of Birth,Suspended,Roles,Joined");
            foreach (var row in rows)
            {
                var dict = (IDictionary<string, object?>)row;
                var rolesVal = dict["roles"];
                var rolesStr = rolesVal is string[] arr ? string.Join("; ", arr)
                             : rolesVal?.ToString() ?? "";
                sb.AppendLine(string.Join(",",
                    CsvEscape(dict["id"]),
                    CsvEscape(dict["full_name"]),
                    CsvEscape(dict["username"]),
                    CsvEscape(dict["email"]),
                    CsvEscape(dict["country_code"]),
                    CsvEscape(dict["date_of_birth"]),
                    CsvEscape(dict["is_suspended"] is true ? "Yes" : "No"),
                    CsvEscape(rolesStr),
                    CsvEscape(dict["created_at"])));
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return Results.File(bytes, "text/csv", $"users_export_{DateTime.UtcNow:yyyy-MM-dd}.csv");
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/export/tournaments ────────────────────────────────
        app.MapGet("/api/admin/export/tournaments", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] string? search = null,
            [FromQuery] string? status = null,
            [FromQuery] string? game = null,
            [FromQuery] string? format = null,
            [FromQuery] decimal? prize_min = null,
            [FromQuery] decimal? prize_max = null,
            [FromQuery] string? date_from = null,
            [FromQuery] string? date_to = null,
            [FromQuery] string? sort_by = null,
            [FromQuery] string? sort_dir = null,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.TournamentsView)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            var parameters = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(status))
            {
                conditions.Add("t.status::text = @status");
                parameters.Add("status", status);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("(t.name ILIKE @search ESCAPE '\\' OR p.username ILIKE @search ESCAPE '\\')");
                parameters.Add("search", EscapeLike(search));
            }
            if (!string.IsNullOrWhiteSpace(game))
            {
                conditions.Add("t.game ILIKE @game ESCAPE '\\'");
                parameters.Add("game", EscapeLike(game));
            }
            if (!string.IsNullOrWhiteSpace(format))
            {
                conditions.Add("t.format = @format");
                parameters.Add("format", format);
            }
            if (prize_min.HasValue)
            {
                conditions.Add("COALESCE(t.prize_pool, 0) >= @prizeMin");
                parameters.Add("prizeMin", prize_min.Value);
            }
            if (prize_max.HasValue)
            {
                conditions.Add("COALESCE(t.prize_pool, 0) <= @prizeMax");
                parameters.Add("prizeMax", prize_max.Value);
            }

            if (!string.IsNullOrWhiteSpace(date_from) && DateTimeOffset.TryParse(date_from, out var df))
            {
                conditions.Add("t.created_at >= @dateFrom");
                parameters.Add("dateFrom", df);
            }
            if (!string.IsNullOrWhiteSpace(date_to) && DateTimeOffset.TryParse(date_to, out var dt2))
            {
                conditions.Add("t.created_at < @dateTo");
                parameters.Add("dateTo", dt2.AddDays(1));
            }

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var allowedSorts = new HashSet<string> { "created_at", "start_date", "prize_pool", "name" };
            var sortColumn = allowedSorts.Contains(sort_by ?? "") ? sort_by! : "created_at";
            var sortDirection = sort_dir?.ToLower() == "asc" ? "ASC" : "DESC";

            var sql = $"""
                SELECT t.id, t.name, t.game, t.status, t.format, t.prize_pool, t.max_teams,
                       t.is_featured, t.start_date, t.created_at
                FROM tournaments t
                LEFT JOIN profiles p ON p.id = t.organizer_id
                {where}
                ORDER BY t.{sortColumn} {sortDirection}
                LIMIT 10000
                """;

            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, parameters, cancellationToken: ct));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ID,Name,Game,Status,Format,Prize Pool,Max Teams,Featured,Start Date,Created");
            foreach (var row in rows)
            {
                var dict = (IDictionary<string, object?>)row;
                sb.AppendLine(string.Join(",",
                    CsvEscape(dict["id"]),
                    CsvEscape(dict["name"]),
                    CsvEscape(dict["game"]),
                    CsvEscape(dict["status"]),
                    CsvEscape(dict["format"]),
                    CsvEscape(dict["prize_pool"]),
                    CsvEscape(dict["max_teams"]),
                    CsvEscape(dict["is_featured"] is true ? "Yes" : "No"),
                    CsvEscape(dict["start_date"]),
                    CsvEscape(dict["created_at"])));
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return Results.File(bytes, "text/csv", $"tournaments_export_{DateTime.UtcNow:yyyy-MM-dd}.csv");
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/export/audit-logs ─────────────────────────────────
        app.MapGet("/api/admin/export/audit-logs", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] string? search = null,
            [FromQuery] string? target_type = null,
            [FromQuery] string? from = null,
            [FromQuery] string? to = null,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.SystemAudit)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            var parameters = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("(p.username ILIKE @search ESCAPE '\\' OR sal.action ILIKE @search ESCAPE '\\' OR sal.target_type ILIKE @search ESCAPE '\\')");
                parameters.Add("search", EscapeLike(search));
            }
            if (!string.IsNullOrWhiteSpace(target_type))
            {
                conditions.Add("sal.target_type = @target_type");
                parameters.Add("target_type", target_type);
            }
            if (!string.IsNullOrWhiteSpace(from) && DateTimeOffset.TryParse(from, out var fd))
            {
                conditions.Add("sal.created_at >= @fromDate");
                parameters.Add("fromDate", fd);
            }
            if (!string.IsNullOrWhiteSpace(to) && DateTimeOffset.TryParse(to, out var td))
            {
                conditions.Add("sal.created_at <= @toDate");
                parameters.Add("toDate", td);
            }

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var sql = $"""
                SELECT sal.id, sal.actor_id, p.username AS actor_name, sal.action, sal.target_type,
                       sal.target_id, sal.details, sal.created_at
                FROM staff_audit_log sal
                LEFT JOIN profiles p ON p.id = sal.actor_id
                {where}
                ORDER BY sal.created_at DESC
                LIMIT 10000
                """;

            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, parameters, cancellationToken: ct));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ID,Admin,Action,Target Type,Target ID,Details,Date");
            foreach (var row in rows)
            {
                var dict = (IDictionary<string, object?>)row;
                var detailsRaw = dict["details"];
                var detailsStr = detailsRaw is string s ? s : detailsRaw?.ToString() ?? "";
                sb.AppendLine(string.Join(",",
                    CsvEscape(dict["id"]),
                    CsvEscape(dict["actor_name"]),
                    CsvEscape(dict["action"]),
                    CsvEscape(dict["target_type"]),
                    CsvEscape(dict["target_id"]),
                    CsvEscape(detailsStr),
                    CsvEscape(dict["created_at"])));
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return Results.File(bytes, "text/csv", $"audit_logs_export_{DateTime.UtcNow:yyyy-MM-dd}.csv");
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/export/disputes ───────────────────────────────────
        app.MapGet("/api/admin/export/disputes", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] string? status = null,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.DisputesView)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var parameters = new DynamicParameters();
            var statusFilter = "";
            if (!string.IsNullOrWhiteSpace(status))
            {
                statusFilter = "WHERE td.status = @status";
                parameters.Add("status", status);
            }

            var sql = $"""
                SELECT td.id, td.reference_number, td.title, td.dispute_reason, td.status,
                       td.resolution_notes, t.name AS tournament_name,
                       p.full_name AS raised_by_name, td.created_at, td.updated_at
                FROM tournament_disputes td
                LEFT JOIN tournaments t ON t.id = td.tournament_id
                LEFT JOIN profiles p ON p.id = td.raised_by_user_id
                {statusFilter}
                ORDER BY td.created_at DESC
                LIMIT 10000
                """;

            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, parameters, cancellationToken: ct));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Reference,Title,Reason,Status,Tournament,Raised By,Resolution,Created,Updated");
            foreach (var row in rows)
            {
                var dict = (IDictionary<string, object?>)row;
                sb.AppendLine(string.Join(",",
                    CsvEscape(dict["reference_number"]),
                    CsvEscape(dict["title"]),
                    CsvEscape(dict["dispute_reason"]),
                    CsvEscape(dict["status"]),
                    CsvEscape(dict["tournament_name"]),
                    CsvEscape(dict["raised_by_name"]),
                    CsvEscape(dict["resolution_notes"]),
                    CsvEscape(dict["created_at"]),
                    CsvEscape(dict["updated_at"])));
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return Results.File(bytes, "text/csv", $"disputes_export_{DateTime.UtcNow:yyyy-MM-dd}.csv");
        }).RequireAuthorization("Admin");

        // ══════════════════════════════════════════════════════════════════════════
        // ── CONTENT MODERATION QUEUE ─────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════════

        // ── GET /api/admin/moderation-queue ──────────────────────────────────────
        app.MapGet("/api/admin/moderation-queue", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] string? status = null,
            [FromQuery(Name = "content_type")] string? contentType = null,
            [FromQuery] int page = 1,
            [FromQuery] int limit = 20,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.ContentModerate) && !userCtx.AdminRoles.Any())
                return Results.Forbid();

            if (page < 1) page = 1;
            if (limit < 1) limit = 20;
            if (limit > 100) limit = 100;
            var offset = (page - 1) * limit;

            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(status))
                filters.Add("mq.status = @status");
            if (!string.IsNullOrWhiteSpace(contentType))
                filters.Add("mq.content_type = @contentType");

            var whereClause = filters.Count > 0
                ? "WHERE " + string.Join(" AND ", filters)
                : "";

            using var conn = db.CreateConnection();

            var countSql = $"SELECT COUNT(*) FROM moderation_queue mq {whereClause}";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                countSql, new { status, contentType }, cancellationToken: ct));

            var dataSql = $"""
                SELECT
                    mq.id,
                    mq.content_type,
                    mq.content_id,
                    mq.field_name,
                    mq.content_text,
                    mq.content_url,
                    mq.reported_by,
                    mq.reported_reason,
                    mq.status,
                    mq.reviewed_by,
                    mq.reviewed_at,
                    mq.review_notes,
                    mq.auto_flagged,
                    mq.created_at,
                    rp.username   AS reporter_username,
                    rp.full_name  AS reporter_full_name,
                    rp.avatar_url AS reporter_avatar_url
                FROM moderation_queue mq
                LEFT JOIN profiles rp ON rp.id = mq.reported_by
                {whereClause}
                ORDER BY mq.created_at DESC
                LIMIT @limit OFFSET @offset
                """;

            var items = await conn.QueryAsync<dynamic>(new CommandDefinition(
                dataSql, new { status, contentType, limit, offset }, cancellationToken: ct));

            return Results.Ok(new { items, total, page, limit });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/moderation-queue/stats ────────────────────────────────
        app.MapGet("/api/admin/moderation-queue/stats", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.ContentModerate) && !userCtx.AdminRoles.Any())
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var row = await conn.QuerySingleAsync<dynamic>(new CommandDefinition("""
                SELECT
                    COUNT(*) FILTER (WHERE status = 'pending')  AS pending,
                    COUNT(*) FILTER (WHERE status = 'approved') AS approved,
                    COUNT(*) FILTER (WHERE status = 'rejected') AS rejected,
                    COUNT(*)                                    AS total
                FROM moderation_queue
                """, cancellationToken: ct));

            return Results.Ok(new
            {
                pending = (long)row.pending,
                approved = (long)row.approved,
                rejected = (long)row.rejected,
                total = (long)row.total
            });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/moderation-queue/{id}/review ─────────────────────────
        app.MapPost("/api/admin/moderation-queue/{id}/review", async (
            Guid id,
            [FromBody] ModerationReviewRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.ContentModerate))
                return Results.Forbid();

            var action = req.Action?.ToLowerInvariant();
            if (action is not "approve" and not "reject")
                return Results.BadRequest(new { error = "Action must be 'approve' or 'reject'" });

            using var conn = db.CreateConnection();

            // Fetch the queue item
            var item = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT id, content_type, content_id, field_name, status FROM moderation_queue WHERE id = @id",
                new { id }, cancellationToken: ct));

            if (item is null)
                return Results.NotFound(new { error = "Moderation item not found" });

            if ((string)item.status != "pending")
                return Results.Conflict(new { error = "Item has already been reviewed" });

            var newStatus = action == "approve" ? "approved" : "rejected";

            // Update the queue item — atomic status check prevents TOCTOU race
            var rowsAffected = await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE moderation_queue
                SET status       = @newStatus,
                    reviewed_by  = @reviewedBy,
                    reviewed_at  = NOW(),
                    review_notes = @notes
                WHERE id = @id AND status = 'pending'
                """, new
            {
                id,
                newStatus,
                reviewedBy = userCtx.UserIdGuid,
                notes = req.Notes ?? ""
            }, cancellationToken: ct));

            if (rowsAffected == 0)
                return Results.Conflict(new { error = "Item has already been reviewed by another moderator" });

            // If rejected, take enforcement action based on content type
            if (newStatus == "rejected")
            {
                var contentType = (string)item.content_type;
                var contentId = (Guid)item.content_id;
                var fieldName = (string)item.field_name;

                switch (contentType)
                {
                    case "tournament":
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE tournaments SET status = 'cancelled' WHERE id = @contentId",
                            new { contentId }, cancellationToken: ct));
                        break;

                    case "team":
                        if (fieldName == "name")
                            await conn.ExecuteAsync(new CommandDefinition(
                                "UPDATE teams SET name = '[Moderated]' WHERE id = @contentId",
                                new { contentId }, cancellationToken: ct));
                        else if (fieldName == "description")
                            await conn.ExecuteAsync(new CommandDefinition(
                                "UPDATE teams SET description = '' WHERE id = @contentId",
                                new { contentId }, cancellationToken: ct));
                        break;

                    case "profile":
                        if (fieldName == "bio")
                            await conn.ExecuteAsync(new CommandDefinition(
                                "UPDATE profiles SET bio = '' WHERE id = @contentId",
                                new { contentId }, cancellationToken: ct));
                        else if (fieldName == "username")
                            await conn.ExecuteAsync(new CommandDefinition(
                                "UPDATE profiles SET username = '[Moderated]' WHERE id = @contentId",
                                new { contentId }, cancellationToken: ct));
                        break;

                    case "match_evidence":
                        // For match evidence, clear the URL/content
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE match_results SET evidence_url = NULL WHERE id = @contentId",
                            new { contentId }, cancellationToken: ct));
                        break;
                }
            }

            // Audit log
            await audit.LogAsync(
                userCtx.UserIdGuid,
                userCtx.Email,
                newStatus == "approved" ? ActionType.Approve : ActionType.Reject,
                TargetType.System,
                id,
                $"moderation:{item.content_type}/{item.content_id}",
                new { action = newStatus, notes = req.Notes ?? "", content_type = (string)item.content_type, field_name = (string)item.field_name },
                ct: ct);

            return Results.Ok(new { success = true, status = newStatus });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/moderation-queue/{id} ──────────────────────────────
        app.MapDelete("/api/admin/moderation-queue/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.ContentModerate))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var item = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT id, content_type, content_id FROM moderation_queue WHERE id = @id",
                new { id }, cancellationToken: ct));

            if (item is null)
                return Results.NotFound(new { error = "Moderation item not found" });

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM moderation_queue WHERE id = @id",
                new { id }, cancellationToken: ct));

            await audit.LogAsync(
                userCtx.UserIdGuid,
                userCtx.Email,
                ActionType.Delete,
                TargetType.System,
                id,
                $"moderation:{item.content_type}/{item.content_id}",
                new { dismissed = true, content_type = (string)item.content_type },
                ct: ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/report-content ─────────────────────────────────────────────
        // Public-facing endpoint: any authenticated user can report content
        app.MapPost("/api/report-content", async (
            [FromBody] ReportContentRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AdminAlertService alerts,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Validate content type
            var allowedTypes = new[] { "tournament", "team", "profile", "match_evidence" };
            var contentType = req.ContentType?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(contentType) || !allowedTypes.Contains(contentType))
                return Results.BadRequest(new { error = "Invalid content_type. Must be one of: tournament, team, profile, match_evidence" });

            if (req.ContentId == Guid.Empty)
                return Results.BadRequest(new { error = "content_id is required" });

            // Validate field_name against allowed values per content type
            var fieldName = req.FieldName ?? "";
            var validFields = contentType switch
            {
                "tournament" => new[] { "name", "description" },
                "team" => new[] { "name", "description" },
                "profile" => new[] { "bio", "username" },
                "match_evidence" => new[] { "" },
                _ => Array.Empty<string>()
            };
            if (!validFields.Contains(fieldName))
                return Results.BadRequest(new { error = $"Invalid field_name '{fieldName}' for content type '{contentType}'" });

            using var conn = db.CreateConnection();

            // Prevent duplicate reports (same user + content_id + field_name within 24h)
            var duplicate = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS (
                    SELECT 1 FROM moderation_queue
                    WHERE reported_by = @reportedBy
                      AND content_id  = @contentId
                      AND field_name  = @fieldName
                      AND created_at  > NOW() - INTERVAL '24 hours'
                )
                """, new { reportedBy = userCtx.UserIdGuid, contentId = req.ContentId, fieldName },
                cancellationToken: ct));

            if (duplicate)
                return Results.Conflict(new { error = "You have already reported this content within the last 24 hours" });

            // Fetch the actual content text for context
            string? contentText = null;
            switch (contentType)
            {
                case "tournament":
                    contentText = fieldName switch
                    {
                        "name" => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                            "SELECT name FROM tournaments WHERE id = @id", new { id = req.ContentId }, cancellationToken: ct)),
                        "description" => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                            "SELECT description FROM tournaments WHERE id = @id", new { id = req.ContentId }, cancellationToken: ct)),
                        _ => null
                    };
                    break;
                case "team":
                    contentText = fieldName switch
                    {
                        "name" => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                            "SELECT name FROM teams WHERE id = @id", new { id = req.ContentId }, cancellationToken: ct)),
                        "description" => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                            "SELECT description FROM teams WHERE id = @id", new { id = req.ContentId }, cancellationToken: ct)),
                        _ => null
                    };
                    break;
                case "profile":
                    contentText = fieldName switch
                    {
                        "bio" => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                            "SELECT bio FROM profiles WHERE id = @id", new { id = req.ContentId }, cancellationToken: ct)),
                        "username" => await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                            "SELECT username FROM profiles WHERE id = @id", new { id = req.ContentId }, cancellationToken: ct)),
                        _ => null
                    };
                    break;
            }

            var reportId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO moderation_queue (content_type, content_id, field_name, content_text, reported_by, reported_reason)
                VALUES (@contentType, @contentId, @fieldName, @contentText, @reportedBy, @reason)
                RETURNING id
                """, new
            {
                contentType,
                contentId = req.ContentId,
                fieldName,
                contentText,
                reportedBy = userCtx.UserIdGuid,
                reason = req.Reason ?? ""
            }, cancellationToken: ct));

            // Generate admin alert
            await alerts.CreateAsync(
                "content_report",
                AlertSeverity.Warning,
                $"Content reported: {contentType}",
                $"User reported {contentType} ({req.ContentId}) field '{fieldName}'. Reason: {req.Reason ?? "No reason provided"}",
                new { report_id = reportId, content_type = contentType, content_id = req.ContentId, field_name = fieldName },
                ct);

            return Results.Ok(new { id = reportId, success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/admin/entity-history/{targetType}/{targetId} ────────────────
        app.MapGet("/api/admin/entity-history/{targetType}/{targetId}", async (
            string targetType,
            Guid targetId,
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] int page = 1,
            [FromQuery] int limit = 20,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Any()) return Results.Forbid();

            var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "User", "Tournament", "Team", "Venue", "Dispute", "Match", "Sponsor", "Payment", "System", "Organization" };
            if (!allowedTypes.Contains(targetType))
                return Results.BadRequest(new { error = "Invalid target type" });

            // AuditService stores target_type as lowercase
            var normalizedType = targetType.ToLowerInvariant();

            using var conn = db.CreateConnection();

            var clampedLimit = Math.Clamp(limit, 1, 50);
            var offset = Math.Max(0, (page - 1) * clampedLimit);

            var countSql = """
                SELECT COUNT(*) FROM (
                    SELECT id FROM audit_logs
                    WHERE lower(target_type) = @targetType AND target_id::text = @targetIdText
                    UNION ALL
                    SELECT id FROM staff_audit_log
                    WHERE lower(target_type) = @targetType AND target_id::text = @targetIdText
                ) combined
                """;
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                countSql, new { targetType = normalizedType, targetIdText = targetId.ToString() }, cancellationToken: ct));

            var sql = """
                SELECT id, admin_id, admin_name, action_type, target_type,
                       target_id, target_name, details, severity, created_at
                FROM (
                    SELECT id, admin_id, admin_name, action_type, target_type,
                           target_id::text AS target_id, target_name, details, severity, created_at
                    FROM audit_logs
                    WHERE lower(target_type) = @targetType AND target_id::text = @targetIdText
                    UNION ALL
                    SELECT sal.id, sal.actor_id AS admin_id, p.username AS admin_name,
                           sal.action AS action_type, sal.target_type,
                           sal.target_id::text AS target_id, NULL AS target_name, sal.details,
                           NULL AS severity, sal.created_at
                    FROM staff_audit_log sal
                    LEFT JOIN profiles p ON p.id = sal.actor_id
                    WHERE lower(sal.target_type) = @targetType AND sal.target_id::text = @targetIdText
                ) combined
                ORDER BY created_at DESC
                LIMIT @limit OFFSET @offset
                """;

            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
                sql, new { targetType = normalizedType, targetIdText = targetId.ToString(), limit = clampedLimit, offset }, cancellationToken: ct));
            DapperJsonbHelper.FixJsonb(rows);

            return Results.Ok(new { data = rows, total, page, limit = clampedLimit });
        }).RequireAuthorization("Admin");


        // ══════════════════════════════════════════════════════════════════════════
        // ── ADMIN ALERTS ──────────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════════

        // ── GET /api/admin/alerts ────────────────────────────────────────────────
        app.MapGet("/api/admin/alerts", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] string? status = null,
            [FromQuery] string? severity = null,
            [FromQuery] string? type = null,
            [FromQuery] int page = 1,
            [FromQuery] int limit = 20,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.AlertsView)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            var parameters = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(status))
            {
                conditions.Add("a.status = @status");
                parameters.Add("status", status);
            }
            if (!string.IsNullOrWhiteSpace(severity))
            {
                conditions.Add("a.severity = @severity");
                parameters.Add("severity", severity);
            }
            if (!string.IsNullOrWhiteSpace(type))
            {
                conditions.Add("a.type = @type");
                parameters.Add("type", type);
            }

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var offset = Math.Max(0, (page - 1) * limit);
            parameters.Add("limit", Math.Clamp(limit, 1, 100));
            parameters.Add("offset", offset);

            var countSql = $"SELECT COUNT(*) FROM admin_alerts a {where}";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, cancellationToken: ct));

            var sql = $"""
                SELECT a.id, a.type, a.severity, a.title, a.message, a.data,
                       a.status, a.acknowledged_by, a.acknowledged_at,
                       a.resolved_by, a.resolved_at, a.created_at,
                       p1.username AS acknowledged_by_name,
                       p2.username AS resolved_by_name
                FROM admin_alerts a
                LEFT JOIN profiles p1 ON p1.id = a.acknowledged_by
                LEFT JOIN profiles p2 ON p2.id = a.resolved_by
                {where}
                ORDER BY
                    CASE a.status WHEN 'active' THEN 0 WHEN 'acknowledged' THEN 1 ELSE 2 END,
                    CASE a.severity WHEN 'critical' THEN 0 WHEN 'warning' THEN 1 ELSE 2 END,
                    a.created_at DESC
                LIMIT @limit OFFSET @offset
                """;

            var alerts = await conn.QueryAsync<dynamic>(new CommandDefinition(sql, parameters, cancellationToken: ct));
            DapperJsonbHelper.FixJsonb(alerts);

            return Results.Ok(new { data = alerts, total, page, limit });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/alerts/summary ────────────────────────────────────────
        app.MapGet("/api/admin/alerts/summary", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.AlertsView)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var sql = """
                SELECT
                    COUNT(*) FILTER (WHERE status = 'active') AS active_count,
                    COUNT(*) FILTER (WHERE status = 'active' AND severity = 'critical') AS critical_count,
                    COUNT(*) FILTER (WHERE status = 'active' AND severity = 'warning') AS warning_count,
                    COUNT(*) FILTER (WHERE status = 'active' AND severity = 'info') AS info_count,
                    COUNT(*) FILTER (WHERE status = 'acknowledged') AS acknowledged_count
                FROM admin_alerts
                """;

            var result = await conn.QuerySingleAsync<dynamic>(new CommandDefinition(sql, cancellationToken: ct));
            return Results.Ok(result);
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/alerts/{id}/acknowledge ───────────────────────────────
        app.MapPut("/api/admin/alerts/{id}/acknowledge", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.AlertsAcknowledge)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var rows = await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE admin_alerts
                SET status = 'acknowledged', acknowledged_by = @userId, acknowledged_at = now()
                WHERE id = @id AND status = 'active'
                """, new { id, userId = userCtx.UserIdGuid }, cancellationToken: ct));

            return rows > 0 ? Results.Ok(new { success = true }) : Results.NotFound();
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/alerts/{id}/resolve ───────────────────────────────────
        app.MapPut("/api/admin/alerts/{id}/resolve", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.AlertsResolve)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var rows = await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE admin_alerts
                SET status = 'resolved', resolved_by = @userId, resolved_at = now()
                WHERE id = @id AND status IN ('active', 'acknowledged')
                """, new { id, userId = userCtx.UserIdGuid }, cancellationToken: ct));

            return rows > 0 ? Results.Ok(new { success = true }) : Results.NotFound();
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/alerts ───────────────────────────────────────────────
        // Create a manual admin alert (for system announcements, etc.)
        app.MapPost("/api/admin/alerts", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromBody] CreateAdminAlertRequest req,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Any()) return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required" });

            var dataJson = "{}";
            if (!string.IsNullOrWhiteSpace(req.Data))
            {
                try { System.Text.Json.JsonDocument.Parse(req.Data); dataJson = req.Data; }
                catch { return Results.BadRequest(new { error = "Data must be valid JSON" }); }
            }

            using var conn = db.CreateConnection();

            var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO admin_alerts (type, severity, title, message, data)
                VALUES (@type, @severity, @title, @message, @data::jsonb)
                RETURNING id
                """, new
            {
                type = req.Type ?? "system_event",
                severity = req.Severity ?? "info",
                title = req.Title,
                message = req.Message,
                data = dataJson
            }, cancellationToken: ct));

            return Results.Ok(new { id, success = true });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/alerts/bulk-acknowledge ───────────────────────────────
        app.MapPut("/api/admin/alerts/bulk-acknowledge", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromBody] BulkAlertActionRequest req,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.AlertsBulkAcknowledge)) return Results.Forbid();

            if (req.AlertIds is null || req.AlertIds.Length == 0)
                return Results.BadRequest(new { error = "No alert IDs provided" });

            using var conn = db.CreateConnection();

            var rows = await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE admin_alerts
                SET status = 'acknowledged', acknowledged_by = @userId, acknowledged_at = now()
                WHERE id = ANY(@ids) AND status = 'active'
                """, new { ids = req.AlertIds, userId = userCtx.UserIdGuid }, cancellationToken: ct));

            return Results.Ok(new { success = true, updated = rows });
        }).RequireAuthorization("Admin");

        // ══════════════════════════════════════════════════════════════════════════
        // ── ADMIN SESSION MANAGEMENT ─────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════════

        static bool HasPermission(UserContext userCtx, string permission) =>
            userCtx.IsSuperAdmin ||
            userCtx.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

        static bool HasSecurityPermission(UserContext userCtx, string permission) =>
            HasPermission(userCtx, permission);

        // ── GET /api/admin/sessions/active ───────────────────────────────────────
        // Lists admin users with last recorded login/revoke activity from audit_logs.
        // This is not a live Supabase JWT session registry.
        app.MapGet("/api/admin/sessions/active", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] int page = 1,
            [FromQuery] int limit = 20,
            [FromQuery] string? search = null,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!HasSecurityPermission(userCtx, Permissions.SecurityViewSessions)) return Results.Forbid();

            var clampedLimit = Math.Clamp(limit, 1, 100);
            var clampedPage = Math.Max(1, page);
            var offset = (clampedPage - 1) * clampedLimit;

            using var conn = db.CreateConnection();

            // Escape LIKE/ILIKE pattern metacharacters for safe search
            var searchPattern = string.IsNullOrWhiteSpace(search) ? null
                : $"%{search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";

            var countSql = """
                SELECT COUNT(DISTINCT aur.user_id)
                FROM admin_user_roles aur
                JOIN profiles p ON p.id = aur.user_id
                WHERE (@search IS NULL OR p.email ILIKE @searchPattern ESCAPE '\' OR p.username ILIKE @searchPattern ESCAPE '\')
                """;
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                countSql, new { search, searchPattern }, cancellationToken: ct));

            var sql = """
                SELECT p.id AS "userId", COALESCE(p.email, '') AS email, p.username,
                       p.full_name AS "fullName", p.avatar_url AS "avatarUrl",
                       p.created_at AS "createdAt",
                       (SELECT MAX(al.created_at) FROM audit_logs al
                        WHERE al.admin_id = p.id AND al.action_type = 'login') AS "lastSignInAt",
                       (SELECT MAX(al.created_at) FROM audit_logs al
                        WHERE al.admin_id = p.id
                          AND al.created_at > NOW() - INTERVAL '15 minutes') AS "lastActivityAt",
                       EXISTS (
                         SELECT 1 FROM audit_logs al
                         WHERE al.admin_id = p.id
                           AND al.created_at > NOW() - INTERVAL '15 minutes'
                       ) AS "hasRecentActivity",
                       ARRAY_AGG(ar.key) AS roles
                FROM admin_user_roles aur
                JOIN profiles p ON p.id = aur.user_id
                JOIN admin_roles ar ON ar.id = aur.role_id
                WHERE (@search IS NULL OR p.email ILIKE @searchPattern ESCAPE '\' OR p.username ILIKE @searchPattern ESCAPE '\')
                GROUP BY p.id, p.email, p.username, p.full_name, p.avatar_url, p.created_at
                ORDER BY "lastActivityAt" DESC NULLS LAST, "lastSignInAt" DESC NULLS LAST
                LIMIT @limit OFFSET @offset
                """;

            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
                sql, new { search, searchPattern, limit = clampedLimit, offset },
                cancellationToken: ct));

            // ARRAY_AGG returns a Postgres text array — Dapper may deserialize as string[] or raw "{a,b}" string
            var items = rows.Select(row =>
            {
                var dict = (IDictionary<string, object?>)row;
                var rolesValue = dict["roles"];
                var rolesArray = rolesValue is string[] arr ? arr
                    : rolesValue?.ToString()?.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries)
                    ?? Array.Empty<string>();
                dict["roles"] = rolesArray;
                return dict;
            }).ToList();

            return Results.Ok(new
            {
                items,
                total,
                page = clampedPage,
                limit = clampedLimit,
                source = "admin_users_with_roles",
                description = "Admin users with last recorded login and recent audit activity. Not live JWT sessions.",
            });
        }).RequireAuthorization(Permissions.SecurityViewSessions);

        // ── GET /api/admin/sessions/audit ────────────────────────────────────────
        // Returns recent admin login/logout/session-revoke activity from audit_logs.
        app.MapGet("/api/admin/sessions/audit", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] int page = 1,
            [FromQuery] int limit = 20,
            [FromQuery] Guid? userId = null,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!HasSecurityPermission(userCtx, Permissions.SecurityViewSessions)) return Results.Forbid();

            var clampedLimit = Math.Clamp(limit, 1, 100);
            var clampedPage = Math.Max(1, page);
            var offset = (clampedPage - 1) * clampedLimit;

            using var conn = db.CreateConnection();

            // Filter to login/logout/revoke actions where the actor or target is an admin
            var conditions = new List<string>
            {
                """
                (
                  al.action_type IN ('login', 'logout')
                  OR (al.action_type = 'logout' AND al.details->>'action' = 'session_revoke')
                )
                """,
                """
                (
                  EXISTS (SELECT 1 FROM admin_user_roles aur WHERE aur.user_id = al.admin_id)
                  OR EXISTS (SELECT 1 FROM admin_user_roles aur WHERE aur.user_id = al.target_id)
                )
                """
            };
            var parameters = new DynamicParameters();

            if (userId.HasValue)
            {
                conditions.Add("al.admin_id = @filterUserId");
                parameters.Add("filterUserId", userId.Value);
            }

            var where = "WHERE " + string.Join(" AND ", conditions);

            var countSql = $"SELECT COUNT(*) FROM audit_logs al {where}";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                countSql, parameters, cancellationToken: ct));

            parameters.Add("limit", clampedLimit);
            parameters.Add("offset", offset);

            var sql = $"""
                SELECT al.id,
                       al.action_type AS "actionType",
                       al.admin_id AS "actorId",
                       al.admin_name AS "actorUsername",
                       p.email AS "actorEmail",
                       al.details->>'ip' AS "ipAddress",
                       al.details->>'user_agent' AS "userAgent",
                       al.details,
                       al.created_at AS "createdAt"
                FROM audit_logs al
                LEFT JOIN profiles p ON p.id = al.admin_id
                {where}
                ORDER BY al.created_at DESC
                LIMIT @limit OFFSET @offset
                """;

            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
                sql, parameters, cancellationToken: ct));
            DapperJsonbHelper.FixJsonb(rows);

            return Results.Ok(new { items = rows, total, page = clampedPage, limit = clampedLimit });
        }).RequireAuthorization(Permissions.SecurityViewSessions);

        // ── POST /api/admin/sessions/{userId}/revoke ─────────────────────────────
        // Force logout a user by invalidating their Supabase Auth refresh tokens,
        // evicting their cached UserContext, and broadcasting ForceLogout via SignalR.
        app.MapPost("/api/admin/sessions/{userId}/revoke", async (
            Guid userId,
            [FromBody] RevokeSessionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            ISupabaseAdminClient supabase,
            AuditService audit,
            HybridCache cache,
            IHubContext<NotificationHub> notificationHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!HasSecurityPermission(userCtx, Permissions.SecurityRevokeSessions)) return Results.Forbid();

            // Cannot revoke own session
            if (userCtx.UserIdGuid == userId)
                return Results.BadRequest(new { error = "Cannot revoke your own session." });

            using var conn = db.CreateConnection();

            // Verify target user exists
            var targetName = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT COALESCE(username, full_name, id::text) FROM profiles WHERE id = @userId",
                new { userId }, cancellationToken: ct));

            if (targetName is null)
                return Results.NotFound(new { error = "User not found." });

            // Invalidate Supabase Auth sessions (ban/unban cycle)
            var partialFailure = false;
            try
            {
                await supabase.LogoutUserAsync(userId.ToString(), ct);
            }
            catch (Exception ex)
            {
                // Log but don't fail — cache eviction below still forces re-auth
                var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Esportra.Api.Endpoints.AdminEndpoints");
                logger.LogWarning(ex, "Supabase force-logout failed for {UserId}, proceeding with cache eviction", userId);
                partialFailure = true;
            }

            // Evict cached UserContext — forces re-authentication on next request
            try
            {
                await cache.RemoveAsync($"user-ctx:{userId}");
            }
            catch (Exception ex)
            {
                // Cache eviction failure — session will expire naturally via TTL
                var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Esportra.Api.Endpoints.AdminEndpoints");
                logger.LogWarning(ex, "Cache eviction failed for {UserId} — session will expire naturally", userId);
            }

            // Insert into revoked_sessions table for server-side session blacklisting
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO revoked_sessions (user_id, revoked_by, reason, expires_at)
                    VALUES (@userId, @adminId, @reason, NOW() + INTERVAL '2 hours')
                    ON CONFLICT DO NOTHING
                    """,
                    new { userId, adminId = userCtx.UserIdGuid, reason = req.Reason });

                // Evict the revocation cache so middleware picks up the new revocation
                await cache.RemoveAsync($"session-revoked:{userId}");
            }
            catch (Exception ex)
            {
                var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Esportra.Api.Endpoints.AdminEndpoints");
                logger.LogWarning(ex, "Failed to insert revoked_sessions record for {UserId}", userId);
            }

            // Broadcast ForceLogout via SignalR to immediately log out the user's browser
            try
            {
                await notificationHub.Clients
                    .Group(NotificationHub.UserGroup(userId.ToString()))
                    .SendAsync(NotificationHubEvents.ForceLogout, new { reason = req.Reason }, ct);
            }
            catch (Exception ex)
            {
                var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Esportra.Api.Endpoints.AdminEndpoints");
                logger.LogWarning(ex, "Failed to broadcast ForceLogout to {UserId}", userId);
            }

            // Audit the session revocation
            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Logout, TargetType.User,
                userId, targetName,
                new { reason = req.Reason, revokedBy = userCtx.Email, action = "session_revoke" },
                AuditSeverity.High, ct);

            return Results.Ok(new
            {
                success = true,
                partial = partialFailure,
                userId,
                username = targetName,
                message = partialFailure
                    ? "Session revoked with partial enforcement"
                    : "Session revoked successfully"
            });
        }).RequireAuthorization(Permissions.SecurityRevokeSessions);

        // ── GET /api/admin/sessions/online-count ─────────────────────────────────
        // Returns count of distinct users active in the last 15 minutes,
        // based on audit_logs entries.
        app.MapGet("/api/admin/sessions/online-count", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!HasSecurityPermission(userCtx, Permissions.SecurityViewSessions)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(DISTINCT al.admin_id)
                FROM audit_logs al
                WHERE al.created_at > NOW() - INTERVAL '15 minutes'
                  AND al.admin_id IS NOT NULL
                  AND EXISTS (
                    SELECT 1 FROM admin_user_roles aur WHERE aur.user_id = al.admin_id
                  )
                """, cancellationToken: ct));

            return Results.Ok(new { count, windowMinutes = 15, source = "recent_admin_audit_activity" });
        }).RequireAuthorization(Permissions.SecurityViewSessions);

        // ══════════════════════════════════════════════════════════════════════
        // IP ALLOWLIST MANAGEMENT (super_admin only)
        // ══════════════════════════════════════════════════════════════════════

        // ── GET /api/admin/ip-allowlist ────────────────────────────────────────
        app.MapGet("/api/admin/ip-allowlist", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            using var conn = db.CreateConnection();

            var entries = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                SELECT a.id, a.ip_address, a.label, a.created_by,
                       a.created_at, a.expires_at, a.is_active,
                       p.username AS created_by_username
                FROM admin_ip_allowlist a
                LEFT JOIN profiles p ON p.id = a.created_by
                ORDER BY a.created_at DESC
                """, cancellationToken: ct));

            return Results.Ok(entries.Select(e => new
            {
                id = (Guid)e.id,
                ipAddress = (string)e.ip_address,
                label = (string)e.label,
                createdBy = (Guid?)e.created_by,
                createdByUsername = (string?)e.created_by_username,
                createdAt = (DateTime)e.created_at,
                expiresAt = (DateTime?)e.expires_at,
                isActive = (bool)e.is_active
            }));
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/ip-allowlist ───────────────────────────────────────
        app.MapPost("/api/admin/ip-allowlist", async (
            [FromBody] AddIpAllowlistRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            // Validate IP format
            if (string.IsNullOrWhiteSpace(req.IpAddress) ||
                !System.Net.IPAddress.TryParse(req.IpAddress.Trim(), out _))
                return Results.BadRequest(new { error = "Invalid IP address format." });

            var ipAddress = req.IpAddress.Trim();
            var label = req.Label?.Trim() ?? "";

            DateTimeOffset? expiresAt = null;
            if (!string.IsNullOrWhiteSpace(req.ExpiresAt))
            {
                if (!DateTimeOffset.TryParse(req.ExpiresAt, out var parsed))
                    return Results.BadRequest(new { error = "Invalid expiresAt date format." });
                expiresAt = parsed;
            }

            using var conn = db.CreateConnection();

            try
            {
                var entry = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition("""
                    INSERT INTO admin_ip_allowlist (ip_address, label, created_by, expires_at)
                    VALUES (@ipAddress, @label, @createdBy, @expiresAt)
                    RETURNING id, ip_address, label, created_by, created_at, expires_at, is_active
                    """, new
                {
                    ipAddress,
                    label,
                    createdBy = userCtx.UserIdGuid,
                    expiresAt = expiresAt?.UtcDateTime
                }, cancellationToken: ct));

                await audit.LogAsync(
                    userCtx.UserIdGuid, userCtx.Email,
                    ActionType.Create, TargetType.System,
                    (Guid)entry!.id, "ip_allowlist",
                    new { ip_address = ipAddress, label },
                    ct: ct);

                return Results.Created($"/api/admin/ip-allowlist/{entry.id}", new
                {
                    id = (Guid)entry.id,
                    ipAddress = (string)entry.ip_address,
                    label = (string)entry.label,
                    createdBy = (Guid?)entry.created_by,
                    createdAt = (DateTime)entry.created_at,
                    expiresAt = (DateTime?)entry.expires_at,
                    isActive = (bool)entry.is_active
                });
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Conflict(new { error = "This IP address is already in the allowlist." });
            }
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/ip-allowlist/{id} ──────────────────────────────────
        app.MapPut("/api/admin/ip-allowlist/{id}", async (
            Guid id,
            [FromBody] UpdateIpAllowlistRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            using var conn = db.CreateConnection();

            // Fetch existing entry
            var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT id, ip_address, label, is_active, expires_at FROM admin_ip_allowlist WHERE id = @id",
                new { id }, cancellationToken: ct));

            if (existing is null)
                return Results.NotFound(new { error = "IP allowlist entry not found." });

            // Build dynamic SET clause for provided fields only
            var setClauses = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("id", id);

            if (req.Label is not null)
            {
                setClauses.Add("label = @label");
                parameters.Add("label", req.Label.Trim());
            }

            if (req.IsActive is not null)
            {
                setClauses.Add("is_active = @isActive");
                parameters.Add("isActive", req.IsActive.Value);
            }

            if (req.ExpiresAt is not null)
            {
                if (req.ExpiresAt == "")
                {
                    // Empty string clears the expiration
                    setClauses.Add("expires_at = NULL");
                }
                else if (DateTimeOffset.TryParse(req.ExpiresAt, out var parsed))
                {
                    setClauses.Add("expires_at = @expiresAt");
                    parameters.Add("expiresAt", parsed.UtcDateTime);
                }
                else
                {
                    return Results.BadRequest(new { error = "Invalid expiresAt date format." });
                }
            }

            if (setClauses.Count == 0)
                return Results.BadRequest(new { error = "No fields to update." });

            // Guard: prevent deactivating the last active IP while the allowlist is enabled.
            if (req.IsActive == false)
            {
                var enabledVal = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT value FROM system_settings WHERE key = 'security.ip_allowlist_enabled'", cancellationToken: ct));
                if (string.Equals(enabledVal, "true", StringComparison.OrdinalIgnoreCase))
                {
                    var remaining = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                        SELECT COUNT(*) FROM admin_ip_allowlist
                        WHERE id != @id AND is_active = TRUE
                          AND (expires_at IS NULL OR expires_at > NOW())
                        """, new { id }, cancellationToken: ct));
                    if (remaining == 0)
                        return Results.BadRequest(new { error = "Cannot remove/deactivate the last active IP while the allowlist is enabled. Disable the allowlist first." });
                }
            }

            var sql = $"UPDATE admin_ip_allowlist SET {string.Join(", ", setClauses)} WHERE id = @id " +
                      "RETURNING id, ip_address, label, created_by, created_at, expires_at, is_active";

            var updated = await conn.QueryFirstOrDefaultAsync<dynamic>(
                new CommandDefinition(sql, parameters, cancellationToken: ct));

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Update, TargetType.System,
                id, "ip_allowlist",
                new
                {
                    ip_address = (string)existing.ip_address,
                    changes = new { req.Label, req.IsActive, req.ExpiresAt }
                },
                ct: ct);

            return Results.Ok(new
            {
                id = (Guid)updated!.id,
                ipAddress = (string)updated.ip_address,
                label = (string)updated.label,
                createdBy = (Guid?)updated.created_by,
                createdAt = (DateTime)updated.created_at,
                expiresAt = (DateTime?)updated.expires_at,
                isActive = (bool)updated.is_active
            });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/ip-allowlist/{id} ───────────────────────────────
        app.MapDelete("/api/admin/ip-allowlist/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            using var conn = db.CreateConnection();

            var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT id, ip_address, label FROM admin_ip_allowlist WHERE id = @id",
                new { id }, cancellationToken: ct));

            if (existing is null)
                return Results.NotFound(new { error = "IP allowlist entry not found." });

            // Guard: prevent deleting the last active IP while the allowlist is enabled.
            var enabledVal = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT value FROM system_settings WHERE key = 'security.ip_allowlist_enabled'", cancellationToken: ct));
            if (string.Equals(enabledVal, "true", StringComparison.OrdinalIgnoreCase))
            {
                var remaining = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM admin_ip_allowlist
                    WHERE id != @id AND is_active = TRUE
                      AND (expires_at IS NULL OR expires_at > NOW())
                    """, new { id }, cancellationToken: ct));
                if (remaining == 0)
                    return Results.BadRequest(new { error = "Cannot remove/deactivate the last active IP while the allowlist is enabled. Disable the allowlist first." });
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM admin_ip_allowlist WHERE id = @id",
                new { id }, cancellationToken: ct));

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Delete, TargetType.System,
                id, "ip_allowlist",
                new { ip_address = (string)existing.ip_address, label = (string)existing.label },
                ct: ct);

            return Results.Ok(new { message = "IP allowlist entry deleted." });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/ip-allowlist/status ────────────────────────────────
        app.MapGet("/api/admin/ip-allowlist/status", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            using var conn = db.CreateConnection();

            var enabledValue = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT value FROM system_settings WHERE key = 'security.ip_allowlist_enabled'",
                cancellationToken: ct)) ?? "false";

            var counts = await conn.QueryFirstAsync<dynamic>(new CommandDefinition("""
                SELECT
                    COUNT(*)                                               AS total,
                    COUNT(*) FILTER (WHERE is_active = TRUE
                        AND (expires_at IS NULL OR expires_at > NOW()))     AS active
                FROM admin_ip_allowlist
                """, cancellationToken: ct));

            return Results.Ok(new
            {
                enabled = string.Equals(enabledValue, "true", StringComparison.OrdinalIgnoreCase),
                totalEntries = (long)counts.total,
                activeEntries = (long)counts.active
            });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/ip-allowlist/toggle ───────────────────────────────
        app.MapPost("/api/admin/ip-allowlist/toggle", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin) return Results.Forbid();

            using var conn = db.CreateConnection();
            // Wrap the read-check-update sequence in a transaction to prevent TOCTOU races
            using var txn = conn.BeginTransaction();

            var currentValue = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT value FROM system_settings WHERE key = 'security.ip_allowlist_enabled'",
                transaction: txn, cancellationToken: ct)) ?? "false";

            var currentlyEnabled = string.Equals(currentValue, "true", StringComparison.OrdinalIgnoreCase);
            var newEnabled = !currentlyEnabled;

            // Safety checks before enabling
            if (newEnabled)
            {
                var activeCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM admin_ip_allowlist
                    WHERE is_active = TRUE
                      AND (expires_at IS NULL OR expires_at > NOW())
                    """, transaction: txn, cancellationToken: ct));

                if (activeCount == 0)
                    return Results.BadRequest(new { error = "Cannot enable with no active IPs." });

                // Check that the caller's IP is in the allowlist
                var clientIp = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                    ?? ctx.Connection.RemoteIpAddress?.ToString();

                if (string.IsNullOrEmpty(clientIp))
                    return Results.BadRequest(new { error = "Cannot determine your IP address." });

                var callerInList = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM admin_ip_allowlist
                    WHERE ip_address = @clientIp
                      AND is_active = TRUE
                      AND (expires_at IS NULL OR expires_at > NOW())
                    """, new { clientIp }, transaction: txn, cancellationToken: ct));

                if (callerInList == 0)
                    return Results.BadRequest(new { error = "Your current IP is not in the allowlist." });
            }

            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE system_settings
                SET value = @newValue, updated_by = @updatedBy, updated_at = NOW()
                WHERE key = 'security.ip_allowlist_enabled'
                """, new
            {
                newValue = newEnabled ? "true" : "false",
                updatedBy = userCtx.UserIdGuid
            }, transaction: txn, cancellationToken: ct));

            txn.Commit();

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.SettingsUpdate, TargetType.System,
                userCtx.UserIdGuid, "ip_allowlist_toggle",
                new { enabled = newEnabled },
                ct: ct);

            return Results.Ok(new { enabled = newEnabled, message = newEnabled ? "IP allowlist enabled." : "IP allowlist disabled." });
        }).RequireAuthorization("Admin");

        // ══════════════════════════════════════════════════════════════════════════
        // ── SCHEDULED REPORTS (Phase 12) ─────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════════

        // ── GET /api/admin/report-schedules ──────────────────────────────────────
        app.MapGet("/api/admin/report-schedules", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.ReportsView)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                SELECT
                    rs.id,
                    rs.name,
                    rs.report_type    AS "reportType",
                    rs.frequency,
                    rs.day_of_week    AS "dayOfWeek",
                    rs.day_of_month   AS "dayOfMonth",
                    rs.time_of_day    AS "timeOfDay",
                    rs.recipients,
                    rs.format,
                    rs.filters,
                    rs.is_active      AS "isActive",
                    rs.last_run_at    AS "lastRunAt",
                    rs.next_run_at    AS "nextRunAt",
                    rs.created_by     AS "createdBy",
                    rs.created_at     AS "createdAt",
                    rs.updated_at     AS "updatedAt",
                    rrl.id            AS "lastRunId",
                    rrl.status        AS "lastRunStatus",
                    rrl.row_count     AS "lastRunRowCount",
                    rrl.completed_at  AS "lastRunCompletedAt",
                    rrl.error_message AS "lastRunErrorMessage"
                FROM report_schedules rs
                LEFT JOIN LATERAL (
                    SELECT id, status, row_count, completed_at, error_message
                    FROM report_run_log
                    WHERE schedule_id = rs.id
                    ORDER BY started_at DESC
                    LIMIT 1
                ) rrl ON TRUE
                ORDER BY rs.created_at DESC
                """, cancellationToken: ct));

            DapperJsonbHelper.FixJsonb(rows);

            return Results.Ok(rows);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/report-schedules ─────────────────────────────────────
        app.MapPost("/api/admin/report-schedules", async (
            CreateReportScheduleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.ReportsCreate)) return Results.Forbid();

            // ── Validation ───────────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "users", "tournaments", "revenue", "activity", "moderation" };
            if (!allowedTypes.Contains(req.ReportType))
                return Results.BadRequest(new { error = $"Invalid reportType. Allowed: {string.Join(", ", allowedTypes)}." });

            var allowedFreqs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "daily", "weekly", "monthly" };
            if (!allowedFreqs.Contains(req.Frequency))
                return Results.BadRequest(new { error = $"Invalid frequency. Allowed: {string.Join(", ", allowedFreqs)}." });

            var allowedFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "csv", "json" };
            var format = string.IsNullOrWhiteSpace(req.Format) ? "csv" : req.Format.ToLower();
            if (!allowedFormats.Contains(format))
                return Results.BadRequest(new { error = "Invalid format. Allowed: csv, json." });

            if (req.Recipients is null || req.Recipients.Length == 0)
                return Results.BadRequest(new { error = "At least one recipient is required." });
            if (req.Recipients.Length > 50)
                return Results.BadRequest(new { error = "Maximum 50 recipients allowed." });

            var emailRegex = new System.Text.RegularExpressions.Regex(
                @"^[^@\s]+@[^@\s]+\.[^@\s]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var invalidEmails = req.Recipients.Where(r => !emailRegex.IsMatch(r ?? "")).ToList();
            if (invalidEmails.Count > 0)
                return Results.BadRequest(new { error = $"Invalid email(s): {string.Join(", ", invalidEmails)}." });

            if (!TimeOnly.TryParse(req.TimeOfDay, out var timeOfDay))
                return Results.BadRequest(new { error = "Invalid timeOfDay format. Use HH:mm." });

            // Weekly requires dayOfWeek 0-6
            if (req.Frequency.Equals("weekly", StringComparison.OrdinalIgnoreCase))
            {
                if (req.DayOfWeek is null or < 0 or > 6)
                    return Results.BadRequest(new { error = "dayOfWeek (0-6) is required for weekly schedules." });
            }
            // Monthly requires dayOfMonth 1-31
            if (req.Frequency.Equals("monthly", StringComparison.OrdinalIgnoreCase))
            {
                if (req.DayOfMonth is null or < 1 or > 31)
                    return Results.BadRequest(new { error = "dayOfMonth (1-31) is required for monthly schedules." });
            }

            var filtersJson = req.Filters is not null
                ? JsonSerializer.Serialize(req.Filters)
                : "{}";

            var nextRun = ComputeNextRun(req.Frequency, req.DayOfWeek, req.DayOfMonth, timeOfDay);

            using var conn = db.CreateConnection();

            var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO report_schedules
                    (name, report_type, frequency, day_of_week, day_of_month, time_of_day,
                     recipients, format, filters, is_active, next_run_at, created_by)
                VALUES
                    (@name, @reportType, @frequency, @dayOfWeek, @dayOfMonth, @timeOfDay::time,
                     @recipients, @format, @filters::jsonb, TRUE, @nextRunAt, @createdBy)
                RETURNING id
                """, new
            {
                name = req.Name.Trim(),
                reportType = req.ReportType.ToLower(),
                frequency = req.Frequency.ToLower(),
                dayOfWeek = req.DayOfWeek,
                dayOfMonth = req.DayOfMonth,
                timeOfDay = timeOfDay.ToString("HH:mm"),
                recipients = req.Recipients,
                format,
                filters = filtersJson,
                nextRunAt = nextRun,
                createdBy = userCtx.UserIdGuid
            }, cancellationToken: ct));

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Create, TargetType.System,
                id, req.Name.Trim(),
                new { reportType = req.ReportType, frequency = req.Frequency },
                ct: ct);

            var schedule = await conn.QuerySingleAsync<dynamic>(new CommandDefinition("""
                SELECT
                    id,
                    name,
                    report_type    AS "reportType",
                    frequency,
                    day_of_week    AS "dayOfWeek",
                    day_of_month   AS "dayOfMonth",
                    time_of_day    AS "timeOfDay",
                    recipients,
                    format,
                    filters,
                    is_active      AS "isActive",
                    last_run_at    AS "lastRunAt",
                    next_run_at    AS "nextRunAt",
                    created_by     AS "createdBy",
                    created_at     AS "createdAt",
                    updated_at     AS "updatedAt"
                FROM report_schedules WHERE id = @id
                """, new { id }, cancellationToken: ct));

            return Results.Created($"/api/admin/report-schedules/{id}", schedule);
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/report-schedules/{id} ─────────────────────────────────
        app.MapPut("/api/admin/report-schedules/{id:guid}", async (
            Guid id,
            UpdateReportScheduleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.ReportsEdit)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT * FROM report_schedules WHERE id = @id", new { id }, cancellationToken: ct));
            if (existing is null) return Results.NotFound(new { error = "Schedule not found." });

            // Validate format if provided
            if (!string.IsNullOrWhiteSpace(req.Format))
            {
                var allowedFmts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "csv", "json" };
                if (!allowedFmts.Contains(req.Format))
                    return Results.BadRequest(new { error = "Invalid format. Allowed: csv, json." });
            }

            // Validate recipients if provided
            if (req.Recipients is { Length: > 0 })
            {
                var emailRegex = new System.Text.RegularExpressions.Regex(
                    @"^[^@\s]+@[^@\s]+\.[^@\s]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var invalidEmails = req.Recipients.Where(r => !emailRegex.IsMatch(r ?? "")).ToList();
                if (invalidEmails.Count > 0)
                    return Results.BadRequest(new { error = $"Invalid email(s): {string.Join(", ", invalidEmails)}." });
            }

            var sets = new List<string>();
            var p = new DynamicParameters();
            p.Add("id", id);

            if (!string.IsNullOrWhiteSpace(req.Name))
            { sets.Add("name = @name"); p.Add("name", req.Name.Trim()); }
            if (req.IsActive.HasValue)
            {
                sets.Add("is_active = @isActive"); p.Add("isActive", req.IsActive.Value);
                // When re-activating, recompute next_run_at so the schedule fires on time
                if (req.IsActive.Value)
                {
                    var existDict = (IDictionary<string, object?>)existing;
                    var freq = existDict["frequency"]?.ToString() ?? "daily";
                    var dowRaw = existDict["day_of_week"];
                    var domRaw = existDict["day_of_month"];
                    var todRaw = existDict["time_of_day"]?.ToString() ?? "08:00";
                    var dow = dowRaw is not null ? Convert.ToInt32(dowRaw) : (int?)null;
                    var dom = domRaw is not null ? Convert.ToInt32(domRaw) : (int?)null;
                    TimeOnly.TryParse(todRaw, out var tod);
                    var nextRun = ComputeNextRun(freq, dow, dom, tod);
                    sets.Add("next_run_at = @nextRunAt"); p.Add("nextRunAt", nextRun);
                }
            }
            if (req.Recipients is { Length: > 0 })
            { sets.Add("recipients = @recipients"); p.Add("recipients", req.Recipients); }
            if (!string.IsNullOrWhiteSpace(req.Format))
            { sets.Add("format = @format"); p.Add("format", req.Format.ToLower()); }

            if (sets.Count == 0)
                return Results.BadRequest(new { error = "No fields to update." });

            var sql = $"UPDATE report_schedules SET {string.Join(", ", sets)} WHERE id = @id";
            await conn.ExecuteAsync(new CommandDefinition(sql, p, cancellationToken: ct));

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Update, TargetType.System,
                id, id.ToString(),
                new { updated = sets },
                ct: ct);

            var updated = await conn.QuerySingleAsync<dynamic>(new CommandDefinition("""
                SELECT
                    id,
                    name,
                    report_type    AS "reportType",
                    frequency,
                    day_of_week    AS "dayOfWeek",
                    day_of_month   AS "dayOfMonth",
                    time_of_day    AS "timeOfDay",
                    recipients,
                    format,
                    filters,
                    is_active      AS "isActive",
                    last_run_at    AS "lastRunAt",
                    next_run_at    AS "nextRunAt",
                    created_by     AS "createdBy",
                    created_at     AS "createdAt",
                    updated_at     AS "updatedAt"
                FROM report_schedules WHERE id = @id
                """, new { id }, cancellationToken: ct));

            return Results.Ok(updated);
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/report-schedules/{id} ──────────────────────────────
        app.MapDelete("/api/admin/report-schedules/{id:guid}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.ReportsDelete)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var name = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT name FROM report_schedules WHERE id = @id", new { id }, cancellationToken: ct));
            if (name is null) return Results.NotFound(new { error = "Schedule not found." });

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM report_schedules WHERE id = @id", new { id }, cancellationToken: ct));

            await audit.LogAsync(
                userCtx.UserIdGuid, userCtx.Email,
                ActionType.Delete, TargetType.System,
                id, name,
                ct: ct);

            return Results.NoContent();
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/report-schedules/{id}/run ────────────────────────────
        app.MapPost("/api/admin/report-schedules/{id:guid}/run", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.ReportsRun)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var schedule = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
                "SELECT * FROM report_schedules WHERE id = @id", new { id }, cancellationToken: ct));
            if (schedule is null) return Results.NotFound(new { error = "Schedule not found." });

            // Guard against concurrent runs on the same schedule
            var alreadyRunning = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM report_run_log
                WHERE schedule_id = @id AND status = 'running'
                """, new { id }, cancellationToken: ct));
            if (alreadyRunning > 0)
                return Results.Conflict(new { error = "A run is already in progress for this schedule." });

            // Insert a 'running' log entry
            var runId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO report_run_log (schedule_id, status, triggered_by)
                VALUES (@scheduleId, 'running', 'manual')
                RETURNING id
                """, new { scheduleId = id }, cancellationToken: ct));

            try
            {
                var schedDict = (IDictionary<string, object?>)schedule;
                var reportType = schedDict["report_type"]?.ToString() ?? "users";
                var format = schedDict["format"]?.ToString() ?? "json";

                // ── Generate report data ─────────────────────────────────────
                var (payload, rowCount) = await GenerateReportAsync(conn, reportType, ct);

                string fileContent;
                string mimeType;

                if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
                {
                    // Serialise to JSON first, then convert rows → CSV
                    var tempJson = JsonSerializer.Serialize(payload);
                    using var doc = JsonDocument.Parse(tempJson);
                    fileContent = ConvertReportToCsv(doc);
                    mimeType = "text/csv";
                }
                else
                {
                    fileContent = JsonSerializer.Serialize(payload);
                    mimeType = "application/json";
                }

                var bytes = System.Text.Encoding.UTF8.GetByteCount(fileContent);
                var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(fileContent))}";

                // Update run log — success
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE report_run_log
                    SET status          = 'success',
                        completed_at    = NOW(),
                        row_count       = @rowCount,
                        file_size_bytes = @fileSize,
                        download_url    = @downloadUrl
                    WHERE id = @runId
                    """, new { runId, rowCount, fileSize = (long)bytes, downloadUrl = dataUri },
                    cancellationToken: ct));

                // Update schedule last_run_at
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE report_schedules SET last_run_at = NOW() WHERE id = @id",
                    new { id }, cancellationToken: ct));

                await audit.LogAsync(
                    userCtx.UserIdGuid, userCtx.Email,
                    ActionType.Create, TargetType.System,
                    id, $"manual_run:{reportType}",
                    new { runId, rowCount },
                    ct: ct);

                var runLog = await conn.QuerySingleAsync<dynamic>(new CommandDefinition("""
                    SELECT id, schedule_id AS "scheduleId", status,
                           started_at AS "startedAt", completed_at AS "completedAt",
                           row_count AS "rowCount", file_size_bytes AS "fileSizeBytes",
                           error_message AS "errorMessage", download_url AS "downloadUrl",
                           triggered_by AS "triggeredBy"
                    FROM report_run_log WHERE id = @runId
                    """, new { runId }, cancellationToken: ct));

                return Results.Ok(runLog);
            }
            catch (Exception ex)
            {
                // Mark as failed
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE report_run_log
                    SET status        = 'failed',
                        completed_at  = NOW(),
                        error_message = @err
                    WHERE id = @runId
                    """, new { runId, err = ex.Message }, cancellationToken: ct));

                var failedLog = await conn.QuerySingleAsync<dynamic>(new CommandDefinition("""
                    SELECT id, schedule_id AS "scheduleId", status,
                           started_at AS "startedAt", completed_at AS "completedAt",
                           row_count AS "rowCount", file_size_bytes AS "fileSizeBytes",
                           error_message AS "errorMessage", download_url AS "downloadUrl",
                           triggered_by AS "triggeredBy"
                    FROM report_run_log WHERE id = @runId
                    """, new { runId }, cancellationToken: ct));

                return Results.Ok(failedLog);
            }
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/report-schedules/{id}/history ─────────────────────────
        app.MapGet("/api/admin/report-schedules/{id:guid}/history", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            [FromQuery] int page = 1,
            [FromQuery] int limit = 20,
            CancellationToken ct = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.ReportsView)) return Results.Forbid();

            if (page < 1) page = 1;
            if (limit < 1) limit = 20;
            if (limit > 100) limit = 100;
            var offset = (page - 1) * limit;

            using var conn = db.CreateConnection();

            var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM report_schedules WHERE id = @id", new { id }, cancellationToken: ct));
            if (exists == 0) return Results.NotFound(new { error = "Schedule not found." });

            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM report_run_log WHERE schedule_id = @id", new { id }, cancellationToken: ct));

            var runs = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                SELECT
                    id,
                    schedule_id      AS "scheduleId",
                    status,
                    started_at       AS "startedAt",
                    completed_at     AS "completedAt",
                    row_count        AS "rowCount",
                    file_size_bytes  AS "fileSizeBytes",
                    error_message    AS "errorMessage",
                    download_url     AS "downloadUrl",
                    triggered_by     AS "triggeredBy"
                FROM report_run_log
                WHERE schedule_id = @id
                ORDER BY started_at DESC
                LIMIT @limit OFFSET @offset
                """, new { id, limit, offset }, cancellationToken: ct));

            return Results.Ok(new { items = runs, total, page, limit });
        }).RequireAuthorization("Admin");

        // ── Phase 13: GDPR / Compliance ───────────────────────────────────────

        // GET /api/admin/gdpr/requests — paginated list with user info
        app.MapGet("/api/admin/gdpr/requests", async (
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct,
            string? status = null,
            string? requestType = null,
            int page = 1,
            int limit = 25) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.GdprView)) return Results.Forbid();

            if (page < 1) page = 1;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;
            var offset = (page - 1) * limit;

            using var conn = db.CreateConnection();

            var conditions = new System.Text.StringBuilder("WHERE 1=1");
            var p = new DynamicParameters();
            if (!string.IsNullOrWhiteSpace(status))
            {
                conditions.Append(" AND gr.status = @status");
                p.Add("status", status);
            }
            if (!string.IsNullOrWhiteSpace(requestType))
            {
                conditions.Append(" AND gr.request_type = @requestType");
                p.Add("requestType", requestType);
            }
            p.Add("limit", limit);
            p.Add("offset", offset);

            var countSql = $"""
                SELECT COUNT(*) FROM gdpr_requests gr {conditions}
                """;
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, p, cancellationToken: ct));

            var querySql = $"""
                SELECT
                    gr.id,
                    gr.user_id            AS "userId",
                    gr.request_type       AS "requestType",
                    gr.status,
                    gr.requested_at       AS "requestedAt",
                    gr.processed_at       AS "processedAt",
                    gr.processed_by       AS "processedBy",
                    pb.username            AS "processedByUsername",
                    gr.notes,
                    gr.download_url       AS "downloadUrl",
                    gr.expires_at         AS "expiresAt",
                    p.username,
                    p.email,
                    p.full_name           AS "fullName"
                FROM gdpr_requests gr
                LEFT JOIN profiles p  ON p.id  = gr.user_id
                LEFT JOIN profiles pb ON pb.id = gr.processed_by
                {conditions}
                ORDER BY gr.requested_at DESC
                LIMIT @limit OFFSET @offset
                """;
            var items = await conn.QueryAsync<dynamic>(new CommandDefinition(querySql, p, cancellationToken: ct));

            return Results.Ok(new { requests = items, total, page, limit });
        }).RequireAuthorization("Admin");

        // POST /api/admin/gdpr/requests/{id}/process — approve or reject
        app.MapPost("/api/admin/gdpr/requests/{id}/process", async (
            Guid id,
            [FromBody] ProcessGdprRequest req,
            IDbConnectionFactory db,
            AuditService audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.GdprProcess)) return Results.Forbid();

            if (req.Action is not ("approve" or "reject"))
                return Results.BadRequest(new { error = "Action must be 'approve' or 'reject'." });

            using var conn = db.CreateConnection();

            var gdprReq = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition("""
                SELECT id, user_id, request_type, status
                FROM gdpr_requests
                WHERE id = @id
                """, new { id }, cancellationToken: ct));

            if (gdprReq is null) return Results.NotFound(new { error = "GDPR request not found." });

            Guid targetUserId = (Guid)gdprReq.user_id;
            string requestType = (string)gdprReq.request_type;

            if (req.Action == "reject")
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE gdpr_requests
                    SET status       = 'rejected',
                        processed_at = NOW(),
                        processed_by = @adminId,
                        notes        = @notes
                    WHERE id = @id
                    """, new { id, adminId = userCtx.UserIdGuid, notes = req.Notes ?? "" },
                    cancellationToken: ct));

                await audit.LogAsync(
                    userCtx.UserIdGuid, userCtx.Email,
                    ActionType.Reject, TargetType.User,
                    targetUserId, targetUserId.ToString(),
                    new { gdpr_request_id = id, request_type = requestType },
                    ct: ct);

                return Results.Ok(new { success = true, status = "rejected" });
            }

            // ── Approve ──────────────────────────────────────────────────────
            // Atomically claim the request; guards against concurrent admin actions
            var claimedId = await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition("""
                UPDATE gdpr_requests SET status = 'processing', processed_by = @adminId
                WHERE id = @id AND status = 'pending'
                RETURNING id
                """, new { id, adminId = userCtx.UserIdGuid }, cancellationToken: ct));

            if (claimedId is null)
                return Results.Conflict(new { error = "Request has already been processed" });

            try
            {
                if (requestType == "export")
                {
                    // Collect user data from multiple tables
                    var profile = await conn.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                        """
                        SELECT id, username, email, full_name, bio, location, country_code,
                               date_of_birth, riot_tag, social_links,
                               avatar_url, card_image_url, banner_url, created_at
                        FROM profiles WHERE id = @uid
                        """,
                        new { uid = targetUserId }, cancellationToken: ct));

                    var tournaments = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                        SELECT tr.id, tr.tournament_id, tr.status, tr.registered_at,
                               t.name AS tournament_name, t.game, t.start_date
                        FROM tournament_registrations tr
                        JOIN tournaments t ON t.id = tr.tournament_id
                        WHERE tr.user_id = @uid OR tr.team_id IN (
                            SELECT team_id FROM team_members WHERE user_id = @uid
                        )
                        ORDER BY tr.registered_at DESC
                        LIMIT 500
                        """, new { uid = targetUserId }, cancellationToken: ct));

                    var teams = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                        SELECT tm.team_id, tm.role, tm.joined_at, t.name AS team_name, t.game
                        FROM team_members tm
                        JOIN teams t ON t.id = tm.team_id
                        WHERE tm.user_id = @uid
                        ORDER BY tm.joined_at DESC
                        LIMIT 200
                        """, new { uid = targetUserId }, cancellationToken: ct));

                    var matches = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                        SELECT m.id, m.status, m.scheduled_at, m.completed_at,
                               m.team1_score, m.team2_score
                        FROM matches m
                        JOIN tournament_registrations tr ON
                            tr.tournament_id = m.tournament_id AND (
                                tr.user_id = @uid OR tr.team_id IN (
                                    SELECT team_id FROM team_members WHERE user_id = @uid
                                )
                            )
                        ORDER BY m.scheduled_at DESC
                        LIMIT 500
                        """, new { uid = targetUserId }, cancellationToken: ct));

                    var consents = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                        SELECT consent_type, granted, recorded_at, version
                        FROM consent_records
                        WHERE user_id = @uid
                        ORDER BY recorded_at DESC
                        """, new { uid = targetUserId }, cancellationToken: ct));

                    var exportPayload = new
                    {
                        exported_at = DateTime.UtcNow,
                        user_id = targetUserId,
                        profile,
                        tournaments = tournaments.ToList(),
                        teams = teams.ToList(),
                        matches = matches.ToList(),
                        consent_records = consents.ToList()
                    };

                    var json = JsonSerializer.Serialize(exportPayload);
                    var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
                    var dataUri = $"data:application/json;base64,{b64}";

                    await conn.ExecuteAsync(new CommandDefinition("""
                        UPDATE gdpr_requests
                        SET status       = 'completed',
                            processed_at = NOW(),
                            processed_by = @adminId,
                            notes        = @notes,
                            download_url = @downloadUrl,
                            expires_at   = NOW() + INTERVAL '7 days'
                        WHERE id = @id
                        """, new { id, adminId = userCtx.UserIdGuid, notes = req.Notes ?? "", downloadUrl = dataUri },
                        cancellationToken: ct));

                    await audit.LogAsync(
                        userCtx.UserIdGuid, userCtx.Email,
                        ActionType.Approve, TargetType.User,
                        targetUserId, targetUserId.ToString(),
                        new { gdpr_request_id = id, request_type = "export" },
                        ct: ct);

                    return Results.Ok(new { success = true, status = "completed", expiresAt = DateTime.UtcNow.AddDays(7) });
                }

                // ── Deletion (right to erasure) ───────────────────────────────
                var partialUuid = targetUserId.ToString("N")[..8];
                var anonUsername = $"deleted_user_{partialUuid}";

                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE profiles
                    SET username          = @anonUsername,
                        bio               = '',
                        avatar_url        = NULL,
                        card_image_url    = NULL,
                        banner_url        = NULL,
                        date_of_birth     = NULL,
                        location          = NULL,
                        country_code      = NULL,
                        riot_tag          = NULL,
                        social_links      = NULL,
                        is_deleted        = TRUE
                    WHERE id = @targetUserId
                    """, new { anonUsername, targetUserId },
                    cancellationToken: ct));

                // Remove sensitive consent records
                await conn.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM consent_records WHERE user_id = @uid",
                    new { uid = targetUserId }, cancellationToken: ct));

                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE gdpr_requests
                    SET status       = 'completed',
                        processed_at = NOW(),
                        processed_by = @adminId,
                        notes        = @notes
                    WHERE id = @id
                    """, new { id, adminId = userCtx.UserIdGuid, notes = req.Notes ?? "" },
                    cancellationToken: ct));

                await audit.LogAsync(
                    userCtx.UserIdGuid, userCtx.Email,
                    ActionType.Delete, TargetType.User,
                    targetUserId, anonUsername,
                    new { gdpr_request_id = id, request_type = "deletion", action = "anonymized" },
                    AuditSeverity.High,
                    ct);

                return Results.Ok(new { success = true, status = "completed" });
            }
            catch (Exception ex)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE gdpr_requests
                    SET status       = 'failed',
                        processed_by = @adminId,
                        notes        = @notes
                    WHERE id = @id
                    """, new { id, adminId = userCtx.UserIdGuid, notes = $"Processing failed: {ex.Message}" },
                    cancellationToken: ct));

                return Results.Problem("GDPR request processing failed. The request has been marked as failed.");
            }
        }).RequireAuthorization("Admin");

        // GET /api/admin/gdpr/stats — compliance dashboard stats
        app.MapGet("/api/admin/gdpr/stats", async (
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.GdprView)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var stats = await conn.QueryFirstAsync<dynamic>(new CommandDefinition("""
                SELECT
                    COUNT(*) FILTER (WHERE status IN ('pending','processing'))                       AS "pendingRequests",
                    COUNT(*) FILTER (WHERE status = 'completed'
                                      AND processed_at::date = CURRENT_DATE)                         AS "completedToday",
                    COUNT(*) FILTER (WHERE request_type = 'export')                                  AS "exportRequests",
                    COUNT(*) FILTER (WHERE request_type = 'deletion')                                AS "deletionRequests",
                    COALESCE(
                        ROUND(
                            AVG(
                                EXTRACT(EPOCH FROM (processed_at - requested_at)) / 86400.0
                            ) FILTER (WHERE status = 'completed' AND processed_at IS NOT NULL),
                            1
                        ),
                        0
                    )                                                                                AS "avgProcessingDays"
                FROM gdpr_requests
                """, cancellationToken: ct));

            return Results.Ok(stats);
        }).RequireAuthorization("Admin");

        // GET /api/admin/gdpr/consent-records — paginated consent audit
        app.MapGet("/api/admin/gdpr/consent-records", async (
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct,
            Guid? userId = null,
            string? consentType = null,
            bool? granted = null,
            int page = 1,
            int limit = 25) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.GdprAudit)) return Results.Forbid();

            if (page < 1) page = 1;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;
            var offset = (page - 1) * limit;

            using var conn = db.CreateConnection();

            var conditions = new System.Text.StringBuilder("WHERE 1=1");
            var p = new DynamicParameters();

            if (userId.HasValue)
            {
                conditions.Append(" AND cr.user_id = @userId");
                p.Add("userId", userId.Value);
            }
            if (!string.IsNullOrWhiteSpace(consentType))
            {
                conditions.Append(" AND cr.consent_type = @consentType");
                p.Add("consentType", consentType);
            }
            if (granted.HasValue)
            {
                conditions.Append(" AND cr.granted = @granted");
                p.Add("granted", granted.Value);
            }
            p.Add("limit", limit);
            p.Add("offset", offset);

            var countSql = $"SELECT COUNT(*) FROM consent_records cr {conditions}";
            var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, p, cancellationToken: ct));

            var querySql = $"""
                SELECT
                    cr.id,
                    cr.user_id      AS "userId",
                    cr.consent_type AS "consentType",
                    cr.granted,
                    cr.ip_address   AS "ipAddress",
                    cr.user_agent   AS "userAgent",
                    cr.recorded_at  AS "recordedAt",
                    cr.version,
                    p.username,
                    p.email
                FROM consent_records cr
                LEFT JOIN profiles p ON p.id = cr.user_id
                {conditions}
                ORDER BY cr.recorded_at DESC
                LIMIT @limit OFFSET @offset
                """;
            var items = await conn.QueryAsync<dynamic>(new CommandDefinition(querySql, p, cancellationToken: ct));

            return Results.Ok(new { records = items, total, page, limit });
        }).RequireAuthorization("Admin");

        // POST /api/gdpr/request — user submits own GDPR request
        app.MapPost("/api/gdpr/request", async (
            [FromBody] SubmitGdprRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var allowedTypes = new HashSet<string> { "export", "deletion" };
            if (string.IsNullOrWhiteSpace(req.RequestType) || !allowedTypes.Contains(req.RequestType))
                return Results.BadRequest(new { error = "RequestType must be 'export' or 'deletion'." });

            using var conn = db.CreateConnection();

            // Enforce one active request per user per type
            var existing = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*) FROM gdpr_requests
                WHERE user_id      = @userId
                  AND request_type = @requestType
                  AND status IN ('pending', 'processing')
                """, new { userId = userCtx.UserIdGuid, requestType = req.RequestType },
                cancellationToken: ct));

            if (existing > 0)
                return Results.Conflict(new { error = $"A {req.RequestType} request is already pending or in progress." });

            Guid newId;
            try
            {
                newId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                    INSERT INTO gdpr_requests (user_id, request_type)
                    VALUES (@userId, @requestType)
                    RETURNING id
                    """, new { userId = userCtx.UserIdGuid, requestType = req.RequestType },
                    cancellationToken: ct));
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Conflict(new { error = $"A {req.RequestType} request is already pending or in progress." });
            }

            return Results.Created($"/api/gdpr/request/{newId}", new { id = newId, status = "pending" });
        }).RequireAuthorization("Authenticated");

        // POST /api/consent — user records a consent decision
        app.MapPost("/api/consent", async (
            [FromBody] RecordConsentRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var allowedConsentTypes = new HashSet<string>
            {
                "marketing", "analytics", "third_party", "terms_of_service"
            };
            if (string.IsNullOrWhiteSpace(req.ConsentType) || !allowedConsentTypes.Contains(req.ConsentType))
                return Results.BadRequest(new { error = $"ConsentType must be one of: {string.Join(", ", allowedConsentTypes)}." });

            // Resolve real client IP — trust X-Forwarded-For behind a proxy
            var ip = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                     ?? ctx.Connection.RemoteIpAddress?.ToString();

            var userAgent = ctx.Request.Headers["User-Agent"].FirstOrDefault();
            var version = string.IsNullOrWhiteSpace(req.Version) ? "1.0" : req.Version;

            using var conn = db.CreateConnection();

            var newId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO consent_records (user_id, consent_type, granted, ip_address, user_agent, version)
                VALUES (@userId, @consentType, @granted, @ip, @userAgent, @version)
                RETURNING id
                """,
                new
                {
                    userId = userCtx.UserIdGuid,
                    consentType = req.ConsentType,
                    granted = req.Granted,
                    ip,
                    userAgent,
                    version
                },
                cancellationToken: ct));

            return Results.Created($"/api/consent/{newId}",
                new { id = newId, consentType = req.ConsentType, granted = req.Granted, version });
        }).RequireAuthorization("Authenticated");

        // ══════════════════════════════════════════════════════════════════════════
        // ── Phase 14: Anomaly Detection ───────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════════

        // ── GET /api/admin/anomalies ──────────────────────────────────────────────
        app.MapGet("/api/admin/anomalies", async (
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct,
            bool? isResolved = null,
            int page = 1,
            int limit = 25) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Any()) return Results.Forbid();

            if (page < 1) page = 1;
            if (limit < 1) limit = 1;
            if (limit > 100) limit = 100;
            var offset = (page - 1) * limit;

            using var conn = db.CreateConnection();

            var where = new System.Text.StringBuilder("WHERE 1=1");
            var p = new DynamicParameters();

            if (isResolved.HasValue)
            {
                where.Append(" AND ae.is_resolved = @isResolved");
                p.Add("isResolved", isResolved.Value);
            }

            p.Add("limit", limit);
            p.Add("offset", offset);

            var countSql = $"SELECT COUNT(*) FROM anomaly_events ae {where}";
            var total = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(countSql, p, cancellationToken: ct));

            var sql = $"""
                SELECT
                    ae.id,
                    ae.rule_id           AS "ruleId",
                    ar.name              AS "ruleName",
                    ar.severity,
                    ae.metric,
                    ae.count_observed    AS "countObserved",
                    ae.threshold_count   AS "thresholdCount",
                    ae.window_minutes    AS "windowMinutes",
                    ae.detected_at       AS "detectedAt",
                    ae.resolved_at       AS "resolvedAt",
                    ae.resolved_by       AS "resolvedBy",
                    p.username           AS "resolvedByUsername",
                    ae.is_resolved       AS "isResolved",
                    ae.details
                FROM anomaly_events ae
                JOIN  anomaly_rules ar ON ar.id = ae.rule_id
                LEFT JOIN profiles p   ON p.id  = ae.resolved_by
                {where}
                ORDER BY
                    CASE WHEN ae.is_resolved = FALSE THEN 0 ELSE 1 END,
                    ae.detected_at DESC
                LIMIT @limit OFFSET @offset
                """;

            var items = await conn.QueryAsync<dynamic>(
                new CommandDefinition(sql, p, cancellationToken: ct));
            DapperJsonbHelper.FixJsonb(items);

            // Aggregate totals across ALL pages — not just the current page.
            var unresolvedTotal = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "SELECT COUNT(*) FROM anomaly_events WHERE is_resolved = FALSE",
                    cancellationToken: ct));

            var criticalTotal = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("""
                    SELECT COUNT(*) FROM anomaly_events ae
                    JOIN anomaly_rules ar ON ar.id = ae.rule_id
                    WHERE ae.is_resolved = FALSE AND ar.severity = 'critical'
                    """, cancellationToken: ct));

            var highTotal = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("""
                    SELECT COUNT(*) FROM anomaly_events ae
                    JOIN anomaly_rules ar ON ar.id = ae.rule_id
                    WHERE ae.is_resolved = FALSE AND ar.severity = 'high'
                    """, cancellationToken: ct));

            return Results.Ok(new { items, total, page, limit, unresolvedTotal, criticalTotal, highTotal });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/anomalies/rules ────────────────────────────────────────
        app.MapGet("/api/admin/anomalies/rules", async (
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Any()) return Results.Forbid();

            using var conn = db.CreateConnection();

            var rules = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                SELECT
                    id,
                    name,
                    description,
                    metric,
                    threshold_count  AS "thresholdCount",
                    window_minutes   AS "windowMinutes",
                    severity,
                    is_active        AS "isActive",
                    last_triggered_at AS "lastTriggeredAt",
                    cooldown_minutes AS "cooldownMinutes",
                    created_at       AS "createdAt"
                FROM anomaly_rules
                ORDER BY severity DESC, name
                """, cancellationToken: ct));

            return Results.Ok(rules);
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/anomalies/rules/{id} ───────────────────────────────────
        app.MapPut("/api/admin/anomalies/rules/{id}", async (
            Guid id,
            UpdateAnomalyRuleRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.SystemSettings)) return Results.Forbid();

            // Validate severity if provided
            if (req.Severity is not null &&
                req.Severity is not ("low" or "medium" or "high" or "critical"))
                return Results.BadRequest(new { error = "Severity must be low, medium, high, or critical" });

            if (req.ThresholdCount.HasValue && req.ThresholdCount.Value < 1)
                return Results.BadRequest(new { error = "thresholdCount must be at least 1" });

            if (req.WindowMinutes.HasValue && req.WindowMinutes.Value < 1)
                return Results.BadRequest(new { error = "windowMinutes must be at least 1" });

            if (req.CooldownMinutes.HasValue && req.CooldownMinutes.Value < 0)
                return Results.BadRequest(new { error = "cooldownMinutes must be non-negative" });

            using var conn = db.CreateConnection();

            var exists = await conn.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM anomaly_rules WHERE id = @id)",
                    new { id }, cancellationToken: ct));

            if (!exists) return Results.NotFound(new { error = "Anomaly rule not found" });

            // Build partial update
            var sets = new List<string>();
            var dp = new DynamicParameters();
            dp.Add("id", id);

            if (req.ThresholdCount.HasValue) { sets.Add("threshold_count  = @thresholdCount"); dp.Add("thresholdCount", req.ThresholdCount.Value); }
            if (req.WindowMinutes.HasValue) { sets.Add("window_minutes   = @windowMinutes"); dp.Add("windowMinutes", req.WindowMinutes.Value); }
            if (req.Severity is not null) { sets.Add("severity         = @severity"); dp.Add("severity", req.Severity); }
            if (req.IsActive.HasValue) { sets.Add("is_active        = @isActive"); dp.Add("isActive", req.IsActive.Value); }
            if (req.CooldownMinutes.HasValue) { sets.Add("cooldown_minutes = @cooldownMinutes"); dp.Add("cooldownMinutes", req.CooldownMinutes.Value); }

            if (sets.Count == 0)
                return Results.BadRequest(new { error = "No fields provided to update" });

            var updated = await conn.QueryFirstOrDefaultAsync<dynamic>(
                new CommandDefinition(
                    $"""
                    UPDATE anomaly_rules
                    SET {string.Join(", ", sets)}
                    WHERE id = @id
                    RETURNING
                        id,
                        name,
                        description,
                        metric,
                        threshold_count  AS "thresholdCount",
                        window_minutes   AS "windowMinutes",
                        severity,
                        is_active        AS "isActive",
                        last_triggered_at AS "lastTriggeredAt",
                        cooldown_minutes AS "cooldownMinutes",
                        created_at       AS "createdAt"
                    """,
                    dp,
                    cancellationToken: ct));

            return Results.Ok(updated);
        }).RequireAuthorization(Permissions.SystemSettings);

        // ── POST /api/admin/anomalies/{id}/resolve ────────────────────────────────
        app.MapPost("/api/admin/anomalies/{id}/resolve", async (
            Guid id,
            ResolveAnomalyRequest req,
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.AdminRoles.Any()) return Results.Forbid();

            using var conn = db.CreateConnection();

            var resolvedById = userCtx.UserIdGuid;
            var notes = req.Notes?.Trim();

            var updated = await conn.QueryFirstOrDefaultAsync<dynamic>(
                new CommandDefinition("""
                    UPDATE anomaly_events
                    SET
                        is_resolved = TRUE,
                        resolved_at = NOW(),
                        resolved_by = @resolvedBy,
                        details     = details || jsonb_build_object('resolution_notes', @notes::text)
                    WHERE id = @id AND is_resolved = FALSE
                    RETURNING
                        id,
                        rule_id      AS "ruleId",
                        metric,
                        count_observed  AS "countObserved",
                        threshold_count AS "thresholdCount",
                        window_minutes  AS "windowMinutes",
                        detected_at  AS "detectedAt",
                        resolved_at  AS "resolvedAt",
                        resolved_by  AS "resolvedBy",
                        is_resolved  AS "isResolved"
                    """,
                    new { id, resolvedBy = resolvedById, notes },
                    cancellationToken: ct));

            if (updated is null)
                return Results.NotFound(new { error = "Anomaly event not found or already resolved" });

            return Results.Ok(updated);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/anomalies/scan ────────────────────────────────────────
        app.MapPost("/api/admin/anomalies/scan", async (
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.SystemSettings)) return Results.Forbid();

            using var conn = db.CreateConnection();

            // Load all active rules
            var rules = (await conn.QueryAsync<AnomalyRuleRow>(
                new CommandDefinition("""
                    SELECT id, metric, name,
                           threshold_count  AS "ThresholdCount",
                           window_minutes   AS "WindowMinutes",
                           severity,
                           cooldown_minutes AS "CooldownMinutes",
                           last_triggered_at AS "LastTriggeredAt"
                    FROM anomaly_rules
                    WHERE is_active = TRUE
                    ORDER BY metric
                    """, cancellationToken: ct))).ToList();

            var newEvents = new List<object>();
            var scanErrors = new List<object>();
            var now = DateTime.UtcNow;

            // Check if tournament_disputes table exists (optional metric)
            var disputesExists = await conn.ExecuteScalarAsync<bool>(
                new CommandDefinition("""
                    SELECT EXISTS (
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name   = 'tournament_disputes'
                    )
                    """, cancellationToken: ct));

            foreach (var rule in rules)
            {
                // Count events in window using parameterized interval
                int observed;
                try
                {
                    observed = rule.Metric switch
                    {
                        "failed_logins" => await conn.ExecuteScalarAsync<int>(
                            new CommandDefinition("""
                                SELECT COUNT(*) FROM audit_logs
                                WHERE action_type = 'FailedLogin'
                                  AND created_at  > NOW() - (@windowMinutes * INTERVAL '1 minute')
                                """,
                                new { windowMinutes = rule.WindowMinutes },
                                cancellationToken: ct)),

                        "new_accounts" => await conn.ExecuteScalarAsync<int>(
                            new CommandDefinition("""
                                SELECT COUNT(*) FROM profiles
                                WHERE created_at > NOW() - (@windowMinutes * INTERVAL '1 minute')
                                """,
                                new { windowMinutes = rule.WindowMinutes },
                                cancellationToken: ct)),

                        "reports" => await conn.ExecuteScalarAsync<int>(
                            new CommandDefinition("""
                                SELECT COUNT(*) FROM moderation_queue
                                WHERE created_at > NOW() - (@windowMinutes * INTERVAL '1 minute')
                                """,
                                new { windowMinutes = rule.WindowMinutes },
                                cancellationToken: ct)),

                        "disputes" when disputesExists => await conn.ExecuteScalarAsync<int>(
                            new CommandDefinition("""
                                SELECT COUNT(*) FROM tournament_disputes
                                WHERE created_at > NOW() - (@windowMinutes * INTERVAL '1 minute')
                                """,
                                new { windowMinutes = rule.WindowMinutes },
                                cancellationToken: ct)),

                        "disputes" => 0,   // table doesn't exist — skip gracefully

                        "registrations" => await conn.ExecuteScalarAsync<int>(
                            new CommandDefinition("""
                                SELECT COUNT(*) FROM tournament_registrations
                                WHERE created_at > NOW() - (@windowMinutes * INTERVAL '1 minute')
                                """,
                                new { windowMinutes = rule.WindowMinutes },
                                cancellationToken: ct)),

                        _ => 0
                    };
                }
                catch (Exception ex)
                {
                    // Log and continue — don't let one rule failure abort the whole scan
                    scanErrors.Add(new { ruleId = rule.Id, metric = rule.Metric, error = ex.Message });
                    continue;
                }

                if (observed <= rule.ThresholdCount)
                    continue;

                // Threshold exceeded — atomically claim the rule with a conditional UPDATE that
                // also enforces the cooldown. If another concurrent scan already claimed it
                // (or the cooldown hasn't elapsed), rowsAffected == 0 and we skip.
                var rowsAffected = await conn.ExecuteAsync(
                    new CommandDefinition("""
                        UPDATE anomaly_rules
                        SET last_triggered_at = NOW()
                        WHERE id = @ruleId
                          AND (last_triggered_at IS NULL
                               OR last_triggered_at < NOW() - (@cooldown * INTERVAL '1 minute'))
                        """,
                        new { ruleId = rule.Id, cooldown = rule.CooldownMinutes },
                        cancellationToken: ct));

                if (rowsAffected == 0)
                    continue; // cooldown not elapsed or concurrent scan already claimed this rule

                // Create anomaly event
                var detailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    rule_name = rule.Metric,
                    window_start = now.AddMinutes(-rule.WindowMinutes),
                    window_end = now,
                    scanned_by = userCtx.UserId
                });

                var eventId = await conn.ExecuteScalarAsync<Guid>(
                    new CommandDefinition("""
                        INSERT INTO anomaly_events
                            (rule_id, metric, count_observed, threshold_count, window_minutes, details)
                        VALUES
                            (@ruleId, @metric, @countObserved, @thresholdCount, @windowMinutes, @details::jsonb)
                        RETURNING id
                        """,
                        new
                        {
                            ruleId = rule.Id,
                            metric = rule.Metric,
                            countObserved = observed,
                            thresholdCount = rule.ThresholdCount,
                            windowMinutes = rule.WindowMinutes,
                            details = detailsJson
                        },
                        cancellationToken: ct));

                // Create admin alert so it surfaces in the alerts dashboard
                await conn.ExecuteAsync(
                    new CommandDefinition("""
                        INSERT INTO admin_alerts (type, severity, title, message, data)
                        VALUES (
                            'anomaly_detected',
                            @severity,
                            @title,
                            @message,
                            @data::jsonb
                        )
                        """,
                        new
                        {
                            severity = rule.Severity,
                            title = $"Anomaly detected: {rule.Metric}",
                            message = $"{observed} events observed in last {rule.WindowMinutes} minute(s) — threshold is {rule.ThresholdCount}.",
                            data = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                anomaly_event_id = eventId,
                                rule_id = rule.Id,
                                metric = rule.Metric,
                                count_observed = observed,
                                threshold_count = rule.ThresholdCount,
                                window_minutes = rule.WindowMinutes
                            })
                        },
                        cancellationToken: ct));

                newEvents.Add(new
                {
                    id = eventId,
                    ruleId = rule.Id,
                    metric = rule.Metric,
                    severity = rule.Severity,
                    countObserved = observed,
                    thresholdCount = rule.ThresholdCount,
                    windowMinutes = rule.WindowMinutes,
                    detectedAt = now
                });
            }

            return Results.Ok(new
            {
                scannedRules = rules.Count,
                detectedCount = newEvents.Count,
                detectedEvents = newEvents,
                scanErrors
            });
        }).RequireAuthorization(Permissions.SystemSettings);

        // ── Phase 15: Dashboard Customization ─────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════




        // ── GET /api/admin/entities/{type}/{id}/preview ───────────────────────
        // Minimal entity data for slide-over previews in cross-linking system
        app.MapGet("/api/admin/entities/{type}/{id}/preview", async (
            string type,
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Permission check based on entity type
            var requiredPermission = type.ToLowerInvariant() switch
            {
                "user" => Permissions.UsersView,
                "verification" => Permissions.UsersView,
                "license" => Permissions.UsersView,
                "tournament" => Permissions.TournamentsView,
                "team" => Permissions.TeamsView,
                "venue" => Permissions.VenuesView,
                "dispute" => Permissions.DisputesView,
                "organization" => Permissions.OrganizationsView,
                _ => null
            };
            if (requiredPermission is null)
                return Results.NotFound(new { error = $"Unknown entity type. Supported: user, license, tournament, team, venue, dispute, organization." });
            if (!userCtx.Permissions.Contains(requiredPermission))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            object? preview = type.ToLowerInvariant() switch
            {
                "user" => await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT p.id, p.username, p.full_name, p.email, p.avatar_url,
                           p.is_suspended, p.created_at,
                           (SELECT COUNT(*) FROM tournaments t WHERE t.organizer_id = p.id) AS tournament_count,
                           (SELECT string_agg(ur.role, ', ') FROM user_roles ur WHERE ur.user_id = p.id AND ur.is_active) AS roles
                    FROM profiles p
                    WHERE p.id = @id
                    """, new { id }),

                "tournament" => await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT t.id, t.name, t.game, t.status, t.format, t.prize_pool,
                           t.max_teams, t.start_date, t.is_featured, t.created_at,
                           p.username AS organizer_name, p.id AS organizer_id
                    FROM tournaments t
                    LEFT JOIN profiles p ON p.id = t.organizer_id
                    WHERE t.id = @id
                    """, new { id }),

                "team" => await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT t.id, t.name, t.tag, t.game, t.logo_url, t.created_at,
                           (SELECT COUNT(*) FROM team_members tm WHERE tm.team_id = t.id) AS member_count,
                           p.username AS captain_name, p.id AS captain_id
                    FROM teams t
                    LEFT JOIN profiles p ON p.id = t.captain_id
                    WHERE t.id = @id
                    """, new { id }),

                "venue" => await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT v.id, v.name, v.status, v.city, v.country,
                           v.total_stations, v.created_at,
                           p.username AS owner_name, p.id AS owner_id
                    FROM venues v
                    LEFT JOIN profiles p ON p.id = v.owner_id
                    WHERE v.id = @id
                    """, new { id }),

                "license" => await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT l.id, l.license_id, l.license_type, l.status,
                           l.issued_at, l.expires_at,
                           p.username, p.full_name, p.id AS user_id
                    FROM licenses l
                    JOIN profiles p ON p.id = l.user_id
                    WHERE l.id = @id
                    """, new { id }),

                "dispute" => await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT d.id, d.status, d.created_at, d.dispute_reason,
                           t.name AS tournament_name, t.id AS tournament_id,
                           p.username AS reporter_name, p.id AS reporter_id
                    FROM tournament_disputes d
                    LEFT JOIN tournaments t ON t.id = d.tournament_id
                    LEFT JOIN profiles p ON p.id = d.raised_by_user_id
                    WHERE d.id = @id
                    """, new { id }),

                "organization" => await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT o.id, o.name, o.slug, o.logo_url, o.created_at,
                           (SELECT COUNT(*) FROM organization_members om WHERE om.organization_id = o.id) AS member_count,
                           p.username AS owner_name, p.id AS owner_id
                    FROM organizations o
                    LEFT JOIN profiles p ON p.id = o.owner_id
                    WHERE o.id = @id
                    """, new { id }),

                "verification" => await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT vr.id, vr.requested_role, vr.status, vr.created_at,
                           vr.business_name, vr.first_name, vr.last_name,
                           p.username, p.id AS user_id
                    FROM verification_requests vr
                    JOIN profiles p ON p.id = vr.user_id
                    WHERE vr.id = @id
                    """, new { id }),

                _ => null
            };

            if (preview is null)
                return Results.NotFound(new { error = $"Entity not found: {type}/{id}" });

            return Results.Ok(new { type, id, preview });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/command-centre ──────────────────────────────────────
        // Aggregated dashboard data for Command Centre
        app.MapGet("/api/admin/command-centre", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains(Permissions.DashboardView))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            // Pending counts with stale detection (3+ days).
            // Disputes read the real tournament_disputes table — urgency is staleness (7+ days open), there is no priority column.
            var pendingCounts = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    (SELECT COUNT(*) FROM verification_requests WHERE status = 'pending') AS verifications,
                    (SELECT COUNT(*) FROM verification_requests WHERE status = 'pending' AND created_at < NOW() - INTERVAL '3 days') AS verifications_stale,
                    (SELECT COUNT(*) FROM tournament_disputes WHERE status = 'open') AS disputes,
                    (SELECT COUNT(*) FROM tournament_disputes WHERE status = 'open' AND created_at < NOW() - INTERVAL '7 days') AS disputes_stale,
                    (SELECT COUNT(*) FROM ghost_approvals WHERE status = 'pending') AS ghost_approvals,
                    (SELECT COUNT(*) FROM admin_alerts WHERE status = 'active') AS alerts,
                    (SELECT COUNT(*) FROM admin_alerts WHERE status = 'active' AND severity = 'critical') AS alerts_critical,
                    (SELECT COUNT(*) FROM moderation_queue WHERE status = 'pending') AS moderation,
                    (SELECT COUNT(*) FROM moderation_queue WHERE status = 'pending' AND created_at >= NOW() - INTERVAL '1 day') AS moderation_today,
                    (SELECT COUNT(*) FROM gdpr_requests WHERE status = 'pending') AS gdpr,
                    (SELECT COUNT(*) FROM gdpr_requests WHERE status = 'pending' AND requested_at < NOW() - INTERVAL '20 days') AS gdpr_due_soon
                """);

            // Quick stats. Presence is an honest heuristic: distinct admins with recent audit activity.
            var stats = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    (SELECT COUNT(*) FROM profiles) AS users_total,
                    (SELECT COUNT(*) FROM profiles WHERE created_at >= NOW() - INTERVAL '7 days') AS users_growth,
                    (SELECT COUNT(*) FROM tournaments) AS tournaments_total,
                    (SELECT COUNT(*) FROM tournaments WHERE created_at >= NOW() - INTERVAL '7 days') AS tournaments_growth,
                    (SELECT COUNT(DISTINCT admin_id) FROM audit_logs WHERE created_at >= NOW() - INTERVAL '15 minutes') AS admins_active_recently
                """);

            // Sponsor fleet KPIs (30-day window, mirrors /api/admin/sponsors/overview)
            var sponsorKpis = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    COALESCE(SUM(t.impressions), 0) AS impressions_30d,
                    COALESCE(SUM(t.clicks), 0) AS clicks_30d,
                    (SELECT COUNT(*) FROM sponsors WHERE is_active) AS active_sponsors
                FROM sponsor_daily_totals t
                WHERE t.stat_date >= CURRENT_DATE - INTERVAL '30 days'
                """);

            // System health: kill switches, unresolved anomalies, enabled feature flags
            var systemHealth = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    (SELECT COUNT(*) FROM system_config WHERE is_kill_switch AND lower(value) IN ('true', '1', 'on')) AS kill_switches_armed,
                    (SELECT COUNT(*) FROM system_config WHERE is_kill_switch) AS kill_switches_total,
                    (SELECT COUNT(*) FROM anomaly_events WHERE is_resolved = FALSE) AS anomalies_unresolved,
                    (SELECT COUNT(*) FROM feature_flags WHERE is_enabled) AS feature_flags_enabled
                """);

            // Signups per day for last 7 days
            var signups7d = await conn.QueryAsync<dynamic>(
                """
                SELECT DATE_TRUNC('day', created_at)::date AS day, COUNT(*) AS count
                FROM profiles
                WHERE created_at >= NOW() - INTERVAL '7 days'
                GROUP BY DATE_TRUNC('day', created_at)
                ORDER BY day
                """);

            // Recent activity from audit logs
            var recentActivity = await conn.QueryAsync<dynamic>(
                """
                SELECT al.id, al.action_type, al.target_type, al.target_id, al.target_name,
                       al.created_at, p.username AS actor_name
                FROM audit_logs al
                LEFT JOIN profiles p ON p.id = al.admin_id
                ORDER BY al.created_at DESC
                LIMIT 10
                """);

            // Oldest pending verifications
            var oldestPending = await conn.QueryAsync<dynamic>(
                """
                SELECT vr.id, vr.requested_role, vr.status, vr.business_name,
                       vr.first_name, vr.last_name, vr.created_at,
                       p.username, p.avatar_url
                FROM verification_requests vr
                JOIN profiles p ON p.id = vr.user_id
                WHERE vr.status = 'pending'
                ORDER BY vr.created_at ASC
                LIMIT 5
                """);

            return Results.Ok(new
            {
                pending_counts = pendingCounts,
                stats,
                sponsor_kpis = sponsorKpis,
                system_health = systemHealth,
                payments_available = false,
                signups_7d = signups7d,
                recent_activity = recentActivity,
                oldest_pending = oldestPending
            });
        }).RequireAuthorization("Admin");

    }
    private sealed record AdminTransferCaptainReq(string NewCaptainId);
    private sealed record AdminEditTeamReq(
        string? Name = null,
        string? Tag = null,
        string? Description = null,
        string? Game = null,
        string? LogoUrl = null,
        bool RemoveLogo = false);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes a user's data from all dependent tables in FK-dependency order.
    /// When externalTxn is provided (bulk mode), caller manages commit/rollback
    /// and must call Supabase auth delete separately after commit.
    /// When externalTxn is null (single mode), creates its own transaction and
    /// deletes from Supabase auth after commit.
    /// </summary>
    private static async Task<IResult> DeleteUserAsync(
        Guid userId,
        System.Data.IDbConnection conn,
        ISupabaseAdminClient supabase,
        CancellationToken ct,
        System.Data.IDbTransaction? externalTxn = null)
    {
        var ownsTransaction = externalTxn is null;
        var txn = externalTxn ?? conn.BeginTransaction();
        try
        {
            await DeleteUserCascadeAsync(userId, conn, txn);
            if (ownsTransaction) txn.Commit();
        }
        catch
        {
            if (ownsTransaction) txn.Rollback();
            throw;
        }
        finally
        {
            if (ownsTransaction) txn.Dispose();
        }

        // Delete from Supabase Auth only in single-user mode.
        // In bulk mode, caller handles this after the outer transaction commits.
        if (ownsTransaction)
            await supabase.DeleteUserAsync(userId.ToString(), ct);

        return Results.Ok(new { success = true });
    }

    /// <summary>
    /// Cascade-deletes all user data from dependent tables within a transaction.
    /// Does NOT touch Supabase Auth — caller is responsible for that.
    /// </summary>
    private static async Task DeleteUserCascadeAsync(
        Guid userId,
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction txn)
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

    private static readonly HashSet<string> SuperAdminOnlyRoles =
        new(StringComparer.OrdinalIgnoreCase) { "super_admin" };

    private static async Task<IResult> AssignRoleToUserAsync(
        Guid userId,
        ManageUserRequest req,
        System.Data.IDbConnection conn,
        UserContext callerCtx,
        CancellationToken ct)
    {
        var role = req.RoleKey ?? req.Role;
        if (string.IsNullOrWhiteSpace(role))
            return Results.BadRequest(new { error = "Role is required for assign_role action." });

        var isAdmin = string.Equals(req.RoleType, "admin", StringComparison.OrdinalIgnoreCase);

        if (isAdmin)
        {
            if (SuperAdminOnlyRoles.Contains(role) && !callerCtx.IsSuperAdmin)
                return Results.Json(
                    new { error = "Only super_admin can assign this role." },
                    statusCode: 403);

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
        UserContext callerCtx,
        CancellationToken ct)
    {
        var role = req.RoleKey ?? req.Role;
        if (string.IsNullOrWhiteSpace(role))
            return Results.BadRequest(new { error = "Role is required for revoke_role action." });

        var isAdmin = string.Equals(req.RoleType, "admin", StringComparison.OrdinalIgnoreCase);

        if (isAdmin)
        {
            if (SuperAdminOnlyRoles.Contains(role) && !callerCtx.IsSuperAdmin)
                return Results.Json(
                    new { error = "Only super_admin can revoke this role." },
                    statusCode: 403);

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

    // ── Scheduled Reports Helpers ─────────────────────────────────────────────

    /// <summary>
    /// Converts a report JSON document to CSV.
    /// Looks for a top-level "rows" array; falls back to a "summary" object.
    /// </summary>
    private static string ConvertReportToCsv(JsonDocument doc)
    {
        var sb = new System.Text.StringBuilder();
        var root = doc.RootElement;

        if (root.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
        {
            var rows = rowsEl.EnumerateArray().ToList();
            if (rows.Count == 0) return "";

            // Header row from first object's property names
            var headers = rows[0].EnumerateObject().Select(p => CsvEscape(p.Name)).ToList();
            sb.AppendLine(string.Join(",", headers));

            // Data rows
            foreach (var row in rows)
            {
                var values = row.EnumerateObject()
                                .Select(p => CsvEscape(p.Value.ToString()))
                                .ToList();
                sb.AppendLine(string.Join(",", values));
            }
        }
        else if (root.TryGetProperty("summary", out var summaryEl))
        {
            var props = summaryEl.EnumerateObject().ToList();
            sb.AppendLine(string.Join(",", props.Select(p => CsvEscape(p.Name))));
            sb.AppendLine(string.Join(",", props.Select(p => CsvEscape(p.Value.ToString()))));
        }

        return sb.ToString();
    }

    private static string CsvEscape(string? value)
    {
        value ??= "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    /// <summary>Computes the next UTC run time for a report schedule.</summary>
    private static DateTime ComputeNextRun(string frequency, int? dayOfWeek, int? dayOfMonth, TimeOnly timeOfDay)
    {
        var now = DateTime.UtcNow;
        var todayAtTime = now.Date.Add(timeOfDay.ToTimeSpan());
        return frequency.ToLower() switch
        {
            "daily" => todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1),
            "weekly" => ComputeNextWeekly(now, dayOfWeek ?? 1, timeOfDay),
            "monthly" => ComputeNextMonthly(now, dayOfMonth ?? 1, timeOfDay),
            _ => now.AddDays(1)
        };
    }

    private static DateTime ComputeNextWeekly(DateTime now, int targetDow, TimeOnly timeOfDay)
    {
        var currentDow = (int)now.DayOfWeek; // 0=Sunday
        var daysUntil = ((targetDow - currentDow) + 7) % 7;
        var candidate = now.Date.AddDays(daysUntil).Add(timeOfDay.ToTimeSpan());
        // If candidate is in the past (same day, time already passed) advance one week
        if (candidate <= now) candidate = candidate.AddDays(7);
        return candidate;
    }

    private static DateTime ComputeNextMonthly(DateTime now, int targetDay, TimeOnly timeOfDay)
    {
        // Clamp to valid days in the current month
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        var clampedDay = Math.Min(targetDay, daysInMonth);
        var candidate = new DateTime(now.Year, now.Month, clampedDay)
                              .Add(timeOfDay.ToTimeSpan());
        if (candidate <= now)
        {
            // Advance to next month
            var nextMonth = now.Month == 12 ? 1 : now.Month + 1;
            var nextYear = now.Month == 12 ? now.Year + 1 : now.Year;
            var daysInNext = DateTime.DaysInMonth(nextYear, nextMonth);
            var clampedNext = Math.Min(targetDay, daysInNext);
            candidate = new DateTime(nextYear, nextMonth, clampedNext)
                            .Add(timeOfDay.ToTimeSpan());
        }
        return candidate;
    }

    /// <summary>
    /// Generates a summary report for the given type.
    /// Returns (payload object, row count).
    /// </summary>
    private static async Task<(object Payload, int RowCount)> GenerateReportAsync(
        System.Data.IDbConnection conn,
        string reportType,
        CancellationToken ct)
    {
        switch (reportType.ToLower())
        {
            case "users":
                {
                    var rows = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                    SELECT
                        p.id,
                        p.full_name,
                        p.username,
                        p.email,
                        p.country_code,
                        p.is_suspended,
                        p.created_at,
                        COALESCE(
                            array_agg(DISTINCT ur.role) FILTER (WHERE ur.role IS NOT NULL),
                            ARRAY[]::text[]
                        ) AS roles
                    FROM profiles p
                    LEFT JOIN user_roles ur ON ur.user_id = p.id AND ur.is_active = TRUE
                    GROUP BY p.id
                    ORDER BY p.created_at DESC
                    LIMIT 1000
                    """, cancellationToken: ct));
                    var list = rows.ToList();
                    return (new { report_type = "users", generated_at = DateTime.UtcNow, rows = list }, list.Count);
                }

            case "tournaments":
                {
                    var rows = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                    SELECT t.id, t.name, t.game, t.status, t.format, t.prize_pool,
                           t.max_teams, t.is_featured, t.start_date, t.created_at
                    FROM tournaments t
                    ORDER BY t.created_at DESC
                    LIMIT 1000
                    """, cancellationToken: ct));
                    var list = rows.ToList();
                    return (new { report_type = "tournaments", generated_at = DateTime.UtcNow, rows = list }, list.Count);
                }

            case "revenue":
                {
                    var row = await conn.QuerySingleAsync<dynamic>(new CommandDefinition("""
                    SELECT
                        COUNT(*)                                                    AS total_entries,
                        COALESCE(SUM(CASE WHEN amount > 0 THEN amount END), 0)      AS total_revenue,
                        COALESCE(AVG(CASE WHEN amount > 0 THEN amount END), 0)      AS avg_transaction,
                        MIN(created_at)                                             AS earliest,
                        MAX(created_at)                                             AS latest
                    FROM wallet_transactions
                    """, cancellationToken: ct));
                    return (new { report_type = "revenue", generated_at = DateTime.UtcNow, summary = row }, 1);
                }

            case "activity":
                {
                    var rows = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                    SELECT
                        DATE_TRUNC('day', created_at) AS activity_date,
                        COUNT(*)                       AS event_count,
                        action_type
                    FROM audit_logs
                    WHERE created_at >= NOW() - INTERVAL '30 days'
                    GROUP BY DATE_TRUNC('day', created_at), action_type
                    ORDER BY activity_date DESC, event_count DESC
                    LIMIT 5000
                    """, cancellationToken: ct));
                    var list = rows.ToList();
                    return (new { report_type = "activity", generated_at = DateTime.UtcNow, rows = list }, list.Count);
                }

            case "moderation":
                {
                    var rows = await conn.QueryAsync<dynamic>(new CommandDefinition("""
                    SELECT mq.id, mq.content_type, mq.status, mq.auto_flagged,
                           mq.created_at, mq.reviewed_at
                    FROM moderation_queue mq
                    ORDER BY mq.created_at DESC
                    LIMIT 1000
                    """, cancellationToken: ct));
                    var list = rows.ToList();
                    return (new { report_type = "moderation", generated_at = DateTime.UtcNow, rows = list }, list.Count);
                }

            default:
                return (new { report_type = reportType, generated_at = DateTime.UtcNow, rows = Array.Empty<object>() }, 0);
        }
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
public sealed record CreateAdminRoleRequest(string Name, string Key, string Description, Guid[] PermissionIds);
public sealed record UpdateAdminRoleRequest(string Name, string Description, Guid[] PermissionIds);
public sealed record AdminUpdateTournamentRequest(
    string? Status = null,
    [property: JsonPropertyName("is_featured")] bool? IsFeatured = null,
    string? Name = null,
    string? Game = null,
    string? Format = null,
    [property: JsonPropertyName("prize_pool")] decimal? PrizePool = null,
    [property: JsonPropertyName("max_teams")] int? MaxTeams = null,
    [property: JsonPropertyName("start_date")] DateTimeOffset? StartDate = null,
    string? Reason = null);
public sealed record AdminUpdateUserRequest(bool? IsAdmin = null, Guid[]? AdminRoles = null);
public sealed record CreateAdminAlertRequest(
    string? Type = null,
    string? Severity = null,
    string Title = "",
    string? Message = null,
    string? Data = null);
public sealed record BulkAlertActionRequest(Guid[] AlertIds);
public sealed record UpdateSystemSettingsRequest(SystemSettingEntry[] Settings);
public sealed record SystemSettingEntry(string Key, string Value);
public sealed record ModerationReviewRequest(string Action, string? Notes);
public sealed record ReportContentRequest(string ContentType, Guid ContentId, string? FieldName, string? Reason);
public sealed record AddIpAllowlistRequest(string IpAddress, string? Label, string? ExpiresAt);
public sealed record UpdateIpAllowlistRequest(string? Label, bool? IsActive, string? ExpiresAt);

// ── Phase 12: Scheduled Reports ───────────────────────────────────────────────
public sealed record CreateReportScheduleRequest(
    string Name,
    string ReportType,
    string Frequency,
    int? DayOfWeek,
    int? DayOfMonth,
    string TimeOfDay,
    string[] Recipients,
    string? Format,
    object? Filters);

public sealed record UpdateReportScheduleRequest(
    string? Name,
    bool? IsActive,
    string[]? Recipients,
    string? Format);

// ── Phase 13: GDPR / Compliance ───────────────────────────────────────────────
public sealed record ProcessGdprRequest(string Action, string? Notes);
public sealed record SubmitGdprRequest(string RequestType);
public sealed record RecordConsentRequest(string ConsentType, bool Granted, string? Version);

// ── Phase 14: Anomaly Detection ───────────────────────────────────────────────
public sealed record UpdateAnomalyRuleRequest(
    int? ThresholdCount,
    int? WindowMinutes,
    string? Severity,
    bool? IsActive,
    int? CooldownMinutes);

public sealed record ResolveAnomalyRequest(string? Notes);

/// <summary>Internal projection used only by the anomaly scan loop.</summary>
internal sealed record AnomalyRuleRow(
    Guid Id,
    string Metric,
    string Name,
    int ThresholdCount,
    int WindowMinutes,
    string Severity,
    int CooldownMinutes,
    DateTime? LastTriggeredAt);
