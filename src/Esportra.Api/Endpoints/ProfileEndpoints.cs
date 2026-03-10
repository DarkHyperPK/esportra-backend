using Dapper;
using Esportra.Contracts.Auth;
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
        "riot_tag", "steam_tag", "phone", "location",
        "social_links", "card_image_url", "country_code"
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
            return await GetProfileResult(userCtx.UserId, db, cache, ct);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id} ────────────────────────────────────────────
        app.MapGet("/api/profiles/{id}", async (
            string               id,
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            return await GetProfileResult(id, db, cache, ct);
        });

        // ── PUT /api/profiles/{id} ────────────────────────────────────────────
        app.MapPut("/api/profiles/{id}", async (
            string                     id,
            [FromBody] Dictionary<string, object?> updates,
            HttpContext                ctx,
            IDbConnectionFactory       db,
            HybridCache                cache,
            CancellationToken          ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Users can only update their own profile; admins can update any
            if (userCtx.UserId != id && !userCtx.Roles.Contains("admin"))
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
            var setClauses = string.Join(", ", valid.Keys.Select(k => $"{k} = @{k}"));
            var parameters = new DynamicParameters();
            foreach (var kv in valid) parameters.Add(kv.Key, kv.Value);
            parameters.Add("id", id);
            parameters.Add("updated_at", DateTime.UtcNow);

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                $"UPDATE profiles SET {setClauses}, updated_at = @updated_at WHERE id = @id RETURNING *",
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
            [FromQuery] string   q,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, username, email, avatar_url
                FROM profiles
                WHERE username ILIKE '%' || @q || '%' OR email ILIKE '%' || @q || '%'
                ORDER BY username
                LIMIT 50
                """,
                new { q });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id}/licenses ────────────────────────────────────
        app.MapGet("/api/profiles/{id}/licenses", async (
            string               id,
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

        // ── GET /api/achievements ───────────────────────────────────────────
        app.MapGet("/api/achievements", async (
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            return await cache.GetOrCreateAsync(
                "achievements:all",
                async (_) =>
                {
                    using var conn = db.CreateConnection();
                    var rows = await conn.QueryAsync<dynamic>(
                        "SELECT * FROM achievements WHERE is_active = TRUE ORDER BY points ASC");
                    return Results.Ok(rows);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) },
                cancellationToken: ct);
        });

        // ── POST /api/profiles/me/achievements/{achievementId} ──────────────
        app.MapPost("/api/profiles/me/achievements/{achievementId}", async (
            string               achievementId,
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
                RETURNING *, (SELECT row_to_json(a) FROM achievements a WHERE a.id = achievement_id) AS achievement
                """,
                new { userId = userCtx.UserId, achievementId });

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
                new { userId = userCtx.UserId, skillLevel = req.SkillLevel });

            await cache.RemoveAsync($"profile-stats:{userCtx.UserId}", ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/{id}/stats ──────────────────────────────────────
        app.MapGet("/api/profiles/{id}/stats", async (
            string               id,
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            return await cache.GetOrCreateAsync(
                $"profile-stats:{id}",
                async (c) =>
                {
                    using var conn = db.CreateConnection();

                    var stats = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        "SELECT * FROM user_statistics WHERE user_id = @id",
                        new { id });

                    var achievements = await conn.QueryAsync<dynamic>(
                        """
                        SELECT ua.id, ua.earned_at, ua.progress,
                               a.id AS achievement_id, a.name, a.description,
                               a.category, a.icon_url, a.points, a.requirements
                        FROM user_achievements ua
                        JOIN achievements a ON a.id = ua.achievement_id
                        WHERE ua.user_id = @id AND a.is_active = TRUE
                        ORDER BY ua.earned_at DESC
                        """,
                        new { id });

                    return Results.Ok(new { statistics = stats, achievements });
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) },
                cancellationToken: ct);
        });
    }

    // ── Shared helper ─────────────────────────────────────────────────────────

    private static async Task<IResult> GetProfileResult(
        string id, IDbConnectionFactory db, HybridCache cache, CancellationToken ct)
    {
        var profile = await cache.GetOrCreateAsync(
            $"profile:{id}",
            async (_) =>
            {
                using var conn = db.CreateConnection();
                return await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT * FROM profiles WHERE id = @id",
                    new { id });
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) },
            cancellationToken: ct);

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
            string               rosterId,
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
