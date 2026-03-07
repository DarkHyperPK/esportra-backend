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
}
