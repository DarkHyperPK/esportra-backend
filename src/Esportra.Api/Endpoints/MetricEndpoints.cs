using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Requests;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: record-metric Edge Function.
/// POST /api/metrics — analytics impressions with GeoIP + visitor fingerprinting.
/// </summary>
public static class MetricEndpoints
{
    public static void MapMetricEndpoints(this WebApplication app)
    {
        app.MapPost("/api/metrics", async (
            [FromBody] RecordMetricRequest req,
            IDbConnectionFactory db,
            IHttpClientFactory httpFactory,
            IConfiguration config,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.SponsorId) ||
                string.IsNullOrWhiteSpace(req.EventType))
                return Results.BadRequest(new { error = "sponsorId and eventType are required." });

            // Resolve client IP (handles proxies/load balancers)
            var ip = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                  ?? ctx.Connection.RemoteIpAddress?.ToString()
                  ?? "0.0.0.0";

            // Daily privacy-preserving visitor ID: SHA256(IP:YYYY-MM-DD:salt)
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var salt = config["Metrics:VisitorSalt"] ?? "esportra-visitor-v1";
            var rawId = $"{ip}:{today}:{salt}";
            var visitorId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawId)));

            // For authenticated users: get country + age from their profile (reliable).
            // For anonymous: fall back to GeoIP for country only.
            string? country = null;
            string? ageGroup = null;
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");

            using var conn = db.CreateConnection();

            if (!string.IsNullOrWhiteSpace(userId))
            {
                var profile = await conn.QuerySingleOrDefaultAsync<(string? country_code, DateTime? date_of_birth)>(
                    "SELECT country_code, date_of_birth FROM public.profiles WHERE id = @userId",
                    new { userId });

                country = profile.country_code;

                if (profile.date_of_birth.HasValue)
                {
                    var age = DateTime.UtcNow.Year - profile.date_of_birth.Value.Year;
                    if (profile.date_of_birth.Value.Date > DateTime.UtcNow.AddYears(-age)) age--;

                    ageGroup = age switch
                    {
                        < 13 => "under_13",
                        < 18 => "13_17",
                        < 25 => "18_24",
                        < 35 => "25_34",
                        _ => "35_plus",
                    };
                }
            }

            // GeoIP fallback only for anonymous visitors (no profile data)
            if (string.IsNullOrEmpty(country) && string.IsNullOrWhiteSpace(userId))
            {
                try
                {
                    var geoHttp = httpFactory.CreateClient("GeoIP");
                    var geoRes = await geoHttp.GetStringAsync($"https://ipapi.co/{ip}/json/", ct);
                    using var geoDoc = JsonDocument.Parse(geoRes);
                    country = geoDoc.RootElement.TryGetProperty("country_code", out var cc)
                        ? cc.GetString() : null;
                }
                catch { /* GeoIP failure is non-fatal */ }
            }

            var metadata = JsonSerializer.Serialize(new { country, age_group = ageGroup });

            await conn.ExecuteAsync("""
                INSERT INTO public.sponsor_impressions
                    (sponsor_id, event_type, page_url, visitor_id, metadata)
                VALUES (@sponsorId, @eventType, @pageUrl, @visitorId, @metadata::jsonb)
                """, new
            {
                sponsorId = req.SponsorId,
                eventType = req.EventType,
                pageUrl = req.PageUrl,
                visitorId,
                metadata,
            });

            return Results.Ok(new { success = true, visitorId });
        });
        // Note: intentionally no RequireAuthorization — metrics work for anonymous visitors too
    }
}
