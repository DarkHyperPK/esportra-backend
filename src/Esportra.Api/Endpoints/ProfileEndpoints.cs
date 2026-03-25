using System.Text.Json;
using Dapper;
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
        "date_of_birth", "faceit_nickname"
    ];

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
            return await GetProfileResult(userCtx.UserIdGuid, db, cache, ct);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id} ────────────────────────────────────────────
        app.MapGet("/api/profiles/{id}", async (
            Guid                 id,
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            return await GetProfileResult(id, db, cache, ct);
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

            // Filter to allowed fields only
            var valid = updates
                .Where(kv => AllowedUpdateFields.Contains(kv.Key) && kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (valid.Count == 0)
                return Results.BadRequest(new { error = "No valid fields to update." });

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

            // Build SET clause dynamically (safe — only allow-listed column names)
            var jsonbFields = new HashSet<string> { "social_links" };
            var setClauses = string.Join(", ", valid.Keys.Select(k =>
                jsonbFields.Contains(k) ? $"{k} = @{k}::jsonb" : $"{k} = @{k}"));
            var parameters = new DynamicParameters();
            foreach (var kv in valid)
            {
                if (jsonbFields.Contains(kv.Key) && kv.Value is JsonElement je)
                    parameters.Add(kv.Key, je.GetRawText());
                else
                    parameters.Add(kv.Key, kv.Value is JsonElement v ? v.ToString() : kv.Value);
            }
            parameters.Add("id", id);
            parameters.Add("updated_at", DateTime.UtcNow);

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                $"UPDATE profiles SET {setClauses}, updated_at = @updated_at WHERE id = @id RETURNING id, username, full_name, avatar_url, is_verified, bio, location, social_links, country_code, card_image_url, banner_url, riot_tag, steam_tag, date_of_birth, faceit_nickname, created_at, updated_at",
                parameters);

            if (row is null) return Results.NotFound();

            // Invalidate cache
            await cache.RemoveAsync($"profile:{id}", ct);

            return Results.Ok(row);
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
            if (verified == true)
            {
                conditions.Add("is_verified = TRUE");
            }

            var where = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var rows = await conn.QueryAsync<dynamic>(
                $"""
                SELECT id, username, full_name, avatar_url, is_verified
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

            using var conn = db.CreateConnection();

            if (!string.IsNullOrWhiteSpace(userIds))
            {
                var idList = userIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => Guid.TryParse(s, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (idList.Length == 0) return Results.Ok(Array.Empty<object>());
                var paramNames = string.Join(", ", idList.Select((_, i) => $"@p{i}::uuid"));
                var parameters = new DynamicParameters();
                for (int i = 0; i < idList.Length; i++)
                    parameters.Add($"p{i}", Guid.Parse(idList[i]));
                var accounts = await conn.QueryAsync<dynamic>(
                    $"SELECT * FROM riot_accounts WHERE user_id IN ({paramNames}) ORDER BY created_at DESC",
                    parameters);
                return Results.Ok(accounts);
            }

            var myAccounts = await conn.QueryAsync<dynamic>(
                "SELECT * FROM riot_accounts WHERE user_id = @userId ORDER BY created_at DESC",
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(myAccounts);
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

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO verified_roles (user_id, role, status, is_active)
                VALUES (@userId, @role, 'pending', FALSE)
                ON CONFLICT (user_id, role) DO UPDATE SET status = 'pending'
                """,
                new { userId = userCtx.UserIdGuid, role = req.Role });

            // Send confirmation email
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
                            licenseType  = req.Role,
                            dashboardUrl = $"{frontendUrl}/verification-status",
                        },
                        ct);
                }
            }
            catch { /* email failure should not block application */ }

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
        Guid id, IDbConnectionFactory db, HybridCache cache, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM profiles WHERE id = @id",
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
    }
}

public sealed record UpdateSkillLevelRequest(string SkillLevel);
public sealed record ResolvePlayersRequest(List<string> Tokens, bool AreUuids = false);
public sealed record VerificationRequestBody(string Role);
