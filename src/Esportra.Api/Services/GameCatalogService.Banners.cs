using System.Data;
using Dapper;

namespace Esportra.Api.Services;

public sealed partial class GameCatalogService
{
    /// <summary>
    /// Returns the canonical catalog banner URL for emails and other server-side consumers.
    /// </summary>
    public async Task<string?> ResolveEmailBannerUrlAsync(string game, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(game)) return null;

        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<string>(
            """
            WITH active_version AS (
                SELECT id
                FROM public.game_catalog_versions
                WHERE is_active = TRUE AND status = 'active'
                LIMIT 1
            )
            SELECT g.banner_url
            FROM active_version av
            JOIN public.game_catalog_games g ON g.version_id = av.id
            LEFT JOIN public.game_catalog_game_aliases a
                ON a.version_id = av.id AND a.game_slug = g.slug
            WHERE g.banner_url IS NOT NULL
              AND (
                    LOWER(g.slug) = LOWER(@game)
                 OR LOWER(g.name) = LOWER(@game)
                 OR LOWER(a.alias) = LOWER(@game)
              )
            LIMIT 1
            """,
            new { game });
    }

    /// <summary>
    /// Backfills missing banner_url values on the active catalog from packaged seed data.
    /// </summary>
    public async Task BackfillActiveCatalogBannerUrlsAsync(CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        var version = await GetActiveVersionAsync(conn);
        if (version is null) return;

        var games = (await conn.QueryAsync<(string Slug, string Name)>(
            """
            SELECT slug, name
            FROM public.game_catalog_games
            WHERE version_id = @versionId
              AND banner_url IS NULL
            """,
            new { versionId = version.Id })).AsList();

        var updated = 0;
        foreach (var (slug, name) in games)
        {
            var bannerUrl = GameCatalogBannerSeed.TryResolve(slug, name);
            if (bannerUrl is null) continue;

            updated += await conn.ExecuteAsync(
                """
                UPDATE public.game_catalog_games
                SET banner_url = @bannerUrl
                WHERE version_id = @versionId
                  AND slug = @slug
                  AND banner_url IS NULL
                """,
                new { bannerUrl, versionId = version.Id, slug });
        }

        if (updated > 0)
        {
            logger.LogInformation("Backfilled banner_url for {Count} catalog game(s).", updated);
        }
    }

    private static string? ResolveBannerUrlForCatalogInsert(string slug, string name, string? explicitBannerUrl)
    {
        return GameCatalogBannerSeed.NormalizeAbsoluteHttpsUrl(explicitBannerUrl)
            ?? GameCatalogBannerSeed.TryResolve(slug, name);
    }
}
