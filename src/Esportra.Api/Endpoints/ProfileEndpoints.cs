using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using Esportra.Api.Helpers;
using Esportra.Api.Middleware;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Supabase;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain 2: Profiles &amp; Connected Accounts
/// GET  /api/profiles/{id}         — fetch profile
/// PUT  /api/profiles/{id}         — update own profile
/// GET  /api/profiles/{id}/stats   — statistics + achievements
/// GET  /api/profiles/me           — authenticated user's own profile (alias)
/// </summary>
public static class ProfileEndpoints
{
    private static readonly string[] AllowedUpdateFields =
    [
        "username", "full_name", "avatar_url", "avatar_seed", "avatar_style", "bio",
        "riot_tag", "steam_tag", "social_links",
        "card_image_url", "country_code", "banner_url",
        "date_of_birth"
    ];

    public static void MapProfileEndpoints(this WebApplication app)
    {
        // ── GET /api/profiles/me ──────────────────────────────────────────────
        app.MapGet("/api/profiles/me", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            return await GetProfileResult(userCtx.UserIdGuid, db, cache, ct, includePrivateFields: true);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id} ────────────────────────────────────────────
        app.MapGet("/api/profiles/{id}", async (
            Guid id,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            return await GetProfileResult(id, db, cache, ct, includePrivateFields: false);
        });

        // ── PUT /api/profiles/{id} ────────────────────────────────────────────
        app.MapPut("/api/profiles/{id}", async (
            Guid id,
            [FromBody] Dictionary<string, object?> updates,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Users can only update their own profile; admins can update any
            if (userCtx.UserIdGuid != id && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            // Filter to allowed fields only; tag fields and clearable media fields may be explicitly null
            var tagFields = new HashSet<string> { "riot_tag", "steam_tag" };
            var clearableFields = new HashSet<string> { "riot_tag", "steam_tag", "banner_url", "card_image_url", "avatar_url" };
            var valid = updates
                .Where(kv => AllowedUpdateFields.Contains(kv.Key) && (kv.Value is not null || clearableFields.Contains(kv.Key)))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Normalize empty tag fields to null (DB has unique partial index on non-empty values)
            NormalizeTagFields(valid, tagFields);

            if (valid.Count == 0)
                return Results.BadRequest(new { error = "No valid fields to update." });

            var fieldError = ValidateProfileUpdateFields(valid);
            if (fieldError is not null) return fieldError;

            using var conn = db.CreateConnection();

            // Username uniqueness check
            if (valid.TryGetValue("username", out var newUsername) && newUsername is string uname)
            {
                var taken = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT id FROM profiles WHERE username = @uname AND id != @id LIMIT 1",
                    new { uname, id });
                if (taken is not null)
                    return Results.Conflict(new { error = "Username already taken." });
            }

            var setClauses = BuildProfileUpdateSql(valid.Keys);
            var parameters = BuildProfileUpdateParameters(valid, id);
            var (row, updateError) = await ExecuteProfileUpdateAsync(conn, setClauses, parameters);
            if (updateError is not null) return updateError;

            if (row is null) return Results.NotFound();

            // If the user switched to a photo upload (avatar_url set, avatar_seed cleared),
            // release any pool claim so they can claim a different avatar in future.
            var isPhotoSwitch =
                valid.TryGetValue("avatar_url", out var newUrl) && !string.IsNullOrWhiteSpace(newUrl as string) &&
                valid.TryGetValue("avatar_seed", out var newSeed) && string.IsNullOrWhiteSpace(newSeed as string);

            if (isPhotoSwitch)
            {
                await conn.ExecuteAsync(
                    "UPDATE avatar_pool SET claimed_by = NULL, claimed_at = NULL WHERE claimed_by = @id",
                    new { id });
            }

            await InvalidateProfileCacheAsync(cache, id, (string?)row?.username, ct);

            var normalized = ProfileResponseNormalizer.ToDictionary(row);
            return Results.Ok(normalized ?? (object)row!);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/by-username/{username} ─────────────────────────
        app.MapGet("/api/profiles/by-username/{username}", async (
            string username,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var profileJson = await cache.GetOrCreateAsync<string?>(
                $"profile-by-username:{username}",
                async token =>
                {
                    using var conn = db.CreateConnection();
                    var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        """
                        SELECT id, username, full_name, avatar_url, avatar_seed, avatar_style,
                               bio, location, social_links, country_code, card_image_url, banner_url,
                               riot_tag, steam_tag, created_at,
                               (settings->>'banner_focal_y')::float AS banner_focal_y
                        FROM profiles WHERE username = @username
                        """,
                        new { username });
                    if (row is null) return null;
                    var normalized = ProfileResponseNormalizer.ToDictionary(row);
                    return JsonSerializer.Serialize(normalized ?? (object)row);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                cancellationToken: ct);

            return profileJson is null ? Results.NotFound() : Results.Content(profileJson, "application/json");
        });

        // ── GET /api/profiles/search ─────────────────────────────────────────
        app.MapGet("/api/profiles/search", async (
            [FromQuery] string? q,
            [FromQuery] string? email,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();

            var conditions = new List<string>();
            var p = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(q))
            {
                conditions.Add("(username ILIKE '%' || @q || '%' OR email ILIKE '%' || @q || '%')");
                p.Add("q", q);
            }
            if (!string.IsNullOrWhiteSpace(email))
            {
                conditions.Add("LOWER(email) = LOWER(@email)");
                p.Add("email", email.Trim());
            }

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var rows = await conn.QueryAsync<dynamic>(
                $"""
                SELECT id, username, email, full_name, avatar_url
                FROM profiles
                {where}
                ORDER BY username
                LIMIT 50
                """, p);
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id}/licenses ────────────────────────────────────
        app.MapGet("/api/profiles/{id}/licenses", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, license_id, license_type, status, issued_at, expires_at,
                       notes, created_at
                FROM licenses
                WHERE user_id = @id
                ORDER BY issued_at DESC
                """,
                new { id });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/me/licenses ─────────────────────────────────────
        app.MapGet("/api/profiles/me/licenses", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, license_id, license_type, status, issued_at, expires_at,
                       notes, created_at
                FROM licenses
                WHERE user_id = @userId
                ORDER BY issued_at DESC
                """,
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/achievements ───────────────────────────────────────────
        // Achievements table not yet migrated — return empty array gracefully
        app.MapGet("/api/achievements", async (
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            try
            {
                var rows = await conn.QueryAsync<dynamic>(
                    "SELECT id, key, name, description, icon_url, points, created_at FROM achievements ORDER BY points ASC");
                return Results.Ok(rows);
            }
            catch { return Results.Ok(Array.Empty<object>()); }
        });

        // ── POST /api/profiles/me/achievements/{achievementId} ──────────────
        app.MapPost("/api/profiles/me/achievements/{achievementId}", async (
            Guid achievementId,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                INSERT INTO user_achievements (user_id, achievement_id)
                VALUES (@userId, @achievementId)
                ON CONFLICT (user_id, achievement_id) DO NOTHING
                RETURNING user_id, achievement_id, earned_at
                """,
                new { userId = userCtx.UserIdGuid, achievementId });

            if (row is not null)
                await cache.RemoveAsync($"profile-stats:{userCtx.UserId}", ct);

            return row is not null
                ? Results.Ok(row)
                : Results.Ok(new { alreadyAwarded = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles ─────────────────────────────────────────────────
        // Paginated list of profiles with optional search
        app.MapGet("/api/profiles", async (
            string? q,
            string? ids,
            string? game,
            int page = 1,
            int limit = 50,
            IDbConnectionFactory db = default!,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            using var conn = db.CreateConnection();

            // Bulk fetch by IDs
            if (!string.IsNullOrWhiteSpace(ids))
            {
                var idStrings = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => Guid.TryParse(s, out _))
                    .Distinct()
                    .ToArray();
                if (idStrings.Length == 0) return Results.Ok(Array.Empty<object>());

                // Build parameterized IN clause: WHERE id IN (@p0, @p1, ...)
                var paramNames = string.Join(", ", idStrings.Select((_, i) => $"@p{i}::uuid"));
                var parameters = new Dapper.DynamicParameters();
                for (int i = 0; i < idStrings.Length; i++)
                    parameters.Add($"p{i}", Guid.Parse(idStrings[i]));

                var byIds = await conn.QueryAsync<dynamic>(
                    $"""
                    SELECT id, username, full_name, avatar_url, bio,
                           riot_tag, steam_tag, country_code
                    FROM profiles
                    WHERE id IN ({paramNames})
                    """,
                    parameters);
                return Results.Ok(byIds);
            }

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, username, full_name, avatar_url, bio,
                       riot_tag, steam_tag, country_code
                FROM profiles
                WHERE (@q IS NULL OR username ILIKE '%' || @q || '%'
                                  OR full_name ILIKE '%' || @q || '%')
                ORDER BY username ASC
                LIMIT @limit OFFSET @offset
                """,
                new { q, limit, offset = (page - 1) * limit });
            return Results.Ok(rows);
        });

        // ── GET /api/profiles/riot-accounts ───────────────────────────────────
        app.MapGet("/api/profiles/riot-accounts", async (
            string? userIds,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                using var conn = db.CreateConnection();

                if (!string.IsNullOrWhiteSpace(userIds))
                {
                    var idList = userIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(s => Guid.TryParse(s, out _))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(Guid.Parse)
                        .ToArray();
                    if (idList.Length == 0) return Results.Ok(Array.Empty<object>());
                    var accounts = await conn.QueryAsync<dynamic>(
                        """
                        SELECT ra.user_id, ra.game_name, ra.tag_line, ra.region, ra.updated_at
                        FROM riot_accounts ra
                        JOIN profiles p ON p.id = ra.user_id
                        WHERE ra.user_id = ANY(@ids)
                          AND COALESCE((p.privacy_settings->>'show_riot_account')::boolean, true) = true
                        ORDER BY ra.updated_at DESC
                        """,
                        new { ids = idList });
                    return Results.Ok(accounts);
                }

                var myAccounts = await conn.QueryAsync<dynamic>(
                    "SELECT * FROM riot_accounts WHERE user_id = @userId ORDER BY updated_at DESC",
                    new { userId = userCtx.UserIdGuid });
                return Results.Ok(myAccounts);
            }
            catch (Exception)
            {
                return Results.Ok(Array.Empty<object>());
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/me/verification ─────────────────────────────────
        app.MapGet("/api/profiles/me/verification", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var verifiedRoles = await conn.QueryAsync<dynamic>(
                "SELECT role, status, is_active, verified_at FROM verified_roles WHERE user_id = @userId",
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(new { verifiedRoles });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/me/verification-requests ────────────────────────
        app.MapGet("/api/profiles/me/verification-requests", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var requests = await conn.QueryAsync<dynamic>(
                """
                SELECT requested_role, status, business_name, business_type,
                       created_at, reviewed_at, rejection_reason, verification_notes
                FROM verification_requests
                WHERE user_id = @userId
                ORDER BY created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(requests);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/profiles/me/verification-requests ───────────────────────
        app.MapPost("/api/profiles/me/verification-requests", async (
            [FromBody] VerificationRequestBody req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService email,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var role = req.EffectiveRole;
            if (string.IsNullOrWhiteSpace(role))
                return Results.BadRequest("Role is required");

            using var conn = db.CreateConnection();

            // Insert into verification_requests (the table admin reads from)
            var organizerJson = req.Organizer_Data is not null
                ? System.Text.Json.JsonSerializer.Serialize(req.Organizer_Data)
                : null;
            var venueJson = req.Venue_Data is not null
                ? System.Text.Json.JsonSerializer.Serialize(req.Venue_Data)
                : null;

            var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT email, username FROM profiles WHERE id = @id",
                new { id = userCtx.UserIdGuid });

            await conn.ExecuteAsync(
                """
                INSERT INTO verification_requests
                    (user_id, requested_role, first_name, last_name, email,
                     date_of_birth, business_name, business_type, business_description,
                     experience_description, cnic_front_url, cnic_back_url,
                     website_url, phone, organizer_data, venue_data, status)
                VALUES
                    (@userId, @role::app_role, @firstName, @lastName, @email,
                     @dob::date, @businessName, @businessType, @businessDesc,
                     @experienceDesc, @cnicFront, @cnicBack,
                     @website, @phone, @organizerData::jsonb, @venueData::jsonb, 'pending')
                ON CONFLICT (user_id, requested_role) WHERE status = 'pending'
                DO UPDATE SET
                    first_name = EXCLUDED.first_name,
                    last_name = EXCLUDED.last_name,
                    business_name = EXCLUDED.business_name,
                    business_description = EXCLUDED.business_description,
                    cnic_front_url = EXCLUDED.cnic_front_url,
                    cnic_back_url = EXCLUDED.cnic_back_url,
                    organizer_data = EXCLUDED.organizer_data,
                    venue_data = EXCLUDED.venue_data,
                    updated_at = NOW()
                """,
                new
                {
                    userId = userCtx.UserIdGuid,
                    role,
                    firstName = req.First_Name ?? "",
                    lastName = req.Last_Name ?? "",
                    email = req.Email ?? (string?)profile?.email ?? "",
                    dob = req.Date_Of_Birth ?? "2000-01-01",
                    businessName = req.Business_Name ?? "",
                    businessType = req.Business_Type ?? "",
                    businessDesc = req.Business_Description ?? "N/A",
                    experienceDesc = req.Experience_Description ?? "N/A",
                    cnicFront = req.Cnic_Front_Url ?? "",
                    cnicBack = req.Cnic_Back_Url ?? "",
                    website = req.Website,
                    phone = req.Contact_Phone,
                    organizerData = organizerJson,
                    venueData = venueJson,
                });

            // Email: application submitted & under review
            try
            {
                var userEmail = req.Email ?? (string?)profile?.email;
                if (userEmail is not null)
                {
                    var frontendUrl = config["FrontendUrl"] ?? "https://esportra.com";
                    await email.SendAsync(
                        userEmail,
                        EmailType.LicenseApplicationReceived,
                        new
                        {
                            username = (string?)profile?.username ?? req.First_Name ?? "there",
                            licenseType = role,
                            dashboardUrl = $"{frontendUrl}/verification",
                        },
                        ct);
                }
            }
            catch { /* email failure should not block submission */ }

            return Results.Ok(new { success = true, status = "pending" });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/profiles/me/verification-requests/organizer ─────────────
        app.MapPost("/api/profiles/me/verification-requests/organizer", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService email,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO verified_roles (user_id, role, status, is_active)
                VALUES (@userId, 'organizer', 'pending', FALSE)
                ON CONFLICT (user_id, role) DO UPDATE SET status = 'pending'
                """,
                new { userId = userCtx.UserIdGuid });

            try
            {
                var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT email, username FROM profiles WHERE id = @id",
                    new { id = userCtx.UserIdGuid });
                if (profile?.email is not null)
                {
                    var frontendUrl = config["FrontendUrl"] ?? "https://esportra.com";
                    await email.SendAsync(
                        (string)profile.email,
                        EmailType.LicenseApplicationReceived,
                        new
                        {
                            username = (string?)profile.username ?? "there",
                            licenseType = "organizer",
                            dashboardUrl = $"{frontendUrl}/verification-status",
                        },
                        ct);
                }
            }
            catch { /* email failure should not block application */ }

            return Results.Ok(new { success = true, status = "pending", role = "organizer" });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/profiles/me/verification-requests/venue_owner ───────────
        app.MapPost("/api/profiles/me/verification-requests/venue_owner", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService email,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO verified_roles (user_id, role, status, is_active)
                VALUES (@userId, 'venue_owner', 'pending', FALSE)
                ON CONFLICT (user_id, role) DO UPDATE SET status = 'pending'
                """,
                new { userId = userCtx.UserIdGuid });

            try
            {
                var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT email, username FROM profiles WHERE id = @id",
                    new { id = userCtx.UserIdGuid });
                if (profile?.email is not null)
                {
                    var frontendUrl = config["FrontendUrl"] ?? "https://esportra.com";
                    await email.SendAsync(
                        (string)profile.email,
                        EmailType.LicenseApplicationReceived,
                        new
                        {
                            username = (string?)profile.username ?? "there",
                            licenseType = "venue_owner",
                            dashboardUrl = $"{frontendUrl}/verification-status",
                        },
                        ct);
                }
            }
            catch { /* email failure should not block application */ }

            return Results.Ok(new { success = true, status = "pending", role = "venue_owner" });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id}/stats ──────────────────────────────────────
        // user_statistics and achievements tables not yet created — graceful fallback
        app.MapGet("/api/profiles/{id}/stats", async (
            Guid id,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var statsJson = await cache.GetOrCreateAsync<string?>(
                $"profile-stats:{id}",
                async token =>
                {
                    using var conn = db.CreateConnection();

                    dynamic? stats = null;
                    try
                    {
                        stats = await conn.QuerySingleOrDefaultAsync<dynamic>(
                            "SELECT user_id, tournaments_entered, tournaments_won, best_placement, games_played, updated_at FROM user_statistics WHERE user_id = @id",
                            new { id });
                    }
                    catch { /* table not yet migrated */ }

                    // Live fallback: if no precomputed row, aggregate from tournament data
                    if (stats is null)
                    {
                        try
                        {
                            stats = await conn.QuerySingleOrDefaultAsync<dynamic>(
                                """
                                WITH user_tps AS (
                                    SELECT tp.id AS participant_id, tp.tournament_id, tp.team_id, tp.status
                                    FROM tournament_participants tp WHERE tp.user_id = @id
                                    UNION
                                    SELECT tp.id AS participant_id, tp.tournament_id, tp.team_id, tp.status
                                    FROM tournament_participants tp
                                    JOIN team_members mem ON mem.team_id = tp.team_id WHERE mem.user_id = @id
                                ),
                                prize_by_currency AS (
                                    SELECT t.currency, COALESCE(SUM(tpl.prize_amount), 0) AS total
                                    FROM user_tps tp
                                    JOIN tournaments t ON t.id = tp.tournament_id
                                    LEFT JOIN tournament_placements tpl
                                        ON tpl.tournament_id = tp.tournament_id
                                        AND (
                                            (tp.team_id IS NOT NULL AND tpl.team_id = tp.team_id)
                                            OR (tp.team_id IS NULL  AND tpl.team_id = tp.participant_id)
                                        )
                                    WHERE tp.status != 'disqualified' AND t.status != 'cancelled'
                                      AND t.currency IS NOT NULL AND tpl.prize_amount > 0
                                    GROUP BY t.currency
                                )
                                SELECT
                                    (SELECT COUNT(DISTINCT tp.tournament_id)::int
                                     FROM user_tps tp JOIN tournaments t ON t.id = tp.tournament_id
                                     WHERE tp.status != 'disqualified' AND t.status != 'cancelled') AS tournaments_entered,
                                    (SELECT COUNT(DISTINCT CASE WHEN tpl.placement = 1 THEN tp.tournament_id END)::int
                                     FROM user_tps tp JOIN tournaments t ON t.id = tp.tournament_id
                                     LEFT JOIN tournament_placements tpl ON tpl.tournament_id = tp.tournament_id
                                         AND ((tp.team_id IS NOT NULL AND tpl.team_id = tp.team_id) OR (tp.team_id IS NULL AND tpl.team_id = tp.participant_id))
                                     WHERE tp.status != 'disqualified' AND t.status != 'cancelled') AS tournaments_won,
                                    (SELECT MIN(tpl.placement)::int
                                     FROM user_tps tp JOIN tournaments t ON t.id = tp.tournament_id
                                     LEFT JOIN tournament_placements tpl ON tpl.tournament_id = tp.tournament_id
                                         AND ((tp.team_id IS NOT NULL AND tpl.team_id = tp.team_id) OR (tp.team_id IS NULL AND tpl.team_id = tp.participant_id))
                                     WHERE tp.status != 'disqualified' AND t.status != 'cancelled') AS best_placement,
                                    COALESCE(
                                        (SELECT jsonb_object_agg(currency, total) FROM prize_by_currency),
                                        '{}'::jsonb
                                    ) AS prize_by_currency,
                                    (SELECT COUNT(DISTINCT bm.id)::int
                                     FROM brkt_matches bm
                                     JOIN (
                                         SELECT DISTINCT team_id AS competitor_id
                                         FROM team_members WHERE user_id = @id
                                         UNION
                                         SELECT id AS competitor_id
                                         FROM tournament_participants WHERE user_id = @id
                                     ) mc ON bm.team1_id = mc.competitor_id OR bm.team2_id = mc.competitor_id
                                     WHERE bm.status = 'completed') AS games_played
                                """,
                                new { id });
                        }
                        catch { /* aggregate query failed */ }
                    }

                    // Placement-based achievements — top 8 finishes from tournament history
                    IEnumerable<dynamic> placementAchievements = Array.Empty<dynamic>();
                    try
                    {
                        placementAchievements = await conn.QueryAsync<dynamic>(
                            """
                            WITH user_tps AS (
                                SELECT tp.id AS participant_id, tp.tournament_id, tp.team_id, tp.status
                                FROM tournament_participants tp
                                WHERE tp.user_id = @id
                                UNION
                                SELECT tp.id AS participant_id, tp.tournament_id, tp.team_id, tp.status
                                FROM tournament_participants tp
                                JOIN team_members mem ON mem.team_id = tp.team_id
                                WHERE mem.user_id = @id
                            )
                            SELECT DISTINCT ON (t.id)
                                t.id            AS tournament_id,
                                t.name          AS tournament_name,
                                t.slug          AS tournament_slug,
                                t.game,
                                t.start_date,
                                tpl.placement,
                                tm.name         AS team_name,
                                tm.logo_url     AS team_logo_url
                            FROM user_tps tp
                            JOIN tournaments t ON t.id = tp.tournament_id
                            JOIN tournament_placements tpl
                                ON tpl.tournament_id = t.id
                                AND (
                                    (tp.team_id IS NOT NULL AND tpl.team_id = tp.team_id)
                                    OR (tp.team_id IS NULL  AND tpl.team_id = tp.participant_id)
                                )
                            LEFT JOIN teams tm ON tm.id = tp.team_id
                            WHERE tp.status != 'disqualified'
                              AND t.status != 'cancelled'
                              AND tpl.placement IS NOT NULL
                              AND tpl.placement <= 8
                            ORDER BY t.id, tpl.placement ASC, t.start_date DESC
                            """, new { id });
                    }
                    catch { /* aggregate query failed */ }

                    IEnumerable<dynamic> achievements = Array.Empty<dynamic>();
                    try
                    {
                        achievements = await conn.QueryAsync<dynamic>(
                            """
                            SELECT ua.earned_at,
                                   a.id AS achievement_id, a.name, a.description,
                                   a.icon_url, a.points
                            FROM user_achievements ua
                            JOIN achievements a ON a.id = ua.achievement_id
                            WHERE ua.user_id = @id
                            ORDER BY ua.earned_at DESC
                            """, new { id });
                    }
                    catch { /* tables not yet migrated */ }

                    string? verifiedRole = null;
                    try
                    {
                        verifiedRole = await conn.QuerySingleOrDefaultAsync<string>(
                            "SELECT role FROM verified_roles WHERE user_id = @id AND is_active = TRUE LIMIT 1",
                            new { id });
                    }
                    catch { /* table not yet migrated */ }

                    long achievementsCount = 0;
                    try
                    {
                        achievementsCount = await conn.ExecuteScalarAsync<long>(
                            "SELECT COUNT(*) FROM user_achievements WHERE user_id = @id",
                            new { id });
                    }
                    catch { /* table not yet migrated */ }

                    var response = new Dictionary<string, object?>
                    {
                        ["statistics"] = stats is null ? null : (object?)(ProfileResponseNormalizer.ToDictionary(stats) ?? new Dictionary<string, object?>()),
                        ["achievements"] = achievements
                            .Select(a => ProfileResponseNormalizer.ToDictionary(a) ?? new Dictionary<string, object?>())
                            .ToList(),
                        ["placement_achievements"] = placementAchievements
                            .Select(a => ProfileResponseNormalizer.ToDictionary(a) ?? new Dictionary<string, object?>())
                            .ToList(),
                        ["verified_role"] = verifiedRole,
                        ["achievements_count"] = achievementsCount,
                    };
                    return JsonSerializer.Serialize(response);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                cancellationToken: ct);

            return statsJson is null
                ? Results.Ok(new { statistics = (object?)null, achievements = Array.Empty<object>(), verified_role = (string?)null, achievements_count = 0 })
                : Results.Content(statsJson, "application/json");
        });

        // ── GET /api/profiles/{id}/tournament-history ─────────────────────────
        app.MapGet("/api/profiles/{id}/tournament-history", async (
            Guid id,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct,
            int page = 1,
            string? game = null) =>
        {
            page = Math.Max(1, page);
            var cacheKey = $"profile-history:{id}:{page}:{game ?? "all"}";
            var historyJson = await cache.GetOrCreateAsync<string>(
                cacheKey,
                async token =>
                {
                    using var conn = db.CreateConnection();
                    var offset = (page - 1) * 20;

                    var rows = await conn.QueryAsync<dynamic>(
                        """
                        WITH user_tps AS (
                            SELECT tp.id AS participant_id, tp.tournament_id, tp.team_id, tp.status
                            FROM tournament_participants tp
                            WHERE tp.user_id = @userId
                            UNION
                            SELECT tp.id AS participant_id, tp.tournament_id, tp.team_id, tp.status
                            FROM tournament_participants tp
                            JOIN team_members mem ON mem.team_id = tp.team_id
                            WHERE mem.user_id = @userId
                        )
                        SELECT * FROM (
                            SELECT DISTINCT ON (t.id)
                                t.id AS tournament_id,
                                t.name AS tournament_name,
                                t.game,
                                t.format,
                                t.start_date,
                                t.status AS tournament_status,
                                t.currency,
                                tpl.placement,
                                tpl.prize_amount AS prize_amount,
                                (tp.team_id IS NOT NULL) AS is_team_tournament,
                                tm.name AS team_name,
                                tm.logo_url AS team_logo_url
                            FROM user_tps tp
                            JOIN tournaments t ON t.id = tp.tournament_id
                            LEFT JOIN tournament_placements tpl
                                ON tpl.tournament_id = t.id
                                AND (
                                    (tp.team_id IS NOT NULL AND tpl.team_id = tp.team_id)
                                    OR (tp.team_id IS NULL  AND tpl.team_id = tp.participant_id)
                                )
                            LEFT JOIN teams tm ON tm.id = tp.team_id
                            WHERE tp.status != 'disqualified'
                              AND t.status != 'cancelled'
                              AND (@game IS NULL OR t.game = @game)
                            ORDER BY t.id
                        ) deduped
                        ORDER BY start_date DESC NULLS LAST
                        LIMIT 20 OFFSET @offset
                        """,
                        new { userId = id, game, offset });

                    var count = await conn.ExecuteScalarAsync<long>(
                        """
                        WITH user_tps AS (
                            SELECT tp.tournament_id, tp.status
                            FROM tournament_participants tp
                            WHERE tp.user_id = @userId
                            UNION
                            SELECT tp.tournament_id, tp.status
                            FROM tournament_participants tp
                            JOIN team_members mem ON mem.team_id = tp.team_id
                            WHERE mem.user_id = @userId
                        )
                        SELECT COUNT(DISTINCT tp.tournament_id)
                        FROM user_tps tp
                        JOIN tournaments t ON t.id = tp.tournament_id
                        WHERE tp.status != 'disqualified'
                          AND t.status != 'cancelled'
                          AND (@game IS NULL OR t.game = @game)
                        """,
                        new { userId = id, game });

                    var items = rows
                        .Select(r => ProfileResponseNormalizer.ToDictionary(r) ?? new Dictionary<string, object?>())
                        .ToList();
                    var response = new Dictionary<string, object?>
                    {
                        ["items"] = items,
                        ["page"] = page,
                        ["pageSize"] = 20,
                        ["hasMore"] = count > (long)page * 20,
                    };
                    return JsonSerializer.Serialize(response);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                cancellationToken: ct);

            return Results.Content(historyJson, "application/json");
        }).WithMetadata(new RateLimitPolicyMetadata("public"));

        // ── GET /api/profiles/{id}/match-history ──────────────────────────────────
        app.MapGet("/api/profiles/{id}/match-history", async (
            Guid id,
            IDbConnectionFactory db,
            int page = 1,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            var offset = (page - 1) * 20;
            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                WITH my_competitors AS (
                    SELECT DISTINCT team_id AS competitor_id
                    FROM team_members
                    WHERE user_id = @id
                    UNION
                    SELECT id AS competitor_id
                    FROM tournament_participants
                    WHERE user_id = @id
                )
                SELECT * FROM (
                    SELECT DISTINCT ON (bm.id)
                        bm.id AS match_id,
                        bm.round_index, bm.bracket_type,
                        bm.team1_score, bm.team2_score, bm.winner_id, bm.is_walkover,
                        mc.competitor_id AS my_competitor_id,
                        CASE WHEN bm.team1_id = mc.competitor_id THEN bm.team1_score ELSE bm.team2_score END AS our_score,
                        CASE WHEN bm.team1_id = mc.competitor_id THEN bm.team2_score ELSE bm.team1_score END AS opp_score,
                        CASE WHEN bm.winner_id = mc.competitor_id THEN 'win'
                             WHEN bm.winner_id IS NOT NULL THEN 'loss'
                             ELSE 'draw' END AS result,
                        COALESCE(opp_t.name, opp_tp.team_name, 'TBD') AS opponent_name,
                        opp_t.logo_url AS opponent_logo_url,
                        COALESCE(my_t.name, my_p.username) AS my_team_name,
                        my_t.logo_url AS my_team_logo_url,
                        t.id AS tournament_id,
                        t.slug AS tournament_slug,
                        t.name AS tournament_name,
                        t.game,
                        COALESCE(bm.updated_at, bm.scheduled_time) AS match_date
                    FROM brkt_matches bm
                    JOIN my_competitors mc ON bm.team1_id = mc.competitor_id OR bm.team2_id = mc.competitor_id
                    JOIN brkt_versions bv ON bv.id = bm.version_id
                    JOIN tournament_stages ts ON ts.id = bv.stage_id
                    JOIN tournaments t ON t.id = ts.tournament_id
                    LEFT JOIN teams my_t ON my_t.id = mc.competitor_id
                    LEFT JOIN tournament_participants my_tp ON my_tp.id = mc.competitor_id AND my_t.id IS NULL
                    LEFT JOIN profiles my_p ON my_p.id = my_tp.user_id
                    LEFT JOIN teams opp_t ON opp_t.id = CASE WHEN bm.team1_id = mc.competitor_id THEN bm.team2_id ELSE bm.team1_id END
                    LEFT JOIN tournament_participants opp_tp
                        ON opp_tp.id = CASE WHEN bm.team1_id = mc.competitor_id THEN bm.team2_id ELSE bm.team1_id END
                        AND opp_t.id IS NULL
                    WHERE bm.status = 'completed'
                    ORDER BY bm.id
                ) deduped
                ORDER BY match_date DESC NULLS LAST
                LIMIT 20 OFFSET @offset
                """,
                new { id, offset });

            var count = await conn.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(DISTINCT bm.id)
                FROM brkt_matches bm
                JOIN (
                    SELECT team_id AS competitor_id FROM team_members WHERE user_id = @id
                    UNION
                    SELECT id FROM tournament_participants WHERE user_id = @id
                ) mc ON bm.team1_id = mc.competitor_id OR bm.team2_id = mc.competitor_id
                WHERE bm.status = 'completed'
                """,
                new { id });

            return Results.Ok(new { items = rows.ToList(), page, pageSize = 20, total = count });
        }).WithMetadata(new RateLimitPolicyMetadata("public"));

        // ── GET /api/profiles/{id}/teams ──────────────────────────────────────
        app.MapGet("/api/profiles/{id}/teams", async (
            Guid id,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var teamsJson = await cache.GetOrCreateAsync<string>(
                $"profile-teams:{id}",
                async token =>
                {
                    using var conn = db.CreateConnection();
                    var rows = await conn.QueryAsync<dynamic>(
                        """
                        SELECT
                            tm.id AS membership_id,
                            tm.team_id,
                            tm.role,
                            tm.joined_at,
                            tm.is_active,
                            t.name AS team_name,
                            t.logo_url AS team_logo_url,
                            t.game AS team_game
                        FROM team_members tm
                        JOIN teams t ON t.id = tm.team_id
                        WHERE tm.user_id = @userId
                        ORDER BY tm.is_active DESC, tm.joined_at DESC
                        """,
                        new { userId = id });

                    var items = rows
                        .Select(r => ProfileResponseNormalizer.ToDictionary(r) ?? new Dictionary<string, object?>())
                        .ToList();
                    return JsonSerializer.Serialize(items);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) },
                cancellationToken: ct);

            return Results.Content(teamsJson, "application/json");
        }).WithMetadata(new RateLimitPolicyMetadata("public"));

        // ── PUT /api/profiles/me/privacy ─────────────────────────────────────
        app.MapPut("/api/profiles/me/privacy", async (
            [FromBody] UpdatePrivacySettingsRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE profiles
                SET privacy_settings = COALESCE(privacy_settings, '{}'::jsonb)
                    || jsonb_build_object(
                        'show_riot_account', @showRiotAccount::boolean,
                        'show_steam_account', @showSteamAccount::boolean
                    )
                WHERE id = @userId
                """,
                new
                {
                    userId = userCtx.UserIdGuid,
                    showRiotAccount = req.ShowRiotAccount,
                    showSteamAccount = req.ShowSteamAccount,
                });

            await cache.RemoveAsync($"profile-linked:{userCtx.UserIdGuid}", ct);
            return Results.Ok(new
            {
                success = true,
                show_riot_account = req.ShowRiotAccount,
                show_steam_account = req.ShowSteamAccount,
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id}/linked-accounts ────────────────────────────
        app.MapGet("/api/profiles/{id}/linked-accounts", async (
            Guid id,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var linkedJson = await cache.GetOrCreateAsync<string>(
                $"profile-linked:{id}",
                async token =>
                {
                    using var conn = db.CreateConnection();

                    var profileMeta = await conn.QuerySingleOrDefaultAsync<(string? PrivacySettings, string? DiscordHandle)>(
                        "SELECT privacy_settings, social_links->>'discord_handle' AS discord_handle FROM profiles WHERE id = @id",
                        new { id });

                    var privacyStr = profileMeta.PrivacySettings;
                    var discordHandle = profileMeta.DiscordHandle;

                    bool showRiot = true;
                    bool showSteam = true;

                    if (!string.IsNullOrWhiteSpace(privacyStr) && privacyStr != "{}")
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(privacyStr);
                            if (doc.RootElement.TryGetProperty("show_riot_account", out var riotProp))
                                showRiot = riotProp.GetBoolean();
                            if (doc.RootElement.TryGetProperty("show_steam_account", out var steamProp))
                                showSteam = steamProp.GetBoolean();
                        }
                        catch { /* malformed JSON — use defaults */ }
                    }

                    dynamic? riotRow = null;
                    if (showRiot)
                    {
                        riotRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
                            "SELECT game_name, tag_line, region FROM riot_accounts WHERE user_id = @id ORDER BY updated_at DESC LIMIT 1",
                            new { id });
                    }

                    dynamic? steamRow = null;
                    if (showSteam)
                    {
                        // steam64_id is intentionally excluded from this query
                        steamRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
                            "SELECT steam_name, profile_url FROM player_steam_accounts WHERE user_id = @id ORDER BY linked_at DESC LIMIT 1",
                            new { id });
                    }

                    var response = new Dictionary<string, object?>
                    {
                        ["riot"] = riotRow is null ? null : (object?)(ProfileResponseNormalizer.ToDictionary(riotRow)),
                        ["steam"] = steamRow is null ? null : (object?)(ProfileResponseNormalizer.ToDictionary(steamRow)),
                        ["discord"] = string.IsNullOrWhiteSpace(discordHandle) ? null
                            : (object?)new Dictionary<string, string?> { ["handle"] = discordHandle },
                    };
                    return JsonSerializer.Serialize(response);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(120) },
                cancellationToken: ct);

            return Results.Content(linkedJson, "application/json");
        }).WithMetadata(new RateLimitPolicyMetadata("public"));
    }

    // ── PUT /api/profiles/{id} helpers ───────────────────────────────────────

    private static readonly string[] DangerousUriSchemes = ["javascript:", "data:", "vbscript:"];
    private const int MaxSocialLinkValueLength = 200;
    private static readonly HashSet<string> AllowedSocialLinkKeys =
        ["twitter", "twitch", "youtube", "instagram", "discord_handle"];

    private static void NormalizeTagFields(Dictionary<string, object?> valid, HashSet<string> tagFields)
    {
        foreach (var tagField in tagFields)
        {
            if (!valid.TryGetValue(tagField, out var v)) continue;
            if (v is null
                || (v is JsonElement je && (je.ValueKind == JsonValueKind.Null || je.GetString() is "" or null))
                || (v is string s && string.IsNullOrWhiteSpace(s)))
                valid[tagField] = null;
        }
    }

    /// <summary>
    /// Validates date_of_birth, country_code, and social_links fields in the update dict.
    /// Normalizes validated values in-place. Returns a 400 IResult on first failure, null on success.
    /// </summary>
    private static IResult? ValidateProfileUpdateFields(Dictionary<string, object?> valid)
    {
        if (valid.TryGetValue("date_of_birth", out var dobValue))
        {
            if (!ProfileFieldValidator.TryValidateDateOfBirth(dobValue, out var normalizedDob, out var dobError))
                return Results.BadRequest(new { error = dobError });
            valid["date_of_birth"] = normalizedDob;
        }

        if (valid.TryGetValue("country_code", out var countryValue))
        {
            if (!ProfileFieldValidator.TryValidateCountryCode(countryValue, out var normalizedCountry, out var countryError))
                return Results.BadRequest(new { error = countryError });
            valid["country_code"] = normalizedCountry;
        }

        if (valid.TryGetValue("social_links", out var socialLinksValue))
        {
            var socialError = ValidateSocialLinks(socialLinksValue, out _);
            if (socialError is not null) return socialError;
        }

        return null;
    }

    /// <summary>
    /// Validates social link JSONB keys against the allowlist and enforces value constraints:
    /// non-empty, ≤ 200 chars, no dangerous URI schemes (javascript:, data:, vbscript:).
    /// Returns a 400 IResult on the first violation, null on success.
    /// </summary>
    private static IResult? ValidateSocialLinks(object? socialLinksValue, out Dictionary<string, string> parsed)
    {
        parsed = new Dictionary<string, string>();
        if (socialLinksValue is not JsonElement slJson || slJson.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in slJson.EnumerateObject())
        {
            if (!AllowedSocialLinkKeys.Contains(prop.Name))
                return Results.BadRequest(new { error = $"Unknown social link key: '{prop.Name}'. Allowed: twitter, twitch, youtube, instagram, discord_handle." });

            var val = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
            if (string.IsNullOrWhiteSpace(val))
                return Results.BadRequest(new { error = $"Value for '{prop.Name}' must not be empty." });

            if (val.Length > MaxSocialLinkValueLength)
                return Results.BadRequest(new { error = $"Value for '{prop.Name}' must be 200 characters or fewer." });

            foreach (var scheme in DangerousUriSchemes)
                if (val.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = $"Value for '{prop.Name}' contains a disallowed URI scheme." });

            parsed[prop.Name] = val;
        }
        return null;
    }

    /// <summary>
    /// Builds the SET clause for an UPDATE statement.
    /// Field names are allow-listed by the caller; this method only picks the cast syntax.
    /// </summary>
    private static string BuildProfileUpdateSql(IEnumerable<string> fieldNames)
    {
        var jsonbFields = new HashSet<string> { "social_links" };
        var dateFields = new HashSet<string> { "date_of_birth" };
        return string.Join(", ", fieldNames.Select(k =>
        {
            if (jsonbFields.Contains(k)) return $"{k} = @{k}::jsonb";
            if (dateFields.Contains(k)) return $"{k} = @{k}::date";
            return $"{k} = @{k}";
        }));
    }

    private static DynamicParameters BuildProfileUpdateParameters(Dictionary<string, object?> valid, Guid id)
    {
        var jsonbFields = new HashSet<string> { "social_links" };
        var dateFields = new HashSet<string> { "date_of_birth" };
        var parameters = new DynamicParameters();
        foreach (var kv in valid)
        {
            if (kv.Value is null)
                parameters.Add(kv.Key, null, DbType.String);
            else if (jsonbFields.Contains(kv.Key) && kv.Value is JsonElement je)
                parameters.Add(kv.Key, je.GetRawText());
            else if (dateFields.Contains(kv.Key))
                parameters.Add(kv.Key, (kv.Value as string)?.Trim());
            else
                parameters.Add(kv.Key, kv.Value is JsonElement v ? v.ToString() : kv.Value);
        }
        parameters.Add("id", id);
        parameters.Add("updated_at", DateTime.UtcNow);
        return parameters;
    }

    private static async Task<(dynamic? Row, IResult? Error)> ExecuteProfileUpdateAsync(
        IDbConnection conn, string setClauses, DynamicParameters parameters)
    {
        try
        {
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                $"UPDATE profiles SET {setClauses}, updated_at = @updated_at WHERE id = @id RETURNING id, username, full_name, avatar_url, avatar_seed, avatar_style, bio, location, social_links, country_code, card_image_url, banner_url, riot_tag, steam_tag, date_of_birth, created_at, updated_at",
                parameters);
            return (row, null);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            var msg = ex.ConstraintName == "profiles_avatar_seed_style_unique"
                ? "This avatar is already claimed by another user."
                : "Username already taken.";
            return (null, Results.Conflict(new { error = msg }));
        }
        catch (PostgresException ex) when (ex.SqlState is "22007" or "22008")
        {
            return (null, Results.BadRequest(new { error = "Enter a valid date (YYYY-MM-DD)." }));
        }
        catch (PostgresException)
        {
            return (null, Results.BadRequest(new { error = "Profile update failed." }));
        }
    }

    private static async Task InvalidateProfileCacheAsync(
        HybridCache cache, Guid id, string? username, CancellationToken ct)
    {
        try
        {
            await cache.RemoveAsync($"profile:{id}", ct);
            if (!string.IsNullOrWhiteSpace(username))
                await cache.RemoveAsync($"profile-by-username:{username}", ct);
            await cache.RemoveAsync($"profile-linked:{id}", ct);
        }
        catch
        {
            // Best-effort cache invalidation should not fail profile updates.
        }
    }

    // ── Shared helper ─────────────────────────────────────────────────────────

    private static async Task<IResult> GetProfileResult(
        Guid id, IDbConnectionFactory db, HybridCache cache, CancellationToken ct, bool includePrivateFields)
    {
        using var conn = db.CreateConnection();
        var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
            includePrivateFields
                ? """
                  SELECT id, username, full_name, avatar_url, avatar_seed, avatar_style, bio, location,
                         social_links, country_code, card_image_url, banner_url,
                         riot_tag, steam_tag, role, base_role, is_admin, admin_roles,
                         is_suspended, suspension_until, suspension_reason, suspension_type,
                         date_of_birth, created_at, updated_at,
                         (country_code IS NOT NULL AND date_of_birth IS NOT NULL) AS profile_complete
                  FROM profiles
                  WHERE id = @id
                  """
                : """
                  SELECT id, username, full_name, avatar_url, avatar_seed, avatar_style, bio, location,
                         social_links, country_code, card_image_url, banner_url,
                         riot_tag, steam_tag, created_at, updated_at
                  FROM profiles
                  WHERE id = @id
                  """,
            new { id });

        if (profile is null) return Results.NotFound();

        var normalized = ProfileResponseNormalizer.ToDictionary(profile);
        return Results.Ok(normalized ?? profile);
    }

    // ── POST /api/profiles/resolve-players — batch resolve by tags/ids ───────
    // Replaces TournamentManage's 4 parallel supabase calls
    public static void MapProfileResolveEndpoint(this WebApplication app)
    {
        app.MapPost("/api/profiles/resolve-players", async (
            [FromBody] ResolvePlayersRequest req,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            if (req.AreUuids)
            {
                // Tokens are UUIDs — resolve by id
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, riot_tag, steam_tag, username, full_name
                    FROM profiles
                    WHERE id = ANY(@ids::uuid[])
                    """,
                    new { ids = req.Tokens.ToArray() });
                return Results.Ok(rows);
            }
            else
            {
                // Tokens are readable tags — search by riot_tag, steam_tag, username, full_name
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, riot_tag, steam_tag, username, full_name,
                           CASE
                             WHEN riot_tag  = ANY(@tokens) THEN 'riot_tag'
                             WHEN steam_tag = ANY(@tokens) THEN 'steam_tag'
                             WHEN username  = ANY(@tokens) THEN 'username'
                             WHEN full_name = ANY(@tokens) THEN 'full_name'
                           END AS matched_field
                    FROM profiles
                    WHERE riot_tag  = ANY(@tokens)
                       OR steam_tag = ANY(@tokens)
                       OR username  = ANY(@tokens)
                       OR full_name = ANY(@tokens)
                    """,
                    new { tokens = req.Tokens.ToArray() });
                return Results.Ok(rows);
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/rosters/{rosterId}/members — get roster members ─────────
        // Replaces supabase.rpc('get_roster_members')
        app.MapGet("/api/rosters/{rosterId}/members", async (
            Guid rosterId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT trm.user_id, trm.is_starter,
                       p.username, p.full_name, p.avatar_url, p.riot_tag, p.steam_tag
                FROM team_roster_members trm
                LEFT JOIN profiles p ON p.id = trm.user_id
                WHERE trm.roster_id = @rosterId
                ORDER BY trm.is_starter DESC, p.username ASC
                """,
                new { rosterId });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/profiles/me/discord-dm ──────────────────────────────────
        // Toggle Discord DM notifications on/off
        app.MapPut("/api/profiles/me/discord-dm", async (
            [FromBody] ToggleDiscordDmRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Check if user has Discord linked before enabling
            if (req.Enabled)
            {
                var hasDiscord = await conn.QuerySingleOrDefaultAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM auth.identities WHERE user_id = @userId AND provider = 'discord')",
                    new { userId = userCtx.UserIdGuid });

                if (!hasDiscord)
                    return Results.BadRequest(new { error = "Link your Discord account first." });
            }

            await conn.ExecuteAsync(
                """
                UPDATE profiles
                SET settings = COALESCE(settings, '{}'::jsonb) || jsonb_build_object('discord_dm_enabled', @enabled::boolean)
                WHERE id = @userId
                """,
                new { userId = userCtx.UserIdGuid, enabled = req.Enabled });

            return Results.Ok(new { success = true, discord_dm_enabled = req.Enabled });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/me/discord-dm ──────────────────────────────────
        app.MapGet("/api/profiles/me/discord-dm", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var result = await conn.QuerySingleOrDefaultAsync<(bool enabled, bool hasDiscord)>(
                """
                SELECT
                    COALESCE((p.settings->>'discord_dm_enabled')::boolean, TRUE) AS enabled,
                    EXISTS(SELECT 1 FROM auth.identities WHERE user_id = p.id AND provider = 'discord') AS has_discord
                FROM profiles p
                WHERE p.id = @userId
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(new { discord_dm_enabled = result.enabled, has_discord = result.hasDiscord });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/me/timezone ────────────────────────────────────
        app.MapGet("/api/profiles/me/timezone", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var tzIana = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT settings->>'timezone_iana' FROM profiles WHERE id = @userId",
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(new { timezone_iana = tzIana });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/profiles/me/timezone ─────────────────────────────────────
        app.MapPut("/api/profiles/me/timezone", async (
            [FromBody] SetTimezoneRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.TimezoneIana)
                || !TimeZoneInfo.TryFindSystemTimeZoneById(req.TimezoneIana, out _))
                return Results.BadRequest(new { error = "Invalid IANA timezone identifier." });

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE profiles
                SET settings = COALESCE(settings, '{}'::jsonb) || jsonb_build_object('timezone_iana', @tzIana)
                WHERE id = @userId
                """,
                new { userId = userCtx.UserIdGuid, tzIana = req.TimezoneIana });

            return Results.Ok(new { timezone_iana = req.TimezoneIana });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/profiles/me/country ──────────────────────────────────────
        app.MapPut("/api/profiles/me/country", async (
            [FromBody] SetCountryRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!ProfileFieldValidator.TryValidateCountryCode(req.CountryCode, out var normalized, out var error))
                return Results.BadRequest(new { error });

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE profiles SET country_code = @countryCode WHERE id = @userId",
                new { userId = userCtx.UserIdGuid, countryCode = normalized });

            return Results.Ok(new { country_code = normalized });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/profiles/me/banner-position ─────────────────────────────
        app.MapPut("/api/profiles/me/banner-position", async (
            [FromBody] SetBannerPositionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.FocalY is null or < 0 or > 100)
                return Results.BadRequest(new { error = "focal_y must be between 0 and 100." });

            using var conn = db.CreateConnection();
            var username = await conn.QuerySingleOrDefaultAsync<string?>(
                """
                UPDATE profiles
                SET settings = COALESCE(settings, '{}'::jsonb) || jsonb_build_object('banner_focal_y', @focalY::float)
                WHERE id = @userId
                RETURNING username
                """,
                new { userId = userCtx.UserIdGuid, focalY = req.FocalY.Value });

            await InvalidateProfileCacheAsync(cache, userCtx.UserIdGuid, username, ct);
            return Results.Ok(new { focal_y = req.FocalY.Value });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/profiles/me/discord ─────────────────────────────────
        // Unlink the Discord identity from the authenticated user.
        // Blocked if the user has active registrations in tournaments that require Discord.
        app.MapDelete("/api/profiles/me/discord", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("ProfileEndpoints.DiscordUnlink");
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var txn = conn.BeginTransaction();

            var blockedByTournament = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM tournament_participants tp
                    JOIN tournaments t ON t.id = tp.tournament_id
                    WHERE tp.user_id = @userId
                      AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                      AND t.status NOT IN ('completed', 'cancelled')
                      AND (
                    COALESCE(NULLIF(t.settings->>'discordLinkCount', '')::int, 0) > 0
                    OR (t.settings->>'requireDiscordLink')::boolean IS TRUE
                )
                )
                """,
                new { userId = userCtx.UserIdGuid }, txn);

            if (blockedByTournament)
            {
                txn.Rollback();
                return Results.BadRequest(new
                {
                    error = "active_registration",
                    message = "You are registered in a tournament that requires a linked Discord account. Withdraw from all such tournaments before unlinking.",
                });
            }

            var identity = await conn.QuerySingleOrDefaultAsync<(string Id, string ProviderId)>(
                "SELECT id::text AS Id, provider_id AS ProviderId FROM auth.identities WHERE user_id = @userId AND provider = 'discord'",
                new { userId = userCtx.UserIdGuid }, txn);

            if (identity == default)
            {
                txn.Rollback();
                return Results.NotFound(new { error = "Discord account not linked" });
            }

            // Delete the Discord identity directly. GoTrue's admin identity-unlink HTTP endpoint
            // was added in GoTrue v2.114.0 and is absent on older self-hosted instances.
            // Direct SQL is equivalent: GoTrue performs the same DELETE internally.
            await conn.ExecuteAsync(
                "DELETE FROM auth.identities WHERE user_id = @userId AND provider = 'discord'",
                new { userId = userCtx.UserIdGuid }, txn);

            txn.Commit();

            // Post-unlink audit: detect any concurrent registration that slipped through the guard window.
            var postUnlinkBlocked = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM tournament_participants tp
                    JOIN tournaments t ON t.id = tp.tournament_id
                    WHERE tp.user_id = @userId
                      AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                      AND t.status NOT IN ('completed', 'cancelled')
                      AND (
                    COALESCE(NULLIF(t.settings->>'discordLinkCount', '')::int, 0) > 0
                    OR (t.settings->>'requireDiscordLink')::boolean IS TRUE
                )
                )
                """,
                new { userId = userCtx.UserIdGuid });

            if (postUnlinkBlocked)
                logger.LogWarning(
                    "[DiscordUnlink] User {UserId} has active Discord-required registrations after unlink — concurrent registration race detected",
                    userCtx.UserId);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/profiles/me/discord-join ──────────────────────────────
        // Auto-join the user to the Esportra Discord server using their OAuth token
        app.MapPost("/api/profiles/me/discord-join", async (
            [FromBody] DiscordJoinRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            DiscordNotificationService discord,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrEmpty(req.ProviderToken))
                return Results.BadRequest(new { error = "Missing Discord provider token" });

            using var conn = db.CreateConnection();

            // Get the user's Discord provider_id from Supabase identities
            var discordId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT provider_id FROM auth.identities WHERE user_id = @userId AND provider = 'discord'",
                new { userId = userCtx.UserIdGuid });

            if (string.IsNullOrEmpty(discordId))
                return Results.BadRequest(new { error = "Discord account not linked" });

            var outcome = await discord.TryAutoJoinGuildAsync(discordId, req.ProviderToken);

            return Results.Ok(new { success = outcome.Success, reason = outcome.Reason });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/me/tournament-discord-prefs ───────────────────
        app.MapGet("/api/profiles/me/tournament-discord-prefs", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<TournamentDiscordPrefDto>(
                """
                SELECT t.id AS TournamentId, t.name AS TournamentName, t.game AS Game,
                       t.start_date AS StartDate,
                       COALESCE(p.discord_dms_enabled, true) AS DiscordDmsEnabled
                FROM tournament_participants tp
                JOIN tournaments t ON t.id = tp.tournament_id
                LEFT JOIN user_tournament_discord_prefs p
                    ON p.user_id = @callerId AND p.tournament_id = t.id
                WHERE (tp.user_id = @callerId OR tp.team_captain_id = @callerId)
                  AND tp.status NOT IN ('cancelled', 'rejected')
                  AND t.status NOT IN ('completed', 'cancelled')
                ORDER BY t.start_date DESC
                """,
                new { callerId = userCtx.UserIdGuid });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/profiles/me/tournament-discord-prefs/{tournamentId} ────
        app.MapPut("/api/profiles/me/tournament-discord-prefs/{tournamentId}", async (
            Guid tournamentId,
            [FromBody] ToggleTournamentDiscordPrefRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isParticipant = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (
                    SELECT 1 FROM tournament_participants
                    WHERE (user_id = @callerId OR team_captain_id = @callerId)
                      AND tournament_id = @tournamentId
                      AND status NOT IN ('cancelled', 'rejected')
                )
                """,
                new { callerId = userCtx.UserIdGuid, tournamentId });

            if (!isParticipant)
                return Results.Forbid();

            await conn.ExecuteAsync(
                """
                INSERT INTO user_tournament_discord_prefs (user_id, tournament_id, discord_dms_enabled)
                VALUES (@userId, @tournamentId, @enabled)
                ON CONFLICT (user_id, tournament_id) DO UPDATE
                    SET discord_dms_enabled = @enabled
                """,
                new { userId = userCtx.UserIdGuid, tournamentId, enabled = req.Enabled });

            return Results.Ok(new { success = true, tournamentId, enabled = req.Enabled });
        }).RequireAuthorization("Authenticated");
    }
}

public static class AvatarEndpoints
{
    public static void MapAvatarEndpoints(this WebApplication app)
    {
        // ── GET /api/avatars/availability ─────────────────────────────────────
        // Returns which seeds in a given style are already claimed by other users.
        // Query params: style (string), seeds (repeated, up to 20)
        app.MapGet("/api/avatars/availability", async (
            HttpContext ctx,
            [FromQuery] string style,
            [FromQuery(Name = "seeds")] string[] seeds,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(style) || seeds.Length == 0)
                return Results.Ok(new { claimed = Array.Empty<string>() });

            var capped = seeds.Take(20).ToArray();

            using var conn = db.CreateConnection();
            var claimed = await conn.QueryAsync<string>(
                """
                SELECT avatar_seed
                FROM profiles
                WHERE avatar_style = @style
                  AND avatar_seed = ANY(@seeds)
                  AND id != @userId
                  AND avatar_seed IS NOT NULL
                """,
                new { style, seeds = capped, userId = userCtx.UserIdGuid });

            return Results.Ok(new { claimed = claimed.ToArray() });
        }).RequireAuthorization("Authenticated");
    }
}

public sealed record TournamentDiscordPrefDto(Guid TournamentId, string TournamentName, string Game, DateTimeOffset StartDate, bool DiscordDmsEnabled);
public sealed record ToggleTournamentDiscordPrefRequest(bool Enabled);
public sealed record SetTimezoneRequest([property: JsonPropertyName("timezone_iana")] string? TimezoneIana);
public sealed record SetBannerPositionRequest([property: JsonPropertyName("focal_y")] double? FocalY);
public sealed record SetCountryRequest([property: JsonPropertyName("country_code")] string? CountryCode);
public sealed record ToggleDiscordDmRequest(bool Enabled);
public sealed record DiscordJoinRequest(string ProviderToken);
public sealed record ResolvePlayersRequest(List<string> Tokens, bool AreUuids = false);
public sealed record TournamentHistoryItemDto(
    Guid TournamentId,
    string TournamentName,
    string? Game,
    string? Format,
    DateTimeOffset? StartDate,
    string? TournamentStatus,
    int? Placement,
    long? PrizeCents,
    bool IsTeamTournament,
    string? TeamName,
    string? TeamLogoUrl
);
public sealed record TournamentHistoryResponseDto(
    IEnumerable<TournamentHistoryItemDto> Items,
    int Page,
    int PageSize,
    bool HasMore
);
public sealed record TeamMembershipDto(
    Guid MembershipId,
    Guid TeamId,
    string? Role,
    DateTimeOffset? JoinedAt,
    bool IsActive,
    string? TeamName,
    string? TeamLogoUrl,
    string? TeamGame
);
public sealed record LinkedAccountsResponseDto(
    RiotAccountPublicDto? Riot,
    SteamAccountPublicDto? Steam
);
public sealed record RiotAccountPublicDto(string? GameName, string? TagLine, string? Region);
public sealed record SteamAccountPublicDto(string? SteamName, string? ProfileUrl);
public sealed record UpdatePrivacySettingsRequest(
    [property: JsonPropertyName("show_riot_account")] bool ShowRiotAccount,
    [property: JsonPropertyName("show_steam_account")] bool ShowSteamAccount);

public sealed record VerificationRequestBody(
    string? Role = null,
    string? Requested_Role = null,
    string? First_Name = null,
    string? Last_Name = null,
    string? Email = null,
    string? Date_Of_Birth = null,
    string? Business_Name = null,
    string? Business_Type = null,
    string? Business_Description = null,
    string? Experience_Description = null,
    string? Cnic_Front_Url = null,
    string? Cnic_Back_Url = null,
    string? Website = null,
    string? Contact_Phone = null,
    object? Organizer_Data = null,
    object? Venue_Data = null)
{
    public string EffectiveRole => Role ?? Requested_Role ?? "";
}
