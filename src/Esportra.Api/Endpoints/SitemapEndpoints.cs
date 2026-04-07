using System.Text;
using Dapper;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Dynamic sitemap generation for SEO.
/// GET /sitemap.xml — returns XML sitemap with all public content
/// </summary>
public static class SitemapEndpoints
{
    private const string BaseUrl = "https://esportra.com";

    public static void MapSitemapEndpoints(this WebApplication app)
    {
        app.MapGet("/sitemap.xml", async (IDbConnectionFactory db, CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");

            // ── Static pages ──
            AddUrl(sb, "/", "daily", "1.0");
            AddUrl(sb, "/tournaments", "hourly", "0.9");
            AddUrl(sb, "/venues", "daily", "0.8");
            AddUrl(sb, "/venues/featured", "daily", "0.7");
            AddUrl(sb, "/leaderboards", "daily", "0.7");
            AddUrl(sb, "/tournament-history", "daily", "0.6");
            AddUrl(sb, "/about", "monthly", "0.4");
            AddUrl(sb, "/contact", "monthly", "0.4");
            AddUrl(sb, "/partners", "monthly", "0.4");
            AddUrl(sb, "/help", "monthly", "0.4");
            AddUrl(sb, "/privacy", "monthly", "0.3");
            AddUrl(sb, "/terms", "monthly", "0.3");

            // ── Tournaments (published, open, ongoing, completed) ──
            var tournaments = await conn.QueryAsync<(string slug, DateTime? updated_at)>(
                @"SELECT slug, updated_at FROM tournaments
                  WHERE slug IS NOT NULL AND status IN ('published','open','ongoing','completed')
                  ORDER BY updated_at DESC NULLS LAST
                  LIMIT 5000", ct);
            foreach (var t in tournaments)
                AddUrl(sb, $"/tournaments/{t.slug}", "daily", "0.8", t.updated_at);

            // ── Venues (published) ──
            var venues = await conn.QueryAsync<(string slug, DateTime? created_at)>(
                @"SELECT slug, created_at FROM venues
                  WHERE slug IS NOT NULL AND status = 'published'
                  ORDER BY created_at DESC NULLS LAST
                  LIMIT 5000", ct);
            foreach (var v in venues)
                AddUrl(sb, $"/venues/{v.slug}", "weekly", "0.7", v.created_at);

            // ── Public player profiles ──
            var players = await conn.QueryAsync<(string username, DateTime? updated_at)>(
                @"SELECT username, updated_at FROM profiles
                  WHERE username IS NOT NULL AND username != ''
                  ORDER BY updated_at DESC NULLS LAST
                  LIMIT 10000", ct);
            foreach (var p in players)
                AddUrl(sb, $"/player/{p.username}", "weekly", "0.5", p.updated_at);

            // ── Organizations ──
            var orgs = await conn.QueryAsync<(string slug, DateTime? updated_at)>(
                @"SELECT slug, updated_at FROM organizations
                  WHERE slug IS NOT NULL
                  ORDER BY updated_at DESC NULLS LAST
                  LIMIT 5000", ct);
            foreach (var o in orgs)
                AddUrl(sb, $"/org/{o.slug}", "weekly", "0.6", o.updated_at);

            sb.AppendLine("</urlset>");

            return Results.Content(sb.ToString(), "application/xml", Encoding.UTF8);
        }).AllowAnonymous();
    }

    private static void AddUrl(StringBuilder sb, string path, string changefreq, string priority, DateTime? lastmod = null)
    {
        sb.AppendLine("  <url>");
        sb.Append("    <loc>").Append(BaseUrl).Append(path).AppendLine("</loc>");
        if (lastmod.HasValue)
            sb.Append("    <lastmod>").Append(lastmod.Value.ToString("yyyy-MM-dd")).AppendLine("</lastmod>");
        sb.Append("    <changefreq>").Append(changefreq).AppendLine("</changefreq>");
        sb.Append("    <priority>").Append(priority).AppendLine("</priority>");
        sb.AppendLine("  </url>");
    }
}
