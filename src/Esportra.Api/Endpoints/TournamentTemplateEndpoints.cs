using System.Security.Cryptography;
using System.Text;
using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

public static class TournamentTemplateEndpoints
{
    public static void MapTournamentTemplateEndpoints(this WebApplication app)
    {
        // ── GET /api/tournament-templates ──────────────────────────────────────
        app.MapGet("/api/tournament-templates", async (
            IDbConnectionFactory db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            var templates = (await conn.QueryAsync<TournamentTemplateResponse>(
                """
                SELECT
                    tt.id, tt.game_catalog_id, tt.slug, tt.rules_text, tt.rules_source_url,
                    tt.rules_updated_at, tt.default_best_of, tt.default_max_teams,
                    tt.recommended_team_counts, tt.is_publisher_endorsed, tt.is_active,
                    tt.rules_stale, tt.sort_order, tt.created_at,
                    g.name AS game_name, g.slug AS game_slug, g.game_type,
                    g.default_mode_key, g.banner_url, g.logo_url, g.icon_url
                FROM tournament_templates tt
                INNER JOIN game_catalog_games g ON tt.game_catalog_id = g.id
                INNER JOIN game_catalog_versions v ON g.version_id = v.id
                WHERE tt.is_active = true AND v.is_active = true AND v.status = 'active'
                ORDER BY tt.sort_order
                """)).AsList();

            var templateList = templates.ToList();
            var contentHash = ComputeContentHash(templateList);
            ctx.Response.Headers.CacheControl = "public, max-age=600";
            ctx.Response.Headers.ETag = $"\"{contentHash}\"";

            if (MatchesETag(ctx.Request.Headers.IfNoneMatch.ToString(), contentHash))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            return Results.Ok(templateList);
        }); // Public — no auth required

        // ── GET /api/tournament-templates/{slug} ───────────────────────────────
        app.MapGet("/api/tournament-templates/{slug}", async (
            string slug,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            var template = await conn.QuerySingleOrDefaultAsync<TournamentTemplateResponse>(
                """
                SELECT
                    tt.id, tt.game_catalog_id, tt.slug, tt.rules_text, tt.rules_source_url,
                    tt.rules_updated_at, tt.default_best_of, tt.default_max_teams,
                    tt.recommended_team_counts, tt.is_publisher_endorsed, tt.is_active,
                    tt.rules_stale, tt.sort_order, tt.created_at,
                    g.name AS game_name, g.slug AS game_slug, g.game_type,
                    g.default_mode_key, g.banner_url, g.logo_url, g.icon_url
                FROM tournament_templates tt
                INNER JOIN game_catalog_games g ON tt.game_catalog_id = g.id
                INNER JOIN game_catalog_versions v ON g.version_id = v.id
                WHERE tt.slug = @slug AND tt.is_active = true AND v.is_active = true AND v.status = 'active'
                """, new { slug });

            return template is null
                ? Results.NotFound(new { error = "Tournament template not found." })
                : Results.Ok(template);
        }); // Public — no auth required
    }

    private static string ComputeContentHash(IEnumerable<TournamentTemplateResponse> templates)
    {
        var combined = string.Join("|", templates.Select(t =>
            $"{t.Id}:{t.Slug}:{t.RulesUpdatedAt:O}:{t.IsActive}:{t.SortOrder}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static bool MatchesETag(string? ifNoneMatch, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
        var incomingETag = ifNoneMatch.Trim('"');
        return incomingETag.Equals(contentHash, StringComparison.OrdinalIgnoreCase);
    }
}

// ── Response records ──────────────────────────────────────────────────────────

public sealed record TournamentTemplateResponse(
    Guid Id,
    Guid GameCatalogId,
    string Slug,
    string RulesText,
    string? RulesSourceUrl,
    DateTime RulesUpdatedAt,
    int DefaultBestOf,
    int DefaultMaxTeams,
    int[] RecommendedTeamCounts,
    bool IsPublisherEndorsed,
    bool IsActive,
    bool RulesStale,
    int SortOrder,
    DateTime CreatedAt,
    string GameName,
    string GameSlug,
    string GameType,
    string DefaultModeKey,
    string? BannerUrl,
    string? LogoUrl,
    string? IconUrl);
