using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Esportra.Contracts.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Endpoints;

public static class TournamentSponsorEndpoints
{
    private static readonly Dictionary<string, int> ZoneCapacities = new(StringComparer.Ordinal)
    {
        ["homepage_ticker"] = 10,
        ["partner_showcase"] = 6,
        ["sidebar_partner"] = 2,
        ["wide_partner"] = 4,
        ["card_badge"] = 1,
        ["partner_logo"] = 4,
    };

    private static readonly HashSet<string> GlobalZones = new(StringComparer.Ordinal)
    {
        "homepage_ticker", "partner_showcase",
    };

    private static readonly Dictionary<string, HashSet<string>> TierZones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["partner"] = new(StringComparer.Ordinal) { "homepage_ticker" },
        ["standard"] = new(StringComparer.Ordinal) { "homepage_ticker" },
        ["diamond"] = new(StringComparer.Ordinal) { "homepage_ticker" },
        ["ascendant"] = new(StringComparer.Ordinal) { "homepage_ticker", "sidebar_partner", "wide_partner", "card_badge" },
        ["radiant"] = new(StringComparer.Ordinal) { "homepage_ticker", "sidebar_partner", "wide_partner", "card_badge", "partner_logo", "partner_showcase" },
    };

    private static readonly HashSet<string> AllZones = new(StringComparer.Ordinal)
    {
        "homepage_ticker", "partner_showcase", "sidebar_partner", "wide_partner", "card_badge", "partner_logo",
    };

    public static void MapTournamentSponsorEndpoints(this WebApplication app)
    {
        // Public read
        app.MapGet("/api/tournaments/{tournamentId:guid}/sponsors", GetTournamentSponsorsAsync);
        app.MapGet("/api/placements/global", GetGlobalPlacementsAsync);
        app.MapGet("/api/sponsors/me/placements", GetMyPlacementsAsync)
            .RequireAuthorization("Authenticated");

        // Admin CRUD
        app.MapGet("/api/admin/placements", GetAllPlacementsAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapGet("/api/admin/placements/{id:guid}", GetPlacementByIdAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapPost("/api/admin/placements", CreatePlacementAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapPut("/api/admin/placements/{id:guid}", UpdatePlacementAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapPut("/api/admin/placements/{id:guid}/resolve", ResolvePlacementReviewAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapPut("/api/admin/placements/{id:guid}/metadata", UpdatePlacementMetadataAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapPut("/api/admin/placements/{id:guid}/replace-creative", ReplaceCreativeAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapPost("/api/admin/placements/{id:guid}/remove-creative", RemoveCreativeAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapPost("/api/admin/placements/{id:guid}/unassign", UnassignPlacementAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapDelete("/api/admin/placements/{id:guid}", DeletePlacementAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapGet("/api/admin/sponsors/{sponsorId:guid}/placements", GetSponsorPlacementsAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
        app.MapGet("/api/admin/tournaments/{tournamentId:guid}/placements", GetTournamentPlacementsAdminAsync)
            .RequireAuthorization(Permissions.SponsorsEdit);
    }

    // ─── Public: Tournament sponsors (consumed by frontend components) ───

    private static async Task<IResult> GetTournamentSponsorsAsync(
        Guid tournamentId,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<PlacementRow>(new CommandDefinition(
            """
            SELECT p.id, p.sponsor_id, p.placement_zone, p.slot_number, p.banner_url, p.logo_url,
                   p.headline, p.cta_text, p.cta_url, p.priority,
                   s.name AS sponsor_name, s.tagline AS sponsor_tagline,
                   s.logo_url AS sponsor_logo_url, s.banner_image_url AS sponsor_banner_url,
                   s.accent_color AS sponsor_accent_color, s.tier AS sponsor_tier,
                   s.website_url AS sponsor_website_url, s.cta_text AS sponsor_cta_text,
                   s.gallery_images AS sponsor_gallery_images
            FROM public.sponsor_placements p
            JOIN public.sponsors s ON s.id = p.sponsor_id
            WHERE p.tournament_id = @tournamentId
              AND p.is_active = true
                            AND p.slot_number IS NOT NULL
                            AND p.review_reason IS NULL
                            AND s.is_active = true
                              AND CASE
                                  WHEN p.placement_zone IN ('partner_logo', 'card_badge', 'homepage_ticker') THEN COALESCE(p.logo_url, '') <> ''
                                        ELSE COALESCE(p.banner_url, '') <> ''
                                    END
              AND (p.starts_at IS NULL OR p.starts_at <= NOW())
              AND (p.ends_at IS NULL OR p.ends_at > NOW())
            ORDER BY p.priority DESC, p.created_at ASC
            """,
            new { tournamentId },
            cancellationToken: ct))).AsList();

        var placements = rows.Select(row =>
        {
            var mediaOverrides = new Dictionary<string, string>();
            if (row.BannerUrl is not null)
                mediaOverrides[$"{row.PlacementZone}_banner"] = row.BannerUrl;
            if (row.LogoUrl is not null)
                mediaOverrides[$"{row.PlacementZone}_logo"] = row.LogoUrl;

            return new TournamentSponsorLinkDto(
                row.Id.ToString(),
                row.SponsorId.ToString(),
                "event_sponsor",
                [row.PlacementZone],
                mediaOverrides.Count > 0 ? mediaOverrides : null,
                row.Priority,
                row.SlotNumber,
                row.Headline,
                row.CtaText,
                NormalizeDestinationUrl(row.CtaUrl),
                new TournamentSponsorDto(
                    row.SponsorId.ToString(),
                    row.SponsorName,
                    row.SponsorTagline,
                    row.SponsorLogoUrl,
                    row.SponsorBannerUrl,
                    row.SponsorAccentColor ?? "#000000",
                    row.SponsorTier ?? "partner",
                    row.CtaText ?? row.SponsorCtaText,
                    null,
                    row.SponsorGalleryImages));
        }).ToList();

        return Results.Ok(placements);
    }

    // ─── Public: Global placements ───────────────────────────────────────

    private static async Task<IResult> GetGlobalPlacementsAsync(
        IDbConnectionFactory connectionFactory,
        CancellationToken ct,
        string? zone = null)
    {
        using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<GlobalPlacementDto>(new CommandDefinition(
            """
                 SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.placement_zone AS PlacementZone,
                     p.slot_number AS SlotNumber,
                   p.banner_url AS BannerUrl, p.logo_url AS LogoUrl, p.headline AS Headline,
                   p.cta_text AS CtaText, p.cta_url AS CtaUrl, p.priority AS Priority,
                   s.name AS SponsorName, s.tier AS SponsorTier,
                   s.logo_url AS SponsorLogoUrl, s.banner_image_url AS SponsorBannerUrl,
                   s.website_url AS SponsorWebsiteUrl
            FROM public.sponsor_placements p
            JOIN public.sponsors s ON s.id = p.sponsor_id
            WHERE p.tournament_id IS NULL
              AND p.is_active = true
              AND p.slot_number IS NOT NULL
              AND p.review_reason IS NULL
              AND s.is_active = true
                            AND CASE
                                        WHEN p.placement_zone = 'homepage_ticker' THEN COALESCE(p.logo_url, '') <> ''
                                        WHEN p.placement_zone = 'partner_showcase' THEN COALESCE(p.banner_url, '') <> ''
                                        ELSE false
                                    END
              AND (p.starts_at IS NULL OR p.starts_at <= NOW())
              AND (p.ends_at IS NULL OR p.ends_at > NOW())
              AND (@zone IS NULL OR p.placement_zone = @zone)
            ORDER BY p.priority DESC, p.created_at ASC
            """,
            new { zone },
            cancellationToken: ct));

        return Results.Ok(rows.Select(row => row with { CtaUrl = NormalizeDestinationUrl(row.CtaUrl) }));
    }

    // ─── Admin: List all placements ──────────────────────────────────────

    private static async Task<IResult> GetMyPlacementsAsync(
            HttpContext context,
            IDbConnectionFactory connectionFactory,
            CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null) return Results.Unauthorized();

        using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PlacementDto>(new CommandDefinition(
                """
                        SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.tournament_id AS TournamentId,
                                     p.placement_zone AS PlacementZone, p.slot_number AS SlotNumber, p.banner_url AS BannerUrl,
                                     p.banner_asset_id AS BannerAssetId, p.logo_url AS LogoUrl, p.logo_asset_id AS LogoAssetId,
                                     p.headline AS Headline, p.cta_text AS CtaText,
                                     p.cta_url AS CtaUrl, p.priority AS Priority, p.is_active AS IsActive,
                                     p.starts_at AS StartsAt, p.ends_at AS EndsAt, p.created_at AS CreatedAt,
                                     p.updated_at AS UpdatedAt,
                                     CASE
                                         WHEN p.review_reason IS NOT NULL THEN 'review'
                                         WHEN NOT p.is_active THEN 'inactive'
                                         WHEN p.slot_number IS NULL
                                             OR (p.placement_zone IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.logo_url, '') = '')
                                             OR (p.placement_zone NOT IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.banner_url, '') = '') THEN 'draft'
                                         WHEN p.starts_at > NOW() THEN 'scheduled'
                                         WHEN p.ends_at <= NOW() THEN 'expired'
                                         ELSE 'live'
                                     END AS Lifecycle, p.review_reason AS ReviewReason,
                                     s.name AS SponsorName, s.tier AS SponsorTier,
                                     s.logo_url AS SponsorLogoUrl, NULL::text AS SponsorWebsiteUrl,
                                     t.name AS TournamentName
                        FROM public.sponsor_accounts account
                        JOIN public.sponsor_placements p ON p.sponsor_id = account.sponsor_id
                        JOIN public.sponsors s ON s.id = p.sponsor_id
                        LEFT JOIN public.tournaments t ON t.id = p.tournament_id
                        WHERE account.user_id = @userId AND account.status = 'active'
                        ORDER BY p.updated_at DESC
                        """,
                new { userId = userContext.UserIdGuid },
                cancellationToken: ct));

        return Results.Ok(rows);
    }

    private static async Task<IResult> GetAllPlacementsAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct,
        Guid? sponsorId = null,
        Guid? tournamentId = null,
                string? zone = null,
                string? scope = null,
                string? status = null,
                string? search = null,
                string? sortBy = null,
                string? sortDirection = null,
                int page = 1,
                int pageSize = 25)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        if (page < 1 || page > 1_000_000)
            return Results.BadRequest(new { error = "Page must be between 1 and 1000000." });
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (search?.Length > 100)
            return Results.BadRequest(new { error = "Search cannot exceed 100 characters." });
        var normalizedScope = scope?.ToLowerInvariant();
        if (normalizedScope is not (null or "all" or "global" or "tournament"))
            return Results.BadRequest(new { error = "Invalid placement scope." });

        var normalizedStatus = status?.ToLowerInvariant();
        var allowedStatuses = new HashSet<string>(["live", "draft", "scheduled", "inactive", "expired", "review"]);
        if (normalizedStatus is not null && !allowedStatuses.Contains(normalizedStatus))
            return Results.BadRequest(new { error = "Invalid placement status." });

        var orderColumn = sortBy?.ToLowerInvariant() switch
        {
            "sponsor" => "SponsorName",
            "tournament" => "TournamentName",
            "zone" => "PlacementZone",
            "status" => "Lifecycle",
            "starts" => "StartsAt",
            "ends" => "EndsAt",
            _ => "UpdatedAt",
        };
        var orderDirection = sortDirection?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true ? "ASC" : "DESC";
        var offset = checked((long)(page - 1) * pageSize);

        using var connection = connectionFactory.CreateConnection();
        const string inventoryCte = """
                        WITH inventory AS (
                            SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.tournament_id AS TournamentId,
                                     p.placement_zone AS PlacementZone, p.slot_number AS SlotNumber, p.banner_url AS BannerUrl,
                                     p.banner_asset_id AS BannerAssetId, p.logo_url AS LogoUrl, p.logo_asset_id AS LogoAssetId,
                                     p.headline AS Headline, p.cta_text AS CtaText,
                                     p.cta_url AS CtaUrl, p.priority AS Priority, p.is_active AS IsActive,
                                     p.starts_at AS StartsAt, p.ends_at AS EndsAt, p.created_at AS CreatedAt,
                                     p.updated_at AS UpdatedAt,
                                     CASE
                                         WHEN p.review_reason IS NOT NULL THEN 'review'
                                         WHEN NOT p.is_active THEN 'inactive'
                                         WHEN p.slot_number IS NULL
                                             OR (p.placement_zone IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.logo_url, '') = '')
                                             OR (p.placement_zone NOT IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.banner_url, '') = '') THEN 'draft'
                                         WHEN p.starts_at > NOW() THEN 'scheduled'
                                         WHEN p.ends_at <= NOW() THEN 'expired'
                                         ELSE 'live'
                                     END AS Lifecycle, p.review_reason AS ReviewReason,
                                     s.name AS SponsorName, s.tier AS SponsorTier,
                                     s.logo_url AS SponsorLogoUrl, s.website_url AS SponsorWebsiteUrl,
                                     t.name AS TournamentName
                            FROM public.sponsor_placements p
                            JOIN public.sponsors s ON s.id = p.sponsor_id
                            LEFT JOIN public.tournaments t ON t.id = p.tournament_id
                            WHERE (@sponsorId IS NULL OR p.sponsor_id = @sponsorId)
                                AND (@tournamentId IS NULL OR p.tournament_id = @tournamentId)
                                AND (@zone IS NULL OR p.placement_zone = @zone)
                                AND (@scope IS NULL OR @scope = 'all'
                                    OR (@scope = 'global' AND p.tournament_id IS NULL)
                                    OR (@scope = 'tournament' AND p.tournament_id IS NOT NULL))
                                AND (@search IS NULL OR s.name ILIKE '%' || @search || '%'
                                    OR t.name ILIKE '%' || @search || '%' OR p.placement_zone ILIKE '%' || @search || '%')
                        )
                        """;
        var sql = $$"""
                        {{inventoryCte}}
                        SELECT * FROM inventory
                        WHERE (@status IS NULL OR Lifecycle = @status)
                        ORDER BY {{orderColumn}} {{orderDirection}}, Id
                        LIMIT @pageSize OFFSET @offset;
                        {{inventoryCte}}
                        SELECT COUNT(*) FROM inventory WHERE (@status IS NULL OR Lifecycle = @status);
                        {{inventoryCte}}
                        SELECT Lifecycle AS Status, COUNT(*) AS Count FROM inventory GROUP BY Lifecycle;
                        """;
        var parameters = new
        {
            sponsorId,
            tournamentId,
            zone,
            scope = normalizedScope,
            status = normalizedStatus,
            search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            pageSize,
            offset,
        };
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        var items = (await grid.ReadAsync<PlacementDto>()).AsList();
        var total = await grid.ReadSingleAsync<long>();
        var counts = (await grid.ReadAsync<StatusCountRow>()).ToDictionary(row => row.Status, row => row.Count);

        return Results.Ok(new PlacementPageDto(items, page, pageSize, total, counts));
    }

    private static async Task<IResult> GetPlacementByIdAsync(
        Guid id,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<PlacementDto>(new CommandDefinition(
            """
                 SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.tournament_id AS TournamentId,
                                         p.placement_zone AS PlacementZone, p.slot_number AS SlotNumber, p.banner_url AS BannerUrl,
                                     p.banner_asset_id AS BannerAssetId, p.logo_url AS LogoUrl, p.logo_asset_id AS LogoAssetId,
                                     p.headline AS Headline, p.cta_text AS CtaText,
                   p.cta_url AS CtaUrl, p.priority AS Priority, p.is_active AS IsActive,
                                     p.starts_at AS StartsAt, p.ends_at AS EndsAt, p.created_at AS CreatedAt,
                                     p.updated_at AS UpdatedAt,
                                     CASE
                                         WHEN p.review_reason IS NOT NULL THEN 'review'
                                         WHEN NOT p.is_active THEN 'inactive'
                                         WHEN p.slot_number IS NULL
                                             OR (p.placement_zone IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.logo_url, '') = '')
                                             OR (p.placement_zone NOT IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.banner_url, '') = '') THEN 'draft'
                                         WHEN p.starts_at > NOW() THEN 'scheduled'
                                         WHEN p.ends_at <= NOW() THEN 'expired'
                                         ELSE 'live'
                                     END AS Lifecycle, p.review_reason AS ReviewReason,
                   s.name AS SponsorName, s.tier AS SponsorTier,
                   s.logo_url AS SponsorLogoUrl, s.website_url AS SponsorWebsiteUrl,
                   t.name AS TournamentName
            FROM public.sponsor_placements p
            JOIN public.sponsors s ON s.id = p.sponsor_id
            LEFT JOIN public.tournaments t ON t.id = p.tournament_id
            WHERE p.id = @id
            """,
            new { id },
            cancellationToken: ct));

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    // ─── Admin: Create placement ─────────────────────────────────────────

    private static async Task<IResult> CreatePlacementAsync(
        [FromBody] CreatePlacementRequest request,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        HybridCache cache,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        if (!AllZones.Contains(request.PlacementZone))
            return Results.UnprocessableEntity(new { error = $"Invalid placement zone: {request.PlacementZone}" });

        if (!SponsorPlacementPolicy.TryGet(request.PlacementZone, out var placementPolicy)
            || !SponsorPlacementPolicy.IsScopeValid(placementPolicy, request.TournamentId))
            return Results.UnprocessableEntity(new { error = "The selected placement zone has an invalid scope." });

        if (!SponsorPlacementPolicy.IsSlotValid(placementPolicy, request.SlotNumber))
            return Results.UnprocessableEntity(new { error = $"Slot must be between 1 and {placementPolicy.Capacity}." });

        if (request.StartsAt.HasValue && request.EndsAt.HasValue && request.StartsAt >= request.EndsAt)
            return Results.UnprocessableEntity(new { error = "End date must be after start date." });

        if (!TryNormalizeOptionalUrl(request.CtaUrl, out var destinationUrl))
            return Results.UnprocessableEntity(new { error = "Destination URL must be a valid HTTP or HTTPS URL." });

        if (request.Priority is < -1000 or > 1000)
            return Results.UnprocessableEntity(new { error = "Priority must be between -1000 and 1000." });

        if (!IsCopyLengthValid(request.Headline, 120) || !IsCopyLengthValid(request.CtaText, 60) || !IsCopyLengthValid(request.CtaUrl, 2048))
            return Results.UnprocessableEntity(new { error = "Placement copy exceeds the allowed length." });

        if (!TryNormalizeMediaUrl(request.BannerUrl, out var bannerUrl)
            || !TryNormalizeMediaUrl(request.LogoUrl, out var logoUrl))
            return Results.UnprocessableEntity(new { error = "Creative URLs must use HTTPS." });
        if ((placementPolicy.RequiredRole == SponsorCreativeRole.Banner && !request.BannerAssetId.HasValue)
            || (placementPolicy.RequiredRole == SponsorCreativeRole.Logo && !request.LogoAssetId.HasValue))
            return Results.UnprocessableEntity(new { error = "The placement requires a validated uploaded creative." });

        using var connection = connectionFactory.CreateConnection();

        var sponsor = await connection.QuerySingleOrDefaultAsync<(string? Tier, bool IsActive)>(new CommandDefinition(
            "SELECT tier AS Tier, is_active AS IsActive FROM public.sponsors WHERE id = @id",
            new { id = request.SponsorId },
            cancellationToken: ct));

        if (sponsor.Tier is null)
            return Results.NotFound(new { error = "Sponsor not found." });

        if (!sponsor.IsActive)
            return Results.UnprocessableEntity(new { error = "Inactive sponsors cannot be assigned." });

        if (!TierZones.ContainsKey(sponsor.Tier))
            return Results.UnprocessableEntity(new { error = $"Unknown sponsor tier '{sponsor.Tier}'." });
        if (!SponsorPlacementPolicy.IsTierAllowed(placementPolicy, sponsor.Tier))
            return Results.UnprocessableEntity(new { error = $"Sponsor tier '{sponsor.Tier}' does not have access to zone '{request.PlacementZone}'." });

        if (request.TournamentId.HasValue)
        {
            var tournamentExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM public.tournaments WHERE id = @id)",
                new { id = request.TournamentId.Value },
                cancellationToken: ct));
            if (!tournamentExists)
                return Results.NotFound(new { error = "Tournament not found." });
        }

        using var transaction = connection.BeginTransaction();
        var bannerAsset = await GetClaimableAssetAsync(connection, transaction, request.BannerAssetId, null, request.PlacementZone, "banner", userContext!.UserIdGuid, ct);
        var logoAsset = await GetClaimableAssetAsync(connection, transaction, request.LogoAssetId, null, request.PlacementZone, "logo", userContext.UserIdGuid, ct);
        if ((request.BannerAssetId.HasValue && bannerAsset is null) || (request.LogoAssetId.HasValue && logoAsset is null))
            return Results.UnprocessableEntity(new { error = "Creative asset is invalid, expired, or already claimed." });

        var id = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            INSERT INTO public.sponsor_placements
                (sponsor_id, tournament_id, placement_zone, slot_number, banner_url, banner_asset_id, logo_url, logo_asset_id,
                 headline, cta_text, cta_url, priority, is_active,
                 assigned_by, starts_at, ends_at)
            VALUES
                (@SponsorId, @TournamentId, @PlacementZone, @SlotNumber, @BannerUrl, @BannerAssetId, @LogoUrl, @LogoAssetId,
                 @Headline, @CtaText, @CtaUrl, @Priority, @IsActive,
                 @AssignedBy, @StartsAt, @EndsAt)
            ON CONFLICT DO NOTHING
            RETURNING id
            """,
            new
            {
                request.SponsorId,
                request.TournamentId,
                request.PlacementZone,
                request.SlotNumber,
                BannerUrl = bannerAsset?.PublicUrl ?? bannerUrl,
                request.BannerAssetId,
                LogoUrl = logoAsset?.PublicUrl ?? logoUrl,
                request.LogoAssetId,
                request.Headline,
                request.CtaText,
                CtaUrl = destinationUrl,
                request.Priority,
                request.IsActive,
                AssignedBy = userContext!.UserIdGuid,
                request.StartsAt,
                request.EndsAt,
            },
            transaction, cancellationToken: ct));

        if (id is null)
        {
            transaction.Rollback();
            return Results.Conflict(new { error = "The slot is occupied or this sponsor is already assigned to the zone." });
        }

        await ClaimAssetsAsync(connection, transaction, [request.BannerAssetId, request.LogoAssetId], ct);
        transaction.Commit();
        try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }

        return Results.Created($"/api/admin/placements/{id}", new { id });
    }

    // ─── Admin: Update placement ─────────────────────────────────────────

    private static async Task<IResult> UpdatePlacementAsync(
        Guid id,
        [FromBody] UpdatePlacementRequest request,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        HybridCache cache,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        if (request.StartsAt.HasValue && request.EndsAt.HasValue && request.StartsAt >= request.EndsAt)
            return Results.UnprocessableEntity(new { error = "End date must be after start date." });

        if (!TryNormalizeOptionalUrl(request.CtaUrl, out var destinationUrl))
            return Results.UnprocessableEntity(new { error = "Destination URL must be a valid HTTP or HTTPS URL." });

        if (request.Priority is < -1000 or > 1000)
            return Results.UnprocessableEntity(new { error = "Priority must be between -1000 and 1000." });

        if (!IsCopyLengthValid(request.Headline, 120) || !IsCopyLengthValid(request.CtaText, 60) || !IsCopyLengthValid(request.CtaUrl, 2048))
            return Results.UnprocessableEntity(new { error = "Placement copy exceeds the allowed length." });

        if (!TryNormalizeMediaUrl(request.BannerUrl, out var bannerUrl)
            || !TryNormalizeMediaUrl(request.LogoUrl, out var logoUrl))
            return Results.UnprocessableEntity(new { error = "Creative URLs must use HTTPS." });

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var oldAssets = await connection.QuerySingleOrDefaultAsync<PlacementAssetRow>(new CommandDefinition(
            "SELECT placement_zone AS PlacementZone, banner_asset_id AS BannerAssetId, logo_asset_id AS LogoAssetId FROM sponsor_placements WHERE id = @id FOR UPDATE",
            new { id }, transaction, cancellationToken: ct));
        if (oldAssets is null) return Results.NotFound();

        if ((request.BannerUrl is not null && !request.BannerAssetId.HasValue)
            || (request.LogoUrl is not null && !request.LogoAssetId.HasValue))
            return Results.UnprocessableEntity(new { error = "Creative URLs must reference a validated uploaded asset." });
        var bannerAsset = await GetClaimableAssetAsync(connection, transaction, request.BannerAssetId, oldAssets.BannerAssetId, oldAssets.PlacementZone, "banner", userContext!.UserIdGuid, ct);
        var logoAsset = await GetClaimableAssetAsync(connection, transaction, request.LogoAssetId, oldAssets.LogoAssetId, oldAssets.PlacementZone, "logo", userContext.UserIdGuid, ct);
        if ((request.BannerAssetId.HasValue && bannerAsset is null) || (request.LogoAssetId.HasValue && logoAsset is null))
            return Results.UnprocessableEntity(new { error = "Creative asset is invalid, expired, or already claimed." });

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE public.sponsor_placements SET
                banner_url = @BannerUrl,
                banner_asset_id = @BannerAssetId,
                logo_url = @LogoUrl,
                logo_asset_id = @LogoAssetId,
                headline = @Headline,
                cta_text = @CtaText,
                cta_url = @CtaUrl,
                priority = COALESCE(@Priority, priority),
                is_active = COALESCE(@IsActive, is_active),
                starts_at = @StartsAt,
                ends_at = @EndsAt,
                updated_at = NOW()
            WHERE id = @id
            """,
            new
            {
                id,
                BannerUrl = bannerAsset?.PublicUrl ?? bannerUrl,
                request.BannerAssetId,
                LogoUrl = logoAsset?.PublicUrl ?? logoUrl,
                request.LogoAssetId,
                request.Headline,
                request.CtaText,
                CtaUrl = destinationUrl,
                request.Priority,
                request.IsActive,
                request.StartsAt,
                request.EndsAt,
            },
            transaction, cancellationToken: ct));

        if (affected == 0) return Results.NotFound();
        await ClaimAssetsAsync(connection, transaction, [request.BannerAssetId, request.LogoAssetId], ct);
        await QueueReplacedAssetsAsync(connection, transaction, oldAssets, request.BannerAssetId, request.LogoAssetId, ct);
        transaction.Commit();
        try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    private static async Task<IResult> ResolvePlacementReviewAsync(
        Guid id,
        [FromBody] ResolvePlacementReviewRequest request,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        HybridCache cache,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();
        if (!SponsorPlacementPolicy.TryGet(request.PlacementZone, out var placementPolicy)
            || !SponsorPlacementPolicy.IsScopeValid(placementPolicy, request.TournamentId))
            return Results.UnprocessableEntity(new { error = "The selected placement zone has an invalid scope." });
        if (!SponsorPlacementPolicy.IsSlotValid(placementPolicy, request.SlotNumber))
            return Results.UnprocessableEntity(new { error = $"Slot must be between 1 and {placementPolicy.Capacity}." });

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        var placement = await connection.QuerySingleOrDefaultAsync<ReviewPlacementRow>(new CommandDefinition(
            """
            SELECT p.sponsor_id AS SponsorId, p.review_reason AS ReviewReason,
                   s.tier AS SponsorTier, s.is_active AS SponsorIsActive
            FROM public.sponsor_placements p
            JOIN public.sponsors s ON s.id = p.sponsor_id
            WHERE p.id = @id
            FOR UPDATE
            """,
            new { id }, transaction, cancellationToken: ct));

        if (placement is null) return Results.NotFound();
        if (placement.ReviewReason is null)
            return Results.Conflict(new { error = "Only placements awaiting review can be resolved." });
        if (!placement.SponsorIsActive)
            return Results.UnprocessableEntity(new { error = "Inactive sponsors cannot be assigned." });

        if (!SponsorPlacementPolicy.IsTierAllowed(placementPolicy, placement.SponsorTier))
            return Results.UnprocessableEntity(new { error = "The sponsor tier does not allow this placement zone." });

        var incompatibleAsset = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS(
                SELECT 1 FROM sponsor_placement_assets a
                WHERE a.id IN (
                    SELECT p2.banner_asset_id FROM sponsor_placements p2 WHERE p2.id = @id AND p2.banner_asset_id IS NOT NULL
                    UNION ALL
                    SELECT p2.logo_asset_id FROM sponsor_placements p2 WHERE p2.id = @id AND p2.logo_asset_id IS NOT NULL
                ) AND (a.placement_zone <> @zone OR a.asset_role <> @role)
            )
            """,
            new { id, zone = request.PlacementZone, role = placementPolicy.RequiredRole.ToString().ToLowerInvariant() },
            transaction, cancellationToken: ct));
        if (incompatibleAsset)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE public.sponsor_placements
                SET banner_url = NULL, banner_asset_id = NULL, logo_url = NULL, logo_asset_id = NULL, updated_at = NOW()
                WHERE id = @id
                """, new { id }, transaction, cancellationToken: ct));
        }

        if (request.TournamentId.HasValue)
        {
            var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM public.tournaments WHERE id = @id)",
                new { id = request.TournamentId.Value }, transaction, cancellationToken: ct));
            if (!exists) return Results.NotFound(new { error = "Tournament not found." });
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE public.sponsor_placements
                SET tournament_id = @TournamentId,
                    placement_zone = @PlacementZone,
                    slot_number = @SlotNumber,
                    review_reason = NULL,
                    is_active = false,
                    updated_at = NOW()
                WHERE id = @Id
                """,
                new { Id = id, request.TournamentId, request.PlacementZone, request.SlotNumber },
                transaction,
                cancellationToken: ct));
            transaction.Commit();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            transaction.Rollback();
            return Results.Conflict(new { error = "The slot is occupied or this sponsor is already assigned to the zone." });
        }

        try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    // ─── Admin: Delete placement ─────────────────────────────────────────

    private static async Task<IResult> DeletePlacementAsync(
        Guid id,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        HybridCache cache,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var assets = await connection.QuerySingleOrDefaultAsync<PlacementAssetRow>(new CommandDefinition(
            """
            DELETE FROM public.sponsor_placements WHERE id = @id
            RETURNING banner_asset_id AS BannerAssetId, logo_asset_id AS LogoAssetId
            """,
            new { id },
            transaction, cancellationToken: ct));

        if (assets is null) return Results.NotFound();
        await QueueReplacedAssetsAsync(connection, transaction, assets, null, null, ct);
        transaction.Commit();
        try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    // ─── Admin: Per-sponsor placements ───────────────────────────────────

    private static async Task<IResult> GetSponsorPlacementsAsync(
        Guid sponsorId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PlacementDto>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.tournament_id AS TournamentId,
                                         p.placement_zone AS PlacementZone, p.slot_number AS SlotNumber, p.banner_url AS BannerUrl,
                                     p.banner_asset_id AS BannerAssetId, p.logo_url AS LogoUrl, p.logo_asset_id AS LogoAssetId,
                                     p.headline AS Headline, p.cta_text AS CtaText,
                   p.cta_url AS CtaUrl, p.priority AS Priority, p.is_active AS IsActive,
                                     p.starts_at AS StartsAt, p.ends_at AS EndsAt, p.created_at AS CreatedAt,
                                     p.updated_at AS UpdatedAt,
                                     CASE
                                         WHEN p.review_reason IS NOT NULL THEN 'review'
                                         WHEN NOT p.is_active THEN 'inactive'
                                         WHEN p.slot_number IS NULL
                                             OR (p.placement_zone IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.logo_url, '') = '')
                                             OR (p.placement_zone NOT IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.banner_url, '') = '') THEN 'draft'
                                         WHEN p.starts_at > NOW() THEN 'scheduled'
                                         WHEN p.ends_at <= NOW() THEN 'expired'
                                         ELSE 'live'
                                     END AS Lifecycle, p.review_reason AS ReviewReason,
                   s.name AS SponsorName, s.tier AS SponsorTier,
                   s.logo_url AS SponsorLogoUrl, s.website_url AS SponsorWebsiteUrl,
                   t.name AS TournamentName
            FROM public.sponsor_placements p
            JOIN public.sponsors s ON s.id = p.sponsor_id
            LEFT JOIN public.tournaments t ON t.id = p.tournament_id
            WHERE p.sponsor_id = @sponsorId
            ORDER BY p.created_at DESC
            """,
            new { sponsorId },
            cancellationToken: ct));

        return Results.Ok(rows);
    }

    // ─── Admin: Per-tournament placements ────────────────────────────────

    private static async Task<IResult> GetTournamentPlacementsAdminAsync(
        Guid tournamentId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PlacementDto>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.tournament_id AS TournamentId,
                                         p.placement_zone AS PlacementZone, p.slot_number AS SlotNumber, p.banner_url AS BannerUrl,
                                     p.banner_asset_id AS BannerAssetId, p.logo_url AS LogoUrl, p.logo_asset_id AS LogoAssetId,
                                     p.headline AS Headline, p.cta_text AS CtaText,
                   p.cta_url AS CtaUrl, p.priority AS Priority, p.is_active AS IsActive,
                                     p.starts_at AS StartsAt, p.ends_at AS EndsAt, p.created_at AS CreatedAt,
                                     p.updated_at AS UpdatedAt,
                                     CASE
                                         WHEN p.review_reason IS NOT NULL THEN 'review'
                                         WHEN NOT p.is_active THEN 'inactive'
                                         WHEN p.slot_number IS NULL
                                             OR (p.placement_zone IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.logo_url, '') = '')
                                             OR (p.placement_zone NOT IN ('partner_logo', 'card_badge', 'homepage_ticker') AND COALESCE(p.banner_url, '') = '') THEN 'draft'
                                         WHEN p.starts_at > NOW() THEN 'scheduled'
                                         WHEN p.ends_at <= NOW() THEN 'expired'
                                         ELSE 'live'
                                     END AS Lifecycle, p.review_reason AS ReviewReason,
                   s.name AS SponsorName, s.tier AS SponsorTier,
                   s.logo_url AS SponsorLogoUrl, s.website_url AS SponsorWebsiteUrl,
                   t.name AS TournamentName
            FROM public.sponsor_placements p
            JOIN public.sponsors s ON s.id = p.sponsor_id
            LEFT JOIN public.tournaments t ON t.id = p.tournament_id
            WHERE p.tournament_id = @tournamentId
            ORDER BY p.placement_zone, p.slot_number
            """,
            new { tournamentId },
            cancellationToken: ct));

        return Results.Ok(rows);
    }

    // ─── Admin: Metadata-only update (preserves asset IDs) ─────────────────

    private static async Task<IResult> UpdatePlacementMetadataAsync(
        Guid id,
        [FromBody] UpdatePlacementMetadataRequest request,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        HybridCache cache,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        if (request.StartsAt.HasValue && request.EndsAt.HasValue && request.StartsAt >= request.EndsAt)
            return Results.UnprocessableEntity(new { error = "End date must be after start date." });
        if (!TryNormalizeOptionalUrl(request.CtaUrl, out var destinationUrl))
            return Results.UnprocessableEntity(new { error = "Destination URL must be a valid HTTP or HTTPS URL." });
        if (request.Priority is < -1000 or > 1000)
            return Results.UnprocessableEntity(new { error = "Priority must be between -1000 and 1000." });
        if (!IsCopyLengthValid(request.Headline, 120) || !IsCopyLengthValid(request.CtaText, 60) || !IsCopyLengthValid(request.CtaUrl, 2048))
            return Results.UnprocessableEntity(new { error = "Placement copy exceeds the allowed length." });

        using var connection = connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE public.sponsor_placements SET
                headline = @Headline,
                cta_text = @CtaText,
                cta_url = @CtaUrl,
                priority = COALESCE(@Priority, priority),
                is_active = COALESCE(@IsActive, is_active),
                starts_at = @StartsAt,
                ends_at = @EndsAt,
                updated_at = NOW()
            WHERE id = @id
            """,
            new { id, request.Headline, request.CtaText, CtaUrl = destinationUrl, request.Priority, request.IsActive, request.StartsAt, request.EndsAt },
            cancellationToken: ct));

        if (affected == 0) return Results.NotFound();
        try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    // ─── Admin: Replace creative (swap one role's asset) ─────────────────

    private static async Task<IResult> ReplaceCreativeAsync(
        Guid id,
        [FromBody] ReplaceCreativeRequest request,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        HybridCache cache,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        var placement = await connection.QuerySingleOrDefaultAsync<PlacementAssetRow>(new CommandDefinition(
            "SELECT placement_zone AS PlacementZone, banner_asset_id AS BannerAssetId, logo_asset_id AS LogoAssetId FROM sponsor_placements WHERE id = @id FOR UPDATE",
            new { id }, transaction, cancellationToken: ct));
        if (placement is null) return Results.NotFound();

        if (!SponsorPlacementPolicy.TryGet(placement.PlacementZone, out var policy))
            return Results.UnprocessableEntity(new { error = "Invalid placement zone." });

        var role = policy.RequiredRole.ToString().ToLowerInvariant();
        var asset = await GetClaimableAssetAsync(connection, transaction, request.AssetId, null, placement.PlacementZone, role, userContext!.UserIdGuid, ct);
        if (asset is null)
            return Results.UnprocessableEntity(new { error = "Creative asset is invalid, expired, or already claimed." });

        var (urlColumn, assetColumn) = policy.RequiredRole == SponsorCreativeRole.Banner
            ? ("banner_url", "banner_asset_id")
            : ("logo_url", "logo_asset_id");

        await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE public.sponsor_placements SET {urlColumn} = @url, {assetColumn} = @assetId, updated_at = NOW() WHERE id = @id",
            new { id, url = asset.PublicUrl, assetId = request.AssetId },
            transaction, cancellationToken: ct));

        await ClaimAssetsAsync(connection, transaction, [request.AssetId], ct);

        var oldAssetId = policy.RequiredRole == SponsorCreativeRole.Banner ? placement.BannerAssetId : placement.LogoAssetId;
        if (oldAssetId.HasValue && oldAssetId != request.AssetId)
        {
            var tempRow = placement with
            {
                BannerAssetId = policy.RequiredRole == SponsorCreativeRole.Banner ? oldAssetId : null,
                LogoAssetId = policy.RequiredRole == SponsorCreativeRole.Logo ? oldAssetId : null,
            };
            await QueueReplacedAssetsAsync(connection, transaction, tempRow, null, null, ct);
        }

        transaction.Commit();
        try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    // ─── Admin: Remove creative (clear URL/asset, keep slot as draft) ────

    private static async Task<IResult> RemoveCreativeAsync(
        Guid id,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        HybridCache cache,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        var placement = await connection.QuerySingleOrDefaultAsync<PlacementAssetRow>(new CommandDefinition(
            "SELECT placement_zone AS PlacementZone, banner_asset_id AS BannerAssetId, logo_asset_id AS LogoAssetId FROM sponsor_placements WHERE id = @id FOR UPDATE",
            new { id }, transaction, cancellationToken: ct));
        if (placement is null) return Results.NotFound();

        if (!SponsorPlacementPolicy.TryGet(placement.PlacementZone, out var policy))
            return Results.UnprocessableEntity(new { error = "Invalid placement zone." });

        var (urlColumn, assetColumn) = policy.RequiredRole == SponsorCreativeRole.Banner
            ? ("banner_url", "banner_asset_id")
            : ("logo_url", "logo_asset_id");

        await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE public.sponsor_placements SET {urlColumn} = NULL, {assetColumn} = NULL, updated_at = NOW() WHERE id = @id",
            new { id }, transaction, cancellationToken: ct));

        await QueueReplacedAssetsAsync(connection, transaction, placement, null, null, ct);
        transaction.Commit();
        try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    // ─── Admin: Unassign (free slot, enqueue cleanup, delete record) ─────

    private static async Task<IResult> UnassignPlacementAsync(
        Guid id,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        HybridCache cache,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (!CanEditSponsors(userContext)) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        var assets = await connection.QuerySingleOrDefaultAsync<PlacementAssetRow>(new CommandDefinition(
            "DELETE FROM public.sponsor_placements WHERE id = @id RETURNING placement_zone AS PlacementZone, banner_asset_id AS BannerAssetId, logo_asset_id AS LogoAssetId",
            new { id }, transaction, cancellationToken: ct));
        if (assets is null) return Results.NotFound();

        await QueueReplacedAssetsAsync(connection, transaction, assets, null, null, ct);
        transaction.Commit();
        try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    // ─── Internal types ──────────────────────────────────────────────────

    private static bool CanEditSponsors(UserContext? userContext) =>
        userContext is not null
        && (userContext.IsSuperAdmin
            || userContext.Permissions.Contains(Permissions.SponsorsEdit, StringComparer.OrdinalIgnoreCase));

    private static bool IsValidScope(string zone, Guid? tournamentId) =>
        GlobalZones.Contains(zone) ? tournamentId is null : tournamentId.HasValue;

    private static bool TryNormalizeOptionalUrl(string? value, out string? normalized)
    {
        normalized = NormalizeDestinationUrl(value);
        return string.IsNullOrWhiteSpace(value) || normalized is not null;
    }

    private static string? NormalizeDestinationUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var candidate = value.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
            candidate = $"https://{candidate}";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;

        return uri.AbsoluteUri;
    }

    private static bool TryNormalizeMediaUrl(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }

    private static bool IsCopyLengthValid(string? value, int maximum) => value is null || value.Trim().Length <= maximum;

    private static async Task QueueReplacedAssetsAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        PlacementAssetRow oldAssets,
        Guid? currentBannerAssetId,
        Guid? currentLogoAssetId,
        CancellationToken ct)
    {
        var oldIds = new[] { oldAssets.BannerAssetId, oldAssets.LogoAssetId }
            .Where(assetId => assetId.HasValue)
            .Select(assetId => assetId!.Value)
            .Distinct()
            .Where(assetId => assetId != currentBannerAssetId && assetId != currentLogoAssetId)
            .ToArray();
        if (oldIds.Length == 0) return;

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO sponsor_asset_cleanup_jobs (asset_id, bucket, object_path)
            SELECT asset.id, asset.bucket, asset.object_path
            FROM sponsor_placement_assets asset
            WHERE asset.id = ANY(@assetIds)
              AND NOT EXISTS (
                SELECT 1 FROM sponsor_placements placement
                WHERE placement.banner_asset_id = asset.id OR placement.logo_asset_id = asset.id)
            ON CONFLICT (bucket, object_path) WHERE completed_at IS NULL AND failed_at IS NULL DO NOTHING
            """,
            new { assetIds = oldIds }, transaction, cancellationToken: ct));
    }

    private static Task<PlacementCreativeAsset?> GetClaimableAssetAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        Guid? assetId,
        Guid? existingAssetId,
        string? zone,
        string role,
        Guid userId,
        CancellationToken ct)
    {
        if (!assetId.HasValue) return Task.FromResult<PlacementCreativeAsset?>(null);
        return connection.QuerySingleOrDefaultAsync<PlacementCreativeAsset>(new CommandDefinition(
            """
            SELECT id, public_url AS PublicUrl
            FROM sponsor_placement_assets
                        WHERE id = @assetId AND asset_role = @role
                              AND deleting_at IS NULL
                              AND (id = @existingAssetId OR (uploaded_by = @userId AND claimed_at IS NULL AND expires_at > NOW()))
              AND (@zone IS NULL OR placement_zone = @zone)
            FOR UPDATE
            """,
            new { assetId, existingAssetId, zone, role, userId }, transaction, cancellationToken: ct));
    }

    private static async Task ClaimAssetsAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        IEnumerable<Guid?> assetIds,
        CancellationToken ct)
    {
        var ids = assetIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        if (ids.Length == 0) return;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE sponsor_placement_assets SET claimed_at = NOW() WHERE id = ANY(@ids) AND claimed_at IS NULL",
            new { ids }, transaction, cancellationToken: ct));
    }

    private sealed record PlacementRow
    {
        public Guid Id { get; init; }
        public Guid SponsorId { get; init; }
        public string PlacementZone { get; init; } = "";
        public int SlotNumber { get; init; }
        public string? BannerUrl { get; init; }
        public string? LogoUrl { get; init; }
        public string? Headline { get; init; }
        public string? CtaText { get; init; }
        public string? CtaUrl { get; init; }
        public int Priority { get; init; }
        public string SponsorName { get; init; } = "";
        public string? SponsorTagline { get; init; }
        public string? SponsorLogoUrl { get; init; }
        public string? SponsorBannerUrl { get; init; }
        public string? SponsorAccentColor { get; init; }
        public string? SponsorTier { get; init; }
        public string? SponsorCtaText { get; init; }
        public string? SponsorWebsiteUrl { get; init; }
        public string[]? SponsorGalleryImages { get; init; }
    }

    private sealed record ReviewPlacementRow(
        Guid SponsorId,
        string? ReviewReason,
        string? SponsorTier,
        bool SponsorIsActive);

    private sealed record StatusCountRow(string Status, int Count);

    private sealed record PlacementAssetRow(string PlacementZone, Guid? BannerAssetId, Guid? LogoAssetId);
    private sealed record PlacementCreativeAsset(Guid Id, string PublicUrl);
}
