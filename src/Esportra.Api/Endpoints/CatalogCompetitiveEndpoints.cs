using Dapper;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Public catalog endpoints for competitive game data (PROJ-075 T3).
/// GET /api/catalog/rank-tiers?game={slug}  — rank tiers for a game
/// GET /api/catalog/game-roles?game={slug}  — roles for a game
/// GET /api/catalog/regions?game={slug}     — regions for a game
/// No authentication required; data is read-only public catalog.
/// </summary>
public static class CatalogCompetitiveEndpoints
{
    public static void MapCatalogCompetitiveEndpoints(this WebApplication app)
    {
        app.MapGet("/api/catalog/rank-tiers", GetRankTiersAsync);
        app.MapGet("/api/catalog/game-roles", GetGameRolesAsync);
        app.MapGet("/api/catalog/regions", GetRegionsAsync);
    }

    // ── GET /api/catalog/rank-tiers?game={slug} ───────────────────────────────

    private static async Task<IResult> GetRankTiersAsync(
        [FromQuery] string? game,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game))
            return Results.BadRequest(new { error = "game query parameter is required." });

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CatalogRankTierRow>(
            """
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT id, game_slug, tier_key, display_name, sort_order, division_count, icon_url
            FROM public.game_catalog_rank_tiers
            WHERE game_slug = @game
              AND version_id = (SELECT id FROM active_v)
            ORDER BY sort_order ASC
            """,
            new { game });

        return Results.Ok(rows);
    }

    // ── GET /api/catalog/game-roles?game={slug} ───────────────────────────────

    private static async Task<IResult> GetGameRolesAsync(
        [FromQuery] string? game,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game))
            return Results.BadRequest(new { error = "game query parameter is required." });

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CatalogGameRoleRow>(
            """
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT id, game_slug, role_key, display_name, sort_order, icon_url
            FROM public.game_catalog_game_roles
            WHERE game_slug = @game
              AND version_id = (SELECT id FROM active_v)
            ORDER BY sort_order ASC
            """,
            new { game });

        return Results.Ok(rows);
    }

    // ── GET /api/catalog/regions?game={slug} ──────────────────────────────────

    private static async Task<IResult> GetRegionsAsync(
        [FromQuery] string? game,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game))
            return Results.BadRequest(new { error = "game query parameter is required." });

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<CatalogRegionRow>(
            """
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT id, game_slug, region_key, display_name
            FROM public.game_catalog_regions
            WHERE game_slug = @game
              AND version_id = (SELECT id FROM active_v)
            ORDER BY display_name ASC
            """,
            new { game });

        return Results.Ok(rows);
    }
}
