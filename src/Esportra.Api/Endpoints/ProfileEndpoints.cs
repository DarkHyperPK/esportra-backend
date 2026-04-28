using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Email;
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
        "username", "full_name", "avatar_url", "bio",
        "riot_tag", "steam_tag", "social_links",
        "card_image_url", "country_code", "banner_url",
        "date_of_birth", "nationality"
    ];

    private const string PublicProfileColumns =
        "p.id, p.username, p.full_name, p.avatar_url, p.is_verified, p.bio, p.location, p.social_links, p.country_code, p.card_image_url, p.banner_url, p.riot_tag, p.steam_tag, p.date_of_birth, p.created_at, p.updated_at";

    private const string PrivateProfileColumns =
        PublicProfileColumns + ", ppd.nationality";

    public static void MapProfileEndpoints(this WebApplication app)
    {
        // ── GET /api/profiles/me ──────────────────────────────────────────────
        app.MapGet("/api/profiles/me", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            return await GetProfileResult(userCtx.UserIdGuid, includePrivate: true, db, cache, ct);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id} ────────────────────────────────────────────
        app.MapGet("/api/profiles/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            var includePrivate = userCtx?.UserIdGuid == id || (userCtx?.Roles.Contains("admin") ?? false);
            return await GetProfileResult(id, includePrivate, db, cache, ct);
        });

        // ── PUT /api/profiles/{id} ────────────────────────────────────────────
        app.MapPut("/api/profiles/{id}", async (
            Guid                       id,
            [FromBody] Dictionary<string, object?> updates,
            HttpContext                ctx,
            IDbConnectionFactory       db,
            HybridCache                cache,
            CancellationToken          ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Users can only update their own profile; admins can update any
            if (userCtx.UserIdGuid != id && !userCtx.Roles.Contains("admin"))
                return Results.Forbid();

            // Filter to allowed fields only (allow some string fields to be cleared)
            var clearableFields = new HashSet<string> { "riot_tag", "steam_tag", "nationality" };
            var valid = updates
                .Where(kv => AllowedUpdateFields.Contains(kv.Key) && (kv.Value is not null || clearableFields.Contains(kv.Key)))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Normalize empty clearable fields to null
            foreach (var clearableField in clearableFields)
            {
                if (valid.TryGetValue(clearableField, out var v))
                {
                    if (v is null || (v is JsonElement je && (je.ValueKind == JsonValueKind.Null || je.GetString() is "" or null))
                        || (v is string s && string.IsNullOrWhiteSpace(s)))
                        valid[clearableField] = null;
                }
            }

            if (valid.Count == 0)
                return Results.BadRequest(new { error = "No valid fields to update." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            // Username uniqueness check
            if (valid.TryGetValue("username", out var newUsername) && newUsername is string uname)
            {
                var taken = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT id FROM profiles WHERE username = @uname AND id != @id LIMIT 1",
                    new { uname, id },
                    tx);
                if (taken is not null)
                    return Results.Conflict(new { error = "Username already taken." });
            }

            var profileExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM profiles WHERE id = @id)",
                new { id },
                tx);

            if (!profileExists)
                return Results.NotFound();

            valid.Remove("nationality", out var nationalityUpdate);

            // Build SET clause dynamically (safe — only allow-listed column names)
            var jsonbFields = new HashSet<string> { "social_links" };
            if (valid.Count > 0)
            {
                var setClauses = string.Join(", ", valid.Keys.Select(k =>
                    jsonbFields.Contains(k) ? $"{k} = @{k}::jsonb" : $"{k} = @{k}"));
                var parameters = new DynamicParameters();
                foreach (var kv in valid)
                {
                    if (kv.Value is null)
                        parameters.Add(kv.Key, null, System.Data.DbType.String);
                    else if (jsonbFields.Contains(kv.Key) && kv.Value is JsonElement je)
                        parameters.Add(kv.Key, je.GetRawText());
                    else
                        parameters.Add(kv.Key, kv.Value is JsonElement v ? v.ToString() : kv.Value);
                }
                parameters.Add("id", id);
                parameters.Add("updated_at", DateTime.UtcNow);

                await conn.ExecuteAsync(
                    $"UPDATE profiles SET {setClauses}, updated_at = @updated_at WHERE id = @id",
                    parameters,
                    tx);
            }

            if (updates.ContainsKey("nationality"))
            {
                var nationality = nationalityUpdate switch
                {
                    JsonElement je when je.ValueKind == JsonValueKind.Null => null,
                    JsonElement je => string.IsNullOrWhiteSpace(je.ToString()) ? null : je.ToString(),
                    string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
                    null => null,
                    _ => nationalityUpdate?.ToString()
                };

                await conn.ExecuteAsync(
                    """
                    INSERT INTO profile_private_details (user_id, nationality)
                    VALUES (@id, @nationality)
                    ON CONFLICT (user_id) DO UPDATE
                    SET nationality = EXCLUDED.nationality,
                        updated_at = NOW()
                    """,
                    new { id, nationality },
                    tx);
            }

            tx.Commit();

            // Invalidate cache
            await cache.RemoveAsync($"profile:{id}", ct);

            return await GetProfileResult(id, includePrivate: true, db, cache, ct);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/by-username/{username} ─────────────────────────
        app.MapGet("/api/profiles/by-username/{username}", async (
            string               username,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, username, full_name, avatar_url FROM profiles WHERE username = @username",
                new { username });
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/search ─────────────────────────────────────────
        app.MapGet("/api/profiles/search", async (
            [FromQuery] string?  q,
            [FromQuery] string?  email,
            [FromQuery] bool?    verified,
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
            if (verified == true)
            {
                conditions.Add("is_verified = TRUE");
            }

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var rows = await conn.QueryAsync<dynamic>(
                $"""
                SELECT id, username, email, full_name, avatar_url, is_verified
                FROM profiles
                {where}
                ORDER BY username
                LIMIT 50
                """, p);
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id}/licenses ────────────────────────────────────
        app.MapGet("/api/profiles/{id}/licenses", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            try
            {
                var rows = await conn.QueryAsync<dynamic>(
                    "SELECT * FROM achievements WHERE is_active = TRUE ORDER BY points ASC");
                return Results.Ok(rows);
            }
            catch { return Results.Ok(Array.Empty<object>()); }
        });

        // ── POST /api/profiles/me/achievements/{achievementId} ──────────────
        app.MapPost("/api/profiles/me/achievements/{achievementId}", async (
            Guid                 achievementId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                INSERT INTO user_achievements (user_id, achievement_id)
                VALUES (@userId, @achievementId)
                ON CONFLICT (user_id, achievement_id) DO NOTHING
                RETURNING id, user_id, achievement_id, created_at, (SELECT row_to_json(a) FROM achievements a WHERE a.id = achievement_id) AS achievement
                """,
                new { userId = userCtx.UserIdGuid, achievementId });

            if (row is not null)
                await cache.RemoveAsync($"profile-stats:{userCtx.UserId}", ct);

            return row is not null
                ? Results.Ok(row)
                : Results.Ok(new { alreadyAwarded = true });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/profiles/me/skill-level ────────────────────────────────
        app.MapPut("/api/profiles/me/skill-level", async (
            [FromBody] UpdateSkillLevelRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            HybridCache                        cache,
            CancellationToken                  ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE user_statistics SET skill_level = @skillLevel WHERE user_id = @userId",
                new { userId = userCtx.UserIdGuid, skillLevel = req.SkillLevel });

            await cache.RemoveAsync($"profile-stats:{userCtx.UserId}", ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles ─────────────────────────────────────────────────
        // Paginated list of profiles with optional search
        app.MapGet("/api/profiles", async (
            string?              q,
            string?              ids,
            string?              game,
            int                  page  = 1,
            int                  limit = 50,
            IDbConnectionFactory db    = default!,
            CancellationToken    ct    = default) =>
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
            string?              userIds,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
                        "SELECT * FROM riot_accounts WHERE user_id = ANY(@ids) ORDER BY created_at DESC",
                        new { ids = idList });
                    return Results.Ok(accounts);
                }

                var myAccounts = await conn.QueryAsync<dynamic>(
                    "SELECT * FROM riot_accounts WHERE user_id = @userId ORDER BY created_at DESC",
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var requests = await conn.QueryAsync<dynamic>(
                "SELECT role, status, is_active, verified_at FROM verified_roles WHERE user_id = @userId ORDER BY verified_at DESC NULLS LAST",
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(requests);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/profiles/me/verification-requests ───────────────────────
        app.MapPost("/api/profiles/me/verification-requests", async (
            [FromBody] VerificationRequestBody req,
            HttpContext                        ctx,
            IDbConnectionFactory               db,
            IEmailService                      email,
            IConfiguration                     config,
            CancellationToken                  ct) =>
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
                            username    = (string?)profile?.username ?? req.First_Name ?? "there",
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            IEmailService        email,
            IConfiguration       config,
            CancellationToken    ct) =>
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
                            username     = (string?)profile.username ?? "there",
                            licenseType  = "organizer",
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            IEmailService        email,
            IConfiguration       config,
            CancellationToken    ct) =>
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
                            username     = (string?)profile.username ?? "there",
                            licenseType  = "venue_owner",
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
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            dynamic? stats = null;
            try { stats = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM user_statistics WHERE user_id = @id", new { id }); }
            catch { /* table not yet migrated */ }

            IEnumerable<dynamic> achievements = Array.Empty<dynamic>();
            try { achievements = await conn.QueryAsync<dynamic>(
                """
                SELECT ua.id, ua.earned_at, ua.progress,
                       a.id AS achievement_id, a.name, a.description,
                       a.category, a.icon_url, a.points, a.requirements
                FROM user_achievements ua
                JOIN achievements a ON a.id = ua.achievement_id
                WHERE ua.user_id = @id AND a.is_active = TRUE
                ORDER BY ua.earned_at DESC
                """, new { id }); }
            catch { /* tables not yet migrated */ }

            return Results.Ok(new { statistics = stats, achievements });
        });
    }

    // ── Shared helper ─────────────────────────────────────────────────────────

    private static async Task<IResult> GetProfileResult(
        Guid id, bool includePrivate, IDbConnectionFactory db, HybridCache cache, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
            $"""
            SELECT {(includePrivate ? PrivateProfileColumns : PublicProfileColumns)}
            FROM profiles p
            LEFT JOIN profile_private_details ppd ON ppd.user_id = p.id
            WHERE p.id = @id
            """,
            new { id });

        return profile is null ? Results.NotFound() : Results.Ok(profile);
    }

    // ── POST /api/profiles/resolve-players — batch resolve by tags/ids ───────
    // Replaces TournamentManage's 4 parallel supabase calls
    public static void MapProfileResolveEndpoint(this WebApplication app)
    {
        app.MapPost("/api/profiles/resolve-players", async (
            [FromBody] ResolvePlayersRequest req,
            IDbConnectionFactory             db,
            CancellationToken                ct) =>
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
            Guid                 rosterId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            CancellationToken                 ct) =>
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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

        // ── POST /api/profiles/me/discord-join ──────────────────────────────
        // Auto-join the user to the Esportra Discord server using their OAuth token
        app.MapPost("/api/profiles/me/discord-join", async (
            [FromBody] DiscordJoinRequest      req,
            HttpContext                         ctx,
            IDbConnectionFactory               db,
            DiscordNotificationService          discord,
            CancellationToken                   ct) =>
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

            var joined = await discord.TryAutoJoinGuildAsync(discordId, req.ProviderToken);

            return Results.Ok(new { success = joined });
        }).RequireAuthorization("Authenticated");
    }
}

public sealed record ToggleDiscordDmRequest(bool Enabled);
public sealed record DiscordJoinRequest(string ProviderToken);
public sealed record UpdateSkillLevelRequest(string SkillLevel);
public sealed record ResolvePlayersRequest(List<string> Tokens, bool AreUuids = false);
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
