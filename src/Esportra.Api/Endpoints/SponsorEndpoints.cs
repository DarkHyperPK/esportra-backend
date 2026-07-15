using System.Text.Json;
using System.Security.Claims;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Esportra.Contracts.Responses;
using Esportra.Api.Services;
using Esportra.Api.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Sponsor portal endpoints — replaces direct Supabase queries from partner-portal frontend.
/// Auth stays on Supabase; data access moves here.
/// </summary>
public static class SponsorEndpoints
{
    public static void MapSponsorEndpoints(this WebApplication app)
    {
        app.MapPost("/api/sponsor-invitations/preview", async (
            [FromBody] PreviewPartnerSponsorInvitationRequest request,
            PartnerSponsorOnboardingService invitations,
            CancellationToken cancellationToken) =>
        {
            var preview = await invitations.PreviewAsync(request.Token ?? string.Empty, cancellationToken);
            return preview is null
                ? Results.NotFound(new { error = "Invitation not found or expired." })
                : Results.Ok(preview);
        }).AllowAnonymous()
          .WithMetadata(new RateLimitPolicyMetadata("strict"));

        app.MapPost("/api/sponsor-invitations/accept", async (
            [FromBody] AcceptPartnerSponsorInvitationRequest request,
            HttpContext context,
            PartnerSponsorOnboardingService invitations,
            CancellationToken cancellationToken) =>
        {
            var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.FindFirstValue("sub");
            var email = context.User.FindFirstValue(ClaimTypes.Email)
                ?? context.User.FindFirstValue("email");
            if (!Guid.TryParse(userIdValue, out var userId) || string.IsNullOrWhiteSpace(email))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "Invalid invitation." });

            var claim = await invitations.ClaimAsync(request.Token, userId, email, cancellationToken);
            return claim is null
                ? Results.BadRequest(new { error = "This invitation cannot be accepted." })
                : Results.Ok(new { accepted = true, claim.SponsorId, claim.Role });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/sponsors/me ─────────────────────────────────────────────
        // Returns sponsor profile + account + stats + daily history for the authenticated partner user.
        app.MapGet("/api/sponsors/me", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var userCtx = ctx.Items["UserContext"] as UserContext;
                if (userCtx is null) return Results.Unauthorized();

                using var conn = db.CreateConnection();

                // 1. Get linked sponsor account
                var account = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                SELECT sa.sponsor_id, sa.role, sa.onboarding_meta
                FROM sponsor_accounts sa
                WHERE sa.user_id = @userId
                LIMIT 1
                """,
                    new { userId = userCtx.UserIdGuid });

                if (account is null)
                    return Results.NotFound(new { error = "No sponsor account linked to this user." });

                string sponsorId = account.sponsor_id.ToString();

                // 2. Get sponsor details
                var sponsor = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT * FROM sponsors WHERE id = @id",
                    new { id = Guid.Parse(sponsorId) });

                if (sponsor is null)
                    return Results.NotFound(new { error = "Sponsor not found." });

                // 3. Get stats (impression + click counts)
                var impressionCount = await conn.ExecuteScalarAsync<long>(
                    """
                SELECT COUNT(*) FROM sponsor_impressions
                WHERE sponsor_id = @sponsorId AND event_type = 'impression'
                """,
                    new { sponsorId = Guid.Parse(sponsorId) });

                var clickCount = await conn.ExecuteScalarAsync<long>(
                    """
                SELECT COUNT(*) FROM sponsor_impressions
                WHERE sponsor_id = @sponsorId AND event_type = 'click'
                """,
                    new { sponsorId = Guid.Parse(sponsorId) });

                // 4. Get daily history (last 90 days)
                var dailyStats = await conn.QueryAsync<dynamic>(
                    """
                SELECT stat_date, impressions, clicks, unique_impressions
                FROM daily_sponsor_stats
                WHERE sponsor_id = @sponsorId
                ORDER BY stat_date ASC
                LIMIT 90
                """,
                    new { sponsorId = Guid.Parse(sponsorId) });

                var history = dailyStats.Select(r => new
                {
                    date = r.stat_date is DateOnly d
                        ? d.ToString("yyyy-MM-dd")
                        : ((DateTime)r.stat_date).ToString("yyyy-MM-dd"),
                    impressions = (long)r.impressions,
                    uniqueImpressions = (long)r.unique_impressions,
                    clicks = (long)r.clicks,
                }).ToList();

                var totalUniqueImpressions = history.Sum(h => h.uniqueImpressions);

                return Results.Ok(new
                {
                    sponsor = MapSponsor(sponsor),
                    account = new
                    {
                        sponsor_id = sponsorId,
                        role = (string)account.role,
                        onboarding_meta = account.onboarding_meta is string json
                            ? JsonSerializer.Deserialize<object>(json)
                            : account.onboarding_meta,
                    },
                    stats = new
                    {
                        impressions = impressionCount,
                        uniqueImpressions = totalUniqueImpressions,
                        clicks = clickCount,
                        ctr = impressionCount > 0 ? Math.Round((double)clickCount / impressionCount * 100, 2) : 0.0,
                    },
                    history,
                });
            }
            catch (Exception ex)
            {
                var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "GET /api/sponsors/me failed");
                return Results.Json(new { error = "We couldn't load your sponsor profile. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/sponsors/me ─────────────────────────────────────────────
        // Update sponsor profile (name, tagline, description, website_url, cta_text, logo_url, banner, gallery)
        app.MapPut("/api/sponsors/me", async (
            [FromBody] SponsorUpdateRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify ownership
            var sponsorId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT sponsor_id FROM sponsor_accounts WHERE user_id = @userId AND role = 'owner' LIMIT 1",
                new { userId = userCtx.UserIdGuid });

            if (sponsorId is null)
                return Results.Forbid();

            // Fetch current tier for permission enforcement
            var currentTier = (await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT tier FROM sponsors WHERE id = @id", new { id = sponsorId.Value }))?.ToLowerInvariant() ?? "partner";
            // Map legacy tiers
            if (currentTier == "standard" || currentTier == "diamond") currentTier = "partner";
            var isHighTier = currentTier == "ascendant" || currentTier == "radiant";

            // Tier enforcement: partner tier cannot set banner, gallery, or detail deck
            if (!isHighTier)
            {
                if (req.BannerImageUrl is not null && req.BannerImageUrl != "")
                    return Results.Json(new { error = "Banner uploads require Ascendant or Radiant tier." }, statusCode: 403);
                if (req.GalleryImages is not null && req.GalleryImages.Length > 0)
                    return Results.Json(new { error = "Gallery uploads require Ascendant or Radiant tier." }, statusCode: 403);
                if (req.DetailDeckUrl is not null && req.DetailDeckUrl != "")
                    return Results.Json(new { error = "Detail deck uploads require Ascendant or Radiant tier." }, statusCode: 403);
            }

            // Gallery image count enforcement
            if (req.GalleryImages is not null)
            {
                var maxImages = currentTier == "radiant" ? 8 : currentTier == "ascendant" ? 5 : 0;
                if (req.GalleryImages.Length > maxImages)
                    return Results.Json(new { error = $"Your tier allows a maximum of {maxImages} gallery images." }, statusCode: 403);
            }

            // Build SET clause from non-null fields
            var sets = new List<string>();
            var p = new DynamicParameters();
            p.Add("id", sponsorId.Value);

            if (req.Name is not null) { sets.Add("name = @name"); p.Add("name", req.Name); }
            if (req.Tagline is not null) { sets.Add("tagline = @tagline"); p.Add("tagline", req.Tagline); }
            if (req.Description is not null) { sets.Add("description = @description"); p.Add("description", req.Description); }
            if (req.WebsiteUrl is not null) { sets.Add("website_url = @websiteUrl"); p.Add("websiteUrl", req.WebsiteUrl); }
            if (req.CtaText is not null) { sets.Add("cta_text = @ctaText"); p.Add("ctaText", req.CtaText); }
            if (req.LogoUrl is not null) { sets.Add("logo_url = @logoUrl"); p.Add("logoUrl", req.LogoUrl); }
            if (req.BannerImageUrl is not null) { sets.Add("banner_image_url = @bannerImageUrl"); p.Add("bannerImageUrl", req.BannerImageUrl); }
            if (req.GalleryImages is not null) { sets.Add("gallery_images = @galleryImages"); p.Add("galleryImages", req.GalleryImages); }
            if (req.DiscountText is not null) { sets.Add("discount_text = @discountText"); p.Add("discountText", req.DiscountText); }
            if (req.DetailDeckUrl is not null) { sets.Add("detail_deck_url = @detailDeckUrl"); p.Add("detailDeckUrl", req.DetailDeckUrl == "" ? (string?)null : req.DetailDeckUrl); }

            if (sets.Count == 0)
                return Results.BadRequest(new { error = "No fields to update." });

            var sql = $"UPDATE sponsors SET {string.Join(", ", sets)} WHERE id = @id RETURNING id, name, tagline, description, website_url, logo_url, banner_image_url, accent_color, tier, placement, cta_text, discount_text, is_active, priority, gallery_images, detail_deck_url, created_at";

            try
            {
                var row = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, p);
                return row is null ? Results.NotFound() : Results.Ok(MapSponsor(row));
            }
            catch (Exception ex)
            {
                var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogError(ex, "PUT /api/sponsors/me failed for sponsor {SponsorId}", sponsorId);
                return Results.Json(new { error = "We couldn't save your changes. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/sponsors/me/demographics ────────────────────────────────
        app.MapGet("/api/sponsors/me/demographics", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var sponsorId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT sponsor_id FROM sponsor_accounts WHERE user_id = @userId LIMIT 1",
                new { userId = userCtx.UserIdGuid });

            if (sponsorId is null)
                return Results.NotFound(new { error = "No sponsor account linked." });

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT metadata
                FROM sponsor_impressions
                WHERE sponsor_id = @sponsorId AND event_type = 'impression'
                """,
                new { sponsorId = sponsorId.Value });

            var countryMap = new Dictionary<string, int>();
            var ageMap = new Dictionary<string, int>();

            foreach (var row in rows)
            {
                string? metaJson = row.metadata?.ToString();
                if (string.IsNullOrEmpty(metaJson)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(metaJson);
                    var root = doc.RootElement;

                    var country = root.TryGetProperty("country", out var c) ? c.GetString() ?? "Unknown" : "Unknown";
                    var ageGroup = root.TryGetProperty("age_group", out var a) ? a.GetString() ?? "Unknown" : "Unknown";

                    countryMap[country] = countryMap.GetValueOrDefault(country) + 1;
                    ageMap[ageGroup] = ageMap.GetValueOrDefault(ageGroup) + 1;
                }
                catch { /* skip malformed metadata */ }
            }

            return Results.Ok(new
            {
                countries = countryMap
                    .Select(kv => new { name = kv.Key, count = kv.Value })
                    .OrderByDescending(x => x.count)
                    .Take(8)
                    .ToList(),
                ageGroups = ageMap
                    .Select(kv => new { group = kv.Key, count = kv.Value })
                    .OrderByDescending(x => x.count)
                    .ToList(),
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/sponsors/me/onboarding ──────────────────────────────────
        app.MapGet("/api/sponsors/me/onboarding", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT sponsor_id, onboarding_meta FROM sponsor_accounts WHERE user_id = @userId LIMIT 1",
                new { userId = userCtx.UserIdGuid });

            if (row is null)
                return Results.NotFound(new { error = "No sponsor account found." });

            return Results.Ok(new
            {
                sponsorId = row.sponsor_id.ToString(),
                meta = row.onboarding_meta is string json
                    ? JsonSerializer.Deserialize<object>(json)
                    : row.onboarding_meta,
            });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/sponsors/me/onboarding/step ─────────────────────────────
        app.MapPut("/api/sponsors/me/onboarding/step", async (
            [FromBody] OnboardingStepRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Get current meta
            if (req.StepName is not "branding" || req.NextStep is < 0 or > 1)
                return Results.BadRequest(new { error = "Invalid onboarding step." });

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT onboarding_meta FROM sponsor_accounts WHERE user_id = @userId AND role = 'owner' LIMIT 1",
                new { userId = userCtx.UserIdGuid });

            if (row is null)
                return Results.NotFound(new { error = "No sponsor account found." });

            // Parse current meta
            string currentJson = row.onboarding_meta?.ToString() ?? """{"completed":false,"current_step":0,"steps":{}}""";
            using var currentDoc = JsonDocument.Parse(currentJson);
            var currentMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(currentJson)
                              ?? new Dictionary<string, object>();

            // Update step data
            var steps = new Dictionary<string, object>();
            if (currentMeta.TryGetValue("steps", out var existingSteps) && existingSteps is JsonElement stepsEl)
            {
                steps = JsonSerializer.Deserialize<Dictionary<string, object>>(stepsEl.GetRawText())
                        ?? new Dictionary<string, object>();
            }
            steps[req.StepName] = req.StepData;

            currentMeta["current_step"] = req.NextStep;
            currentMeta["steps"] = steps;

            var updatedJson = JsonSerializer.Serialize(currentMeta);

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE sponsor_accounts
                SET onboarding_meta = @meta::jsonb
                WHERE user_id = @userId
                RETURNING onboarding_meta
                """,
                new { userId = userCtx.UserIdGuid, meta = updatedJson });

            if (updated is null)
                return Results.Json(new { error = "We couldn't save your changes. Please try again." }, statusCode: 500);

            return Results.Ok(new { success = true, meta = JsonSerializer.Deserialize<object>(updatedJson) });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/sponsors/me/onboarding/complete ────────────────────────
        app.MapPost("/api/sponsors/me/onboarding/complete", async (
            [FromBody] OnboardingCompleteRequest request,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT onboarding_meta FROM sponsor_accounts WHERE user_id = @userId AND role = 'owner' LIMIT 1",
                new { userId = userCtx.UserIdGuid });

            if (row is null)
                return Results.NotFound(new { error = "No sponsor account found." });

            if (!request.AcceptLegalTerms || request.TermsVersion != "2026-07")
                return Results.BadRequest(new { error = "Legal terms must be accepted." });

            string currentJson = row.onboarding_meta?.ToString() ?? """{"completed":false,"current_step":0,"steps":{}}""";
            var currentMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(currentJson)
                              ?? new Dictionary<string, object>();

            var steps = new Dictionary<string, object>();
            if (currentMeta.TryGetValue("steps", out var existingSteps) && existingSteps is JsonElement stepsEl)
            {
                steps = JsonSerializer.Deserialize<Dictionary<string, object>>(stepsEl.GetRawText())
                        ?? new Dictionary<string, object>();
            }
            if (!steps.ContainsKey("branding"))
                return Results.BadRequest(new { error = "Complete branding before finishing onboarding." });
            var ipAddress = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            steps["legal"] = new
            {
                agreed_at = DateTimeOffset.UtcNow,
                ip = ipAddress,
                terms_version = request.TermsVersion,
            };

            currentMeta["completed"] = true;
            currentMeta["current_step"] = 3;
            currentMeta["completed_at"] = DateTime.UtcNow.ToString("o");
            currentMeta["steps"] = steps;

            var updatedJson = JsonSerializer.Serialize(currentMeta);

            await conn.ExecuteAsync(
                """
                UPDATE sponsor_accounts
                SET onboarding_meta = @meta::jsonb
                WHERE user_id = @userId
                """,
                new { userId = userCtx.UserIdGuid, meta = updatedJson });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/sponsors/active ─────────────────────────────────────────
        // Public — returns active sponsors, optionally filtered by placement
        app.MapGet("/api/sponsors/active", async (
            [FromQuery] string? placement,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, name, tagline, description, website_url, logo_url, banner_image_url,
                       accent_color, tier, placement, cta_text, discount_text, is_active,
                       priority, gallery_images, detail_deck_url, start_date, end_date, created_at
                FROM sponsors
                WHERE is_active = TRUE
                ORDER BY priority DESC
                """);

            var now = DateTime.UtcNow;
            var sponsors = rows
                .Where(r =>
                {
                    if (r.start_date is not null && (DateTime)r.start_date > now) return false;
                    if (r.end_date is not null && (DateTime)r.end_date < now) return false;
                    return true;
                })
                .Where(r =>
                {
                    if (string.IsNullOrEmpty(placement)) return true;
                    var p = r.placement as string[];
                    return p?.Contains(placement) == true;
                })
                .Select(MapSponsor)
                .ToList();

            return Results.Ok(sponsors);
        });

        // ── GET /api/sponsors/all ────────────────────────────────────────────
        // Admin — returns all sponsors
        app.MapGet("/api/sponsors/all", async (
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, name, tagline, description, website_url, logo_url, banner_image_url,
                       accent_color, tier, placement, cta_text, discount_text, is_active,
                       priority, gallery_images, detail_deck_url, start_date, end_date, created_at
                FROM sponsors
                ORDER BY priority DESC
                """);

            return Results.Ok(rows.Select(MapSponsor).ToList());
        }).RequireAuthorization(Permissions.SponsorsView);

        // ── GET /api/system/branding ─────────────────────────────────────────
        // Public — returns platform logo + icon URLs
        app.MapGet("/api/system/branding", async (
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var result = await cache.GetOrCreateAsync("system-branding", async cancel =>
            {
                using var conn = db.CreateConnection();
                var rows = await conn.QueryAsync<dynamic>(
                    "SELECT key, value FROM system_settings WHERE key IN ('platform_logo_url', 'platform_icon_url')");

                var settings = rows.ToDictionary(
                    r => (string)r.key,
                    r => r.value?.ToString()?.Trim('"') ?? "");

                return new BrandingResponse(
                    LogoUrl: settings.GetValueOrDefault("platform_logo_url", "/logo.svg"),
                    IconUrl: settings.GetValueOrDefault("platform_icon_url", "/logo.svg"));
            }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1) }, cancellationToken: ct);

            return Results.Ok(result);
        });
    }

    /// <summary>Maps a dynamic DB row to a consistent sponsor shape.</summary>
    private static object MapSponsor(dynamic r)
    {
        var dict = (IDictionary<string, object>)r;
        return new
        {
            id = r.id.ToString(),
            name = (string?)r.name ?? "",
            tagline = (string?)r.tagline,
            description = (string?)r.description,
            website_url = (string?)r.website_url ?? "",
            logo_url = (string?)r.logo_url,
            banner_image_url = (string?)r.banner_image_url,
            accent_color = (string?)r.accent_color ?? "#8b5cf6",
            tier = (string?)r.tier ?? "standard",
            placement = r.placement as string[] ?? Array.Empty<string>(),
            cta_text = (string?)r.cta_text ?? "Learn More",
            discount_text = (string?)r.discount_text,
            is_active = (bool?)r.is_active ?? true,
            priority = (int?)r.priority ?? 0,
            gallery_images = r.gallery_images as string[] ?? Array.Empty<string>(),
            detail_deck_url = (string?)r.detail_deck_url,
            start_date = dict.TryGetValue("start_date", out var sd) ? sd?.ToString() : null,
            end_date = dict.TryGetValue("end_date", out var ed) ? ed?.ToString() : null,
            created_at = r.created_at?.ToString("o") ?? "",
        };
    }
}
