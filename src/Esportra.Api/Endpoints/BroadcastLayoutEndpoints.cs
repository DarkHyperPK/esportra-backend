using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Esportra.Api.Endpoints;

public static class BroadcastLayoutEndpoints
{
    public static void MapBroadcastLayoutEndpoints(this WebApplication app)
    {
        // ── GET /api/broadcast/layouts — user's own layouts (paginated) ─────
        app.MapGet("/api/broadcast/layouts", async (
            string?              game,
            int                  limit  = 20,
            int                  offset = 0,
            HttpContext          ctx    = null!,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            limit  = Math.Clamp(limit, 1, 50);
            offset = Math.Max(offset, 0);

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, name, game, resolution_width, resolution_height,
                       widgets, is_template, is_public, thumbnail_url,
                       created_at, updated_at
                FROM broadcast_overlay_layouts
                WHERE user_id = @UserId
                  AND (@Game IS NULL OR game = @Game)
                ORDER BY updated_at DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new { UserId = userCtx.UserIdGuid, Game = game, Limit = limit, Offset = offset });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/broadcast/layouts/templates — platform templates ────────
        app.MapGet("/api/broadcast/layouts/templates", async (
            string?              game,
            int                  limit  = 50,
            int                  offset = 0,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            limit  = Math.Clamp(limit, 1, 50);
            offset = Math.Max(offset, 0);

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, name, game, resolution_width, resolution_height,
                       widgets, thumbnail_url, created_at
                FROM broadcast_overlay_layouts
                WHERE is_template = true
                  AND (@Game IS NULL OR game = @Game)
                ORDER BY created_at DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new { Game = game, Limit = limit, Offset = offset });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/broadcast/layouts/community — public layouts ────────────
        app.MapGet("/api/broadcast/layouts/community", async (
            string?              game,
            int                  limit  = 20,
            int                  offset = 0,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            limit  = Math.Clamp(limit, 1, 50);
            offset = Math.Max(offset, 0);

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT l.id, l.name, l.game, l.resolution_width, l.resolution_height,
                       l.widgets, l.thumbnail_url, l.created_at,
                       p.display_name AS author_name, p.avatar_url AS author_avatar
                FROM broadcast_overlay_layouts l
                LEFT JOIN profiles p ON p.id = l.user_id
                WHERE l.is_public = true AND l.is_template = false
                  AND (@Game IS NULL OR l.game = @Game)
                ORDER BY l.created_at DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new { Game = game, Limit = limit, Offset = offset });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/broadcast/layouts/{id} — specific layout ───────────────
        app.MapGet("/api/broadcast/layouts/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var layout = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, user_id, name, game, resolution_width, resolution_height,
                       widgets, is_template, is_public, thumbnail_url,
                       created_at, updated_at
                FROM broadcast_overlay_layouts
                WHERE id = @Id
                  AND (user_id = @UserId OR is_template = true OR is_public = true)
                """,
                new { Id = id, UserId = userCtx.UserIdGuid });

            if (layout is null)
                return Results.NotFound(new { error = "Layout not found." });

            return Results.Ok(layout);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/broadcast/layouts — create new layout ─────────────────
        app.MapPost("/api/broadcast/layouts", async (
            [FromBody] CreateOverlayLayoutRequest req,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            var widgetsJson = req.WidgetsJson ?? "[]";

            using var conn = db.CreateConnection();
            var id = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO broadcast_overlay_layouts
                    (user_id, name, game, resolution_width, resolution_height, widgets, is_public)
                VALUES
                    (@UserId, @Name, @Game, @Width, @Height, @Widgets::jsonb, @IsPublic)
                RETURNING id
                """,
                new
                {
                    UserId   = userCtx.UserIdGuid,
                    Name     = req.Name.Trim(),
                    Game     = req.Game,
                    Width    = req.ResolutionWidth,
                    Height   = req.ResolutionHeight,
                    Widgets  = widgetsJson,
                    IsPublic = req.IsPublic
                });

            return Results.Created($"/api/broadcast/layouts/{id}", new { id });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/broadcast/layouts/{id} — update layout ─────────────────
        app.MapPut("/api/broadcast/layouts/{id}", async (
            Guid                 id,
            [FromBody] UpdateOverlayLayoutRequest req,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var setClauses = new List<string> { "updated_at = now()" };
            var parameters = new DynamicParameters();
            parameters.Add("Id", id);
            parameters.Add("UserId", userCtx.UserIdGuid);

            if (req.Name is not null)
            {
                setClauses.Add("name = @Name");
                parameters.Add("Name", req.Name.Trim());
            }
            if (req.WidgetsJson is not null)
            {
                setClauses.Add("widgets = @Widgets::jsonb");
                parameters.Add("Widgets", req.WidgetsJson);
            }
            if (req.IsPublic.HasValue)
            {
                setClauses.Add("is_public = @IsPublic");
                parameters.Add("IsPublic", req.IsPublic.Value);
            }
            if (req.ThumbnailUrl is not null)
            {
                setClauses.Add("thumbnail_url = @ThumbnailUrl");
                parameters.Add("ThumbnailUrl", req.ThumbnailUrl);
            }
            if (req.ResolutionWidth.HasValue)
            {
                setClauses.Add("resolution_width = @Width");
                parameters.Add("Width", req.ResolutionWidth.Value);
            }
            if (req.ResolutionHeight.HasValue)
            {
                setClauses.Add("resolution_height = @Height");
                parameters.Add("Height", req.ResolutionHeight.Value);
            }

            using var conn = db.CreateConnection();
            var rows = await conn.ExecuteAsync(
                $"""
                UPDATE broadcast_overlay_layouts
                SET {string.Join(", ", setClauses)}
                WHERE id = @Id AND user_id = @UserId AND is_template = false
                """,
                parameters);

            if (rows == 0)
                return Results.NotFound(new { error = "Layout not found or not editable." });

            return Results.Ok(new { updated = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/broadcast/layouts/{id} — delete layout ──────────────
        app.MapDelete("/api/broadcast/layouts/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.ExecuteAsync(
                """
                DELETE FROM broadcast_overlay_layouts
                WHERE id = @Id AND user_id = @UserId AND is_template = false
                """,
                new { Id = id, UserId = userCtx.UserIdGuid });

            if (rows == 0)
                return Results.NotFound(new { error = "Layout not found or not deletable." });

            return Results.Ok(new { deleted = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/broadcast/layouts/{id}/duplicate — clone a layout ─────
        app.MapPost("/api/broadcast/layouts/{id}/duplicate", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Fetch source layout (own, template, or public)
            var source = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT name, game, resolution_width, resolution_height, widgets
                FROM broadcast_overlay_layouts
                WHERE id = @Id
                  AND (user_id = @UserId OR is_template = true OR is_public = true)
                """,
                new { Id = id, UserId = userCtx.UserIdGuid });

            if (source is null)
                return Results.NotFound(new { error = "Source layout not found." });

            var newId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO broadcast_overlay_layouts
                    (user_id, name, game, resolution_width, resolution_height, widgets)
                VALUES
                    (@UserId, @Name, @Game, @Width, @Height, @Widgets::jsonb)
                RETURNING id
                """,
                new
                {
                    UserId = userCtx.UserIdGuid,
                    Name   = $"{source.name} (Copy)",
                    source.game,
                    Width  = (int)source.resolution_width,
                    Height = (int)source.resolution_height,
                    Widgets = ((JsonElement)source.widgets).GetRawText()
                });

            return Results.Created($"/api/broadcast/layouts/{newId}", new { id = newId });
        }).RequireAuthorization("Authenticated");
    }
}
