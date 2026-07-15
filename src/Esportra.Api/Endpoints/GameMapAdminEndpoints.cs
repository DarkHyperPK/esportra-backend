using System.Net.Http.Headers;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Core.Audit;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Admin endpoints for managing game maps (CS2, Valorant, R6 Siege).
/// Allows admins to add/edit/delete maps and upload map images.
/// Maps added here appear in tournament map pool and veto system.
/// </summary>
public static class GameMapAdminEndpoints
{
    private static readonly string[] SupportedGames = ["Counter-Strike 2", "Valorant", "Rainbow Six Siege"];
    private const string StorageBucket = "system.assets.games";

    public static void MapGameMapAdminEndpoints(this WebApplication app)
    {
        // ── GET /api/admin/game-maps ─────────────────────────────────────────
        // List all game maps, optionally filtered by game
        app.MapGet("/api/admin/game-maps", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            IConfiguration config,
            [FromQuery] string? game,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesPermission(ctx, out _, out var authResult))
                return authResult!;

            using var conn = db.CreateConnection();
            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/') ?? "";

            IEnumerable<dynamic> maps;
            if (!string.IsNullOrWhiteSpace(game))
            {
                maps = await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, game, map_name, map_image_url, is_active, created_at
                    FROM game_maps
                    WHERE game ILIKE @game
                    ORDER BY game, map_name
                    """,
                    new { game });
            }
            else
            {
                maps = await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, game, map_name, map_image_url, is_active, created_at
                    FROM game_maps
                    ORDER BY game, map_name
                    """);
            }

            // Enrich R6 maps with fallback images
            var result = maps.Select(m => EnrichMapWithImage(m, supabaseUrl)).ToList();
            return Results.Ok(new { maps = result, total = result.Count });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/game-maps ─────────────────────────────────────────
        // Create a new game map
        app.MapPost("/api/admin/game-maps", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            [FromBody] CreateMapRequest req,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesPermission(ctx, out var userCtx, out var authResult))
                return authResult!;

            if (string.IsNullOrWhiteSpace(req.MapName))
                return Results.BadRequest(new { error = "Map name is required." });

            if (string.IsNullOrWhiteSpace(req.Game))
                return Results.BadRequest(new { error = "Game is required." });

            if (!SupportedGames.Contains(req.Game, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unsupported game. Supported: {string.Join(", ", SupportedGames)}" });

            using var conn = db.CreateConnection();

            // Check for duplicate
            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM game_maps WHERE game ILIKE @game AND map_name ILIKE @mapName)",
                new { game = req.Game, mapName = req.MapName });

            if (exists)
                return Results.Conflict(new { error = $"Map '{req.MapName}' already exists for {req.Game}." });

            var mapId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO game_maps (game, map_name, is_active)
                VALUES (@game, @mapName, @isActive)
                RETURNING id
                """,
                new { game = req.Game, mapName = req.MapName, isActive = req.IsActive ?? true });

            await audit.LogAsync(
                userCtx!.UserIdGuid, userCtx.Email,
                ActionType.Create, TargetType.System,
                mapId, req.MapName,
                new { game = req.Game, mapName = req.MapName },
                AuditSeverity.Medium, ct);

            return Results.Created($"/api/admin/game-maps/{mapId}", new { id = mapId, game = req.Game, map_name = req.MapName, is_active = req.IsActive ?? true });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/game-maps/{id} ────────────────────────────────────
        // Update a game map
        app.MapPut("/api/admin/game-maps/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            [FromBody] UpdateMapRequest req,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesPermission(ctx, out var userCtx, out var authResult))
                return authResult!;

            using var conn = db.CreateConnection();

            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, game, map_name FROM game_maps WHERE id = @id", new { id });

            if (existing is null)
                return Results.NotFound(new { error = "Map not found." });

            var updates = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("id", id);

            if (!string.IsNullOrWhiteSpace(req.MapName))
            {
                updates.Add("map_name = @mapName");
                parameters.Add("mapName", req.MapName);
            }

            if (req.IsActive.HasValue)
            {
                updates.Add("is_active = @isActive");
                parameters.Add("isActive", req.IsActive.Value);
            }

            if (updates.Count == 0)
                return Results.BadRequest(new { error = "No fields to update." });

            await conn.ExecuteAsync(
                $"UPDATE game_maps SET {string.Join(", ", updates)} WHERE id = @id",
                parameters);

            await audit.LogAsync(
                userCtx!.UserIdGuid, userCtx.Email,
                ActionType.Update, TargetType.System,
                id, (string)existing.map_name,
                new { game = (string)existing.game, changes = req },
                AuditSeverity.Medium, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/game-maps/{id} ─────────────────────────────────
        // Delete a game map (or deactivate if in use)
        app.MapDelete("/api/admin/game-maps/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesPermission(ctx, out var userCtx, out var authResult))
                return authResult!;

            using var conn = db.CreateConnection();

            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, game, map_name FROM game_maps WHERE id = @id", new { id });

            if (existing is null)
                return Results.NotFound(new { error = "Map not found." });

            // Check if map is in use in any tournament
            var inUse = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournament_map_pools WHERE map_id = @id)",
                new { id });

            if (inUse)
            {
                // Soft delete - just deactivate
                await conn.ExecuteAsync("UPDATE game_maps SET is_active = FALSE WHERE id = @id", new { id });
                await audit.LogAsync(
                    userCtx!.UserIdGuid, userCtx.Email,
                    ActionType.Update, TargetType.System,
                    id, (string)existing.map_name,
                    new { game = (string)existing.game, action = "deactivated", reason = "in_use_by_tournaments" },
                    AuditSeverity.Medium, ct);
                return Results.Ok(new { success = true, deactivated = true, message = "Map is in use by tournaments. Deactivated instead of deleted." });
            }

            await conn.ExecuteAsync("DELETE FROM game_maps WHERE id = @id", new { id });

            await audit.LogAsync(
                userCtx!.UserIdGuid, userCtx.Email,
                ActionType.Delete, TargetType.System,
                id, (string)existing.map_name,
                new { game = (string)existing.game },
                AuditSeverity.Medium, ct);

            return Results.Ok(new { success = true, deleted = true });
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/game-maps/{id}/image ─────────────────────────────
        // Upload or replace map image
        app.MapPost("/api/admin/game-maps/{id}/image", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            AuditService audit,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!TryRequireGamesPermission(ctx, out var userCtx, out var authResult))
                return authResult!;

            using var conn = db.CreateConnection();

            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, game, map_name FROM game_maps WHERE id = @id", new { id });

            if (existing is null)
                return Results.NotFound(new { error = "Map not found." });

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file provided." });

            // Validate file type
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!new[] { ".jpg", ".jpeg", ".png", ".webp", ".avif" }.Contains(ext))
                return Results.BadRequest(new { error = "Invalid file type. Allowed: jpg, png, webp, avif" });

            // Validate file size (10MB max)
            if (file.Length > 10 * 1024 * 1024)
                return Results.BadRequest(new { error = "File too large. Maximum 10MB." });

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/')
                ?? throw new InvalidOperationException("Supabase:Url not configured");
            var serviceKey = config["Supabase:ServiceKey"]
                ?? throw new InvalidOperationException("Supabase:ServiceKey not configured");

            // Build storage path: game-slug/maps/map-slug.ext
            var gameSlug = SlugifyGame((string)existing.game);
            var mapSlug = Slugify((string)existing.map_name);
            var storagePath = $"{gameSlug}/maps/{mapSlug}{ext}";

            // Upload to Supabase Storage
            var client = httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
            client.DefaultRequestHeaders.Add("apikey", serviceKey);
            client.DefaultRequestHeaders.Add("x-upsert", "true");

            using var stream = file.OpenReadStream();
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "image/png");

            var uploadUrl = $"{supabaseUrl}/storage/v1/object/{StorageBucket}/{storagePath}";
            var response = await client.PostAsync(uploadUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogError("Map image upload failed: {Status} {Body}", response.StatusCode, errorBody);
                return Results.Problem("Failed to upload image to storage.", statusCode: 500);
            }

            // Build public URL and update database
            var publicUrl = $"{supabaseUrl}/storage/v1/object/public/{StorageBucket}/{storagePath}";
            await conn.ExecuteAsync(
                "UPDATE game_maps SET map_image_url = @url WHERE id = @id",
                new { url = publicUrl, id });

            await audit.LogAsync(
                userCtx!.UserIdGuid, userCtx.Email,
                ActionType.Update, TargetType.System,
                id, (string)existing.map_name,
                new { game = (string)existing.game, action = "image_uploaded", path = storagePath },
                AuditSeverity.Low, ct);

            return Results.Ok(new { success = true, map_image_url = publicUrl });
        }).DisableAntiforgery().RequireAuthorization("Admin");
    }

    private static bool TryRequireGamesPermission(HttpContext ctx, out UserContext? userCtx, out IResult? authResult)
    {
        userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null)
        {
            authResult = Results.Unauthorized();
            return false;
        }

        if (!userCtx.Permissions.Contains(Permissions.GamesManage) && !userCtx.IsSuperAdmin)
        {
            authResult = Results.Forbid();
            return false;
        }

        authResult = null;
        return true;
    }

    private static object EnrichMapWithImage(dynamic map, string supabaseUrl)
    {
        string? imageUrl = map.map_image_url;
        string game = map.game;
        string mapName = map.map_name;

        // R6 fallback - if no image URL, try to resolve from catalog
        if (string.IsNullOrEmpty(imageUrl) && game.Contains("Rainbow Six", StringComparison.OrdinalIgnoreCase))
        {
            var slug = Slugify(mapName);
            imageUrl = $"{supabaseUrl}/storage/v1/object/public/{StorageBucket}/r6/maps/{slug}.avif";
        }

        return new
        {
            id = (Guid)map.id,
            game,
            map_name = mapName,
            map_image_url = imageUrl,
            is_active = (bool)map.is_active,
            created_at = map.created_at
        };
    }

    private static string Slugify(string name) =>
        name.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("'", "")
            .Replace(":", "");

    private static string SlugifyGame(string game) => game switch
    {
        "Counter-Strike 2" => "cs2",
        "Valorant" => "valorant",
        "Rainbow Six Siege" => "r6",
        _ => Slugify(game)
    };

    public sealed record CreateMapRequest(string Game, string MapName, bool? IsActive);
    public sealed record UpdateMapRequest(string? MapName, bool? IsActive);
}
