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
            IDbConnectionFactory            db,
            HttpContext                     ctx,
            CancellationToken              ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.SponsorId) ||
                string.IsNullOrWhiteSpace(req.EventType))
                return Results.BadRequest(new { error = "sponsorId and eventType are required." });

            // Resolve client IP (handles proxies/load balancers)
            var ip = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
                  ?? ctx.Connection.RemoteIpAddress?.ToString()
                  ?? "0.0.0.0";

            // Daily privacy-preserving visitor ID: SHA256(IP:YYYY-MM-DD:salt)
            var today     = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var salt      = "esportra-visitor-v1"; // rotate annually
            var rawId     = $"{ip}:{today}:{salt}";
            var visitorId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawId)));

            // GeoIP via ip-api.com (free, no key needed)
            string? country = null;
            try
            {
                using var geoHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var geoRes = await geoHttp.GetStringAsync($"http://ip-api.com/json/{ip}?fields=countryCode", ct);
                using var geoDoc = JsonDocument.Parse(geoRes);
                country = geoDoc.RootElement.TryGetProperty("countryCode", out var cc)
                    ? cc.GetString() : null;
            }
            catch { /* GeoIP failure is non-fatal */ }

            // Optional: age group from authenticated user's profile
            string? ageGroup = null;
            var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? ctx.User.FindFirstValue("sub");

            if (!string.IsNullOrWhiteSpace(userId))
            {
                using var conn = db.CreateConnection();
                var dob = await conn.QuerySingleOrDefaultAsync<DateTime?>(
                    "SELECT date_of_birth FROM public.profiles WHERE id = @userId",
                    new { userId });

                if (dob.HasValue)
                {
                    var age = DateTime.UtcNow.Year - dob.Value.Year;
                    if (dob.Value.Date > DateTime.UtcNow.AddYears(-age)) age--;

                    ageGroup = age switch
                    {
                        < 13  => "under_13",
                        < 18  => "13_17",
                        < 25  => "18_24",
                        < 35  => "25_34",
                        _     => "35_plus",
                    };
                }
            }

            var metadata = JsonSerializer.Serialize(new { country, age_group = ageGroup });

            using var insertConn = db.CreateConnection();
            await insertConn.ExecuteAsync("""
                INSERT INTO public.sponsor_impressions
                    (sponsor_id, event_type, page_url, visitor_id, metadata)
                VALUES (@sponsorId, @eventType, @pageUrl, @visitorId, @metadata::jsonb)
                """, new
            {
                sponsorId = req.SponsorId,
                eventType = req.EventType,
                pageUrl   = req.PageUrl,
                visitorId,
                metadata,
            });

            return Results.Ok(new { success = true, visitorId });
        });
        // Note: intentionally no RequireAuthorization — metrics work for anonymous visitors too
    }
}
