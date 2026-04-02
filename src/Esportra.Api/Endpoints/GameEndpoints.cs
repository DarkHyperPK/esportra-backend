using System.Text.Json;
using Dapper;
using Esportra.Infrastructure.Database;
using Esportra.Infrastructure.Integrations;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: rawg-proxy Edge Function.
/// GET /api/games/search   — search games with 7-day DB cache
/// GET /api/games/{id}/screenshots
/// </summary>
public static class GameEndpoints
{
    public static void MapGameEndpoints(this WebApplication app)
    {
        // ── GET /api/games/search?q={query} ───────────────────────────────────
        app.MapGet("/api/games/search", async (
            string               q,
            IDbConnectionFactory db,
            RawgApiClient        rawg,
            CancellationToken    ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Please enter a search term." });

            using var conn = db.CreateConnection();

            // Check 7-day cache in games_metadata table (table may not exist in all environments)
            string? cached = null;
            try
            {
                cached = await conn.QuerySingleOrDefaultAsync<string>("""
                    SELECT rawg_data FROM public.games_metadata
                    WHERE LOWER(game_name) = LOWER(@name)
                      AND last_updated > NOW() - INTERVAL '7 days'
                    """, new { name = q });
            }
            catch { /* games_metadata table not yet migrated — skip cache */ }

            if (cached is not null)
            {
                return Results.Text(
                    $$"""{"isCached":true,"data":{{cached}}}""",
                    "application/json");
            }

            // Cache miss — call RAWG
            var rawJson = await rawg.SearchGamesAsync(q, ct);

            // Try to cache the first result
            using var doc = JsonDocument.Parse(rawJson);
            var results = doc.RootElement.TryGetProperty("results", out var r) ? r : default;
            if (results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
            {
                var first  = results[0];
                var rawgId = first.TryGetProperty("id",               out var id)  ? id.GetInt32()    : (int?)null;
                var bg     = first.TryGetProperty("background_image", out var bi)  ? bi.GetString()   : null;

                if (rawgId.HasValue)
                {
                    try
                    {
                        await conn.ExecuteAsync("""
                            INSERT INTO public.games_metadata (game_name, rawg_id, background_image, last_updated, rawg_data)
                            VALUES (@name, @rawgId, @bg, NOW(), @data::jsonb)
                            ON CONFLICT (game_name) DO UPDATE SET
                                rawg_id = EXCLUDED.rawg_id,
                                background_image = EXCLUDED.background_image,
                                last_updated = EXCLUDED.last_updated,
                                rawg_data = EXCLUDED.rawg_data
                            """, new { name = q, rawgId, bg, data = rawJson });
                    }
                    catch { /* games_metadata table not yet migrated — skip cache write */ }
                }
            }

            return Results.Text(
                $$"""{"isCached":false,"data":{{rawJson}}}""",
                "application/json");
        }); // Public — game search doesn't require auth

        // ── GET /api/games/maps?game={game} ──────────────────────────────────
        // Used by StepFormatRules.tsx
        app.MapGet("/api/games/maps", async (
            string               game,
            IDbConnectionFactory db) =>
        {
            if (string.IsNullOrWhiteSpace(game))
                return Results.BadRequest(new { error = "Please select a game." });

            using var conn = db.CreateConnection();
            var maps = await conn.QueryAsync<dynamic>(
                """
                SELECT id::text as id, game, map_name, map_image_url, is_active
                FROM public.game_maps
                WHERE game ILIKE @game AND is_active = true
                ORDER BY map_name ASC
                """, new { game });

            return Results.Ok(maps);
        }); // Public

        // ── GET /api/game-maps?game={game}&is_active=true ────────────────────
        // Used by MapPoolManager.tsx
        app.MapGet("/api/game-maps", async (
            string               game,
            bool?                is_active,
            string?              map_name,
            IDbConnectionFactory db) =>
        {
            if (string.IsNullOrWhiteSpace(game))
                return Results.BadRequest(new { error = "Please select a game." });

            using var conn = db.CreateConnection();
            var sql = "SELECT id::text as id, game, map_name, map_image_url, is_active FROM public.game_maps WHERE game ILIKE @game";
            if (is_active.HasValue) sql += " AND is_active = @is_active";
            if (!string.IsNullOrWhiteSpace(map_name)) sql += " AND map_name ILIKE @map_name";
            sql += " ORDER BY map_name ASC";

            var maps = await conn.QueryAsync<dynamic>(sql, new { game, is_active, map_name });
            return Results.Ok(maps);
        }); // Public

        // ── GET /api/games/{id}/screenshots ───────────────────────────────────
        app.MapGet("/api/games/{id:int}/screenshots", async (
            int              id,
            RawgApiClient    rawg,
            CancellationToken ct) =>
        {
            var json = await rawg.GetScreenshotsAsync(id, ct);
            return Results.Text(json, "application/json");
        }); // Public

        // ── GET /api/games/igdb-assets?game={game} ─────────────────────────
        // Returns IGDB artworks, screenshots, cover, and videos for a game (7-day DB cache)
        app.MapGet("/api/games/igdb-assets", async (
            string               game,
            IDbConnectionFactory db,
            IgdbApiClient        igdb,
            CancellationToken    ct) =>
        {
            if (string.IsNullOrWhiteSpace(game))
                return Results.BadRequest(new { error = "Please select a game." });

            using var conn = db.CreateConnection();

            // Check cache
            string? cachedJson = null;
            try
            {
                cachedJson = await conn.QuerySingleOrDefaultAsync<string>("""
                    SELECT igdb_assets FROM public.games_metadata
                    WHERE LOWER(game_name) = LOWER(@name)
                      AND igdb_assets IS NOT NULL
                      AND last_updated > NOW() - INTERVAL '7 days'
                    """, new { name = game });
            }
            catch { /* column may not exist yet */ }

            if (cachedJson is not null)
                return Results.Text(cachedJson, "application/json");

            // Fetch from IGDB
            var assets = await igdb.GetGameAssetsAsync(game, ct);
            if (assets is null)
                return Results.Ok(new { banners = Array.Empty<string>(), cover = (string?)null, videos = Array.Empty<object>() });

            var response = new
            {
                banners = assets.Banners,
                cover = assets.Cover,
                videos = assets.Videos.Select(v => new { videoId = v.VideoId, name = v.Name })
            };

            // Cache the full response as JSON
            var responseJson = JsonSerializer.Serialize(response);
            try
            {
                await conn.ExecuteAsync("""
                    INSERT INTO public.games_metadata (game_name, igdb_assets, last_updated)
                    VALUES (@name, @json::jsonb, NOW())
                    ON CONFLICT (game_name) DO UPDATE SET
                        igdb_assets = EXCLUDED.igdb_assets,
                        last_updated = EXCLUDED.last_updated
                    """, new { name = game, json = responseJson });
            }
            catch { /* cache write failure is non-critical */ }

            return Results.Ok(response);
        }); // Public

        // ── GET /api/games/igdb-banner?game={game} ──────────────────────────
        // Legacy: returns first IGDB banner for backward compat
        app.MapGet("/api/games/igdb-banner", async (
            string               game,
            IDbConnectionFactory db,
            IgdbApiClient        igdb,
            CancellationToken    ct) =>
        {
            if (string.IsNullOrWhiteSpace(game))
                return Results.BadRequest(new { error = "Please select a game." });

            using var conn = db.CreateConnection();

            // Check cache
            string? cachedJson = null;
            try
            {
                cachedJson = await conn.QuerySingleOrDefaultAsync<string>("""
                    SELECT igdb_assets FROM public.games_metadata
                    WHERE LOWER(game_name) = LOWER(@name)
                      AND igdb_assets IS NOT NULL
                      AND last_updated > NOW() - INTERVAL '7 days'
                    """, new { name = game });
            }
            catch { /* column may not exist yet */ }

            if (cachedJson is not null)
            {
                using var doc = JsonDocument.Parse(cachedJson);
                var banners = doc.RootElement.GetProperty("banners");
                var banner = banners.GetArrayLength() > 0 ? banners[0].GetString() : null;
                return Results.Ok(new { banner, cover = (string?)null, isCached = true });
            }

            var assets = await igdb.GetGameAssetsAsync(game, ct);
            if (assets is null)
                return Results.Ok(new { banner = (string?)null, cover = (string?)null, isCached = false });

            // Cache via full assets
            var response = new { banners = assets.Banners, cover = assets.Cover, videos = assets.Videos.Select(v => new { videoId = v.VideoId, name = v.Name }) };
            try
            {
                await conn.ExecuteAsync("""
                    INSERT INTO public.games_metadata (game_name, igdb_assets, last_updated)
                    VALUES (@name, @json::jsonb, NOW())
                    ON CONFLICT (game_name) DO UPDATE SET
                        igdb_assets = EXCLUDED.igdb_assets,
                        last_updated = EXCLUDED.last_updated
                    """, new { name = game, json = JsonSerializer.Serialize(response) });
            }
            catch { /* non-critical */ }

            return Results.Ok(new { banner = assets.Banners.Count > 0 ? assets.Banners[0] : null, cover = assets.Cover, isCached = false });
        }); // Public
    }
}
