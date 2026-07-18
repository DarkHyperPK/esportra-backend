using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Esportra.Contracts.Responses;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class TournamentSponsorEndpoints
{
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

        // Admin CRUD
        app.MapGet("/api/admin/placements", GetAllPlacementsAsync)
            .RequireAuthorization("Admin");
        app.MapGet("/api/admin/placements/{id:guid}", GetPlacementByIdAsync)
            .RequireAuthorization("Admin");
        app.MapPost("/api/admin/placements", CreatePlacementAsync)
            .RequireAuthorization("Admin");
        app.MapPut("/api/admin/placements/{id:guid}", UpdatePlacementAsync)
            .RequireAuthorization("Admin");
        app.MapDelete("/api/admin/placements/{id:guid}", DeletePlacementAsync)
            .RequireAuthorization("Admin");
        app.MapGet("/api/admin/sponsors/{sponsorId:guid}/placements", GetSponsorPlacementsAsync)
            .RequireAuthorization("Admin");
        app.MapGet("/api/admin/tournaments/{tournamentId:guid}/placements", GetTournamentPlacementsAdminAsync)
            .RequireAuthorization("Admin");
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
            SELECT p.id, p.sponsor_id, p.placement_zone, p.banner_url, p.logo_url,
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
              AND (p.starts_at IS NULL OR p.starts_at <= NOW())
              AND (p.ends_at IS NULL OR p.ends_at > NOW())
            ORDER BY p.priority DESC, p.created_at ASC
            """,
            new { tournamentId },
            cancellationToken: ct))).AsList();

        var grouped = rows.GroupBy(r => r.SponsorId).Select(g =>
        {
            var first = g.First();
            var mediaOverrides = new Dictionary<string, string>();
            foreach (var row in g)
            {
                if (row.BannerUrl is not null)
                    mediaOverrides[$"{row.PlacementZone}_banner"] = row.BannerUrl;
                if (row.LogoUrl is not null)
                    mediaOverrides[$"{row.PlacementZone}_logo"] = row.LogoUrl;
            }

            return new TournamentSponsorLinkDto(
                first.Id.ToString(),
                first.SponsorId.ToString(),
                "event_sponsor",
                g.Select(r => r.PlacementZone).Distinct().ToList(),
                mediaOverrides.Count > 0 ? mediaOverrides : null,
                first.Priority,
                new TournamentSponsorDto(
                    first.SponsorId.ToString(),
                    first.SponsorName,
                    first.SponsorTagline,
                    first.SponsorLogoUrl,
                    first.SponsorBannerUrl,
                    first.SponsorAccentColor ?? "#000000",
                    first.SponsorTier ?? "partner",
                    first.SponsorCtaText ?? first.CtaText,
                    first.SponsorWebsiteUrl,
                    first.SponsorGalleryImages));
        }).ToList();

        return Results.Ok(grouped);
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
                   p.banner_url AS BannerUrl, p.logo_url AS LogoUrl, p.headline AS Headline,
                   p.cta_text AS CtaText, p.cta_url AS CtaUrl, p.priority AS Priority,
                   s.name AS SponsorName, s.tier AS SponsorTier,
                   s.logo_url AS SponsorLogoUrl, s.banner_image_url AS SponsorBannerUrl,
                   s.website_url AS SponsorWebsiteUrl
            FROM public.sponsor_placements p
            JOIN public.sponsors s ON s.id = p.sponsor_id
            WHERE p.tournament_id IS NULL
              AND p.is_active = true
              AND (p.starts_at IS NULL OR p.starts_at <= NOW())
              AND (p.ends_at IS NULL OR p.ends_at > NOW())
              AND (@zone IS NULL OR p.placement_zone = @zone)
            ORDER BY p.priority DESC, p.created_at ASC
            """,
            new { zone },
            cancellationToken: ct));

        return Results.Ok(rows);
    }

    // ─── Admin: List all placements ──────────────────────────────────────

    private static async Task<IResult> GetAllPlacementsAsync(
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct,
        Guid? sponsorId = null,
        Guid? tournamentId = null,
        string? zone = null)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null || !userContext.IsSuperAdmin) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PlacementDto>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.tournament_id AS TournamentId,
                   p.placement_zone AS PlacementZone, p.banner_url AS BannerUrl,
                   p.logo_url AS LogoUrl, p.headline AS Headline, p.cta_text AS CtaText,
                   p.cta_url AS CtaUrl, p.priority AS Priority, p.is_active AS IsActive,
                   p.starts_at AS StartsAt, p.ends_at AS EndsAt, p.created_at AS CreatedAt,
                   s.name AS SponsorName, s.tier AS SponsorTier,
                   s.logo_url AS SponsorLogoUrl, s.website_url AS SponsorWebsiteUrl,
                   t.name AS TournamentName
            FROM public.sponsor_placements p
            JOIN public.sponsors s ON s.id = p.sponsor_id
            LEFT JOIN public.tournaments t ON t.id = p.tournament_id
            WHERE (@sponsorId IS NULL OR p.sponsor_id = @sponsorId)
              AND (@tournamentId IS NULL OR p.tournament_id = @tournamentId)
              AND (@zone IS NULL OR p.placement_zone = @zone)
            ORDER BY p.created_at DESC
            """,
            new { sponsorId, tournamentId, zone },
            cancellationToken: ct));

        return Results.Ok(rows);
    }

    private static async Task<IResult> GetPlacementByIdAsync(
        Guid id,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null || !userContext.IsSuperAdmin) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<PlacementDto>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.tournament_id AS TournamentId,
                   p.placement_zone AS PlacementZone, p.banner_url AS BannerUrl,
                   p.logo_url AS LogoUrl, p.headline AS Headline, p.cta_text AS CtaText,
                   p.cta_url AS CtaUrl, p.priority AS Priority, p.is_active AS IsActive,
                   p.starts_at AS StartsAt, p.ends_at AS EndsAt, p.created_at AS CreatedAt,
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
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null || !userContext.IsSuperAdmin) return Results.Forbid();

        if (!AllZones.Contains(request.PlacementZone))
            return Results.UnprocessableEntity(new { error = $"Invalid placement zone: {request.PlacementZone}" });

        using var connection = connectionFactory.CreateConnection();

        var sponsor = await connection.QuerySingleOrDefaultAsync<(string? Tier, bool IsActive)>(new CommandDefinition(
            "SELECT tier AS Tier, is_active AS IsActive FROM public.sponsors WHERE id = @id",
            new { id = request.SponsorId },
            cancellationToken: ct));

        if (sponsor.Tier is null)
            return Results.NotFound(new { error = "Sponsor not found." });

        var allowedZones = TierZones.GetValueOrDefault(sponsor.Tier) ?? TierZones["partner"];
        if (!allowedZones.Contains(request.PlacementZone))
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

        var id = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            INSERT INTO public.sponsor_placements
                (sponsor_id, tournament_id, placement_zone, banner_url, logo_url,
                 headline, cta_text, cta_url, priority, is_active,
                 assigned_by, starts_at, ends_at)
            VALUES
                (@SponsorId, @TournamentId, @PlacementZone, @BannerUrl, @LogoUrl,
                 @Headline, @CtaText, @CtaUrl, @Priority, @IsActive,
                 @AssignedBy, @StartsAt, @EndsAt)
            ON CONFLICT (sponsor_id, COALESCE(tournament_id, '00000000-0000-0000-0000-000000000000'::uuid), placement_zone)
            DO NOTHING
            RETURNING id
            """,
            new
            {
                request.SponsorId,
                request.TournamentId,
                request.PlacementZone,
                request.BannerUrl,
                request.LogoUrl,
                request.Headline,
                request.CtaText,
                request.CtaUrl,
                request.Priority,
                request.IsActive,
                AssignedBy = userContext.UserIdGuid,
                request.StartsAt,
                request.EndsAt,
            },
            cancellationToken: ct));

        if (id is null)
            return Results.Conflict(new { error = "Placement already exists for this sponsor/tournament/zone combination." });

        return Results.Created($"/api/admin/placements/{id}", new { id });
    }

    // ─── Admin: Update placement ─────────────────────────────────────────

    private static async Task<IResult> UpdatePlacementAsync(
        Guid id,
        [FromBody] UpdatePlacementRequest request,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null || !userContext.IsSuperAdmin) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE public.sponsor_placements SET
                banner_url = COALESCE(@BannerUrl, banner_url),
                logo_url = COALESCE(@LogoUrl, logo_url),
                headline = COALESCE(@Headline, headline),
                cta_text = COALESCE(@CtaText, cta_text),
                cta_url = COALESCE(@CtaUrl, cta_url),
                priority = COALESCE(@Priority, priority),
                is_active = COALESCE(@IsActive, is_active),
                starts_at = COALESCE(@StartsAt, starts_at),
                ends_at = COALESCE(@EndsAt, ends_at),
                updated_at = NOW()
            WHERE id = @id
            """,
            new
            {
                id,
                request.BannerUrl,
                request.LogoUrl,
                request.Headline,
                request.CtaText,
                request.CtaUrl,
                request.Priority,
                request.IsActive,
                request.StartsAt,
                request.EndsAt,
            },
            cancellationToken: ct));

        return affected > 0 ? Results.NoContent() : Results.NotFound();
    }

    // ─── Admin: Delete placement ─────────────────────────────────────────

    private static async Task<IResult> DeletePlacementAsync(
        Guid id,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null || !userContext.IsSuperAdmin) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM public.sponsor_placements WHERE id = @id",
            new { id },
            cancellationToken: ct));

        return affected > 0 ? Results.NoContent() : Results.NotFound();
    }

    // ─── Admin: Per-sponsor placements ───────────────────────────────────

    private static async Task<IResult> GetSponsorPlacementsAsync(
        Guid sponsorId,
        HttpContext context,
        IDbConnectionFactory connectionFactory,
        CancellationToken ct)
    {
        var userContext = context.Items["UserContext"] as UserContext;
        if (userContext is null || !userContext.IsSuperAdmin) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PlacementDto>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.tournament_id AS TournamentId,
                   p.placement_zone AS PlacementZone, p.banner_url AS BannerUrl,
                   p.logo_url AS LogoUrl, p.headline AS Headline, p.cta_text AS CtaText,
                   p.cta_url AS CtaUrl, p.priority AS Priority, p.is_active AS IsActive,
                   p.starts_at AS StartsAt, p.ends_at AS EndsAt, p.created_at AS CreatedAt,
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
        if (userContext is null || !userContext.IsSuperAdmin) return Results.Forbid();

        using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PlacementDto>(new CommandDefinition(
            """
            SELECT p.id AS Id, p.sponsor_id AS SponsorId, p.tournament_id AS TournamentId,
                   p.placement_zone AS PlacementZone, p.banner_url AS BannerUrl,
                   p.logo_url AS LogoUrl, p.headline AS Headline, p.cta_text AS CtaText,
                   p.cta_url AS CtaUrl, p.priority AS Priority, p.is_active AS IsActive,
                   p.starts_at AS StartsAt, p.ends_at AS EndsAt, p.created_at AS CreatedAt,
                   s.name AS SponsorName, s.tier AS SponsorTier,
                   s.logo_url AS SponsorLogoUrl, s.website_url AS SponsorWebsiteUrl,
                   t.name AS TournamentName
            FROM public.sponsor_placements p
            JOIN public.sponsors s ON s.id = p.sponsor_id
            LEFT JOIN public.tournaments t ON t.id = p.tournament_id
            WHERE p.tournament_id = @tournamentId
            ORDER BY p.placement_zone, p.priority DESC
            """,
            new { tournamentId },
            cancellationToken: ct));

        return Results.Ok(rows);
    }

    // ─── Internal types ──────────────────────────────────────────────────

    private sealed record PlacementRow
    {
        public Guid Id { get; init; }
        public Guid SponsorId { get; init; }
        public string PlacementZone { get; init; } = "";
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
}
