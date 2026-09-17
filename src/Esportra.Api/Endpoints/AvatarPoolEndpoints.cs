using Dapper;
using Esportra.Api.Helpers;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class AvatarPoolEndpoints
{
    public static void MapAvatarPoolEndpoints(this WebApplication app)
    {
        // ── GET /api/avatars/pool ─────────────────────────────────────────────
        // Browse available (unclaimed) pool items, plus any owned by the caller.
        // Query params: style (optional filter), page (0-based), pageSize (max 32)
        app.MapGet("/api/avatars/pool", async (
            HttpContext ctx,
            [FromQuery] string? style,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            pageSize = Math.Clamp(pageSize == 0 ? 16 : pageSize, 1, 32);

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, style, seed, claimed_by, claimed_at
                FROM avatar_pool
                WHERE (claimed_by IS NULL OR claimed_by = @userId)
                  AND (@style IS NULL OR style = @style)
                ORDER BY created_at ASC
                LIMIT @pageSize OFFSET @offset
                """,
                new { userId = userCtx.UserIdGuid, style, pageSize, offset = page * pageSize });

            var total = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM avatar_pool
                WHERE (claimed_by IS NULL OR claimed_by = @userId)
                  AND (@style IS NULL OR style = @style)
                """,
                new { userId = userCtx.UserIdGuid, style });

            return Results.Ok(new { items = rows, total, page, pageSize });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/avatars/mine ─────────────────────────────────────────────
        // Returns the avatar currently owned by the caller, or null.
        app.MapGet("/api/avatars/mine", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var owned = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, style, seed, claimed_at FROM avatar_pool WHERE claimed_by = @userId",
                new { userId = userCtx.UserIdGuid });

            var releaseCount = await conn.ExecuteScalarAsync<int>(
                "SELECT avatar_release_count FROM profiles WHERE id = @userId",
                new { userId = userCtx.UserIdGuid });

            const int maxReleases = 2;
            return Results.Ok(new
            {
                owned,
                releases_used = releaseCount,
                releases_remaining = maxReleases - releaseCount,
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/avatars/claim ───────────────────────────────────────────
        // Claim a pool item. Fails if the user already owns one or item is taken.
        app.MapPost("/api/avatars/claim", async (
            [FromBody] ClaimAvatarRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Check user doesn't already own one
            var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM avatar_pool WHERE claimed_by = @userId LIMIT 1",
                new { userId = userCtx.UserIdGuid });

            if (existing is not null)
                return Results.Conflict(new { error = "You already own an avatar. Release it before claiming a new one." });

            // Claim atomically — only succeeds if item is unclaimed
            var claimed = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE avatar_pool
                SET claimed_by = @userId, claimed_at = now()
                WHERE id = @itemId AND claimed_by IS NULL
                RETURNING id, style, seed, claimed_at
                """,
                new { userId = userCtx.UserIdGuid, itemId = req.ItemId });

            if (claimed is null)
                return Results.Conflict(new { error = "This avatar was just claimed by someone else." });

            // Denormalise onto profile so the existing avatar_url computation keeps working
            await conn.ExecuteAsync(
                "UPDATE profiles SET avatar_seed = @seed, avatar_style = @style, avatar_url = NULL, updated_at = now() WHERE id = @userId",
                new { userId = userCtx.UserIdGuid, seed = (string)claimed.seed, style = (string)claimed.style });

            return Results.Ok(new { claimed });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/avatars/claim ─────────────────────────────────────────
        // Release the caller's currently owned avatar back to the pool.
        app.MapDelete("/api/avatars/claim", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            const int maxReleases = 2;
            using var conn = db.CreateConnection();

            var releaseCount = await conn.ExecuteScalarAsync<int>(
                "SELECT avatar_release_count FROM profiles WHERE id = @userId",
                new { userId = userCtx.UserIdGuid });

            if (releaseCount >= maxReleases)
                return Results.Conflict(new { error = $"You've used all {maxReleases} of your avatar releases." });

            var released = await conn.ExecuteAsync(
                "UPDATE avatar_pool SET claimed_by = NULL, claimed_at = NULL WHERE claimed_by = @userId",
                new { userId = userCtx.UserIdGuid });

            if (released == 0)
                return Results.NotFound(new { error = "You don't own an avatar to release." });

            // Clear denormalised fields and increment release counter
            await conn.ExecuteAsync(
                """
                UPDATE profiles
                SET avatar_seed = NULL,
                    avatar_style = NULL,
                    avatar_release_count = avatar_release_count + 1,
                    updated_at = now()
                WHERE id = @userId
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(new { released = true, releases_remaining = maxReleases - releaseCount - 1 });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/admin/avatars/pool ──────────────────────────────────────
        // Admin: all pool items (claimed + unclaimed) with claimant username.
        app.MapGet("/api/admin/avatars/pool", async (
            HttpContext ctx,
            [FromQuery] string? style,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

            pageSize = Math.Clamp(pageSize == 0 ? 20 : pageSize, 1, 50);

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT ap.id, ap.style, ap.seed, ap.claimed_by, ap.claimed_at, ap.created_at,
                       p.username AS claimed_by_username
                FROM avatar_pool ap
                LEFT JOIN profiles p ON p.id = ap.claimed_by
                WHERE (@style IS NULL OR ap.style = @style)
                ORDER BY ap.created_at DESC
                LIMIT @pageSize OFFSET @offset
                """,
                new { style, pageSize, offset = page * pageSize });

            var total = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM avatar_pool WHERE (@style IS NULL OR style = @style)",
                new { style });

            var totalClaimed = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM avatar_pool WHERE claimed_by IS NOT NULL");

            return Results.Ok(new
            {
                items = rows,
                total,
                claimed = totalClaimed,
                available = total - totalClaimed,
                page,
                pageSize,
            });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/admin/avatars/pool/{id} ───────────────────────────────
        // Admin: remove an unclaimed pool item. Refuses if currently claimed.
        app.MapDelete("/api/admin/avatars/pool/{id:guid}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

            using var conn = db.CreateConnection();

            var item = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, claimed_by FROM avatar_pool WHERE id = @id",
                new { id });

            if (item is null) return Results.NotFound(new { error = "Item not found." });
            if (item.claimed_by is not null)
                return Results.Conflict(new { error = "Cannot delete a claimed avatar. The user must release it first." });

            await conn.ExecuteAsync("DELETE FROM avatar_pool WHERE id = @id", new { id });
            return Results.Ok(new { deleted = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/admin/avatars/pool/batch ────────────────────────────────
        // Admin-only: insert a batch of (style, seed) combos into the pool.
        // Existing (style, seed) pairs are silently skipped (ON CONFLICT DO NOTHING).
        app.MapPost("/api/admin/avatars/pool/batch", async (
            [FromBody] List<AvatarPoolItemInput> items,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

            if (items.Count == 0) return Results.BadRequest(new { error = "No items provided." });
            if (items.Count > 500) return Results.BadRequest(new { error = "Max 500 items per batch." });

            using var conn = db.CreateConnection();
            var inserted = 0;

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Style) || string.IsNullOrWhiteSpace(item.Seed)) continue;
                inserted += await conn.ExecuteAsync(
                    """
                    INSERT INTO avatar_pool (style, seed)
                    VALUES (@style, @seed)
                    ON CONFLICT ON CONSTRAINT avatar_pool_style_seed_unique DO NOTHING
                    """,
                    new { style = item.Style.Trim(), seed = item.Seed.Trim() });
            }

            return Results.Ok(new { inserted, total = items.Count });
        }).RequireAuthorization("Authenticated");
    }
}

public sealed record ClaimAvatarRequest(Guid ItemId);
public sealed record AvatarPoolItemInput(string Style, string Seed);
