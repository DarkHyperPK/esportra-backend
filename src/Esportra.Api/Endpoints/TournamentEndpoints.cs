using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Core.Bracket;
using Esportra.Core.Tournaments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain 4: Tournaments
///
/// Key perf fix: useTournaments had N+1 — one count query per tournament.
/// Now a single query with correlated subcount. 3 queries → 1.
///
/// useTournamentDashboard had sequential queries — now one consolidated JOIN.
/// </summary>
public static class TournamentEndpoints
{
    private static readonly JsonSerializerOptions s_snakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> AllowedCreateStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "draft", "open", "published" };

    private static string? SerializeTournamentSettings(object? settings, bool supportsMapVeto)
    {
        if (settings is null) return null;

        var json = JsonSerializer.Serialize(settings);
        var node = JsonNode.Parse(json);
        if (node is JsonObject obj)
        {
            if (!supportsMapVeto)
                obj["mapVetoEnabled"] = false;

            return obj.ToJsonString();
        }

        var fallback = new JsonObject();
        if (!supportsMapVeto)
            fallback["mapVetoEnabled"] = false;

        return fallback.ToJsonString();
    }

    private static string? SerializeJson(object? value)
    {
        if (value is null) return null;
        if (value is string s) return string.IsNullOrWhiteSpace(s) ? null : s;
        return JsonSerializer.Serialize(value);
    }

    private static string? NormalizeTournamentStatusGroup(string? statusGroup)
    {
        if (string.IsNullOrWhiteSpace(statusGroup)) return null;
        return statusGroup.Trim().ToLowerInvariant() switch
        {
            "upcoming" or "live" or "completed" or "cancelled" => statusGroup.Trim().ToLowerInvariant(),
            _ => null,
        };
    }

    private static string? ReconcileEffectiveStatus(string? status, DateTimeOffset? startDate, DateTimeOffset? endDate)
    {
        if (string.IsNullOrEmpty(status)) return null;
        var now = DateTimeOffset.UtcNow;
        if (status is "open" or "published" or "check_in" && startDate is not null && now >= startDate)
            return "ongoing";
        if (status is "ongoing" && endDate is not null && now >= endDate)
            return "completed";
        return status;
    }

    /// Typed DTOfor tournament list rows — required so HybridCache (System.Text.Json) can
    /// serialize/deserialize the cached results. Dapper dynamic (ExpandoObject) is NOT
    /// serializable by STJ and causes 500s when HybridCache tries to write to Redis.
    private sealed record TournamentListRow(
        Guid Id,
        string Name,
        string? Slug,
        string Game,
        string Status,
        string? Format,
        string? GameMode,
        DateTime? StartDate,
        DateTime? EndDate,
        DateTime? RegistrationDeadline,
        int? MaxTeams,
        int? MinTeams,
        int? TeamSize,
        decimal? EntryFee,
        decimal? PrizePool,
        string? BannerUrl,
        string? LogoUrl,
        bool IsPublic,
        Guid OrganizerId,
        Guid? VenueId,
        string? Description,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        string? Region,
        string? Currency,
        long CurrentParticipants,
        string? OrganizerName,
        string? OrganizationSlug,
        string? OrganizerUsername,
        string? OrganizerFullName,
        string? WinnerTeamName = null,
        string? VenueCity = null,
        string? VenueCountry = null,
        string? GameBackgroundImage = null,
        Guid? CardBadgePlacementId = null,
        Guid? CardBadgeSponsorId = null,
        string? CardBadgeSponsorName = null,
        string? CardBadgeLogoUrl = null,
        string? CardBadgeHeadline = null,
        string? CardBadgeCtaUrl = null
    );

    private const string TournamentListSql = """
        SELECT t.id, t.name, t.slug, t.game,
               CASE
                   WHEN t.status::text IN ('open', 'published', 'check_in') AND t.start_date IS NOT NULL AND t.start_date <= NOW() THEN 'ongoing'
                   WHEN t.status::text = 'ongoing' AND t.end_date IS NOT NULL AND t.end_date <= NOW() THEN 'completed'
                   ELSE t.status::text
               END AS status,
               t.format, t.game_mode,
               t.start_date, t.end_date, t.registration_deadline,
               t.max_teams, t.min_teams, t.team_size,
               t.entry_fee, t.prize_pool,
               t.banner_url, t.logo_url, t.is_public,
               t.organizer_id, t.venue_id, t.description,
               t.created_at, t.updated_at, t.region, t.currency,
               (SELECT COUNT(*) FROM tournament_participants tp
                WHERE tp.tournament_id = t.id AND tp.status NOT IN ('rejected', 'cancelled', 'disqualified')) AS current_participants,
               o.name   AS organizer_name,
               o.slug   AS organization_slug,
               p.username      AS organizer_username,
               p.full_name     AS organizer_full_name,
               COALESCE(wt.name, wtp.team_name, wp.username) AS winner_team_name,
               v.city   AS venue_city,
               v.country AS venue_country,
               gm.background_image AS game_background_image,
               badge.id AS card_badge_placement_id,
               badge.sponsor_id AS card_badge_sponsor_id,
               badge.sponsor_name AS card_badge_sponsor_name,
               badge.logo_url AS card_badge_logo_url,
               badge.headline AS card_badge_headline,
               badge.cta_url AS card_badge_cta_url
        FROM tournaments t
        LEFT JOIN organizations o  ON o.id  = t.organization_id
        LEFT JOIN profiles      p  ON p.id  = t.organizer_id
        LEFT JOIN teams wt ON wt.id = t.winner_id
        LEFT JOIN tournament_participants wtp ON wtp.id = t.winner_id
        LEFT JOIN profiles wp ON wp.id = wtp.user_id
        LEFT JOIN venues        v  ON v.id  = t.venue_id
        LEFT JOIN games_metadata gm ON LOWER(gm.game_name) = LOWER(t.game)
                LEFT JOIN LATERAL (
                        SELECT sp.id, sp.sponsor_id, s.name AS sponsor_name, sp.logo_url, sp.headline, sp.cta_url
                        FROM sponsor_placements sp
                        JOIN sponsors s ON s.id = sp.sponsor_id AND s.is_active = true
                        WHERE sp.tournament_id = t.id AND sp.placement_zone = 'card_badge'
                            AND sp.slot_number = 1 AND sp.is_active = true AND sp.review_reason IS NULL
                            AND COALESCE(sp.logo_url, '') <> ''
                            AND (sp.starts_at IS NULL OR sp.starts_at <= NOW())
                            AND (sp.ends_at IS NULL OR sp.ends_at > NOW())
                        LIMIT 1
                ) badge ON true
        WHERE t.deleted_at IS NULL
          AND (t.is_public = TRUE OR t.organizer_id = @organizerGuid)
          AND (@status IS NULL OR t.status::text = @status)
          AND (@game   IS NULL OR t.game   ILIKE '%' || @game || '%')
          AND (@q      IS NULL OR t.name   ILIKE '%' || @q   || '%')
          AND (@organizerGuid IS NULL OR t.organizer_id = @organizerGuid)
          AND (@isOnline IS NULL
               OR (@isOnline = TRUE  AND t.venue_id IS NULL)
               OR (@isOnline = FALSE AND t.venue_id IS NOT NULL))
          AND (@city    IS NULL OR v.city    ILIKE '%' || @city    || '%')
          AND (@country IS NULL OR v.country ILIKE '%' || @country || '%')
          AND (@region  IS NULL OR t.region = @region)
          AND (@statusGroup IS NULL OR (
               (@statusGroup = 'upcoming'  AND t.status::text IN ('published', 'open', 'check_in', 'closed')
                                           AND (t.start_date IS NULL OR t.start_date > NOW()))
            OR (@statusGroup = 'live'      AND (t.status::text = 'ongoing'
                                           OR (t.status::text IN ('open', 'published', 'check_in') AND t.start_date IS NOT NULL AND t.start_date <= NOW())))
            OR (@statusGroup = 'completed' AND (t.status::text = 'completed'
                                           OR (t.status::text = 'ongoing' AND t.end_date IS NOT NULL AND t.end_date <= NOW())))
            OR (@statusGroup = 'cancelled'  AND t.status::text = 'cancelled')
          ))
        """;

    public static void MapTournamentEndpoints(this WebApplication app)
    {
        // ── GET /api/tournaments ───────────────────────────────────────────────
        // Replaces useTournaments N+1: participant count in a correlated subquery.
        app.MapGet("/api/tournaments", async (
            string? status,
            string? game,
            string? q,
            string? organizer_id,
            string? ids,
            bool? is_online,
            string? city,
            string? country,
            string? region,
            string? status_group,
            int limit = 50,
            int offset = 0,
            IDbConnectionFactory db = null!,
            HybridCache cache = null!,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 200);
            offset = Math.Max(offset, 0);
            var normalizedStatusGroup = NormalizeTournamentStatusGroup(status_group);
            // Public browse: newest tournaments first so recent publishes/completions surface
            // without needing region/format filters to narrow the result set.
            var orderSql = normalizedStatusGroup switch
            {
                "upcoming" or "live" =>
                    "ORDER BY t.created_at DESC NULLS LAST, t.start_date ASC NULLS LAST",
                "completed" or "cancelled" =>
                    "ORDER BY t.created_at DESC NULLS LAST, COALESCE(t.end_date, t.updated_at, t.start_date) DESC NULLS LAST",
                _ => "ORDER BY t.created_at DESC NULLS LAST, t.start_date ASC NULLS LAST",
            };
            var listSql = $"{TournamentListSql}\n{orderSql}\nLIMIT @limit OFFSET @offset";
            // Bulk fetch by IDs — bypass cache for direct lookup
            if (!string.IsNullOrWhiteSpace(ids))
            {
                var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : (Guid?)null)
                    .Where(g => g.HasValue)
                    .Select(g => g!.Value)
                    .ToArray();
                if (idList.Length == 0) return Results.Ok(Array.Empty<object>());
                using var conn2 = db.CreateConnection();
                var rows2 = (await conn2.QueryAsync<TournamentListRow>(
                    """
                    SELECT t.id, t.name, t.slug, t.game,
                           CASE
                               WHEN t.status::text IN ('open', 'published', 'check_in') AND t.start_date IS NOT NULL AND t.start_date <= NOW() THEN 'ongoing'
                               WHEN t.status::text = 'ongoing' AND t.end_date IS NOT NULL AND t.end_date <= NOW() THEN 'completed'
                               ELSE t.status::text
                           END AS status,
                           t.format, t.game_mode,
                           t.start_date, t.end_date, t.registration_deadline,
                           t.max_teams, t.min_teams, t.team_size,
                           t.entry_fee, t.prize_pool,
                           t.banner_url, t.logo_url, t.is_public,
                           t.organizer_id, t.venue_id, t.description,
                           t.created_at, t.updated_at, t.region, t.currency,
                           (SELECT COUNT(*) FROM tournament_participants tp
                            WHERE tp.tournament_id = t.id AND tp.status NOT IN ('rejected', 'cancelled', 'disqualified')) AS current_participants,
                           o.name   AS organizer_name,
                           o.slug   AS organization_slug,
                           p.username      AS organizer_username,
                           p.full_name     AS organizer_full_name,
                           COALESCE(wt.name, wtp.team_name, wp.username) AS winner_team_name,
                           v.city   AS venue_city,
                           v.country AS venue_country,
                           gm.background_image AS game_background_image,
                           badge.id AS card_badge_placement_id, badge.sponsor_id AS card_badge_sponsor_id,
                           badge.sponsor_name AS card_badge_sponsor_name, badge.logo_url AS card_badge_logo_url,
                           badge.headline AS card_badge_headline, badge.cta_url AS card_badge_cta_url
                    FROM tournaments t
                    LEFT JOIN organizations o  ON o.id  = t.organization_id
                    LEFT JOIN profiles      p  ON p.id  = t.organizer_id
                    LEFT JOIN teams wt ON wt.id = t.winner_id
                    LEFT JOIN tournament_participants wtp ON wtp.id = t.winner_id
                    LEFT JOIN profiles wp ON wp.id = wtp.user_id
                    LEFT JOIN venues        v  ON v.id  = t.venue_id
                    LEFT JOIN games_metadata gm ON LOWER(gm.game_name) = LOWER(t.game)
                                        LEFT JOIN LATERAL (
                                                SELECT sp.id, sp.sponsor_id, s.name AS sponsor_name, sp.logo_url, sp.headline, sp.cta_url
                                                FROM sponsor_placements sp JOIN sponsors s ON s.id = sp.sponsor_id AND s.is_active = true
                                                WHERE sp.tournament_id = t.id AND sp.placement_zone = 'card_badge' AND sp.slot_number = 1
                                                    AND sp.is_active = true AND sp.review_reason IS NULL AND sp.logo_asset_id IS NOT NULL
                                                    AND (sp.starts_at IS NULL OR sp.starts_at <= NOW()) AND (sp.ends_at IS NULL OR sp.ends_at > NOW())
                                                ORDER BY sp.priority DESC, sp.created_at ASC LIMIT 1
                                        ) badge ON true
                    WHERE t.id = ANY(@idList) AND t.deleted_at IS NULL
                    ORDER BY t.start_date ASC
                    """,
                    new { idList })).AsList();
                return Results.Json(rows2, s_snakeCase);
            }

            var cacheKey = $"tournaments:{status}:{normalizedStatusGroup}:{game}:{q}:{organizer_id}:{is_online}:{city}:{country}:{region}:{limit}:{offset}";
            Guid? organizerGuid = Guid.TryParse(organizer_id, out var g) ? g : null;
            var rows = await cache.GetOrCreateAsync<List<TournamentListRow>>(
                cacheKey,
                async (_) =>
                {
                    using var conn = db.CreateConnection();
                    return (await conn.QueryAsync<TournamentListRow>(
                        listSql,
                        new { status, statusGroup = normalizedStatusGroup, game, q, organizerGuid, isOnline = is_online, city, country, region, limit, offset })).AsList();
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                tags: ["tournament-list"],
                cancellationToken: ct);
            return Results.Json(rows, s_snakeCase);
        });

        // ── GET /api/tournaments/filters — distinct values from actual content ─
        app.MapGet("/api/tournaments/filters", async (
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var result = await cache.GetOrCreateAsync("tournament-filters", async _ =>
            {
                using var conn = db.CreateConnection();
                var games = (await conn.QueryAsync<string>(
                    """
                    SELECT DISTINCT game FROM tournaments
                    WHERE is_public = TRUE AND deleted_at IS NULL
                      AND game IS NOT NULL AND game <> ''
                    ORDER BY game
                    """)).AsList();
                var cities = (await conn.QueryAsync<string>(
                    """
                    SELECT DISTINCT v.city FROM tournaments t
                    JOIN venues v ON v.id = t.venue_id
                    WHERE t.is_public = TRUE AND t.deleted_at IS NULL
                      AND v.city IS NOT NULL AND v.city <> ''
                    ORDER BY v.city
                    """)).AsList();
                var countries = (await conn.QueryAsync<string>(
                    """
                    SELECT DISTINCT v.country FROM tournaments t
                    JOIN venues v ON v.id = t.venue_id
                    WHERE t.is_public = TRUE AND t.deleted_at IS NULL
                      AND v.country IS NOT NULL AND v.country <> ''
                    ORDER BY v.country
                    """)).AsList();
                return new { games, cities, countries };
            }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) }, cancellationToken: ct);
            return Results.Ok(result);
        });

        // ── GET /api/tournaments/upcoming ─────────────────────────────────────
        // Returns public, non-deleted tournaments with status published, open, or check_in.
        app.MapGet("/api/tournaments/upcoming", async (
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var rows = await cache.GetOrCreateAsync<List<TournamentListRow>>(
                "tournaments:upcoming",
                async (_) =>
                {
                    using var conn = db.CreateConnection();
                    return (await conn.QueryAsync<TournamentListRow>(
                        """
                        SELECT t.id, t.name, t.slug, t.game,
                               CASE
                                   WHEN t.status::text IN ('open', 'published', 'check_in') AND t.start_date IS NOT NULL AND t.start_date <= NOW() THEN 'ongoing'
                                   WHEN t.status::text = 'ongoing' AND t.end_date IS NOT NULL AND t.end_date <= NOW() THEN 'completed'
                                   ELSE t.status::text
                               END AS status,
                               t.format, t.game_mode,
                               t.start_date, t.end_date, t.registration_deadline,
                               t.max_teams, t.min_teams, t.team_size,
                               t.entry_fee, t.prize_pool,
                               t.banner_url, t.logo_url, t.is_public,
                               t.organizer_id, t.venue_id, t.description,
                               t.created_at, t.updated_at, t.region, t.currency,
                               (SELECT COUNT(*) FROM tournament_participants tp
                                WHERE tp.tournament_id = t.id AND tp.status NOT IN ('rejected', 'cancelled', 'disqualified')) AS current_participants,
                               o.name AS organizer_name,
                               o.slug AS organization_slug,
                               p.username   AS organizer_username,
                               p.full_name  AS organizer_full_name,
                               COALESCE(wt.name, wtp.team_name, wp.username) AS winner_team_name,
                               v.city   AS venue_city,
                               v.country AS venue_country,
                               badge.id AS card_badge_placement_id, badge.sponsor_id AS card_badge_sponsor_id,
                               badge.sponsor_name AS card_badge_sponsor_name, badge.logo_url AS card_badge_logo_url,
                               badge.headline AS card_badge_headline, badge.cta_url AS card_badge_cta_url
                        FROM tournaments t
                        LEFT JOIN organizations o ON o.id = t.organization_id
                        LEFT JOIN profiles      p ON p.id = t.organizer_id
                        LEFT JOIN teams wt ON wt.id = t.winner_id
                        LEFT JOIN tournament_participants wtp ON wtp.id = t.winner_id
                        LEFT JOIN profiles wp ON wp.id = wtp.user_id
                        LEFT JOIN venues        v ON v.id  = t.venue_id
                                                LEFT JOIN LATERAL (
                                                        SELECT sp.id, sp.sponsor_id, s.name AS sponsor_name, sp.logo_url, sp.headline, sp.cta_url
                                                        FROM sponsor_placements sp JOIN sponsors s ON s.id = sp.sponsor_id AND s.is_active = true
                                                        WHERE sp.tournament_id = t.id AND sp.placement_zone = 'card_badge' AND sp.slot_number = 1
                                                            AND sp.is_active = true AND sp.review_reason IS NULL AND sp.logo_asset_id IS NOT NULL
                                                            AND (sp.starts_at IS NULL OR sp.starts_at <= NOW()) AND (sp.ends_at IS NULL OR sp.ends_at > NOW())
                                                        ORDER BY sp.priority DESC, sp.created_at ASC LIMIT 1
                                                ) badge ON true
                        WHERE t.is_public = TRUE
                          AND t.deleted_at IS NULL
                          AND t.status::text IN ('published', 'open', 'check_in')
                          AND (t.start_date IS NULL OR t.start_date > NOW())
                        ORDER BY t.created_at DESC NULLS LAST, t.start_date ASC NULLS LAST
                        LIMIT 100
                        """)).AsList();
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                tags: ["tournament-list"],
                cancellationToken: ct);
            return Results.Json(rows, s_snakeCase);
        }); // Public

        // ── GET /api/tournaments/{slugOrId}/access ─────────────────────────────
        // Lightweight staff/organizer access for route gates (not full dashboard).
        app.MapGet("/api/tournaments/{slugOrId}/access", async (
            string slugOrId,
            HttpContext ctx,
            IStaffAuthorizationService staffAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var tournamentId = await staffAuth.ResolveTournamentIdBySlugAsync(slugOrId, ct);
            if (tournamentId is null) return Results.NotFound();

            var access = await staffAuth.ResolveTournamentAccessAsync(userCtx, tournamentId.Value, ct);
            return Results.Ok(new
            {
                tournamentId = access.TournamentId,
                role = access.Role,
                permissions = access.Permissions,
                isOrganizer = access.IsOrganizer,
                isPlatformAdmin = access.IsPlatformAdmin,
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{slugOrId} ────────────────────────────────────
        // Replaces useTournamentDashboard — consolidated tournament + participants + stages.
        app.MapGet("/api/tournaments/{slugOrId}", async (
            string slugOrId,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService gameCatalog,
            IStaffAuthorizationService staffAuth,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            // Support slug OR uuid lookup (with ILIKE slug fallback)
            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT t.*,
                       (SELECT COUNT(*) FROM tournament_participants tp WHERE tp.tournament_id = t.id AND tp.status NOT IN ('rejected', 'cancelled', 'disqualified')) AS current_participants,
                       (SELECT COUNT(*) FROM tournament_participants tp WHERE tp.tournament_id = t.id AND tp.status = 'checked_in') AS checked_in_count,
                       o.name       AS organization_name, o.slug AS organization_slug,
                       o.logo_url   AS organization_logo,  o.owner_id AS organization_owner_id,
                       p.username   AS organizer_username,  p.avatar_url AS organizer_avatar,
                       v.name       AS venue_name,
                       COALESCE(wt.name, wtp.team_name, wp.username) AS winner_team_name,
                       COALESCE(wt.logo_url, wp.avatar_url) AS winner_team_logo,
                       gm.background_image AS game_background_image
                FROM tournaments t
                LEFT JOIN organizations o  ON o.id  = t.organization_id
                LEFT JOIN profiles      p  ON p.id  = t.organizer_id
                LEFT JOIN venues        v  ON v.id  = t.venue_id
                LEFT JOIN teams wt ON wt.id = t.winner_id
                LEFT JOIN tournament_participants wtp ON wtp.id = t.winner_id
                LEFT JOIN profiles wp ON wp.id = wtp.user_id
                LEFT JOIN games_metadata gm ON LOWER(gm.game_name) = LOWER(t.game)
                WHERE t.deleted_at IS NULL
                  AND (t.slug = @slugOrId
                    OR t.id::text = @slugOrId
                    OR t.slug ILIKE @slugOrId)
                LIMIT 1
                """,
                new { slugOrId });

            if (tournament is null) return Results.NotFound();

            var tournamentId = (Guid)tournament.id;

            // Fetch participants and stages sequentially (Npgsql connections are NOT thread-safe)
            await ReconcileStaleStatusAsync(conn, tournamentId, tournament);

            var allParticipants = await conn.QueryAsync<dynamic>(
                """
                SELECT tp.*, teams.name AS team_name, teams.logo_url AS team_logo,
                       p.username AS gamer_tag
                FROM tournament_participants tp
                LEFT JOIN teams    ON teams.id = tp.team_id
                LEFT JOIN profiles p ON p.id   = tp.user_id
                WHERE tp.tournament_id = @tournamentId
                ORDER BY tp.created_at ASC
                """,
                new { tournamentId });

            var stages = await conn.QueryAsync<dynamic>(
                "SELECT * FROM tournament_stages WHERE tournament_id = @tournamentId ORDER BY stage_order",
                new { tournamentId });

            var access = await ResolveAccessAsync(ctx, staffAuth, tournamentId, ct);
            var participants = FilterParticipantsForRole(allParticipants, access.IsOrganizer);
            var mockCount = await FetchMockCountAsync(conn, tournamentId, access.IsOrganizer);
            var participantMode = await ResolveParticipantModeAsync(gameCatalog, tournament);

            return Results.Ok(new
            {
                tournament,
                participantMode,
                participants,
                stages,
                isOrganizer = access.IsOrganizer,
                staffPermissions = access.StaffPermissions,
                staffRole = access.StaffRole,
                mockCount,
            });
        });

        // ── POST /api/tournaments ──────────────────────────────────────────────
        // Handles slug uniqueness + stages + map pool in one transaction.
        app.MapPost("/api/tournaments", async (
            [FromBody] CreateTournamentRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService gameCatalog,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // TEMPORARY: Verified organizer gate (will be replaced by AuthorizationEnforcementMiddleware)
            if (!userCtx.Roles.Contains("organizer", StringComparer.OrdinalIgnoreCase))
                return Results.Json(new { error = "Organizer role required to create tournaments" }, statusCode: 403);

            using var conn = db.CreateConnection();

            var isVerifiedOrganizer = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM verified_roles
                    WHERE user_id = @userId AND role = 'organizer'::app_role
                      AND status = 'approved' AND is_active = TRUE
                ) AND EXISTS(
                    SELECT 1 FROM organizations WHERE owner_id = @userId
                )
                """,
                new { userId = userCtx.UserIdGuid });
            if (!isVerifiedOrganizer)
                return Results.Json(new { error = "Approved organizer verification and organization required" }, statusCode: 403);

            using var tx = conn.BeginTransaction();

            try
            {
                var uniqueSlug = await ResolveUniqueSlugAsync(conn, tx, req.Slug, req.Name);

                var catalog = await gameCatalog.ResolveTournamentAsync(
                    req.Game, req.GameMode, req.TeamSize, req.Format, req.TournamentType,
                    req.Stages?.Select(s => s.Format).ToArray(),
                    req.MapPoolIds is { Count: > 0 }, req.Settings, conn, tx);

                var organizationId = await ResolveOrganizationIdAsync(conn, tx, req.OrganizationId, userCtx.UserIdGuid);

                var reservedSlots = TournamentInviteSlots.ResolveForWrite(req.ReservedInviteSlots, req.Settings);
                var validationError = ValidateCreateTournamentConstraints(req, reservedSlots);
                if (validationError is not null) { tx.Rollback(); return Results.BadRequest(new { error = validationError }); }

                var dates = NormalizeTournamentDates(req);
                var defaults = NormalizeTournamentDefaults(req);

                var tournament = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO tournaments (
                        name, description, slug, game, format, game_mode, max_teams, min_teams, team_size,
                        entry_fee, prize_pool, prize_distribution, start_date, end_date, registration_deadline,
                        status, banner_url, logo_url, organization_id, venue_id, is_public,
                        check_in_required, check_in_deadline, auto_remove_unchecked,
                        rewards, stream_url, settings, organizer_id, rules, payment_instructions, region, currency, server_region,
                        reserved_invite_slots, invite_expiry_days,
                        payout_method, manual_payout_notes
                    ) VALUES (
                        @name, @description, @slug, @game, @format, @gameMode, @maxTeams, 2, @teamSize,
                        @entryFee, @prizePool,
                        CASE WHEN @prizeDistribution IS NOT NULL THEN @prizeDistribution::jsonb ELSE '[]'::jsonb END,
                        @startDate, @endDate, @registrationDeadline,
                        @status::tournament_status, @bannerUrl, @logoUrl, @organizationId, @venueId, @isPublic,
                        @checkInRequired, @checkInDeadline, @autoRemoveUnchecked,
                        @rewards, @streamUrl, @settings::jsonb, @organizerId, @rules, @paymentInstructions, @region, @currency, @serverRegion,
                        @reservedInviteSlots, @inviteExpiryDays,
                        @payoutMethod, @manualPayoutNotes
                    )
                    RETURNING id, name, description, slug, game, format, game_mode, max_teams, min_teams, team_size,
                             entry_fee, prize_pool, prize_distribution, start_date, end_date, registration_deadline,
                             status, banner_url, logo_url, organization_id, venue_id, is_public,
                             check_in_required, check_in_deadline, auto_remove_unchecked,
                             rewards, stream_url, settings, organizer_id, created_at, rules, payment_instructions, region, currency
                    """,
                    new
                    {
                        name = req.Name,
                        description = req.Description,
                        slug = uniqueSlug,
                        game = catalog.GameName,
                        format = catalog.TournamentStructure,
                        gameMode = catalog.GameMode,
                        maxTeams = req.MaxTeams,
                        teamSize = catalog.TeamSize,
                        entryFee = defaults.EntryFee,
                        prizePool = defaults.PrizePool,
                        prizeDistribution = SerializeJson(req.PrizeDistribution),
                        startDate = req.StartDate,
                        endDate = dates.EndDate,
                        registrationDeadline = dates.RegistrationDeadline,
                        status = dates.Status,
                        bannerUrl = req.BannerUrl,
                        logoUrl = req.LogoUrl,
                        organizationId,
                        venueId = defaults.VenueId,
                        isPublic = defaults.IsPublic,
                        checkInRequired = defaults.CheckInRequired,
                        checkInDeadline = req.CheckInDeadline,
                        autoRemoveUnchecked = defaults.AutoRemoveUnchecked,
                        rewards = req.Rewards,
                        streamUrl = req.StreamUrl,
                        settings = SerializeTournamentSettingsOrEmpty(req.Settings, catalog.SupportsMapVeto),
                        organizerId = userCtx.UserIdGuid,
                        rules = req.Rules,
                        paymentInstructions = req.PaymentInstructions,
                        region = req.Region,
                        currency = defaults.Currency,
                        serverRegion = req.ServerRegion,
                        reservedInviteSlots = reservedSlots,
                        inviteExpiryDays = dates.InviteExpiryDays,
                        payoutMethod = defaults.PayoutMethod,
                        manualPayoutNotes = req.ManualPayoutNotes,
                    },
                    tx);

                var tournamentId = (Guid)tournament.id;

                // BR tournaments start with no stages — organizers configure via stage setup wizard
                await InsertStagesAsync(conn, tx, tournamentId, req.Stages, req.MaxTeams);
                await InsertMapPoolAsync(conn, tx, tournamentId, req.MapPoolIds);

                tx.Commit();

                // Invalidate all tournament list cache entries
                try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }

                await ScheduleCheckinJobIfNeededAsync(ctx, tournamentId, req, ct);

                return Results.Ok(tournament);
            }
            catch (GameCatalogValidationException ex)
            {
                tx.Rollback();
                return Results.BadRequest(new { error = ex.Message });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/tournaments/{id} ──────────────────────────────────────────
        app.MapPut("/api/tournaments/{id}", async (
            Guid id,
            [FromBody] UpdateTournamentRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService gameCatalog,
            TournamentWinnerService winnerService,
            TournamentAuthorizationService tournamentAuth,
            PlacementResolutionService placementResolution,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var existingTournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT organizer_id, game, game_mode, team_size, format, status,
                       start_date, end_date, registration_deadline, max_teams
                FROM tournaments
                WHERE id = @id
                """,
                new { id });
            if (existingTournament is null) return Results.NotFound();

            var validationResult = await ValidateTournamentUpdateAsync(conn, id, req, existingTournament, gameCatalog);
            if (validationResult.error is not null) return validationResult.error;

            var catalog = validationResult.ctx!.Catalog;
            var effectiveEndDate = validationResult.ctx.EffectiveEndDate;
            var reservedSlotsForUpdate = validationResult.ctx.ReservedSlotsForUpdate;

            using var tx = conn.BeginTransaction();

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE tournaments SET
                    name                 = COALESCE(@name, name),
                    description          = COALESCE(@description, description),
                    game                 = @game,
                    format               = @format,
                    game_mode            = @gameMode,
                    status               = CASE WHEN @status IS NOT NULL THEN @status::tournament_status ELSE status END,
                    max_teams            = COALESCE(@maxTeams, max_teams),
                    team_size            = @teamSize,
                    entry_fee            = COALESCE(@entryFee, entry_fee),
                    prize_pool           = COALESCE(@prizePool, prize_pool),
                    start_date           = COALESCE(@startDate, start_date),
                    end_date             = COALESCE(@endDate, end_date),
                    registration_deadline = COALESCE(@registrationDeadline, registration_deadline),
                    banner_url           = COALESCE(@bannerUrl, banner_url),
                    logo_url             = COALESCE(@logoUrl, logo_url),
                    is_public            = COALESCE(@isPublic, is_public),
                    check_in_required    = COALESCE(@checkInRequired, check_in_required),
                    check_in_deadline    = COALESCE(@checkInDeadline, check_in_deadline),
                    rewards              = COALESCE(@rewards, rewards),
                    stream_url           = COALESCE(@streamUrl, stream_url),
                    rules                = COALESCE(@rules, rules),
                    payment_instructions = COALESCE(@paymentInstructions, payment_instructions),
                    region               = COALESCE(@region, region),
                    currency             = COALESCE(@currency, currency),
                    settings             = CASE WHEN @settings IS NOT NULL THEN @settings::jsonb ELSE settings END,
                    prize_distribution   = CASE WHEN @prizeDistribution IS NOT NULL THEN @prizeDistribution::jsonb ELSE prize_distribution END,
                    reserved_invite_slots = COALESCE(@reservedInviteSlots, reserved_invite_slots),
                    invite_expiry_days   = COALESCE(@inviteExpiryDays, invite_expiry_days),
                    payout_method        = COALESCE(@payoutMethod, payout_method),
                    manual_payout_notes  = COALESCE(@manualPayoutNotes, manual_payout_notes),
                    deleted_at           = CASE WHEN @clearDeletedAt THEN NULL ELSE COALESCE(@deletedAt, deleted_at) END,
                    updated_at           = NOW()
                WHERE id = @id
                RETURNING id, name, description, slug, game, format, game_mode, max_teams, min_teams, team_size,
                         entry_fee, prize_pool, start_date, end_date, registration_deadline,
                         status, banner_url, logo_url, organization_id, venue_id, is_public,
                         check_in_required, check_in_deadline, auto_remove_unchecked,
                         rewards, stream_url, rules, payment_instructions, region, currency, settings,
                         reserved_invite_slots, invite_expiry_days, organizer_id, created_at, updated_at
                """,
                new
                {
                    id,
                    name = req.Name,
                    description = req.Description,
                    game = catalog.GameName,
                    format = catalog.TournamentStructure,
                    gameMode = catalog.GameMode,
                    status = req.Status,
                    maxTeams = req.MaxTeams,
                    teamSize = catalog.TeamSize,
                    entryFee = req.EntryFee,
                    prizePool = req.PrizePool,
                    startDate = req.StartDate,
                    endDate = req.EndDate is not null ? effectiveEndDate : null,
                    registrationDeadline = req.RegistrationDeadline,
                    bannerUrl = req.BannerUrl,
                    logoUrl = req.LogoUrl,
                    isPublic = req.IsPublic,
                    checkInRequired = req.CheckInRequired,
                    checkInDeadline = req.CheckInDeadline,
                    rewards = req.Rewards,
                    streamUrl = req.StreamUrl,
                    rules = req.Rules,
                    paymentInstructions = req.PaymentInstructions,
                    region = req.Region,
                    currency = req.Currency,
                    settings = SerializeTournamentSettings(req.Settings, catalog.SupportsMapVeto),
                    prizeDistribution = SerializeJson(req.PrizeDistribution),
                    reservedInviteSlots = reservedSlotsForUpdate,
                    inviteExpiryDays = req.InviteExpiryDays.HasValue
                                             ? Math.Clamp(req.InviteExpiryDays.Value, 1, 365)
                                             : (int?)null,
                    payoutMethod = req.PayoutMethod is "gateway" or "manual" ? req.PayoutMethod : null,
                    manualPayoutNotes = req.ManualPayoutNotes,
                    deletedAt = req.DeletedAt,
                    clearDeletedAt = req.ClearDeletedAt,
                }, tx);

            if (updated is not null && req.DeletedAt is not null && !req.ClearDeletedAt)
                await MockTeamCleanup.DeleteForTournamentAsync(conn, tx, id, ct);

            tx.Commit();

            await ApplyPostUpdateEffectsAsync(conn, id, req, updated, winnerService, placementResolution, ctx, ct);

            if (updated is not null)
                await RescheduleCheckinJobAsync(ctx, id, updated, ct);

            try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/tournaments/{id} — permanent delete (organizer only, must be soft-deleted first)
        app.MapDelete("/api/tournaments/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageStaffAsync(userCtx, id, ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT organizer_id, deleted_at FROM tournaments WHERE id = @id", new { id });
            if (row is null) return Results.NotFound();
            if (row.deleted_at is null)
                return Results.BadRequest(new { error = "Tournament must be moved to trash before it can be permanently deleted." });

            using var tx = conn.BeginTransaction();
            try
            {
                await MockTeamCleanup.DeleteForTournamentAsync(conn, tx, id, ct);
                await conn.ExecuteAsync("DELETE FROM tournaments WHERE id = @id", new { id }, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
            return Results.Ok(new { deleted = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/me/history ─────────────────────────────────
        // Returns tournaments the current user has participated in.
        app.MapGet("/api/tournaments/me/history", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync(
                """
                SELECT t.id, t.name, t.start_date, t.status, t.slug, t.game,
                       t.banner_url, t.logo_url, t.prize_pool
                FROM tournament_participants tp
                JOIN tournaments t ON t.id = tp.tournament_id
                WHERE tp.user_id = @userId
                ORDER BY tp.created_at DESC
                LIMIT 200
                """,
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/me/registration-status ────────────────────────
        // With ?ids=id1,id2,id3 → { id1: true, id2: false, ... } (batch check)
        // Without ids           → [{ tournament_id: "...", id: "..." }, ...] (all registrations)
        app.MapGet("/api/tournaments/me/registration-status", async (
            string? ids,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Get the user's team IDs first
            var teamIds = (await conn.QueryAsync<Guid>(
                "SELECT team_id FROM team_members WHERE user_id = @userId AND is_active = TRUE",
                new { userId = userCtx.UserIdGuid })).ToArray();

            // No specific IDs → return all user registrations as array
            if (string.IsNullOrWhiteSpace(ids))
            {
                IEnumerable<dynamic> rows;
                if (teamIds.Length > 0)
                {
                    rows = await conn.QueryAsync(
                        """
                        SELECT DISTINCT tp.id, tp.tournament_id
                        FROM tournament_participants tp
                        WHERE (tp.user_id = @userId OR tp.team_id = ANY(@teamIds))
                          AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                        """,
                        new { userId = userCtx.UserIdGuid, teamIds });
                }
                else
                {
                    rows = await conn.QueryAsync(
                        "SELECT id, tournament_id FROM tournament_participants WHERE user_id = @userId AND status NOT IN ('cancelled', 'rejected', 'disqualified')",
                        new { userId = userCtx.UserIdGuid });
                }

                var result = rows.Select(r => new Dictionary<string, string>
                {
                    ["id"] = ((Guid)r.id).ToString(),
                    ["tournament_id"] = ((Guid)r.tournament_id).ToString()
                });
                return Results.Ok(result);
            }

            // Specific IDs → return status map
            var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (idList.Length == 0) return Results.Ok(new { });

            IEnumerable<Guid> registeredIdGuids;
            if (teamIds.Length > 0)
            {
                registeredIdGuids = await conn.QueryAsync<Guid>(
                    """
                    SELECT DISTINCT tournament_id FROM tournament_participants
                    WHERE tournament_id = ANY(@ids)
                      AND status NOT IN ('cancelled', 'rejected')
                      AND (user_id = @userId OR team_id = ANY(@teamIds))
                    """,
                    new { ids = idList, userId = userCtx.UserIdGuid, teamIds });
            }
            else
            {
                registeredIdGuids = await conn.QueryAsync<Guid>(
                    "SELECT tournament_id FROM tournament_participants WHERE tournament_id = ANY(@ids) AND user_id = @userId AND status NOT IN ('cancelled', 'rejected', 'disqualified')",
                    new { ids = idList, userId = userCtx.UserIdGuid });
            }

            var registeredSet = registeredIdGuids.Select(g => g.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var statusMap = idList.ToDictionary(id => id, id => registeredSet.Contains(id));

            return Results.Ok(statusMap);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/register ────────────────────────────────
        app.MapPost("/api/tournaments/{id}/register", async (
            Guid id,
            [FromBody] RegisterTournamentRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService gameCatalog,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var txn = conn.BeginTransaction();

            var tourn = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT status, max_teams, entry_fee, payment_instructions, game,
                       reserved_invite_slots, settings,
                       registration_deadline, start_date,
                       COALESCE(settings->>'registrationType', 'open') AS registration_type
                FROM tournaments
                WHERE id = @id
                FOR UPDATE
                """,
                new { id }, txn);
            if (tourn is null) { txn.Rollback(); return Results.NotFound(); }

            var windowError = TournamentTimelineValidator.ValidateRegistrationWindow(
                (string?)tourn.status,
                (DateTimeOffset?)tourn.registration_deadline,
                (DateTimeOffset?)tourn.start_date,
                TournamentTimelineValidator.ParseRegistrationOpensAt(tourn.settings));
            if (windowError is not null)
            { txn.Rollback(); return Results.BadRequest(new { error = windowError }); }

            if (string.Equals((string?)tourn.registration_type, "invite_only", StringComparison.OrdinalIgnoreCase))
            { txn.Rollback(); return Results.BadRequest(new { error = "This tournament is invite-only." }); }
            if (string.Equals((string?)tourn.registration_type, "closed", StringComparison.OrdinalIgnoreCase))
            { txn.Rollback(); return Results.BadRequest(new { error = "This tournament is closed for direct registration." }); }

            int reservedSlots = TournamentInviteSlots.ResolveFromRow(tourn);
            var capacityError = await CheckRegistrationCapacityAsync(
                conn, txn, id, userCtx.UserIdGuid, (int?)tourn.max_teams, reservedSlots);
            if (capacityError is not null) { txn.Rollback(); return capacityError; }

            var ids = ParseParticipantIds(req);

            try
            {
                await gameCatalog.ValidateRegistrationAsync(conn, txn, id, ids.TeamId, ids.RosterId, userCtx.UserIdGuid, req.RosterLineup);
            }
            catch (GameCatalogValidationException ex)
            {
                txn.Rollback();
                return Results.BadRequest(new { error = ex.Message });
            }

            var identity = await ResolveParticipantIdentityAsync(conn, txn, userCtx, req, ids);
            var (regStatus, paymentStatus, entryFeePaid, entryFeeAmount) = DerivePaymentStatus((decimal?)tourn.entry_fee);

            var membersResult = await BuildTeamMembersAsync(conn, txn, id, ids.RosterId, req, ids.ParticipantType, identity.SoloDisplayName, gameCatalog);
            if (membersResult.Error is not null) { txn.Rollback(); return membersResult.Error; }

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO tournament_participants
                    (tournament_id, user_id, team_id, team_captain_id, team_name,
                     team_members, roster_lineup, team_contact_email, roster_id, roster_name,
                     status, participant_type, source, entry_fee_amount, entry_fee_paid,
                     payment_status, payment_receipt_url)
                VALUES (@tournamentId, @userId, @teamId, @teamCaptainId, @teamName,
                        @teamMembers::jsonb, @rosterLineup::jsonb, @teamContactEmail, @rosterId, @rosterName,
                        @regStatus::registration_status, @participantType::registration_type, 'open',
                        @entryFeeAmount, @entryFeePaid,
                        @paymentStatus, @paymentReceiptUrl)
                RETURNING id, tournament_id, user_id, team_id, team_captain_id, team_name,
                         team_members, roster_lineup, team_contact_email, roster_id, roster_name,
                         status, participant_type, source, entry_fee_amount, entry_fee_paid,
                         payment_status, payment_receipt_url, created_at
                """,
                new
                {
                    tournamentId = id,
                    userId = userCtx.UserIdGuid,
                    teamId = identity.TeamId,
                    teamCaptainId = identity.CaptainId,
                    teamName = identity.TeamName,
                    teamMembers = membersResult.TeamMembersJson,
                    rosterLineup = membersResult.RosterLineupJson,
                    teamContactEmail = req.TeamContactEmail,
                    rosterId = ids.RosterId,
                    rosterName = req.RosterName,
                    regStatus,
                    participantType = ids.ParticipantType,
                    entryFeeAmount,
                    entryFeePaid,
                    paymentStatus,
                    paymentReceiptUrl = req.PaymentReceiptUrl,
                }, txn);

            txn.Commit();
            return Results.Ok(row);
        }).RequireAuthorization("Authenticated");

        // ── Tournament Staff endpoints ───────────────────────────────────────
        MapTournamentStaffEndpoints(app);

        // ── Organizer dispute endpoints ─────────────────────────────────────
        MapOrganizerDisputeEndpoints(app);

        // ── DELETE /api/tournaments/{id}/register — withdraw ──────────────────
        app.MapDelete("/api/tournaments/{id}/register", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var affected = await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET status = 'cancelled'
                WHERE tournament_id = @id
                  AND (user_id = @userId OR team_captain_id = @userId)
                  AND status NOT IN ('rejected', 'cancelled')
                """,
                new { id, userId = userCtx.UserIdGuid });

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "No active registration found to withdraw." });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/participants/{participantId}/approve-payment ──
        app.MapPost("/api/tournaments/{id}/participants/{participantId}/approve-payment", async (
            Guid id,
            Guid participantId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await CanManageTournamentParticipantsAsync(conn, userCtx, id))
                return Results.Forbid();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET payment_status = 'approved', entry_fee_paid = true, status = 'approved'
                WHERE id = @participantId AND tournament_id = @id
                  AND status = 'pending' AND payment_status = 'pending'
                """,
                new { participantId, id });

            if (affected == 0) return Results.NotFound(new { error = "Participant not found or not pending payment." });

            // Create in-app notification for the player
            var participant = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT tp.user_id, t.name AS tournament_name FROM tournament_participants tp JOIN tournaments t ON t.id = tp.tournament_id WHERE tp.id = @participantId",
                new { participantId });
            if (participant is not null)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO notifications (user_id, type, title, message, data)
                    VALUES (@userId, 'tournament_announcement', @title,
                            @message, @data::jsonb)
                    """,
                    new
                    {
                        userId = (Guid)participant.user_id,
                        title = $"💰 Payment Confirmed!",
                        message = $"You're officially in! Your payment for {(string)participant.tournament_name} has been approved. Time to prepare for battle!",
                        data = $"{{\"tournament_id\":\"{id}\"}}"
                    });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/participants/{participantId}/reject-payment ──
        app.MapPost("/api/tournaments/{id}/participants/{participantId}/reject-payment", async (
            Guid id,
            Guid participantId,
            [FromBody] PaymentRejectionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await CanManageTournamentParticipantsAsync(conn, userCtx, id))
                return Results.Forbid();

            var affected = await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET payment_status = 'rejected', payment_rejection_reason = @reason, status = 'rejected'
                WHERE id = @participantId AND tournament_id = @id
                  AND status = 'pending' AND payment_status = 'pending'
                """,
                new { participantId, id, reason = req.Reason });

            if (affected == 0) return Results.NotFound(new { error = "Participant not found or not pending payment." });

            // Create in-app notification for the player
            var participant = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT tp.user_id, t.name AS tournament_name FROM tournament_participants tp JOIN tournaments t ON t.id = tp.tournament_id WHERE tp.id = @participantId",
                new { participantId });
            if (participant is not null)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO notifications (user_id, type, title, message, data)
                    VALUES (@userId, 'tournament_announcement', @title,
                            @message, @data::jsonb)
                    """,
                    new
                    {
                        userId = (Guid)participant.user_id,
                        title = "❌ Payment Not Accepted",
                        message = $"Your payment for {(string)participant.tournament_name} was not accepted. Reason: {req.Reason ?? "No reason provided."} — You can resubmit if eligible.",
                        data = $"{{\"tournament_id\":\"{id}\"}}"
                    });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/upload-receipt ────────────────────────────
        app.MapPost("/api/tournaments/{id}/upload-receipt", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("receipt");
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No file uploaded." });
            if (file.Length > 5 * 1024 * 1024) return Results.BadRequest(new { error = "File must be under 5MB." });

            var allowed = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
            if (!allowed.Contains(file.ContentType))
                return Results.BadRequest(new { error = "Only JPEG, PNG, WebP, or PDF files are accepted." });

            using var conn = db.CreateConnection();

            // Verify user is registered
            var participant = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id FROM tournament_participants WHERE tournament_id = @tournamentId AND (user_id = @userId OR team_captain_id = @userId) AND status NOT IN ('cancelled', 'rejected', 'disqualified')",
                new { tournamentId = id, userId = userCtx.UserIdGuid });
            if (participant is null) return Results.NotFound(new { error = "You are not registered for this tournament." });

            // Upload to Supabase storage
            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/') ?? config["SupabaseUrl"]?.TrimEnd('/');
            var serviceKey = config["Supabase:ServiceKey"] ?? config["Supabase:ServiceRoleKey"] ?? config["SupabaseServiceRoleKey"];
            if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceKey))
                return Results.Json(new { error = "File storage is temporarily unavailable. Please try again later." }, statusCode: 500);

            var ext = Path.GetExtension(file.FileName) ?? ".jpg";
            var storagePath = $"{id}/{userCtx.UserIdGuid}{ext}";
            var bucket = "tournaments.payment.receipts";

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceKey);
            http.DefaultRequestHeaders.Add("apikey", serviceKey);
            http.DefaultRequestHeaders.Add("x-upsert", "true");

            using var stream = file.OpenReadStream();
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);

            var uploadUrl = $"{supabaseUrl}/storage/v1/object/{bucket}/{storagePath}";
            var resp = await http.PostAsync(uploadUrl, content, ct);

            if (!resp.IsSuccessStatusCode)
            {
                return Results.Json(new { error = "We couldn't upload your file. Please try again." }, statusCode: 500);
            }

            // Store the storage path (not a public URL — bucket is private)
            var receiptRef = $"{bucket}/{storagePath}";

            // Update participant record
            await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET payment_receipt_url = @url, payment_status = 'pending'
                WHERE id = @participantId
                """,
                new { url = receiptRef, participantId = (Guid)participant.id });

            return Results.Ok(new { receiptUrl = receiptRef });
        }).RequireAuthorization("Authenticated").DisableAntiforgery();

        // ── GET /api/tournaments/{id}/participants/{participantId}/receipt ────
        // Fallback stream when direct public storage URLs are unavailable (RLS / migration).
        app.MapGet("/api/tournaments/{id}/participants/{participantId}/receipt", async (
            Guid id,
            Guid participantId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IConfiguration config,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await CanManageTournamentParticipantsAsync(conn, userCtx, id))
                return Results.Forbid();

            var receiptRef = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT payment_receipt_url FROM tournament_participants WHERE id = @participantId AND tournament_id = @id",
                new { participantId, id });
            if (string.IsNullOrWhiteSpace(receiptRef)) return Results.NotFound(new { error = "No receipt found." });

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/') ?? config["SupabaseUrl"]?.TrimEnd('/');
            var serviceKey = config["Supabase:ServiceKey"] ?? config["Supabase:ServiceRoleKey"] ?? config["SupabaseServiceRoleKey"];
            if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceKey))
                return Results.Json(new { error = "File storage is temporarily unavailable. Please try again later." }, statusCode: 500);

            if (!TryParseStorageRef(receiptRef, out var bucket, out var storagePath))
                return Results.BadRequest(new { error = "Invalid receipt reference." });

            using var http = httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceKey);
            http.DefaultRequestHeaders.Add("apikey", serviceKey);

            var objectUrl = $"{supabaseUrl}/storage/v1/object/{bucket}/{storagePath}";
            using var objectResp = await http.GetAsync(objectUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!objectResp.IsSuccessStatusCode)
                return Results.NotFound(new { error = "Receipt file not found." });

            var contentType = objectResp.Content.Headers.ContentType?.MediaType
                ?? InferReceiptContentType(storagePath);
            var bytes = await objectResp.Content.ReadAsByteArrayAsync(ct);
            return Results.File(bytes, contentType);
        }).RequireAuthorization("Authenticated");

        app.MapPost("/api/tournaments/{id}/check-in", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var checkInEnabled = await conn.ExecuteScalarAsync<bool>(
                "SELECT COALESCE(check_in_required, false) FROM tournaments WHERE id = @id",
                new { id });
            if (!checkInEnabled)
                return Results.BadRequest(new { error = "Check-in is not enabled for this tournament." });

            var updated = await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET status = 'checked_in', checked_in_at = NOW()
                WHERE tournament_id = @id
                  AND (user_id = @userId OR team_captain_id = @userId)
                  AND status = 'approved'
                """,
                new { id, userId = userCtx.UserIdGuid });

            return updated > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "No eligible registration found for check-in." });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/participants/{pid}/approve-check-in ─────
        app.MapPost("/api/tournaments/{id}/participants/{pid}/approve-check-in", async (
            Guid id,
            Guid pid,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var updated = await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET status = 'checked_in', checked_in_at = NOW()
                WHERE id = @pid AND tournament_id = @id
                  AND status IN ('approved', 'pending')
                """,
                new { pid, id });

            return updated > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Participant not found or not eligible for check-in." });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/my-status ────────────────────────────────
        // Consolidated endpoint: returns ban status, registration, team info for current user.
        app.MapGet("/api/tournaments/{id}/my-status", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Check user ban
            var userBan = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT ban_reason FROM tournament_bans WHERE tournament_id = @id AND user_id = @userId AND is_active = TRUE",
                new { id, userId = userCtx.UserIdGuid });

            // 2. Get user's teams (owned + captained + member of)
            var userTeamIds = (await conn.QueryAsync<Guid>(
                """
                SELECT DISTINCT t.id FROM teams t
                LEFT JOIN team_members tm ON tm.team_id = t.id
                WHERE (t.owner_id = @userId OR (tm.user_id = @userId AND tm.is_active = TRUE))
                """,
                new { userId = userCtx.UserIdGuid })).ToArray();

            // 3. Check team bans
            dynamic? teamBan = null;
            if (userTeamIds.Length > 0)
            {
                teamBan = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT ban_reason, team_id FROM tournament_bans WHERE tournament_id = @id AND is_active = TRUE AND team_id = ANY(@teamIds)",
                    new { id, teamIds = userTeamIds });
            }

            // 4. Get user's registration (solo or via team) – exclude cancelled/rejected
            var registration = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT tp.*, t.name AS team_name, t.logo_url AS team_logo
                FROM tournament_participants tp
                LEFT JOIN teams t ON t.id = tp.team_id
                WHERE tp.tournament_id = @id
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                  AND (tp.user_id = @userId OR tp.team_captain_id = @userId
                       OR (tp.team_id = ANY(@teamIds) AND tp.participant_type = 'team'))
                ORDER BY CASE WHEN tp.user_id = @userId THEN 0 ELSE 1 END, tp.created_at DESC
                LIMIT 1
                """,
                new { id, userId = userCtx.UserIdGuid, teamIds = userTeamIds });

            // 5. Get user's captain teams for registration options
            var captainTeams = await conn.QueryAsync<dynamic>(
                $"""
                SELECT t.id, t.name, t.logo_url
                FROM teams t
                WHERE (t.owner_id = @userId
                   OR t.id IN (
                       SELECT team_id FROM team_members
                       WHERE user_id = @userId AND role = 'captain' AND is_active = TRUE
                   ))
                  AND {TeamKindSql.RealTeamWhere}
                ORDER BY t.created_at DESC
                LIMIT 50
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(new
            {
                userBan = userBan is not null ? new { banReason = (string)userBan.ban_reason } : null,
                teamBan = teamBan is not null ? new { banReason = (string)teamBan.ban_reason, teamId = ((Guid)teamBan.team_id).ToString() } : null,
                registration,
                captainTeams,
                userTeamIds = userTeamIds.Select(g => g.ToString()),
            });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/tournaments/{id}/banner ────────────────────────────────────
        app.MapPut("/api/tournaments/{id}/banner", async (
            Guid id,
            [FromBody] UpdateBannerRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                "UPDATE tournaments SET banner_url = @url, updated_at = NOW() WHERE id = @id",
                new { id, url = req.Url });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/registrations — organizer: all registrations ───
        app.MapGet("/api/tournaments/{id}/registrations", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var regs = await conn.QueryAsync<dynamic>(
                """
                SELECT tp.*,
                       t.name AS team_name, t.logo_url AS team_logo,
                       p.username, p.full_name, p.avatar_url, p.riot_tag
                FROM tournament_participants tp
                LEFT JOIN teams    t ON t.id = tp.team_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE tp.tournament_id = @id
                ORDER BY tp.created_at ASC
                """,
                new { id });

            return Results.Ok(regs);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/remove-unchecked — remove unchecked participants ──
        app.MapPost("/api/tournaments/{id}/remove-unchecked", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var checkInEnabled = await conn.ExecuteScalarAsync<bool>(
                "SELECT COALESCE(check_in_required, false) FROM tournaments WHERE id = @id",
                new { id });
            if (!checkInEnabled)
                return Results.BadRequest(new { error = "Check-in is not enabled for this tournament." });

            var removed = await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET status = 'cancelled'
                WHERE tournament_id = @id
                  AND checked_in_at IS NULL
                  AND status IN ('pending', 'approved')
                """,
                new { id });

            return Results.Ok(new { removedCount = removed });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/participants/{participantId}/check-in — organizer manual check-in ──
        app.MapPost("/api/tournaments/{id}/participants/{participantId}/check-in", async (
            Guid id,
            Guid participantId,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var updated = await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET status = 'checked_in', checked_in_at = NOW()
                WHERE id = @participantId AND tournament_id = @id
                  AND status IN ('pending', 'approved')
                """,
                new { participantId, id });

            return updated > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Participant not found or already checked in." });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/ban-participant ──────────────────────────
        app.MapPost("/api/tournaments/{id}/ban-participant", async (
            Guid id,
            [FromBody] BanParticipantRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            MatchFinalizationService finalizer,
            IHubContext<BracketHub> bracketHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var participantId = Guid.Parse(req.ParticipantId);

            var participant = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT user_id, team_id FROM tournament_participants WHERE id = @pid AND tournament_id = @id",
                new { pid = participantId, id });
            if (participant is null)
                return Results.NotFound(new { error = "Participant not found" });

            Guid? banUserId = null, banTeamId = null;
            if (participant.team_id is not null) banTeamId = (Guid)participant.team_id;
            else if (participant.user_id is not null) banUserId = (Guid)participant.user_id;
            else if (req.UserId is not null) banUserId = Guid.Parse(req.UserId);

            if (banUserId is null && banTeamId is null)
                return Results.BadRequest(new { error = "Cannot determine ban target" });

            using var tx = conn.BeginTransaction();

            await conn.ExecuteAsync(
                """
                INSERT INTO tournament_bans (tournament_id, participant_id, user_id, team_id, ban_reason, banned_by, banned_at, is_active)
                VALUES (@tournamentId, @participantId, @userId, @teamId, @banReason, @bannedBy, NOW(), TRUE)
                """,
                new { tournamentId = id, participantId, userId = banUserId, teamId = banTeamId, banReason = req.BanReason, bannedBy = userCtx.UserIdGuid }, tx);

            await conn.ExecuteAsync(
                "UPDATE tournament_participants SET status = 'disqualified' WHERE id = @pid AND status NOT IN ('cancelled', 'rejected', 'disqualified')",
                new { pid = participantId }, tx);

            var bannedSlotId = banTeamId ?? participantId;
            var versionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM public.brkt_versions WHERE tournament_id = @id ORDER BY created_at DESC LIMIT 1",
                new { id }, tx);

            await CascadeForfeitMatchesAsync(conn, tx, id, bannedSlotId, versionId, finalizer, bracketHub, ct);
            await RemoveFromBRGroupsAsync(conn, tx, participantId, banTeamId, id);
            await NotifyBannedUsersAsync(conn, tx, banTeamId, banUserId, id, req.BanReason);

            tx.Commit();

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/participants/{pid} — single participant ───
        app.MapGet("/api/tournaments/{id}/participants/{pid}", async (
            Guid id,
            Guid pid,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var flat = await conn.QueryAsync<dynamic>(
                """
                SELECT tp.id, tp.tournament_id, tp.user_id, tp.team_id, tp.team_captain_id,
                       tp.participant_type::text AS participant_type,
                       tp.status::text AS status, tp.created_at, tp.checked_in_at, tp.is_mock,
                       COALESCE(t.name, tp.team_name) AS team_name, t.logo_url AS team_logo_url,
                       t.tag AS team_tag,
                       COALESCE(t.is_solo, false) AS is_solo,
                       COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) AS team_kind,
                       tm.user_id AS member_user_id,
                       p.username AS member_username,
                       sp.username AS solo_username,
                       sp.full_name AS solo_full_name,
                       sp.riot_tag AS solo_riot_tag,
                       sp.avatar_url AS solo_avatar_url
                FROM tournament_participants tp
                LEFT JOIN teams t ON t.id = tp.team_id
                LEFT JOIN team_members tm ON tm.team_id = tp.team_id
                  AND tm.is_active = true
                  AND COALESCE(tp.participant_type::text, '') != 'solo'
                LEFT JOIN profiles p ON p.id = tm.user_id
                LEFT JOIN profiles sp ON sp.id = tp.user_id
                WHERE tp.tournament_id = @id AND tp.id = @pid
                ORDER BY tp.created_at ASC
                """, new { id, pid });

            var rows = flat.AsList();
            if (rows.Count == 0) return Results.NotFound();

            var first = rows[0];
            var isSoloEntry = string.Equals(first.participant_type as string, "solo", StringComparison.OrdinalIgnoreCase);
            var members = rows
                .Where(m => m.member_user_id is not null)
                .Select(m => new { user_id = (Guid)m.member_user_id, username = (string)m.member_username })
                .Distinct()
                .ToList();

            return Results.Ok(ParticipantResponseHelper.EnrichParticipant(
                first,
                isSoloEntry ? null : members,
                isSoloEntry ? string.Empty : string.Join(", ", members.Select(m => m.username))));
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/stages ─────────────────────────────────
        app.MapGet("/api/tournaments/{id}/stages", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewTournamentPublicDataAsync(conn, ctx, id))
                return Results.NotFound();

            var rows = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM tournament_stages WHERE tournament_id = @id ORDER BY stage_order",
                new { id })).ToList();

            var enriched = new List<object>();
            foreach (var stage in rows)
            {
                var stageId = (Guid)stage.id;
                var format = ((string?)stage.format ?? "single_elimination").ToLowerInvariant();
                string progressLabel;

                if (format is "battle_royale")
                {
                    var snapshot = await StageCompletionHelper.EvaluateBattleRoyaleAsync(conn, stage, stageId, ct: ct);
                    progressLabel = snapshot.ProgressLabel;
                }
                else
                {
                    progressLabel = await StageCompletionHelper.EvaluateBracketProgressLabelAsync(conn, stage, stageId);
                }

                enriched.Add(new
                {
                    id = stage.id,
                    tournament_id = stage.tournament_id,
                    name = stage.name,
                    format = stage.format,
                    stage_order = stage.stage_order,
                    best_of = stage.best_of,
                    bo_mode = stage.bo_mode,
                    round_bo_overrides = stage.round_bo_overrides is string rbo
                        ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(rbo)
                        : null,
                    capacity = stage.capacity,
                    advancement_count = stage.advancement_count,
                    config = stage.config,
                    starts_at = stage.starts_at,
                    ends_at = stage.ends_at,
                    created_at = stage.created_at,
                    updated_at = stage.updated_at,
                    progress_label = progressLabel,
                    scheduling_config = stage.scheduling_config,
                });
            }

            return Results.Ok(enriched);
        });

        // ── GET /api/tournaments/{id}/bracket-versions ───────────────────────
        app.MapGet("/api/tournaments/{id}/bracket-versions", async (
            Guid id,
            string? status,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewTournamentPublicDataAsync(conn, ctx, id))
                return Results.NotFound();

            dynamic rows;
            if (!string.IsNullOrEmpty(status))
            {
                var statuses = status.Split(',');
                rows = await conn.QueryAsync<dynamic>(
                    "SELECT id, stage_id, status, version_number, created_at FROM brkt_versions WHERE tournament_id = @id AND status = ANY(@statuses) ORDER BY created_at DESC",
                    new { id, statuses });
            }
            else
            {
                rows = await conn.QueryAsync<dynamic>(
                    "SELECT id, stage_id, status, version_number, created_at FROM brkt_versions WHERE tournament_id = @id ORDER BY created_at DESC",
                    new { id });
            }
            return Results.Ok(rows);
        });

        // ── GET /api/tournaments/{id}/participants ───────────────────────────
        app.MapGet("/api/tournaments/{id}/participants", async (
            Guid id,
            string? status,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            // Build optional status filter — exclude rejected/cancelled by default
            var statusFilter = !string.IsNullOrEmpty(status)
                ? "AND tp.status::text = @status"
                : "AND tp.status NOT IN ('rejected', 'cancelled', 'disqualified')";

            // Fetch participants with team member roster details
            var flat = await conn.QueryAsync<dynamic>(
                $"""
                SELECT tp.id, tp.tournament_id, tp.user_id, tp.team_id, tp.team_captain_id,
                       tp.participant_type::text AS participant_type,
                       tp.status::text AS status, tp.created_at, tp.checked_in_at, tp.is_mock,
                       COALESCE(t.name, tp.team_name) AS team_name, t.logo_url AS team_logo_url,
                       t.tag AS team_tag,
                       COALESCE(t.is_solo, false) AS is_solo,
                       COALESCE(t.team_kind, CASE WHEN COALESCE(t.is_solo, false) THEN 'solo' ELSE 'team' END) AS team_kind,
                       tm.user_id AS member_user_id,
                       p.username AS member_username,
                       sp.username AS solo_username,
                       sp.full_name AS solo_full_name,
                       sp.riot_tag AS solo_riot_tag,
                       sp.avatar_url AS solo_avatar_url
                FROM tournament_participants tp
                LEFT JOIN teams t ON t.id = tp.team_id
                LEFT JOIN team_members tm ON tm.team_id = tp.team_id
                  AND tm.is_active = true
                  AND COALESCE(tp.participant_type::text, '') != 'solo'
                LEFT JOIN profiles p ON p.id = tm.user_id
                LEFT JOIN profiles sp ON sp.id = tp.user_id
                WHERE tp.tournament_id = @id {statusFilter}
                ORDER BY tp.created_at ASC
                LIMIT 2048
                """, new { id, status });

            // Group by participant to nest members
            var grouped = flat
                .GroupBy(r => (Guid)r.id)
                .Select(g =>
                {
                    var first = g.First();
                    var isSoloEntry = string.Equals(first.participant_type as string, "solo", StringComparison.OrdinalIgnoreCase);
                    var members = g
                        .Where(m => m.member_user_id is not null)
                        .Select(m => new { user_id = (Guid)m.member_user_id, username = (string)m.member_username })
                        .Distinct()
                        .ToList();

                    return ParticipantResponseHelper.EnrichParticipant(
                        first,
                        isSoloEntry ? null : members,
                        isSoloEntry ? string.Empty : string.Join(", ", members.Select(m => m.username)));
                })
                .ToList();

            return Results.Ok(grouped);
        });

        // ── GET /api/tournaments/{id}/match-proofs ───────────────────────────
        app.MapGet("/api/tournaments/{id}/match-proofs", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT match_id, image_url FROM tournament_match_results WHERE tournament_id = @id AND image_url IS NOT NULL",
                new { id });
            return Results.Ok(rows);
        });

        // ── GET /api/tournaments/{id}/match-games ────────────────────────────
        app.MapGet("/api/tournaments/{id}/match-games", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT g.*, gm.map_name
                FROM brkt_match_games g
                LEFT JOIN game_maps gm ON gm.id = g.map_id
                JOIN brkt_matches m ON m.id = g.match_id
                JOIN brkt_versions v ON v.id = m.version_id
                WHERE v.tournament_id = @id AND g.status = 'completed'
                LIMIT 2000
                """,
                new { id });
            DapperJsonbHelper.FixJsonb(rows);
            return Results.Ok(rows);
        });

        // ── GET /api/teams/search — search teams by name ─────────────────────────
        app.MapGet("/api/teams/search", async (
            string? name,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            if (string.IsNullOrEmpty(name)) return Results.Ok(Array.Empty<object>());

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, name, owner_id, logo_url
                FROM teams
                WHERE name ILIKE @pattern
                ORDER BY name ASC
                LIMIT 10
                """,
                new { pattern = $"%{name}%" });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── BR Game Data ─────────────────────────────────────────────────────
        // Deprecated: legacy JSON BR game state. New player and organizer flows use
        // relational br_lobbies/br_lobby_evidence endpoints in BRGroupEndpoints.
        // Keep temporarily for rollback and older clients.

        // GET /api/tournaments/{id}/br-games — read BR game data (any authenticated user)
        app.MapGet("/api/tournaments/{id}/br-games", async (
            Guid id,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT games::text FROM br_game_data WHERE tournament_id = @tid",
                new { tid = id.ToString() });

            if (row is null)
                return Results.Ok(new { games = (object?)null });

            var gamesElement = JsonSerializer.Deserialize<JsonElement>(row);
            return Results.Ok(new { games = gamesElement });
        }).RequireAuthorization("Authenticated");

        // PUT /api/tournaments/{id}/br-games — upsert BR game data (organizer/staff only)
        app.MapPut("/api/tournaments/{id}/br-games", async (
            Guid id,
            [FromBody] BRGameDataRequest req,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isStaff = await StaffAuthHelper.CanActOnTournamentAsync(conn, userCtx.UserIdGuid, id);
            var isAdmin = StaffAuthHelper.IsPlatformAdmin(userCtx);
            if (!isStaff && !isAdmin)
                return Results.Json(new { error = "Only tournament organizers can update game data." }, statusCode: 403);

            var gamesJson = JsonSerializer.Serialize(req.Games);
            await conn.ExecuteAsync(
                """
                INSERT INTO br_game_data (tournament_id, games, updated_at, updated_by)
                VALUES (@tid, @games::jsonb, NOW(), @uid)
                ON CONFLICT (tournament_id) DO UPDATE SET
                    games      = @games::jsonb,
                    updated_at = NOW(),
                    updated_by = @uid
                """,
                new { tid = id.ToString(), games = gamesJson, uid = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // PUT /api/tournaments/{id}/br-games/evidence — submit evidence (any tournament participant)
        app.MapPut("/api/tournaments/{id}/br-games/evidence", async (
            Guid id,
            [FromBody] BRSubmitEvidenceRequest req,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isParticipant = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tournament_participants tp
                    JOIN team_members tm ON tm.team_id = tp.team_id
                    WHERE tp.tournament_id = @tid AND tm.user_id = @uid AND tm.is_active = TRUE
                )
                """,
                new { tid = id, uid = userCtx.UserIdGuid });

            if (!isParticipant)
            {
                var isStaff = await StaffAuthHelper.CanActOnTournamentAsync(conn, userCtx.UserIdGuid, id);
                if (!isStaff && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                    return Results.Json(new { error = "You must be a tournament participant to submit evidence." }, statusCode: 403);
            }

            var existing = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT games::text FROM br_game_data WHERE tournament_id = @tid",
                new { tid = id.ToString() });

            var games = existing is not null
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing) ?? new()
                : new Dictionary<string, JsonElement>();

            var gameKey = $"game_{req.GameNumber}";
            BRGameDataInternal gameData;
            if (games.TryGetValue(gameKey, out var elem))
                gameData = JsonSerializer.Deserialize<BRGameDataInternal>(elem.GetRawText()) ?? new();
            else
                gameData = new() { gameNumber = req.GameNumber, status = "pending" };

            // Resolve team to tournament_participants team_id for consistency
            var tpTeamId = await conn.QuerySingleOrDefaultAsync<string?>(
                """
                SELECT tp.team_id::text FROM tournament_participants tp
                JOIN team_members tm ON tm.team_id = tp.team_id
                WHERE tp.tournament_id = @tournId AND tm.user_id = @uid AND tm.is_active = TRUE
                LIMIT 1
                """,
                new { tournId = id, uid = userCtx.UserIdGuid });
            var resolvedTeamId = tpTeamId ?? req.TeamId;

            // Resolve team name from DB
            var teamName = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT name FROM teams WHERE id = @tid",
                new { tid = Guid.Parse(resolvedTeamId) }) ?? "Unknown Team";

            gameData.evidence = (gameData.evidence ?? new())
                .Where(e => e.teamId != resolvedTeamId)
                .Append(new BREvidenceItem
                {
                    teamId = resolvedTeamId,
                    teamName = teamName,
                    imageUrl = req.ImageUrl,
                    submittedBy = userCtx.UserId,
                    submittedAt = DateTime.UtcNow.ToString("o"),
                    placement = req.Placement,
                    kills = req.Kills,
                    reviewed = false,
                })
                .ToList();

            games[gameKey] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(gameData));

            var gamesJson = JsonSerializer.Serialize(games);
            await conn.ExecuteAsync(
                """
                INSERT INTO br_game_data (tournament_id, games, updated_at, updated_by)
                VALUES (@tid, @games::jsonb, NOW(), @uid)
                ON CONFLICT (tournament_id) DO UPDATE SET
                    games      = @games::jsonb,
                    updated_at = NOW(),
                    updated_by = @uid
                """,
                new { tid = id.ToString(), games = gamesJson, uid = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tournament Staff
    // ══════════════════════════════════════════════════════════════════════════

    private static void MapTournamentStaffEndpoints(WebApplication app)
    {
        // ── GET /api/tournaments/{tournamentId}/staff ────────────────────────
        app.MapGet("/api/tournaments/{tournamentId}/staff", async (
            Guid tournamentId,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, tournamentId, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT os.id, sta.tournament_id, os.user_id, os.role,
                       os.permissions, os.status, os.created_at, os.updated_at,
                       os.accepted_at,
                       jsonb_build_object(
                           'full_name', p.full_name,
                           'username', p.username,
                           'email', p.email,
                           'avatar_url', p.avatar_url
                       ) AS profiles
                FROM organization_staff os
                JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                LEFT JOIN profiles p ON p.id = os.user_id
                WHERE sta.tournament_id = @tournamentId AND os.status = 'active'
                ORDER BY os.created_at ASC
                LIMIT 200
                """,
                new { tournamentId });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{tournamentId}/staff — invite ──────────────
        app.MapPost("/api/tournaments/{tournamentId}/staff", async (
            Guid tournamentId,
            [FromBody] InviteTournamentStaffRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageStaffAsync(userCtx, tournamentId, ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            // Look up user by email
            var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, email FROM profiles WHERE email ILIKE @email LIMIT 1",
                new { email = req.UserEmail });
            if (profile is null) return Results.BadRequest(new { error = "User not found." });

            string userId = profile.id;

            // Get the organization_id for this tournament
            var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT organization_id FROM tournaments WHERE id = @tid",
                new { tid = tournamentId });
            if (orgId is null) return Results.BadRequest(new { error = "Tournament has no organization." });

            var permissionsJson = JsonSerializer.Serialize(req.Permissions ?? Array.Empty<string>());

            // Upsert organization_staff record
            var orgStaffId = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT id FROM organization_staff WHERE organization_id = @orgId AND user_id = @userId",
                new { orgId, userId });

            if (orgStaffId is not null)
            {
                // Update existing org staff — keep current status if already active, reset to pending otherwise
                await conn.ExecuteAsync(
                    """
                    UPDATE organization_staff
                    SET role = @role, permissions = @permissions::text[],
                        assigned_by = @assignedBy, updated_at = NOW(),
                        status = CASE WHEN status = 'active' THEN 'active' ELSE 'pending' END
                    WHERE id = @id::uuid
                    """,
                    new
                    {
                        id = orgStaffId,
                        role = req.Role,
                        permissions = req.Permissions ?? Array.Empty<string>(),
                        assignedBy = userCtx.UserIdGuid
                    });
            }
            else
            {
                orgStaffId = (await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO organization_staff
                        (organization_id, user_id, role, permissions, assigned_by, status)
                    VALUES
                        (@orgId, @userId::uuid, @role, @permissions::text[], @assignedBy, 'pending')
                    RETURNING id
                    """,
                    new
                    {
                        orgId,
                        userId,
                        role = req.Role,
                        permissions = req.Permissions ?? Array.Empty<string>(),
                        assignedBy = userCtx.UserIdGuid
                    })).ToString();
            }

            // Upsert tournament assignment
            await conn.ExecuteAsync(
                """
                INSERT INTO staff_tournament_assignments (organization_staff_id, tournament_id, assigned_by)
                VALUES (@orgStaffId::uuid, @tournamentId, @assignedBy)
                ON CONFLICT (organization_staff_id, tournament_id) DO NOTHING
                """,
                new { orgStaffId, tournamentId, assignedBy = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/tournaments/staff/{staffId} — update role/permissions ───
        app.MapPut("/api/tournaments/staff/{staffId}", async (
            Guid staffId,
            [FromBody] UpdateTournamentStaffRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT sta.tournament_id
                FROM organization_staff os
                JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                WHERE os.id = @staffId
                LIMIT 1
                """,
                new { staffId });
            if (tournamentId is null) return Results.NotFound();

            if (!await tournamentAuth.CanManageStaffAsync(userCtx, tournamentId.Value, ct))
                return Results.Forbid();

            await conn.ExecuteAsync(
                """
                UPDATE organization_staff
                SET role = @role, permissions = @permissions::text[], updated_at = NOW()
                WHERE id = @staffId
                """,
                new { staffId, role = req.Role, permissions = req.Permissions ?? Array.Empty<string>() });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/tournaments/staff/{staffId} — remove ────────────────
        app.MapDelete("/api/tournaments/staff/{staffId}", async (
            Guid staffId,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT sta.tournament_id
                FROM organization_staff os
                JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                WHERE os.id = @staffId
                LIMIT 1
                """,
                new { staffId });
            if (tournamentId is null) return Results.NotFound();

            if (!await tournamentAuth.CanManageStaffAsync(userCtx, tournamentId.Value, ct))
                return Results.Forbid();

            // Remove tournament assignments then the org staff record
            await conn.ExecuteAsync(
                "DELETE FROM staff_tournament_assignments WHERE organization_staff_id = @staffId",
                new { staffId });
            await conn.ExecuteAsync(
                "DELETE FROM organization_staff WHERE id = @staffId",
                new { staffId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/staff/my-invites ────────────────────────────
        // OBSOLETE: Use GET /api/organizations/staff/invites instead.
        app.MapGet("/api/tournaments/staff/my-invites", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT os.id, os.organization_id, os.user_id, os.role,
                       os.permissions, os.status, os.assigned_by,
                       os.created_at, os.updated_at, os.accepted_at,
                       jsonb_build_object(
                           'id', t.id, 'name', t.name, 'game', t.game,
                           'start_date', t.start_date
                       ) AS tournament,
                       jsonb_build_object(
                           'full_name', p.full_name,
                           'username', p.username,
                           'email', p.email
                       ) AS organizer_profile
                FROM organization_staff os
                JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                JOIN tournaments t ON t.id = sta.tournament_id
                LEFT JOIN profiles p ON p.id = os.assigned_by
                WHERE os.user_id = @userId AND os.status = 'pending'
                ORDER BY os.created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });
            ctx.Response.Headers.Append("Deprecation", "true");
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/staff/my-assignments ────────────────────────
        // Navigation/listing only — not for authorization. Prefer
        // GET /api/organizations/staff/assignments or GET /api/tournaments/{id}/access.
        app.MapGet("/api/tournaments/staff/my-assignments", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT DISTINCT ON (t.id)
                       os.id, t.id AS tournament_id, os.user_id, os.role,
                       CASE WHEN os.role = 'admin' THEN @allPerms::text[] ELSE os.permissions END AS permissions,
                       os.status, os.assigned_by,
                       os.created_at, os.updated_at, os.accepted_at,
                       os.id AS organization_staff_id,
                       jsonb_build_object(
                           'id', t.id, 'name', t.name, 'slug', t.slug,
                           'game', t.game, 'start_date', t.start_date,
                           'organizer_id', t.organizer_id
                       ) AS tournament,
                       jsonb_build_object(
                           'full_name', p.full_name,
                           'username', p.username,
                           'email', p.email
                       ) AS organizer_profile
                FROM organization_staff os
                JOIN tournaments t ON t.deleted_at IS NULL
                  AND (
                    t.organization_id = os.organization_id
                    OR (
                      os.role = 'admin'
                      AND EXISTS (
                        SELECT 1 FROM organizations o
                        WHERE o.id = os.organization_id
                          AND o.owner_id = t.organizer_id
                      )
                    )
                  )
                LEFT JOIN staff_tournament_assignments sta
                  ON sta.organization_staff_id = os.id AND sta.tournament_id = t.id
                LEFT JOIN profiles p ON p.id = os.assigned_by
                WHERE os.user_id = @userId AND os.status = 'active'
                  AND (os.role = 'admin' OR sta.id IS NOT NULL)
                ORDER BY t.id, os.updated_at DESC
                """,
                new { userId = userCtx.UserIdGuid, allPerms = StaffAuthHelper.AllStaffPermissions });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/staff/{inviteId}/respond ───────────────────
        app.MapPost("/api/tournaments/staff/{inviteId}/respond", async (
            Guid inviteId,
            [FromBody] RespondToStaffInviteRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Only the invited user can respond
            var inviteUserId = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT user_id FROM organization_staff WHERE id = @inviteId AND status = 'pending'",
                new { inviteId });
            if (inviteUserId is null) return Results.NotFound(new { error = "Invite not found or already responded." });
            if (inviteUserId != userCtx.UserId) return Results.Forbid();

            var now = DateTime.UtcNow;
            await conn.ExecuteAsync(
                """
                UPDATE organization_staff
                SET status = @status::text, accepted_at = @acceptedAt, responded_at = @respondedAt
                WHERE id = @inviteId AND status = 'pending'
                """,
                new
                {
                    inviteId,
                    status = req.Accept ? "active" : "declined",
                    acceptedAt = req.Accept ? now : (DateTime?)null,
                    respondedAt = now,
                });

            return Results.Ok(new { success = true, accepted = req.Accept });
        }).RequireAuthorization("Authenticated");
    }

    // ── Slug helper ───────────────────────────────────────────────────────────
    // Simple slug: lowercase, replace non-alphanumeric with hyphens, collapse.
    private static string Slugify(string input) =>
        System.Text.RegularExpressions.Regex.Replace(
            input.ToLowerInvariant().Trim(),
            @"[^a-z0-9]+", "-").Trim('-');

    // ══════════════════════════════════════════════════════════════════════════
    // Organizer Dispute Endpoints (tournament_disputes)
    // ══════════════════════════════════════════════════════════════════════════

    private static void MapOrganizerDisputeEndpoints(WebApplication app)
    {
        // ── GET /api/organizer/disputes ───────────────────────────────────────
        // Replaces 5 sequential Supabase calls: owned tournaments + staff tournaments
        // + tournament names + disputes + filer profiles + match context
        app.MapGet("/api/organizer/disputes", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            const string disputeListSqlTemplate = """
                SELECT td.id, td.reference_number, td.title, td.description, td.status, td.dispute_reason,
                       td.resolution_notes, td.evidence_url, td.created_at, td.updated_at,
                       td.tournament_id, td.match_id, td.raised_by_user_id, td.team_id,
                       t.name AS tournament_name,
                       COALESCE(p.full_name, p.username, 'Unknown') AS raised_by_name,
                       td_team.name AS team_name,
                       CASE WHEN bm.id IS NOT NULL THEN jsonb_build_object(
                           'match_number', bm.match_number,
                           'round_index', bm.round_index,
                           'best_of', bm.best_of,
                           'bracket_type', bm.bracket_type,
                           'scheduled_time', bm.scheduled_time,
                           'team1_score', bm.team1_score,
                           'team2_score', bm.team2_score,
                           'team1_name', t1.name,
                           'team2_name', t2.name,
                           'team1_id', bm.team1_id,
                           'team2_id', bm.team2_id
                       ) ELSE NULL END AS match,
                       -- Match reports (screenshots, scoreboard, riot match id)
                       (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                           'id', mrr.id,
                           'game_number', mrr.game_number,
                           'reported_by_team_id', mrr.reported_by_team_id,
                           'riot_match_id', mrr.riot_match_id,
                           'map_name', mrr.map_name,
                           'team1_score', mrr.team1_score,
                           'team2_score', mrr.team2_score,
                           'match_data', mrr.match_data,
                           'screenshot_urls', mrr.screenshot_urls,
                           'status', mrr.status,
                           'created_at', mrr.created_at
                       ) ORDER BY mrr.game_number, mrr.created_at), '[]'::jsonb)
                       FROM match_result_reports mrr
                       WHERE mrr.match_id = td.match_id) AS reports,
                       -- Riot accounts for all players in both teams
                       CASE WHEN bm.id IS NOT NULL THEN (
                           SELECT COALESCE(jsonb_agg(jsonb_build_object(
                               'team_id', tm.team_id,
                               'team_name', CASE WHEN tm.team_id = bm.team1_id THEN t1.name ELSE t2.name END,
                               'user_id', tm.user_id,
                               'username', COALESCE(pr.full_name, pr.username),
                               'game_name', ra.game_name,
                               'tag_line', ra.tag_line,
                               'puuid', ra.puuid
                           )), '[]'::jsonb)
                           FROM team_members tm
                           INNER JOIN riot_accounts ra ON ra.user_id = tm.user_id
                           LEFT JOIN profiles pr ON pr.id = tm.user_id
                           WHERE tm.team_id IN (bm.team1_id, bm.team2_id)
                             AND tm.is_active = true
                       ) ELSE '[]'::jsonb END AS riot_accounts,
                       -- Disputing party counter-evidence (match_disputes)
                       (SELECT row_to_json(sub)::jsonb FROM (
                           SELECT md.id, md.reason,
                                  CASE
                                      WHEN md.evidence_urls IS NOT NULL
                                           AND COALESCE(array_length(md.evidence_urls, 1), 0) > 0
                                      THEN md.evidence_urls
                                      WHEN td.evidence_url IS NOT NULL
                                      THEN ARRAY[td.evidence_url]::text[]
                                      ELSE COALESCE(md.evidence_urls, '{}'::text[])
                                  END AS evidence_urls,
                                  md.disputed_by_team_id, md.disputed_by_user_id, md.created_at, md.status,
                                  COALESCE(pr_md.full_name, pr_md.username) AS disputed_by_name,
                                  CASE WHEN md.disputed_by_team_id = bm.team1_id THEN t1.name
                                       WHEN md.disputed_by_team_id = bm.team2_id THEN t2.name
                                       ELSE td_team.name END AS disputed_by_team_name
                           FROM match_disputes md
                           LEFT JOIN profiles pr_md ON pr_md.id = md.disputed_by_user_id
                           WHERE md.match_id = td.match_id
                           ORDER BY md.created_at DESC
                           LIMIT 1
                       ) sub) AS match_dispute
                FROM tournament_disputes td
                JOIN tournaments t ON t.id = td.tournament_id
                LEFT JOIN profiles p ON p.id = td.raised_by_user_id
                LEFT JOIN teams td_team ON td_team.id = td.team_id
                LEFT JOIN brkt_matches bm ON bm.id = td.match_id
                LEFT JOIN teams t1 ON t1.id = bm.team1_id
                LEFT JOIN teams t2 ON t2.id = bm.team2_id
                WHERE (t.organizer_id = @userId
                   OR __STAFF_ACCESS__)
                  AND td.dispute_reason NOT IN ('ban_appeal', 'general_support')
                ORDER BY td.created_at DESC
                """;

            var disputeListSql = disputeListSqlTemplate.Replace(
                "__STAFF_ACCESS__", StaffAuthHelper.StaffTournamentAccessExistsSql);

            var rows = await conn.QueryAsync<dynamic>(
                disputeListSql,
                new { userId = userCtx.UserIdGuid });

            DapperJsonbHelper.FixJsonb(rows);
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizer/disputes/unread-count ─────────────────────────
        // pending_count: open disputes awaiting organizer action (tab badge)
        // unread_count: pending with unread activity (comments / updates since last read)
        app.MapGet("/api/organizer/disputes/unread-count", async (
            [FromQuery(Name = "tournament_id")] Guid? tournamentIdFromSnake,
            [FromQuery(Name = "tournamentId")] Guid? tournamentIdFromCamel,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var tournamentId = tournamentIdFromSnake ?? tournamentIdFromCamel;
            if (tournamentId is null) return Results.BadRequest(new { error = "tournament_id is required." });

            using var conn = db.CreateConnection();

            var isOrganizer = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @tournamentId AND organizer_id = @userId)",
                new { tournamentId, userId = userCtx.UserIdGuid });

            var canAssist = await StaffAuthHelper.CanActOnTournamentAsync(
                conn, userCtx.UserIdGuid, tournamentId.Value, StaffAuthHelper.PermDisputesAssist);

            if (!isOrganizer && !canAssist && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            const string pendingFilter = """
                FROM tournament_disputes td
                WHERE td.tournament_id = @tournamentId
                  AND td.status IN ('open', 'in_review')
                  AND td.dispute_reason NOT IN ('ban_appeal', 'general_support')
                  AND td.raised_by_user_id != @userId
                """;

            var pendingCount = await conn.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*)::int {pendingFilter}",
                new { tournamentId, userId = userCtx.UserIdGuid });

            var unreadCount = pendingCount;
            try
            {
                unreadCount = await conn.ExecuteScalarAsync<int>(
                    $"""
                    SELECT COUNT(*)::int
                    {pendingFilter}
                      AND (
                          NOT EXISTS (
                              SELECT 1 FROM dispute_read_receipts drr
                              WHERE drr.dispute_id = td.id AND drr.user_id = @userId
                          )
                          OR td.updated_at > (
                              SELECT drr.last_read_at FROM dispute_read_receipts drr
                              WHERE drr.dispute_id = td.id AND drr.user_id = @userId
                          )
                          OR EXISTS (
                              SELECT 1 FROM dispute_comments dc
                              WHERE dc.dispute_id = td.id
                                AND dc.is_internal = FALSE
                                AND dc.user_id != @userId
                                AND dc.created_at > COALESCE((
                                    SELECT drr.last_read_at FROM dispute_read_receipts drr
                                    WHERE drr.dispute_id = td.id AND drr.user_id = @userId
                                ), '1970-01-01'::timestamptz)
                          )
                      )
                    """,
                    new { tournamentId, userId = userCtx.UserIdGuid });
            }
            catch
            {
                // dispute_read_receipts may not exist yet on older DBs — badge still shows pending
                unreadCount = pendingCount;
            }

            return Results.Ok(new { unread_count = unreadCount, pending_count = pendingCount });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizer/disputes/{disputeId}/read ────────────────────
        app.MapPost("/api/organizer/disputes/{disputeId}/read", async (
            Guid disputeId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT td.tournament_id
                FROM tournament_disputes td
                WHERE td.id = @disputeId
                """,
                new { disputeId });
            if (tournamentId is null) return Results.NotFound();

            var isOrganizer = await StaffAuthHelper.IsTournamentOrganizerOrOrgOwnerAsync(
                conn, userCtx.UserIdGuid, tournamentId.Value);
            var canAssist = await StaffAuthHelper.CanActOnTournamentAsync(
                conn, userCtx.UserIdGuid, tournamentId.Value, StaffAuthHelper.PermDisputesAssist);
            if (!isOrganizer && !canAssist && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            await conn.ExecuteAsync(
                """
                INSERT INTO dispute_read_receipts (dispute_id, user_id, last_read_at)
                VALUES (@disputeId, @userId, NOW())
                ON CONFLICT (dispute_id, user_id)
                DO UPDATE SET last_read_at = NOW()
                """,
                new { disputeId, userId = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizer/disputes/{disputeId} ──────────────────────────
        app.MapGet("/api/organizer/disputes/{disputeId}", async (
            Guid disputeId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, status, dispute_reason, resolution_notes, updated_at FROM tournament_disputes WHERE id = @disputeId",
                new { disputeId });
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/organizer/disputes/{disputeId} ──────────────────────────
        app.MapPut("/api/organizer/disputes/{disputeId}", async (
            Guid disputeId,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var req = await System.Text.Json.JsonSerializer.DeserializeAsync<UpdateDisputeRequest>(
                ctx.Request.Body, s_snakeCase, ct);
            if (req is null) return Results.BadRequest("Invalid body");

            using var conn = db.CreateConnection();

            var tournamentId = await conn.ExecuteScalarAsync<Guid?>(
                "SELECT tournament_id FROM tournament_disputes WHERE id = @disputeId",
                new { disputeId });
            if (tournamentId is null) return Results.NotFound();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, tournamentId.Value, ct: ct))
                return Results.Forbid();

            var setClauses = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("disputeId", disputeId);

            if (req.Status is not null) { setClauses.Add("status = @status"); parameters.Add("status", req.Status); }
            if (req.AssignedToUserId is not null) { setClauses.Add("assigned_to_user_id = @assignedTo"); parameters.Add("assignedTo", Guid.Parse(req.AssignedToUserId)); }
            if (req.ResolutionNotes is not null) { setClauses.Add("resolution_notes = @notes"); parameters.Add("notes", req.ResolutionNotes); }
            setClauses.Add("updated_at = NOW()");

            var sql = $"UPDATE tournament_disputes SET {string.Join(", ", setClauses)} WHERE id = @disputeId";
            await conn.ExecuteAsync(sql, parameters);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizer/disputes/{disputeId}/comments ─────────────────
        app.MapGet("/api/organizer/disputes/{disputeId}/comments", async (
            Guid disputeId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT dc.id, dc.user_id, dc.comment, dc.is_internal, dc.created_at, dc.attachment_url,
                       COALESCE(p.full_name, p.username, 'Unknown') AS user_name
                FROM dispute_comments dc
                LEFT JOIN profiles p ON p.id = dc.user_id
                WHERE dc.dispute_id = @disputeId
                ORDER BY dc.created_at ASC
                """,
                new { disputeId });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizer/disputes/{disputeId}/comments ────────────────
        app.MapPost("/api/organizer/disputes/{disputeId}/comments", async (
            Guid disputeId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> notifHub,
            IHubContext<MatchHub> matchHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var req = await System.Text.Json.JsonSerializer.DeserializeAsync<AddDisputeCommentRequest>(
                ctx.Request.Body, s_snakeCase, ct);
            if (req is null) return Results.BadRequest("Invalid body");

            using var conn = db.CreateConnection();

            // Insert comment
            await conn.ExecuteAsync(
                """
                INSERT INTO dispute_comments (dispute_id, user_id, comment, is_internal, attachment_url)
                VALUES (@disputeId, @userId, @comment, FALSE, @attachmentUrl)
                """,
                new { disputeId, userId = userCtx.UserIdGuid, comment = req.Comment ?? "", attachmentUrl = req.AttachmentUrl });

            await conn.ExecuteAsync(
                "UPDATE tournament_disputes SET updated_at = NOW() WHERE id = @disputeId",
                new { disputeId });

            // Scope broadcast to match group
            var matchId1 = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT match_id FROM tournament_disputes WHERE id = @disputeId", new { disputeId });
            if (matchId1 is not null)
                await matchHub.Clients.Group(MatchHub.MatchGroup(matchId1.Value.ToString()))
                    .SendAsync(MatchHubEvents.DisputeCommentAdded,
                        new { disputeId, userId = userCtx.UserIdGuid.ToString() }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizer/disputes/{disputeId}/resolve ─────────────────
        app.MapPost("/api/organizer/disputes/{disputeId}/resolve", async (
            Guid disputeId,
            ResolveDisputeRequest2 req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> notifHub,
            IHubContext<MatchHub> matchHub,
            IHubContext<BracketHub> bracketHub,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req is null) return Results.BadRequest(new { error = "Invalid body" });

            var status = req.Status?.Trim().ToLowerInvariant();
            if (status is not ("resolved" or "rejected"))
                return Results.BadRequest(new { error = "Status must be 'resolved' or 'rejected'." });

            var notes = (req.ResolutionNotes ?? req.ResolutionNotesSnake)?.Trim();
            if (string.IsNullOrWhiteSpace(notes) || notes.Length < 10)
                return Results.BadRequest(new { error = "Resolution notes must be at least 10 characters." });

            var reportIdCandidate = req.ReportId ?? req.ReportIdSnake;
            var reportIdRaw = string.IsNullOrWhiteSpace(reportIdCandidate) ? null : reportIdCandidate.Trim();

            var logger = loggerFactory.CreateLogger("DisputeResolve");
            try
            {
                using var conn = db.CreateConnection();

                var disputeRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT match_id, tournament_id, status FROM tournament_disputes WHERE id = @disputeId",
                    new { disputeId });
                if (disputeRow is null) return Results.NotFound(new { error = "Dispute not found" });

                var currentStatus = (string?)disputeRow.status;
                if (currentStatus is "resolved" or "rejected")
                    return Results.BadRequest(new { error = "This dispute has already been closed." });

                Guid? disputeMatchId = disputeRow.match_id as Guid?;
                Guid tournamentId = (Guid)disputeRow.tournament_id;

                var isOwner = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @tid AND organizer_id = @userId)",
                    new { tid = tournamentId, userId = userCtx.UserIdGuid });

                var canAssist = disputeMatchId is not null
                    && await StaffAuthHelper.CanActOnBracketMatchAsync(
                        conn, userCtx.UserIdGuid, disputeMatchId.Value, StaffAuthHelper.PermDisputesAssist);

                if (!isOwner && !canAssist && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                    return Results.Forbid();

                // Update dispute status
                await conn.ExecuteAsync(
                    """
                UPDATE tournament_disputes
                SET status = @status::text, resolution_notes = @notes,
                    assigned_to_user_id = @userId, updated_at = NOW()
                WHERE id = @disputeId
                """,
                    new { disputeId, status, notes, userId = userCtx.UserIdGuid });

                var enforcedReport = false;

                // If resolving with an accepted report: enforce scores on the match
                if (status == "resolved" && reportIdRaw is not null)
                {
                    if (!Guid.TryParse(reportIdRaw, out var reportId))
                        return Results.BadRequest(new { error = "Invalid report id." });

                    var report = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        """
                    SELECT mrr.match_id, mrr.team1_score, mrr.team2_score,
                           mrr.reported_by_team_id,
                           bm.team1_id, bm.team2_id, bm.version_id
                    FROM match_result_reports mrr
                    JOIN brkt_matches bm ON bm.id = mrr.match_id
                    WHERE mrr.id = @reportId
                    """,
                        new { reportId });

                    if (report is null)
                        return Results.BadRequest(new { error = "Report not found." });

                    enforcedReport = true;
                    Guid matchId = (Guid)report.match_id;
                    if (disputeMatchId is not null && matchId != disputeMatchId.Value)
                        return Results.BadRequest(new { error = "Report does not belong to this dispute's match." });

                    int t1Score = (int)report.team1_score;
                    int t2Score = (int)report.team2_score;

                    if (t1Score == t2Score)
                        return Results.BadRequest(new { error = "Reported scores cannot be tied." });

                    Guid winnerId = t1Score > t2Score ? (Guid)report.team1_id : (Guid)report.team2_id;
                    Guid loserId = t1Score > t2Score ? (Guid)report.team2_id : (Guid)report.team1_id;

                    // Enforce scores + winner on match
                    await conn.ExecuteAsync(
                        """
                    UPDATE brkt_matches
                    SET team1_score = @t1, team2_score = @t2,
                        winner_id = @winner, loser_id = @loser, status = 'completed', updated_at = NOW()
                    WHERE id = @matchId
                    """,
                        new { t1 = t1Score, t2 = t2Score, winner = winnerId, loser = loserId, matchId });

                    // Advance winner/loser through bracket edges (with seeds)
                    var sourceMatch = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        "SELECT team1_id, team2_id, team1_seed, team2_seed FROM brkt_matches WHERE id = @matchId",
                        new { matchId });

                    var advancements = await conn.QueryAsync<dynamic>(
                        "SELECT target_match_id, target_slot, type FROM brkt_advancements WHERE source_match_id = @matchId",
                        new { matchId });

                    foreach (var adv in advancements)
                    {
                        Guid teamId = (string?)adv.type == "winner" ? winnerId : loserId;

                        // Determine the seed of the advancing team
                        int? teamSeed = null;
                        if (sourceMatch is not null)
                        {
                            if ((Guid?)sourceMatch.team1_id == teamId)
                                teamSeed = (int?)sourceMatch.team1_seed;
                            else if ((Guid?)sourceMatch.team2_id == teamId)
                                teamSeed = (int?)sourceMatch.team2_seed;
                        }

                        string teamField = (int)adv.target_slot == 1 ? "team1_id" : "team2_id";
                        string seedField = (int)adv.target_slot == 1 ? "team1_seed" : "team2_seed";
                        await conn.ExecuteAsync(
                            $"UPDATE brkt_matches SET {teamField} = @teamId, {seedField} = @teamSeed WHERE id = @targetId",
                            new { teamId, teamSeed, targetId = (Guid)adv.target_match_id });
                    }

                    // Mark report as accepted, others for this match as rejected
                    await conn.ExecuteAsync(
                        """
                    UPDATE match_result_reports SET status = 'accepted'  WHERE id = @reportId;
                    UPDATE match_result_reports SET status = 'rejected', responded_at = NOW(), responded_by = @userId
                      WHERE match_id = @matchId AND id != @reportId AND status IN ('disputed', 'pending');
                    """,
                        new { reportId, matchId, userId = userCtx.UserIdGuid });

                    await conn.ExecuteAsync(
                        """
                    UPDATE match_disputes
                    SET status = 'resolved',
                        resolution = @notes,
                        resolved_at = NOW(),
                        resolved_by = @userId
                    WHERE match_id = @matchId AND status = 'pending'
                    """,
                        new { matchId, notes, userId = userCtx.UserIdGuid });

                    var versionId = (Guid?)report.version_id;
                    if (versionId is not null)
                    {
                        await bracketHub.Clients
                            .Group(BracketHub.BracketGroup(versionId.Value.ToString()))
                            .SendAsync(BracketHubEvents.MatchUpdated, new { versionId, matchId }, ct);
                    }

                    // Notify both team captains about enforced result
                    var captains = await conn.QueryAsync<dynamic>(
                        """
                        SELECT tm.user_id, t.name AS team_name,
                               CASE WHEN t.id = @team1Id THEN @t1Score ELSE @t2Score END AS own_score,
                               CASE WHEN t.id = @team1Id THEN @t2Score ELSE @t1Score END AS opp_score
                        FROM teams t
                        JOIN team_members tm ON tm.team_id = t.id AND tm.role = 'captain' AND tm.is_active = true
                        WHERE t.id IN (@team1Id, @team2Id)
                        """,
                        new
                        {
                            team1Id = (Guid)report.team1_id,
                            team2Id = (Guid)report.team2_id,
                            t1Score,
                            t2Score
                        });

                    foreach (var captain in captains)
                    {
                        Guid captainId = (Guid)captain.user_id;
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                            VALUES (@userId, 'result_accepted', '⚖️ Match Result Enforced',
                                    @message, '/user/matches', @data::jsonb, FALSE)
                            """,
                            new
                            {
                                userId = captainId,
                                message = $"The organizer has made the final call — match result: {captain.own_score} – {captain.opp_score} for your team.",
                                data = System.Text.Json.JsonSerializer.Serialize(new { match_id = matchId, dispute_id = disputeId }),
                            });
                        await notifHub.Clients.Group($"user:{captainId}")
                            .SendAsync("NewNotification", new { type = "result_accepted" }, ct);
                    }
                }

                if (disputeMatchId is not null && !enforcedReport)
                {
                    var matchId = disputeMatchId.Value;
                    var actorId = userCtx.UserIdGuid;

                    if (status == "resolved")
                    {
                        await conn.ExecuteAsync(
                            """
                        UPDATE match_disputes
                        SET status = 'resolved',
                            resolution = @notes,
                            resolved_at = NOW(),
                            resolved_by = @userId
                        WHERE match_id = @matchId AND status = 'pending'
                        """,
                            new { matchId, notes, userId = actorId });

                        await conn.ExecuteAsync(
                            """
                        UPDATE match_result_reports
                        SET status = 'rejected', responded_at = NOW(), responded_by = @userId
                        WHERE match_id = @matchId AND status = 'disputed'
                        """,
                            new { matchId, userId = actorId });
                    }
                    else
                    {
                        await conn.ExecuteAsync(
                            """
                        UPDATE match_disputes
                        SET status = 'rejected',
                            resolution = @notes,
                            resolved_at = NOW(),
                            resolved_by = @userId
                        WHERE match_id = @matchId AND status = 'pending'
                        """,
                            new { matchId, notes, userId = actorId });

                        await conn.ExecuteAsync(
                            """
                        UPDATE match_result_reports
                        SET status = 'rejected', responded_at = NOW(), responded_by = @userId
                        WHERE match_id = @matchId AND status IN ('disputed', 'pending')
                        """,
                            new { matchId, userId = actorId });
                    }
                }

                if (disputeMatchId is not null)
                {
                    await matchHub.Clients
                        .Group(MatchHub.MatchGroup(disputeMatchId.Value.ToString()))
                        .SendAsync(MatchHubEvents.DisputeResolved,
                            new { match_id = disputeMatchId.Value, dispute_id = disputeId, status }, ct);
                }

                // Notify the dispute filer (best-effort — don't fail the request)
                try
                {
                    var dispute = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        "SELECT raised_by_user_id, title FROM tournament_disputes WHERE id = @disputeId",
                        new { disputeId });

                    if (dispute is not null)
                    {
                        Guid filerId = (Guid)dispute.raised_by_user_id;
                        string title = ((string?)dispute.title) ?? "Your dispute";
                        var notifType = status == "resolved" ? "dispute_resolved" : "dispute_rejected";
                        var notifTitle = status == "resolved"
                            ? "✅ Dispute Resolved"
                            : "❌ Dispute Rejected";
                        var notifMsg = status == "resolved"
                            ? $"Your dispute \"{title}\" has been resolved by the organizer. Check the outcome in your disputes page."
                            : $"Your dispute \"{title}\" was reviewed and rejected by the organizer.";

                        await conn.ExecuteAsync(
                            """
                    INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                    VALUES (@userId, @type, @title, @message, '/user/my-disputes',
                            @data::jsonb, FALSE)
                    """,
                            new
                            {
                                userId = filerId,
                                type = notifType,
                                title = notifTitle,
                                message = notifMsg,
                                data = System.Text.Json.JsonSerializer.Serialize(new { dispute_id = disputeId }),
                            });

                        await notifHub.Clients.Group($"user:{filerId}")
                            .SendAsync("NewNotification", new { type = notifType }, ct);
                    }
                }
                catch (Exception notifEx)
                {
                    logger.LogWarning(notifEx, "Failed to send resolve notification for dispute {DisputeId} (non-fatal)", disputeId);
                }

                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resolve dispute {DisputeId}", disputeId);
                return Results.Json(new { error = "We couldn't resolve this dispute. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/captain ──────────────────────────────────────
        // Returns tournaments where the current user is a team captain
        // Used by Details.tsx captain-match view
        app.MapGet("/api/tournaments/captain", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var tournaments = await conn.QueryAsync<dynamic>(
                """
                SELECT DISTINCT t.id, t.name, t.slug, t.game,
                       CASE
                           WHEN t.status::text IN ('open', 'published', 'check_in') AND t.start_date IS NOT NULL AND t.start_date <= NOW() THEN 'ongoing'
                           WHEN t.status::text = 'ongoing' AND t.end_date IS NOT NULL AND t.end_date <= NOW() THEN 'completed'
                           ELSE t.status::text
                       END AS status,
                       t.start_date, t.logo_url, t.format,
                       tp.team_id, teams.name AS team_name, teams.logo_url AS team_logo
                FROM public.team_members tm
                JOIN public.tournament_participants tp ON tp.team_id = tm.team_id
                JOIN public.tournaments t ON t.id = tp.tournament_id
                JOIN public.teams ON teams.id = tm.team_id
                WHERE tm.user_id = @userId
                  AND tm.role = 'captain'
                  AND tm.is_active = TRUE
                  AND t.status IN ('ongoing', 'check_in', 'open', 'published')
                ORDER BY t.start_date DESC
                """, new { userId = userCtx.UserIdGuid });

            return Results.Ok(tournaments);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/my-registrations ────────────────────────────
        // Used by RaiseDispute.tsx — returns tournaments the user is registered in
        app.MapGet("/api/tournaments/my-registrations", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var registrations = await conn.QueryAsync<dynamic>(
                """
                SELECT tp.tournament_id, t.name AS tournament_name, t.slug AS tournament_slug,
                       tp.team_id, tp.status, t.start_date, t.game
                FROM public.tournament_participants tp
                JOIN public.tournaments t ON t.id = tp.tournament_id
                WHERE tp.user_id = @userId
                ORDER BY t.start_date DESC
                """, new { userId = userCtx.UserIdGuid });

            return Results.Ok(registrations);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/announcements ──────────────────────────
        app.MapGet("/api/tournaments/{id}/announcements", async (
            Guid id,
            IDbConnectionFactory db,
            [FromQuery] int limit = 50,
            [FromQuery] int offset = 0,
            CancellationToken ct = default) =>
        {
            using var conn = db.CreateConnection();
            var announcements = await conn.QueryAsync<object>(
                """
                SELECT ta.id, ta.tournament_id, ta.sender_id, ta.title, ta.content,
                       ta.created_at, ta.updated_at, p.username AS sender_name
                FROM public.tournament_announcements ta
                LEFT JOIN public.profiles p ON p.id = ta.sender_id
                WHERE ta.tournament_id = @id
                ORDER BY ta.created_at DESC
                LIMIT @limit OFFSET @offset
                """, new { id, limit = Math.Clamp(limit, 1, 100), offset = Math.Max(offset, 0) });

            return Results.Ok(announcements);
        }); // Public

        // ── GET /api/disputes/mine ───────────────────────────────────────────
        // Player-facing: disputes filed by user OR on matches they participated in
        app.MapGet("/api/disputes/mine", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var disputes = await conn.QueryAsync<dynamic>(
                """
                SELECT td.id, td.reference_number, td.tournament_id, td.match_id, td.raised_by_user_id,
                       td.team_id, td.title, td.description, td.evidence_url,
                       td.status, td.resolution_notes, td.dispute_reason,
                       td.created_at, td.updated_at,
                       t.name AS tournament_name, t.slug AS tournament_slug,
                       CASE WHEN bm.id IS NOT NULL THEN jsonb_build_object(
                           'match_number', bm.match_number,
                           'round_index', bm.round_index,
                           'best_of', bm.best_of,
                           'bracket_type', bm.bracket_type,
                           'scheduled_time', bm.scheduled_time,
                           'team1_score', bm.team1_score,
                           'team2_score', bm.team2_score,
                           'team1_name', t1.name,
                           'team2_name', t2.name,
                           'team1_id', bm.team1_id,
                           'team2_id', bm.team2_id
                       ) ELSE NULL END AS match,
                       (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                           'id', mrr.id,
                           'game_number', mrr.game_number,
                           'reported_by_team_id', mrr.reported_by_team_id,
                           'riot_match_id', mrr.riot_match_id,
                           'map_name', mrr.map_name,
                           'team1_score', mrr.team1_score,
                           'team2_score', mrr.team2_score,
                           'match_data', mrr.match_data,
                           'screenshot_urls', mrr.screenshot_urls,
                           'status', mrr.status,
                           'created_at', mrr.created_at
                       ) ORDER BY mrr.game_number, mrr.created_at), '[]'::jsonb)
                       FROM match_result_reports mrr
                       WHERE mrr.match_id = td.match_id) AS reports,
                       CASE WHEN bm.id IS NOT NULL THEN (
                           SELECT COALESCE(jsonb_agg(jsonb_build_object(
                               'team_id', tm.team_id,
                               'team_name', CASE WHEN tm.team_id = bm.team1_id THEN t1.name ELSE t2.name END,
                               'user_id', tm.user_id,
                               'username', COALESCE(pr.full_name, pr.username),
                               'game_name', ra.game_name,
                               'tag_line', ra.tag_line,
                               'puuid', ra.puuid
                           )), '[]'::jsonb)
                           FROM team_members tm
                           INNER JOIN riot_accounts ra ON ra.user_id = tm.user_id
                           LEFT JOIN profiles pr ON pr.id = tm.user_id
                           WHERE tm.team_id IN (bm.team1_id, bm.team2_id)
                             AND tm.is_active = true
                       ) ELSE '[]'::jsonb END AS riot_accounts,
                       (SELECT row_to_json(sub)::jsonb FROM (
                           SELECT md.id, md.reason,
                                  CASE
                                      WHEN md.evidence_urls IS NOT NULL
                                           AND COALESCE(array_length(md.evidence_urls, 1), 0) > 0
                                      THEN md.evidence_urls
                                      WHEN td.evidence_url IS NOT NULL
                                      THEN ARRAY[td.evidence_url]::text[]
                                      ELSE COALESCE(md.evidence_urls, '{}'::text[])
                                  END AS evidence_urls,
                                  md.disputed_by_team_id, md.disputed_by_user_id, md.created_at, md.status,
                                  COALESCE(pr_md.full_name, pr_md.username) AS disputed_by_name,
                                  CASE WHEN md.disputed_by_team_id = bm.team1_id THEN t1.name
                                       WHEN md.disputed_by_team_id = bm.team2_id THEN t2.name
                                       ELSE td_team.name END AS disputed_by_team_name
                           FROM match_disputes md
                           LEFT JOIN profiles pr_md ON pr_md.id = md.disputed_by_user_id
                           WHERE md.match_id = td.match_id
                           ORDER BY md.created_at DESC
                           LIMIT 1
                       ) sub) AS match_dispute
                FROM public.tournament_disputes td
                LEFT JOIN public.tournaments t ON t.id = td.tournament_id
                LEFT JOIN teams td_team ON td_team.id = td.team_id
                LEFT JOIN brkt_matches bm ON bm.id = td.match_id
                LEFT JOIN teams t1 ON t1.id = bm.team1_id
                LEFT JOIN teams t2 ON t2.id = bm.team2_id
                WHERE td.raised_by_user_id = @userId
                   OR EXISTS (
                       SELECT 1 FROM brkt_matches bm2
                       JOIN team_members tm ON tm.team_id IN (bm2.team1_id, bm2.team2_id)
                       WHERE bm2.id = td.match_id
                         AND tm.user_id = @userId
                         AND tm.is_active = true
                   )
                   OR EXISTS (
                       SELECT 1 FROM match_result_reports mrr2
                       WHERE mrr2.match_id = td.match_id
                         AND mrr2.reported_by = @userId
                   )
                ORDER BY td.created_at DESC
                """, new { userId = userCtx.UserIdGuid });

            DapperJsonbHelper.FixJsonb(disputes);
            return Results.Ok(disputes);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/disputes/{disputeId} ────────────────────────────────────
        // Player-facing: get single dispute details (ownership check)
        app.MapGet("/api/disputes/{disputeId}", async (
            Guid disputeId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var dispute = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT td.id, td.reference_number, td.tournament_id, td.match_id, td.raised_by_user_id,
                       td.team_id, td.title, td.description, td.evidence_url,
                       td.status, td.resolution_notes, td.dispute_reason,
                       td.created_at, td.updated_at,
                       t.name AS tournament_name
                FROM public.tournament_disputes td
                LEFT JOIN public.tournaments t ON t.id = td.tournament_id
                WHERE td.id = @disputeId
                """, new { disputeId });

            if (dispute is null) return Results.NotFound();

            // Allow access if the user filed it, or is organizer/staff
            Guid filerId = (Guid)dispute.raised_by_user_id;
            if (filerId != userCtx.UserIdGuid)
            {
                Guid tournamentId = (Guid)dispute.tournament_id;
                var canView = await StaffAuthHelper.CanActOnTournamentAsync(
                    conn, userCtx.UserIdGuid, tournamentId, StaffAuthHelper.PermDisputesAssist);
                if (!canView && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                    return Results.Forbid();
            }

            return Results.Ok(dispute);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/disputes/{disputeId}/comments ───────────────────────────
        app.MapGet("/api/disputes/{disputeId}/comments", async (
            Guid disputeId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var comments = await conn.QueryAsync<dynamic>(
                """
                SELECT dc.id, dc.dispute_id, dc.user_id, dc.comment,
                       dc.is_internal, dc.attachment_url, dc.created_at,
                       p.username AS author_name
                FROM public.dispute_comments dc
                LEFT JOIN public.profiles p ON p.id = dc.user_id
                WHERE dc.dispute_id = @disputeId AND dc.is_internal = FALSE
                ORDER BY dc.created_at ASC
                """, new { disputeId });

            return Results.Ok(comments);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/disputes/{disputeId}/comments ──────────────────────────
        app.MapPost("/api/disputes/{disputeId}/comments", async (
            Guid disputeId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<MatchHub> matchHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var req = await System.Text.Json.JsonSerializer.DeserializeAsync<AddDisputeCommentRequest>(
                ctx.Request.Body, s_snakeCase, ct);
            if (req is null) return Results.BadRequest("Invalid body");

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                """
                INSERT INTO dispute_comments (dispute_id, user_id, comment, is_internal, attachment_url)
                VALUES (@disputeId, @userId, @comment, FALSE, @attachmentUrl)
                """,
                new { disputeId, userId = userCtx.UserIdGuid, comment = req.Comment ?? "", attachmentUrl = req.AttachmentUrl });

            await conn.ExecuteAsync(
                "UPDATE tournament_disputes SET updated_at = NOW() WHERE id = @disputeId",
                new { disputeId });

            // Scope broadcast to match group
            var matchId2 = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT match_id FROM tournament_disputes WHERE id = @disputeId", new { disputeId });
            if (matchId2 is not null)
                await matchHub.Clients.Group(MatchHub.MatchGroup(matchId2.Value.ToString()))
                    .SendAsync(MatchHubEvents.DisputeCommentAdded,
                        new { disputeId, userId = userCtx.UserIdGuid.ToString() }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/disputes/{disputeId} ──────────────────────────────────
        app.MapPatch("/api/disputes/{disputeId}", async (
            Guid disputeId,
            [FromBody] UpdateDisputeRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var setClauses = new List<string> { "updated_at = NOW()" };
            if (req.Status is not null) setClauses.Add("status = @status::text");

            var sql = $"UPDATE tournament_disputes SET {string.Join(", ", setClauses)} WHERE id = @disputeId";
            await conn.ExecuteAsync(sql, new { disputeId, status = req.Status });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/my-bans ─────────────────────────────────────
        app.MapGet("/api/tournaments/my-bans", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var bans = await conn.QueryAsync<dynamic>(
                """
                SELECT tb.id, tb.tournament_id, tb.ban_reason, tb.banned_at, tb.is_active,
                       t.name AS tournament_name, t.slug AS tournament_slug
                FROM public.tournament_bans tb
                JOIN public.tournaments t ON t.id = tb.tournament_id
                WHERE tb.user_id = @userId AND tb.is_active = TRUE
                ORDER BY tb.banned_at DESC
                """, new { userId = userCtx.UserIdGuid });
            return Results.Ok(bans);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/team-bans ────────────────────────────────────
        app.MapGet("/api/tournaments/team-bans", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var bans = await conn.QueryAsync<dynamic>(
                """
                SELECT tb.id, tb.tournament_id, tb.team_id, tb.ban_reason, tb.banned_at,
                       t.name AS tournament_name, t.slug AS tournament_slug,
                       tm.name AS team_name
                FROM public.tournament_bans tb
                JOIN public.tournaments t ON t.id = tb.tournament_id
                JOIN public.teams tm ON tm.id = tb.team_id
                WHERE tb.team_id IN (
                    SELECT team_id FROM team_members
                    WHERE user_id = @userId AND role = 'captain' AND is_active = TRUE
                ) AND tb.is_active = TRUE
                ORDER BY tb.banned_at DESC
                """, new { userId = userCtx.UserIdGuid });
            return Results.Ok(bans);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/ban-status ──────────────────────────────
        app.MapGet("/api/tournaments/{id}/ban-status", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var ban = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, ban_reason, banned_at
                FROM public.tournament_bans
                WHERE tournament_id = @id AND is_active = TRUE
                  AND (user_id = @userId
                       OR team_id IN (SELECT team_id FROM team_members WHERE user_id = @userId AND is_active = TRUE))
                LIMIT 1
                """, new { id, userId = userCtx.UserIdGuid });
            return Results.Ok(new { isBanned = ban is not null, ban });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/bans ────────────────────────────────────
        app.MapGet("/api/tournaments/{id}/bans", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var bans = await conn.QueryAsync<dynamic>(
                """
                SELECT tb.id, tb.tournament_id, tb.user_id, tb.team_id,
                       tb.ban_reason, tb.banned_at, tb.is_active, tb.banned_by,
                       p.username AS banned_username, p.avatar_url AS banned_avatar
                FROM public.tournament_bans tb
                LEFT JOIN public.profiles p ON p.id = tb.user_id
                WHERE tb.tournament_id = @id
                ORDER BY tb.banned_at DESC
                """, new { id });
            return Results.Ok(bans);
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/tournaments/{id}/bans/{banId} ─────────────────────────
        app.MapDelete("/api/tournaments/{id}/bans/{banId}", async (
            Guid id,
            Guid banId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Lift ban and record who did it
            var ban = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE tournament_bans
                SET is_active = FALSE, lifted_by = @liftedBy, lifted_at = NOW()
                WHERE id = @banId AND tournament_id = @id AND is_active = TRUE
                RETURNING participant_id
                """, new { banId, id, liftedBy = userCtx.UserIdGuid });

            if (ban is null)
                return Results.NotFound(new { error = "Ban not found or already lifted." });

            // Restore participant status if they were disqualified
            if (ban.participant_id is not null)
            {
                // Only restore if no other active bans exist for this participant
                var otherBans = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM tournament_bans WHERE participant_id = @pid AND is_active = TRUE",
                    new { pid = (Guid)ban.participant_id });

                if (otherBans == 0)
                {
                    await conn.ExecuteAsync(
                        "UPDATE tournament_participants SET status = 'approved' WHERE id = @pid AND status = 'disqualified'",
                        new { pid = (Guid)ban.participant_id });
                }
            }

            return Results.Ok(new { success = true, participantRestored = ban.participant_id is not null });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/participants/me ─────────────────────────
        app.MapGet("/api/tournaments/{id}/participants/me", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var participant = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT tp.*, t.name AS team_name, t.logo_url AS team_logo
                FROM public.tournament_participants tp
                LEFT JOIN public.teams t ON t.id = tp.team_id
                WHERE tp.tournament_id = @id AND tp.user_id = @userId
                LIMIT 1
                """, new { id, userId = userCtx.UserIdGuid });
            return participant is null ? Results.NotFound() : Results.Ok(participant);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/map-pool ────────────────────────────────
        app.MapGet("/api/tournaments/{id}/map-pool", async (
            Guid id,
            IDbConnectionFactory db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/');
            var maps = GameMapImageHelper.EnrichRows(
                await conn.QueryAsync<GameMapRow>(
                """
                SELECT gm.id::text as id, gm.game, gm.map_name, gm.map_image_url, gm.is_active
                FROM public.tournament_map_pools tmp
                JOIN public.game_maps gm ON gm.id = tmp.map_id
                WHERE tmp.tournament_id = @id
                ORDER BY gm.map_name ASC
                """, new { id }),
                supabaseUrl);
            return Results.Ok(maps);
        });

        // ── POST /api/tournaments/{id}/map-pool ───────────────────────────────
        app.MapPost("/api/tournaments/{id}/map-pool", async (
            Guid id,
            [FromBody] AddMapToPoolRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                "INSERT INTO tournament_map_pools (tournament_id, map_id) VALUES (@id, @mapId) ON CONFLICT DO NOTHING",
                new { id, mapId = req.MapId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/tournaments/{id}/map-pool/{mapId} ─────────────────────
        app.MapDelete("/api/tournaments/{id}/map-pool/{mapId}", async (
            Guid id,
            Guid mapId,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                "DELETE FROM tournament_map_pools WHERE tournament_id = @id AND map_id = @mapId",
                new { id, mapId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/match-reports ───────────────────────────
        app.MapGet("/api/tournaments/{id}/match-reports", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var reports = await conn.QueryAsync<dynamic>(
                """
                SELECT mr.*, p.username AS reported_by_name
                FROM public.tournament_match_results mr
                LEFT JOIN public.profiles p ON p.id = mr.reporter_user_id
                WHERE mr.tournament_id = @id
                ORDER BY mr.created_at DESC
                """, new { id });
            return Results.Ok(reports);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/result-reports ─────────────────────────
        // Returns match_result_reports (with screenshots) for all matches in this tournament
        app.MapGet("/api/tournaments/{id}/result-reports", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var reports = await conn.QueryAsync<dynamic>(
                """
                SELECT mrr.id, mrr.match_id, mrr.game_number,
                       mrr.reported_by_team_id, mrr.team1_score, mrr.team2_score,
                       mrr.map_name, mrr.screenshot_urls, mrr.match_data,
                       mrr.comment, mrr.status, mrr.created_at
                FROM public.match_result_reports mrr
                JOIN public.brkt_matches m ON m.id = mrr.match_id
                JOIN public.brkt_versions v ON v.id = m.version_id
                WHERE v.tournament_id = @id
                ORDER BY mrr.created_at DESC
                """, new { id });
            DapperJsonbHelper.FixJsonb(reports);
            return Results.Ok(reports);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/announcements ──────────────────────────
        app.MapPost("/api/tournaments/{id}/announcements", async (
            Guid id,
            [FromBody] CreateAnnouncementRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> notifHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Validate tournament exists + verify organizer in a single query
            var tourney = await conn.QuerySingleOrDefaultAsync<(string name, string slug, Guid organizer_id)>(
                "SELECT name, slug, organizer_id FROM tournaments WHERE id = @id", new { id });
            if (tourney == default)
                return Results.NotFound(new { error = "Tournament not found" });
            if (tourney.organizer_id != userCtx.UserIdGuid)
                return Results.Json(new { error = "Only the tournament organizer can post announcements" }, statusCode: 403);

            var announcement = await conn.QuerySingleAsync<(Guid id, Guid tournament_id, string title, string content, DateTime created_at)>(
                """
                INSERT INTO tournament_announcements (tournament_id, sender_id, title, content)
                VALUES (@tournamentId, @senderId, @title, @content)
                RETURNING id, tournament_id, title, content, created_at
                """,
                new { tournamentId = id, senderId = userCtx.UserIdGuid, title = req.Title, content = req.Content });

            // Collect all participant user IDs (solo + team members), excluding sender
            var userIds = (await conn.QueryAsync<Guid>(
                """
                SELECT DISTINCT uid FROM (
                    SELECT tp.user_id AS uid FROM tournament_participants tp
                    WHERE tp.tournament_id = @id AND tp.user_id IS NOT NULL
                    UNION
                    SELECT tm.user_id AS uid FROM tournament_participants tp
                    JOIN team_members tm ON tm.team_id = tp.team_id AND tm.is_active = TRUE
                    WHERE tp.tournament_id = @id AND tp.team_id IS NOT NULL
                ) sub
                WHERE uid != @senderId
                """,
                new { id, senderId = userCtx.UserIdGuid })).ToList();

            if (userIds.Count > 0)
            {
                var notifLink = $"/tournaments/{tourney.slug}";
                var notifData = JsonSerializer.Serialize(new { tournament_id = id, announcement_id = announcement.id });
                var notifTitle = $"📢 {tourney.name}";

                try
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                        VALUES (@userId, 'tournament_announcement', @title, @message, @link, @data::jsonb, FALSE)
                        """,
                        userIds.Select(uid => new
                        {
                            userId = uid,
                            title = notifTitle,
                            message = req.Content,
                            link = notifLink,
                            data = notifData
                        }));

                    var pushTasks = userIds.Select(uid =>
                        notifHub.Clients
                            .Group(NotificationHub.UserGroup(uid.ToString()))
                            .SendAsync(NotificationHubEvents.NewNotification,
                                new { type = "tournament_announcement", title = notifTitle, message = req.Content, link = notifLink }, ct));
                    await Task.WhenAll(pushTasks);
                }
                catch (Exception ex)
                {
                    ctx.RequestServices.GetRequiredService<ILogger<Program>>()
                        .LogError(ex, "Failed to deliver announcement notifications for tournament {TournamentId}", id);
                }
            }

            return Results.Created($"/api/tournaments/{id}/announcements/{announcement.id}", announcement);
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/tournaments/{id}/announcements/{announcementId} ────────
        app.MapPatch("/api/tournaments/{id}/announcements/{announcementId}", async (
            Guid id,
            Guid announcementId,
            [FromBody] UpdateAnnouncementRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var updated = await conn.QuerySingleOrDefaultAsync<object>(
                """
                UPDATE tournament_announcements
                SET title      = COALESCE(@title, title),
                    content    = COALESCE(@content, content),
                    updated_at = NOW()
                WHERE id = @announcementId AND tournament_id = @id
                RETURNING id, tournament_id, sender_id, title, content, created_at, updated_at
                """,
                new { announcementId, id, title = req.Title, content = req.Content });
            return updated is null
                ? Results.NotFound(new { error = "Announcement not found" })
                : Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/tournaments/{id}/announcements/{announcementId} ───────
        app.MapDelete("/api/tournaments/{id}/announcements/{announcementId}", async (
            Guid id,
            Guid announcementId,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, id, ct: ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var rows = await conn.ExecuteAsync(
                "DELETE FROM tournament_announcements WHERE id = @announcementId AND tournament_id = @id",
                new { announcementId, id });
            return rows == 0
                ? Results.NotFound(new { error = "Announcement not found" })
                : Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournament-participants/{id} ─────────────────────────────
        app.MapGet("/api/tournament-participants/{id}", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var participant = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT tp.*, t.name AS team_name, t.logo_url AS team_logo,
                       p.username, p.avatar_url
                FROM public.tournament_participants tp
                LEFT JOIN public.teams t ON t.id = tp.team_id
                LEFT JOIN public.profiles p ON p.id = tp.user_id
                WHERE tp.id = @id
                """, new { id });
            return participant is null ? Results.NotFound() : Results.Ok(participant);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/disputes ────────────────────────────────────────────────
        app.MapPost("/api/disputes", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            Esportra.Core.Alerts.AdminAlertService alertService,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>(ct);
            if (body is null) return Results.BadRequest("Invalid body");

            // Accept both snake_case and camelCase
            Guid tournamentId = body.TryGetValue("tournament_id", out var tid) ? tid.GetGuid()
                              : body.TryGetValue("tournamentId", out tid) ? tid.GetGuid() : Guid.Empty;
            if (tournamentId == Guid.Empty) return Results.BadRequest(new { error = "tournament_id is required" });

            Guid? matchId = body.TryGetValue("match_id", out var mid) ? mid.GetGuid()
                          : body.TryGetValue("matchId", out mid) ? mid.GetGuid() : null;
            Guid? teamId = body.TryGetValue("team_id", out var tmid) ? tmid.GetGuid()
                         : body.TryGetValue("teamId", out tmid) ? tmid.GetGuid() : null;
            string? title = body.TryGetValue("title", out var t) ? t.GetString() : null;
            string? description = body.TryGetValue("description", out var d) ? d.GetString() : null;
            string? evidenceUrl = body.TryGetValue("evidence_url", out var eu) ? eu.GetString()
                                : body.TryGetValue("evidenceUrl", out eu) ? eu.GetString() : null;
            string? reason = body.TryGetValue("dispute_reason", out var dr) ? dr.GetString()
                           : body.TryGetValue("reason", out dr) ? dr.GetString() : null;

            using var conn = db.CreateConnection();
            var dispute = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO tournament_disputes
                    (tournament_id, match_id, team_id, raised_by_user_id, title,
                     description, evidence_url, dispute_reason, status, reference_number)
                VALUES
                    (@tournamentId, @matchId, @teamId, @userId, @title,
                     @description, @evidenceUrl, @reason, 'open',
                     'DSP-' || LPAD(nextval('dispute_reference_seq')::text, 4, '0'))
                RETURNING id, tournament_id, match_id, team_id, raised_by_user_id, title,
                         description, evidence_url, dispute_reason, status, reference_number, created_at
                """,
                new
                {
                    tournamentId,
                    matchId,
                    teamId,
                    userId = userCtx.UserIdGuid,
                    title,
                    description,
                    evidenceUrl,
                    reason,
                });

            // Create admin alert for new dispute
            await alertService.CreateAsync(
                "dispute_filed",
                Esportra.Core.Alerts.AlertSeverity.Warning,
                $"Dispute filed — {(string)dispute.reference_number}",
                $"{title ?? "Dispute"}: {reason ?? "No reason given"}",
                new { dispute_id = (Guid)dispute.id, tournament_id = tournamentId, reference = (string)dispute.reference_number },
                ct);

            return Results.Created($"/api/disputes/{dispute.id}", dispute);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/disputes/notify-admins ──────────────────────────────────
        app.MapPost("/api/disputes/notify-admins", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>(ct);
            if (body is null) return Results.BadRequest("Invalid body");

            Guid disputeId = body.TryGetValue("dispute_id", out var did) ? did.GetGuid()
                           : body.TryGetValue("disputeId", out did) ? did.GetGuid()
                           : body.TryGetValue("DisputeId", out did) ? did.GetGuid() : Guid.Empty;
            if (disputeId == Guid.Empty) return Results.BadRequest(new { error = "dispute_id is required" });

            string? type = body.TryGetValue("type", out var tv) ? tv.GetString() : null;
            string? title = body.TryGetValue("title", out var ttl) ? ttl.GetString() : null;
            string? message = body.TryGetValue("message", out var msg) ? msg.GetString() : null;
            string? link = body.TryGetValue("link", out var lnk) ? lnk.GetString() : null;

            using var conn = db.CreateConnection();

            // Query moderators + ops_admins from both profiles.admin_roles and admin_user_roles
            var adminIds = (await conn.QueryAsync<Guid>(
                """
                SELECT DISTINCT uid FROM (
                    SELECT p.id AS uid FROM profiles p
                    WHERE 'moderator' = ANY(p.admin_roles) OR 'ops_admin' = ANY(p.admin_roles)
                    UNION
                    SELECT aur.user_id AS uid FROM admin_user_roles aur
                    JOIN admin_roles ar ON ar.id = aur.role_id
                    WHERE lower(ar.name) = ANY(ARRAY['moderator', 'ops_admin'])
                ) sub
                """)).ToList();

            if (adminIds.Count == 0) return Results.Ok(new { notified = 0 });

            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                SELECT uid, @type, @title, @message, @link,
                       jsonb_build_object('dispute_id', @disputeId::text)::jsonb, FALSE
                FROM UNNEST(@adminIds::uuid[]) AS uid
                WHERE uid IS NOT NULL
                """,
                new
                {
                    adminIds = adminIds.ToArray(),
                    type = type ?? "dispute_filed",
                    title = title ?? "🚨 New Dispute Filed",
                    message = message ?? "A new dispute requires admin review and resolution.",
                    link = link ?? "",
                    disputeId,
                });
            return Results.Ok(new { notified = adminIds.Count });
        }).RequireAuthorization("Authenticated");

        MapMockEndpoints(app);
    }

    // ── GET /api/tournaments/by-slug/{slug} — fetch by slug or id ────────────
    // Replaces ManageBracketPage's supabase.from('tournaments') call
    // Already handled by GET /api/tournaments/{id} if id-based,
    // but ManageBracketPage needs slug support with org join.

    // ── POST /api/profiles/resolve-players — batch resolve player tags ───────
    // Replaces TournamentManage's 4 parallel profile lookups

    // ── POST /api/rosters/{rosterId}/members — get roster members via RPC ────
    // Replaces TournamentManage's supabase.rpc('get_roster_members')

    // ── POST /api/tournaments/{id}/mock/generate ─────────────────────────────
    // Generates fictitious checked-in participants so organizers can test
    // bracket generation and stage flow while the tournament is in draft.
    private static void MapMockEndpoints(WebApplication app)
    {
        app.MapPost("/api/tournaments/{id}/mock/generate", async (
            Guid id,
            [FromBody] MockGenerateRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MockEndpoints");
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                using var conn = db.CreateConnection();
                using var tx = conn.BeginTransaction();

                logger.LogInformation("[mock/generate] Fetching tournament {Id}", id);

                var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT organizer_id, status, is_public, max_teams, team_size, game, format, check_in_required FROM tournaments WHERE id = @id AND deleted_at IS NULL FOR UPDATE",
                    new { id }, tx);
                if (tournament is null) return Results.NotFound();
                if ((Guid)tournament.organizer_id != userCtx.UserIdGuid && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                    return Results.Forbid();

                var tStatus = ((string)tournament.status).ToLowerInvariant();
                var isPublic = (bool)tournament.is_public;
                logger.LogInformation("[mock/generate] Tournament status={Status} isPublic={IsPublic} format={Format} maxTeams={Max}",
                    tStatus, isPublic, (string)tournament.format, (int)tournament.max_teams);

                if (isPublic || tStatus != "draft")
                {
                    return Results.BadRequest(new
                    {
                        error = "Mock teams can only be generated while the tournament is a private draft."
                    });
                }

                var safety = await CheckMockSimulationSafetyAsync(conn, tx, id);
                if (!safety.CanRegenerate)
                    return Results.Json(MockOperationError(safety.Error, ctx.TraceIdentifier), statusCode: StatusCodes.Status409Conflict);

                var maxTeams = (int)tournament.max_teams;
                var count = req.Count.HasValue
                    ? Math.Clamp(req.Count.Value, 2, maxTeams)
                    : maxTeams;

                if (count < 2)
                    return Results.BadRequest(new { error = "At least 2 teams are required." });

                logger.LogInformation("[mock/generate] Generating {Count} mock participants", count);

                await ClearMockSimulationDataAsync(conn, tx, id, ct);

                logger.LogInformation("[mock/generate] Cleared existing mock data, inserting {Count} rows", count);

                var teamSize = (int)tournament.team_size;
                var participantType = teamSize == 1 ? "solo" : "team";
                var mockNames = MockTeamNames.Generate(count);
                var rows = mockNames.Select(name =>
                {
                    var mockId = Guid.NewGuid();
                    return new TeamCreationHelper.MockTeamParams(
                        mockId,
                        name,
                        TeamCreationHelper.BuildMockTag(mockId),
                        (string)tournament.game,
                        (Guid)tournament.organizer_id,
                        teamSize == 1,
                        Math.Max(teamSize, 1));
                }).ToList();

                var participantRows = rows.Select(r => new
                {
                    mockId = r.MockId,
                    tournamentId = id,
                    teamName = r.TeamName,
                    participantType,
                    mockStatus = (bool)(tournament.check_in_required ?? false) ? "checked_in" : "approved",
                }).ToList();

                await TeamCreationHelper.UpsertMockTeamsAsync(conn, tx, rows);

                await conn.ExecuteAsync(
                    """
                    INSERT INTO tournament_participants
                        (id, tournament_id, team_id, team_name, participant_type, status, is_mock, checked_in_at, created_at, updated_at)
                    VALUES
                        (@mockId, @tournamentId, @mockId, @teamName, @participantType::registration_type,
                         @mockStatus::registration_status, TRUE, CASE WHEN @mockStatus = 'checked_in' THEN NOW() ELSE NULL END, NOW(), NOW())
                    """,
                    participantRows, tx);

                tx.Commit();
                logger.LogInformation("[mock/generate] Committed {Count} mock participants for tournament {Id}", count, id);

                var inserted = await conn.QuerySingleAsync<int>(
                    "SELECT COUNT(*) FROM tournament_participants WHERE tournament_id = @id AND is_mock = TRUE",
                    new { id });

                return Results.Ok(new { generated = inserted });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[mock/generate] Failed for tournament {Id}: {Message}", id, ex.Message);
                return Results.Json(
                    MockOperationError("We couldn't generate mock teams right now. Please try again, and report this if it keeps happening.", ctx.TraceIdentifier),
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/tournaments/{id}/mock ─────────────────────────────────
        // Clears all mock participants and bracket data derived from them.
        // Tournament settings, stage configs, and real participants are untouched.
        app.MapDelete("/api/tournaments/{id}/mock", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MockEndpoints");
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                using var conn = db.CreateConnection();
                using var tx = conn.BeginTransaction();

                var organizerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    "SELECT organizer_id FROM tournaments WHERE id = @id AND deleted_at IS NULL FOR UPDATE",
                    new { id }, tx);
                if (organizerId is null) return Results.NotFound();
                if (organizerId != userCtx.UserIdGuid && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                    return Results.Forbid();

                var safety = await CheckMockSimulationSafetyAsync(conn, tx, id);
                if (!safety.CanRegenerate)
                    return Results.Json(MockOperationError(safety.Error, ctx.TraceIdentifier), statusCode: StatusCodes.Status409Conflict);

                await ClearMockSimulationDataAsync(conn, tx, id, ct);

                tx.Commit();

                logger.LogInformation("[mock/clear] Cleared mock data for tournament {Id}", id);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[mock/clear] Failed for tournament {Id}: {Message}", id, ex.Message);
                return Results.Json(
                    MockOperationError("We couldn't clear mock teams right now. Please try again, and report this if it keeps happening.", ctx.TraceIdentifier),
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).RequireAuthorization("Authenticated");
    }

    private static object MockOperationError(string? message, string traceId) => new
    {
        error = message ?? "Mock tournament operation failed.",
        message = message ?? "Mock tournament operation failed.",
        traceId,
    };

    private static async Task<MockSimulationSafety> CheckMockSimulationSafetyAsync(IDbConnection conn, IDbTransaction tx, Guid tournamentId)
    {
        var realParticipantCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM public.tournament_participants
            WHERE tournament_id = @tournamentId
              AND COALESCE(is_mock, FALSE) = FALSE
              AND status::text NOT IN ('rejected', 'cancelled', 'disqualified')
            """,
            new { tournamentId }, tx);

        if (realParticipantCount > 0)
            return new(false, "Mock teams cannot be regenerated while real participants are registered. Clear mock mode separately and manage real registrations directly.");

        var paymentParticipantCount = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM public.tournament_participants
            WHERE tournament_id = @tournamentId
              AND status::text NOT IN ('rejected', 'cancelled', 'disqualified')
              AND COALESCE(payment_status, '') NOT IN ('', 'not_required', 'waived')
            """,
            new { tournamentId }, tx);

        if (paymentParticipantCount > 0)
            return new(false, "Mock teams cannot be regenerated because payment or registration records would be affected.");

        var nonMockBracketTeams = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM public.brkt_matches m
            JOIN public.brkt_versions v ON v.id = m.version_id
            LEFT JOIN public.tournament_participants tp1 ON tp1.id = m.team1_id
            LEFT JOIN public.tournament_participants tp2 ON tp2.id = m.team2_id
            WHERE v.tournament_id = @tournamentId
              AND (
                    (m.team1_id IS NOT NULL AND COALESCE(tp1.is_mock, FALSE) = FALSE)
                 OR (m.team2_id IS NOT NULL AND COALESCE(tp2.is_mock, FALSE) = FALSE)
              )
            """,
            new { tournamentId }, tx);

        if (nonMockBracketTeams > 0)
            return new(false, "Mock teams cannot be regenerated because bracket data contains real teams.");

        return new(true, null);
    }

    private static async Task ClearMockSimulationDataAsync(
        IDbConnection conn,
        IDbTransaction tx,
        Guid tournamentId,
        CancellationToken ct)
    {
        // Clear simulation state: stage participants + bracket versions that have
        // teams seeded. Preserve TBD bracket structures (no team slots filled) so
        // organizers don't lose their pre-scheduled match structure.
        await conn.ExecuteAsync(
            """
            UPDATE public.tournament_stages
            SET status = 'draft',
                updated_at = NOW()
            WHERE tournament_id = @tournamentId
              AND status::text <> 'draft'
            """,
            new { tournamentId }, tx);

        // Un-seed all bracket versions back to TBD state (preserves structure + scheduled times).
        // Clear match events, games, disputes that reference these matches.
        var allVersionIds = (await conn.QueryAsync<Guid>(
            "SELECT id FROM public.brkt_versions WHERE tournament_id = @tournamentId",
            new { tournamentId }, tx)).ToArray();

        if (allVersionIds.Length > 0)
        {
            await conn.ExecuteAsync(
                """
                DELETE FROM public.dispute_comments dc
                USING public.tournament_disputes td
                JOIN public.brkt_matches m ON m.id = td.match_id
                WHERE dc.dispute_id = td.id
                  AND m.version_id = ANY(@vids)
                """,
                new { vids = allVersionIds }, tx);
            await conn.ExecuteAsync(
                """
                DELETE FROM public.tournament_disputes td
                USING public.brkt_matches m
                WHERE td.match_id = m.id
                  AND m.version_id = ANY(@vids)
                """,
                new { vids = allVersionIds }, tx);
            await conn.ExecuteAsync(
                """
                DELETE FROM public.match_disputes md
                USING public.brkt_matches m
                WHERE md.match_id = m.id
                  AND m.version_id = ANY(@vids)
                """,
                new { vids = allVersionIds }, tx);
            await conn.ExecuteAsync(
                """
                DELETE FROM public.match_result_reports r
                USING public.brkt_matches m
                WHERE r.match_id = m.id
                  AND m.version_id = ANY(@vids)
                """,
                new { vids = allVersionIds }, tx);
            await conn.ExecuteAsync(
                """
                DELETE FROM public.match_completed_events e
                USING public.brkt_matches m
                WHERE e.match_id = m.id
                  AND m.version_id = ANY(@vids)
                """,
                new { vids = allVersionIds }, tx);
            await conn.ExecuteAsync(
                "DELETE FROM public.brkt_match_games WHERE match_id IN (SELECT id FROM public.brkt_matches WHERE version_id = ANY(@vids))",
                new { vids = allVersionIds }, tx);
            await conn.ExecuteAsync(
                "DELETE FROM public.brkt_match_events WHERE match_id IN (SELECT id FROM public.brkt_matches WHERE version_id = ANY(@vids))",
                new { vids = allVersionIds }, tx);
        }

        // Reset all matches to TBD — structure, advancements, layout, and scheduled times preserved
        await conn.ExecuteAsync(
            """
            UPDATE public.brkt_matches
            SET team1_id = NULL, team2_id = NULL,
                team1_seed = NULL, team2_seed = NULL,
                winner_id = NULL, loser_id = NULL,
                status = 'pending'
            WHERE version_id IN (SELECT id FROM public.brkt_versions WHERE tournament_id = @tournamentId)
            """,
            new { tournamentId }, tx);

        await conn.ExecuteAsync(
            "DELETE FROM public.stage_participants WHERE stage_id IN (SELECT id FROM public.tournament_stages WHERE tournament_id = @tournamentId)",
            new { tournamentId }, tx);

        await MockTeamCleanup.DeleteForTournamentAsync(conn, tx, tournamentId, ct);
    }

    private sealed record MockSimulationSafety(bool CanRegenerate, string? Error);

    // ── GET /api/tournaments/{slugOrId} — helpers ────────────────────────────────

    private sealed record TournamentAccess(bool IsOrganizer, string[]? StaffPermissions, string? StaffRole);

    private static async Task ReconcileStaleStatusAsync(IDbConnection conn, Guid tournamentId, dynamic tournament)
    {
        var rawStatus = tournament.status?.ToString() as string;
        var reconciledStatus = ReconcileEffectiveStatus(
            rawStatus,
            (DateTimeOffset?)tournament.start_date,
            (DateTimeOffset?)tournament.end_date);
        if (reconciledStatus is null || reconciledStatus == rawStatus) return;

        // Guard in SQL prevents duplicate writes if two concurrent GETs see the same stale status
        await conn.ExecuteAsync(
            "UPDATE tournaments SET status = @newStatus::tournament_status, updated_at = NOW() WHERE id = @tournamentId AND status != @newStatus::tournament_status",
            new { newStatus = reconciledStatus, tournamentId });
        tournament.status = reconciledStatus;
    }

    private static async Task<TournamentAccess> ResolveAccessAsync(
        HttpContext ctx, IStaffAuthorizationService staffAuth, Guid tournamentId, CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return new(false, null, null);

        var access = await staffAuth.ResolveTournamentAccessAsync(userCtx, tournamentId, ct);
        var isOrganizer = access.IsOrganizer || access.IsPlatformAdmin;
        string[]? staffPermissions = null;
        string? staffRole = null;
        if (!isOrganizer && access.Role != "none")
        {
            staffPermissions = access.Permissions;
            staffRole = access.Role;
        }
        return new(isOrganizer, staffPermissions, staffRole);
    }

    private static IEnumerable<dynamic> FilterParticipantsForRole(IEnumerable<dynamic> allParticipants, bool isOrganizer)
    {
        if (isOrganizer) return allParticipants;
        return allParticipants.Where(p =>
        {
            string status = (string)p.status;
            return status != "rejected" && status != "cancelled";
        });
    }

    private static async Task<int> FetchMockCountAsync(IDbConnection conn, Guid tournamentId, bool isOrganizer)
    {
        if (!isOrganizer) return 0;
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tournament_participants WHERE tournament_id = @tournamentId AND is_mock = TRUE",
            new { tournamentId });
    }

    private static async Task<string> ResolveParticipantModeAsync(GameCatalogService gameCatalog, dynamic tournament)
    {
        try
        {
            return await gameCatalog.ResolveParticipantModeAsync(
                (string)tournament.game,
                tournament.game_mode as string,
                (int?)tournament.team_size);
        }
        catch
        {
            return ((int?)tournament.team_size ?? 1) > 1 ? "team" : "solo";
        }
    }

    // ── POST /api/tournaments — helpers ──────────────────────────────────────────

    private sealed record TournamentCreateDates(DateTime EndDate, DateTime RegistrationDeadline, string Status, int InviteExpiryDays);
    private sealed record TournamentCreateDefaults(bool IsPublic, bool CheckInRequired, bool AutoRemoveUnchecked, decimal EntryFee, decimal PrizePool, string Currency, Guid? VenueId, string PayoutMethod);
    private sealed record StageInsertParams(Guid TournamentId, string Name, string Format, int StageOrder, int BestOf, string BoMode, string? RoundBoOverrides, int? Capacity, int? AdvancementCount, string? Config, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

    private static async Task<string> ResolveUniqueSlugAsync(IDbConnection conn, IDbTransaction tx, string? requestedSlug, string name)
    {
        var slug = requestedSlug ?? Slugify(name);
        var exists = await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM tournaments WHERE slug = @slug)", new { slug }, tx);
        if (exists) return $"{slug}-{DateTime.UtcNow.Ticks % 9999:x4}";
        return slug;
    }

    private static async Task<Guid?> ResolveOrganizationIdAsync(IDbConnection conn, IDbTransaction tx, string? orgIdStr, Guid userId)
    {
        Guid? orgId = Guid.TryParse(orgIdStr, out var g) ? g : (Guid?)null;
        if (!orgId.HasValue)
            orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM organizations WHERE owner_id = @userId LIMIT 1",
                new { userId }, tx);
        return orgId;
    }

    private static string? ValidateCreateTournamentConstraints(CreateTournamentRequest req, int reservedSlots)
    {
        if (req.MaxTeams > 0 && reservedSlots > req.MaxTeams)
            return "Reserved invite slots cannot exceed max teams.";

        var dateOrderError = TournamentTimelineValidator.ValidateDateOrder(
            req.StartDate, req.EndDate ?? req.StartDate.AddHours(2));
        if (dateOrderError is not null) return dateOrderError;

        return TournamentTimelineValidator.ValidateRegistrationDeadline(
            req.RegistrationDeadline ?? req.StartDate.AddDays(-1), req.StartDate);
    }

    private static TournamentCreateDates NormalizeTournamentDates(CreateTournamentRequest req)
    {
        var endDate = req.EndDate ?? req.StartDate.AddHours(2);
        var regDeadline = req.RegistrationDeadline ?? req.StartDate.AddDays(-1);
        var status = AllowedCreateStatuses.Contains(req.Status ?? "") ? req.Status! : "draft";
        var inviteExpiryDays = Math.Clamp(req.InviteExpiryDays ?? 7, 1, 365);
        return new(endDate, regDeadline, status, inviteExpiryDays);
    }

    private static TournamentCreateDefaults NormalizeTournamentDefaults(CreateTournamentRequest req)
        => new(
            IsPublic: req.IsPublic ?? false,
            CheckInRequired: req.CheckInRequired ?? false,
            AutoRemoveUnchecked: req.AutoRemoveUnchecked ?? false,
            EntryFee: req.EntryFee ?? 0m,
            PrizePool: req.PrizePool ?? 0m,
            Currency: req.Currency ?? "USD",
            VenueId: Guid.TryParse(req.VenueId, out var vg) ? vg : (Guid?)null,
            PayoutMethod: req.PayoutMethod is "gateway" ? "gateway" : "manual");

    private static string SerializeTournamentSettingsOrEmpty(object? settings, bool supportsMapVeto)
        => SerializeTournamentSettings(settings, supportsMapVeto) ?? "{}";

    private static DateTimeOffset? ParseStageDateTimeOffset(string? value)
        => value is not null && DateTimeOffset.TryParse(value, out var result) ? result : (DateTimeOffset?)null;

    private static StageInsertParams MapStageToInsertParams(StageRequest s, int index, Guid tournamentId, int maxTeams)
        => new(
            TournamentId: tournamentId,
            Name: s.Name,
            Format: s.Format,
            StageOrder: s.StageOrder ?? index,
            BestOf: s.BestOf ?? 1,
            BoMode: s.BoMode ?? "per_stage",
            RoundBoOverrides: s.RoundBoOverrides is { Count: > 0 } ? JsonSerializer.Serialize(s.RoundBoOverrides) : null,
            Capacity: string.Equals(s.Format, "battle_royale", StringComparison.OrdinalIgnoreCase)
                ? s.Capacity ?? maxTeams
                : s.Capacity,
            AdvancementCount: s.AdvancementCount,
            Config: s.Config is not null ? JsonSerializer.Serialize(s.Config) : null,
            StartsAt: ParseStageDateTimeOffset(s.StartsAt),
            EndsAt: ParseStageDateTimeOffset(s.EndsAt));

    private static async Task InsertStagesAsync(
        IDbConnection conn, IDbTransaction tx, Guid tournamentId, List<StageRequest>? stages, int maxTeams)
    {
        if (stages is not { Count: > 0 }) return;
        await conn.ExecuteAsync(
            """
            INSERT INTO tournament_stages
                (tournament_id, name, format, stage_order, best_of, bo_mode,
                 round_bo_overrides, capacity, advancement_count, config, starts_at, ends_at)
            VALUES
                (@TournamentId, @Name, @Format, @StageOrder, @BestOf, @BoMode,
                 CASE WHEN @RoundBoOverrides::text IS NOT NULL THEN @RoundBoOverrides::jsonb ELSE NULL END,
                 @Capacity, @AdvancementCount,
                 CASE WHEN @Config::text IS NOT NULL THEN @Config::jsonb ELSE NULL END,
                 @StartsAt, @EndsAt)
            """,
            stages.Select((s, i) => MapStageToInsertParams(s, i, tournamentId, maxTeams)),
            tx);
    }

    private static async Task InsertMapPoolAsync(
        IDbConnection conn, IDbTransaction tx, Guid tournamentId, List<string>? mapPoolIds)
    {
        if (mapPoolIds is not { Count: > 0 }) return;
        await conn.ExecuteAsync(
            "INSERT INTO tournament_map_pools (tournament_id, map_id) VALUES (@tournamentId, @mapId)",
            mapPoolIds.Where(m => Guid.TryParse(m, out _)).Select(m => new { tournamentId, mapId = Guid.Parse(m) }),
            tx);
    }

    private static async Task ScheduleCheckinJobIfNeededAsync(
        HttpContext ctx, Guid tournamentId, CreateTournamentRequest req, CancellationToken ct)
    {
        if (!(req.CheckInRequired ?? false) || !req.CheckInDeadline.HasValue || !(req.AutoRemoveUnchecked ?? false))
            return;

        var jobScheduler = ctx.RequestServices.GetRequiredService<Esportra.Api.ScheduledJobs.JobSchedulingService>();
        await jobScheduler.ScheduleTournamentCheckinDeadlineAsync(tournamentId, req.CheckInDeadline.Value, ct);
    }

    // ── POST /api/tournaments/{id}/register — helpers ────────────────────────────

    private sealed record ParticipantIds(Guid? TeamId, Guid? CaptainId, Guid? RosterId, string ParticipantType);
    private sealed record ParticipantIdentity(Guid? TeamId, Guid CaptainId, string? TeamName, string? SoloDisplayName);
    private sealed record TeamMembersBuildResult(string TeamMembersJson, string? RosterLineupJson, IResult? Error);

    private static ParticipantIds ParseParticipantIds(RegisterTournamentRequest req)
    {
        Guid? teamId = req.TeamId is not null ? Guid.Parse(req.TeamId) : null;
        Guid? captainId = req.TeamCaptainId is not null ? Guid.Parse(req.TeamCaptainId) : null;
        Guid? rosterId = req.RosterId is not null ? Guid.Parse(req.RosterId) : null;
        var participantType = teamId is not null ? "team" : "solo";
        return new(teamId, captainId, rosterId, participantType);
    }

    private static async Task<ParticipantIdentity> ResolveParticipantIdentityAsync(
        IDbConnection conn, IDbTransaction txn, UserContext userCtx, RegisterTournamentRequest req, ParticipantIds ids)
    {
        if (ids.ParticipantType != "solo")
        {
            var captainId = ids.CaptainId ?? userCtx.UserIdGuid;
            return new(ids.TeamId, captainId, req.TeamName, null);
        }

        var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT username FROM profiles WHERE id = @uid",
            new { uid = userCtx.UserIdGuid }, txn);
        var displayName = (string?)profile?.username ?? "Solo Player";
        return new(null, userCtx.UserIdGuid, displayName, displayName);
    }

    private static async Task<IResult?> CheckRegistrationCapacityAsync(
        IDbConnection conn, IDbTransaction txn, Guid id, Guid userId, int? maxTeams, int reservedSlots)
    {
        if (maxTeams.HasValue && maxTeams.Value > 0)
        {
            if (reservedSlots > 0)
            {
                var openCap = Math.Max(maxTeams.Value - reservedSlots, 0);
                var openCount = await conn.QuerySingleAsync<int>(
                    """
                    SELECT COUNT(*)
                    FROM tournament_participants
                    WHERE tournament_id = @id
                      AND status NOT IN ('rejected', 'cancelled', 'disqualified')
                      AND COALESCE(source, 'open') = 'open'
                    """,
                    new { id }, txn);
                if (openCount >= openCap)
                    return Results.BadRequest(new { error = "Open registration slots are full. Invited teams still have guaranteed slots." });
            }
            else
            {
                var count = await conn.QuerySingleAsync<int>(
                    "SELECT COUNT(*) FROM tournament_participants WHERE tournament_id = @id AND status NOT IN ('rejected', 'cancelled', 'disqualified')",
                    new { id }, txn);
                if (count >= maxTeams.Value)
                    return Results.BadRequest(new { error = "Tournament has reached maximum capacity." });
            }
        }

        var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id FROM tournament_participants WHERE tournament_id = @id AND user_id = @userId AND status NOT IN ('cancelled', 'rejected', 'disqualified')",
            new { id, userId }, txn);
        if (existing is not null)
            return Results.Conflict(new { error = "You are already registered for this tournament." });

        return null;
    }

    private static (string RegStatus, string PaymentStatus, bool EntryFeePaid, decimal EntryFeeAmount) DerivePaymentStatus(decimal? rawFee)
    {
        var fee = rawFee ?? 0m;
        return fee > 0
            ? ("pending", "pending", false, fee)
            : ("approved", "not_required", true, 0m);
    }

    private static async Task<TeamMembersBuildResult> BuildTeamMembersAsync(
        IDbConnection conn, IDbTransaction txn, Guid id,
        Guid? rosterIdGuid, RegisterTournamentRequest req, string participantType, string? soloDisplayName, GameCatalogService gameCatalog)
    {
        if (rosterIdGuid.HasValue)
        {
            var usesRosterPool = await gameCatalog.TournamentUsesRosterPoolAsync(conn, txn, id);
            if (usesRosterPool && !string.IsNullOrWhiteSpace(req.RosterLineup))
            {
                try
                {
                    var (members, lineup) = await RosterRegistrationHelper.BuildFromSubmittedLineupAsync(
                        conn, rosterIdGuid.Value, req.RosterLineup, txn);
                    return new(members, lineup, null);
                }
                catch (InvalidOperationException ex)
                {
                    return new(string.Empty, null, Results.BadRequest(new { error = ex.Message }));
                }
            }

            var (m, l) = await RosterRegistrationHelper.BuildRegistrationSnapshotAsync(conn, rosterIdGuid.Value, txn);
            return new(m, l, null);
        }

        if (participantType == "solo" && !string.IsNullOrWhiteSpace(soloDisplayName))
            return new(System.Text.Json.JsonSerializer.Serialize(new[] { soloDisplayName }), null, null);

        if (!string.IsNullOrWhiteSpace(req.TeamMembers))
            return new($"[\"{req.TeamMembers.Replace(",", "\",\"")}\"]", null, null);

        return new("[]", null, null);
    }

    // ── POST /api/tournaments/{id}/ban-participant — helpers ─────────────────────

    private static async Task CascadeForfeitMatchesAsync(
        IDbConnection conn, IDbTransaction tx,
        Guid tournamentId, Guid bannedSlotId, Guid? versionId,
        MatchFinalizationService finalizer, IHubContext<BracketHub> bracketHub, CancellationToken ct)
    {
        if (!versionId.HasValue) return;

        for (var forfeitPass = 0; forfeitPass < 20; forfeitPass++)
        {
            var pendingMatches = (await conn.QueryAsync<dynamic>(
                """
                SELECT id, team1_id, team2_id, best_of
                FROM public.brkt_matches
                WHERE version_id = @versionId
                  AND (team1_id = @bannedSlotId OR team2_id = @bannedSlotId)
                  AND status NOT IN ('completed', 'disputed')
                ORDER BY round_index ASC, match_number ASC
                """,
                new { versionId, bannedSlotId }, tx)).AsList();

            if (pendingMatches.Count == 0) break;

            foreach (var match in pendingMatches)
            {
                Guid matchId = (Guid)match.id;
                Guid? team1 = (Guid?)match.team1_id;
                Guid? team2 = (Guid?)match.team2_id;
                Guid? opponent = team1 == bannedSlotId ? team2 : team1;

                if (opponent.HasValue)
                {
                    int bestOf = (int)(match.best_of ?? 1);
                    int winnerScore = bestOf <= 1 ? 1 : (int)Math.Ceiling(bestOf / 2.0);
                    int t1Score = team1 == opponent ? winnerScore : 0;
                    int t2Score = team2 == opponent ? winnerScore : 0;
                    // FinalizeAsync uses its own connection — cannot participate in our tx.
                    // TODO: If our tx rolls back (e.g. notification INSERT fails), these match
                    // forfeits are already committed. Track as a known partial-state risk.
                    await finalizer.FinalizeAsync(matchId, opponent.Value, bannedSlotId, t1Score, t2Score, ct);
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE public.brkt_matches
                        SET status = 'completed', winner_id = NULL, loser_id = @bannedSlotId,
                            result_notes = 'Forfeit — team banned', version = version + 1, updated_at = NOW()
                        WHERE id = @matchId AND status != 'completed'
                        """,
                        new { matchId, bannedSlotId }, tx);
                }
            }
        }

        await bracketHub.Clients
            .Group(BracketHub.BracketGroup(versionId.Value.ToString()))
            .SendAsync(BracketHubEvents.MatchUpdated,
                new { versionId, reason = "participant_banned", bannedSlotId },
                ct);

        var pendingCount = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM brkt_matches WHERE version_id = @versionId AND status != 'completed'",
            new { versionId }, tx);

        if (pendingCount != 0) return;

        var stageId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT stage_id FROM brkt_versions WHERE id = @versionId", new { versionId }, tx);
        if (!stageId.HasValue) return;

        await conn.ExecuteAsync(
            "UPDATE tournament_stages SET status = 'completed' WHERE id = @stageId",
            new { stageId }, tx);

        var gfWinnerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT winner_id FROM brkt_matches
            WHERE version_id = @versionId
              AND status = 'completed' AND winner_id IS NOT NULL
            ORDER BY round_index DESC, match_number DESC
            LIMIT 1
            """,
            new { versionId }, tx);

        if (gfWinnerId.HasValue)
            await conn.ExecuteAsync(
                "SELECT public.admin_set_tournament_winner(@p_tournament_id, @p_winner_id)",
                new { p_tournament_id = tournamentId, p_winner_id = gfWinnerId.Value }, tx);

        await bracketHub.Clients
            .Group(BracketHub.BracketGroup(versionId.Value.ToString()))
            .SendAsync(BracketHubEvents.StageCompleted,
                new { versionId, tournamentId },
                ct);
    }

    private static async Task RemoveFromBRGroupsAsync(
        IDbConnection conn, IDbTransaction tx, Guid participantId, Guid? banTeamId, Guid tournamentId)
    {
        if (banTeamId.HasValue)
        {
            await conn.ExecuteAsync(
                """
                DELETE FROM br_group_teams
                WHERE team_id = @teamId
                  AND group_id IN (SELECT id FROM br_groups WHERE stage_id IN (
                      SELECT id FROM tournament_stages WHERE tournament_id = @tournamentId
                  ))
                """,
                new { teamId = banTeamId, tournamentId }, tx);
        }
        else
        {
            await conn.ExecuteAsync(
                """
                DELETE FROM br_group_teams
                WHERE participant_id = @participantId
                  AND group_id IN (SELECT id FROM br_groups WHERE stage_id IN (
                      SELECT id FROM tournament_stages WHERE tournament_id = @tournamentId
                  ))
                """,
                new { participantId, tournamentId }, tx);
        }
    }

    private static async Task NotifyBannedUsersAsync(
        IDbConnection conn, IDbTransaction tx, Guid? banTeamId, Guid? banUserId, Guid tournamentId, string? banReason)
    {
        var tournamentName = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM tournaments WHERE id = @tournamentId", new { tournamentId }, tx);
        var reason = banReason ?? "No reason provided";

        var notifyUserIds = new List<Guid>();
        if (banTeamId.HasValue)
            notifyUserIds.AddRange(await conn.QueryAsync<Guid>(
                "SELECT user_id FROM team_members WHERE team_id = @teamId AND is_active = TRUE",
                new { teamId = banTeamId }, tx));
        else if (banUserId.HasValue)
            notifyUserIds.Add(banUserId.Value);

        var dataJson = System.Text.Json.JsonSerializer.Serialize(new { tournament_id = tournamentId.ToString() });
        foreach (var uid in notifyUserIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, data)
                VALUES (@userId, 'tournament_announcement', @title, @message, @data::jsonb)
                """,
                new
                {
                    userId = uid,
                    title = "Banned from Tournament",
                    message = $"You have been banned from {tournamentName ?? "a tournament"}. Reason: {reason}",
                    data = dataJson
                }, tx);
        }
    }

    // ── PUT /api/tournaments/{id} — helpers ──────────────────────────────────────

    private sealed record TournamentUpdateContext(
        TournamentCatalogResolution Catalog,
        DateTimeOffset? EffectiveEndDate,
        int? ReservedSlotsForUpdate);

    private static IResult? ValidateStatusTransition(string? existingStatus, string? newStatus)
    {
        if (newStatus is null || string.Equals(newStatus, existingStatus, StringComparison.OrdinalIgnoreCase))
            return null;
        var allowed = existingStatus?.ToLowerInvariant() switch
        {
            "draft" => new[] { "open", "published", "cancelled" },
            "open" => new[] { "ongoing", "check_in", "cancelled", "draft" },
            "published" => new[] { "open", "ongoing", "cancelled" },
            "check_in" => new[] { "ongoing", "cancelled" },
            "ongoing" => new[] { "completed", "cancelled" },
            "approved" => new[] { "open", "published", "cancelled" },
            _ => Array.Empty<string>(),
        };
        return allowed.Contains(newStatus.ToLowerInvariant())
            ? null
            : Results.BadRequest(new { error = $"Cannot transition tournament from '{existingStatus}' to '{newStatus}'." });
    }

    private static async Task<IResult?> ValidateMockGuardAsync(IDbConnection conn, Guid id, string? existingStatus, string? newStatus)
    {
        var isPublishingTransition = newStatus is "open" or "published"
            && !string.Equals(newStatus, existingStatus, StringComparison.OrdinalIgnoreCase);
        if (!isPublishingTransition) return null;
        var mockCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tournament_participants WHERE tournament_id = @id AND is_mock = TRUE",
            new { id });
        return mockCount > 0
            ? Results.BadRequest(new { error = $"Cannot publish tournament: {mockCount} mock participant(s) still exist. Clear mock data before publishing." })
            : null;
    }

    private static async Task<(TournamentCatalogResolution? catalog, IResult? error)> ResolveCatalogAsync(
        IDbConnection conn, UpdateTournamentRequest req,
        string existingGame, string? existingGameMode, int? existingTeamSize, string? existingFormat,
        GameCatalogService gameCatalog)
    {
        try
        {
            var catalog = await gameCatalog.ResolveTournamentAsync(
                req.Game ?? existingGame,
                req.GameMode ?? existingGameMode,
                req.TeamSize ?? existingTeamSize,
                req.Format ?? existingFormat,
                null, Array.Empty<string>(), false, req.Settings, conn);
            return (catalog, null);
        }
        catch (GameCatalogValidationException ex)
        {
            return (null, Results.BadRequest(new { error = ex.Message }));
        }
    }

    private static (DateTimeOffset? effectiveEndDate, IResult? error) ValidateDates(
        UpdateTournamentRequest req,
        DateTimeOffset? existingStartDate, DateTimeOffset? existingEndDate, DateTimeOffset? existingRegistrationDeadline)
    {
        var effectiveStartDate = req.StartDate ?? existingStartDate;
        var effectiveEndDate = req.EndDate ?? existingEndDate;
        var effectiveRegistrationDeadline = req.RegistrationDeadline ?? existingRegistrationDeadline;

        if (effectiveStartDate is not null
            && effectiveEndDate is not null
            && effectiveEndDate < effectiveStartDate
            && req.EndDate is not null
            && req.StartDate is null)
        {
            effectiveEndDate = effectiveStartDate.Value.AddHours(4);
        }

        var dateOrderError = TournamentTimelineValidator.ValidateDateOrder(effectiveStartDate, effectiveEndDate);
        if (dateOrderError is not null) return (null, Results.BadRequest(new { error = dateOrderError }));

        var registrationDeadlineError = TournamentTimelineValidator.ValidateRegistrationDeadline(effectiveRegistrationDeadline, effectiveStartDate);
        if (registrationDeadlineError is not null) return (null, Results.BadRequest(new { error = registrationDeadlineError }));

        return (effectiveEndDate, null);
    }

    private static async Task<(int? reservedSlots, IResult? error)> ValidateInviteSlotsAsync(
        IDbConnection conn, Guid id, UpdateTournamentRequest req, int? existingMaxTeams)
    {
        var effectiveMaxTeams = req.MaxTeams ?? existingMaxTeams;
        int? reservedSlotsForUpdate = req.ReservedInviteSlots.HasValue
            ? req.ReservedInviteSlots.Value
            : req.Settings is not null && TournamentInviteSlots.TryReadFromSettingsIfPresent(req.Settings, out var settingsSlots)
                ? settingsSlots
                : null;

        if (effectiveMaxTeams is > 0 && reservedSlotsForUpdate is > 0 && reservedSlotsForUpdate > effectiveMaxTeams)
            return (null, Results.BadRequest(new { error = "Reserved invite slots cannot exceed max teams." }));

        if (reservedSlotsForUpdate.HasValue)
        {
            var activeInviteCount = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)::int
                FROM public.tournament_invitations
                WHERE tournament_id = @id
                  AND status <> 'revoked'
                """,
                new { id });
            if (reservedSlotsForUpdate.Value < activeInviteCount)
                return (null, Results.BadRequest(new { error = $"Reserved invite slots cannot be less than active invitations ({activeInviteCount})." }));
        }

        return (reservedSlotsForUpdate, null);
    }

    private static async Task<(TournamentUpdateContext? ctx, IResult? error)> ValidateTournamentUpdateAsync(
        IDbConnection conn, Guid id, UpdateTournamentRequest req, dynamic existing, GameCatalogService gameCatalog)
    {
        string existingStatus = (string?)existing.status ?? string.Empty;
        string existingGame = (string)existing.game;
        string? existingGameMode = (string?)existing.game_mode;
        int? existingTeamSize = (int?)existing.team_size;
        string? existingFormat = (string?)existing.format;
        DateTimeOffset? existingStartDate = (DateTimeOffset?)existing.start_date;
        DateTimeOffset? existingEndDate = (DateTimeOffset?)existing.end_date;
        DateTimeOffset? existingRegistrationDeadline = (DateTimeOffset?)existing.registration_deadline;
        int? existingMaxTeams = (int?)existing.max_teams;

        var statusError = ValidateStatusTransition(existingStatus, req.Status);
        if (statusError is not null) return (null, statusError);

        var mockError = await ValidateMockGuardAsync(conn, id, existingStatus, req.Status);
        if (mockError is not null) return (null, mockError);

        var catalogResult = await ResolveCatalogAsync(conn, req, existingGame, existingGameMode, existingTeamSize, existingFormat, gameCatalog);
        if (catalogResult.error is not null) return (null, catalogResult.error);

        var datesResult = ValidateDates(req, existingStartDate, existingEndDate, existingRegistrationDeadline);
        if (datesResult.error is not null) return (null, datesResult.error);

        var slotsResult = await ValidateInviteSlotsAsync(conn, id, req, existingMaxTeams);
        if (slotsResult.error is not null) return (null, slotsResult.error);

        return (new TournamentUpdateContext(catalogResult.catalog!, datesResult.effectiveEndDate, slotsResult.reservedSlots), null);
    }

    private static async Task<Guid?> ResolveCompletionWinnerIdAsync(IDbConnection conn, Guid id, UpdateTournamentRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.WinnerTeamName))
        {
            var byName = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT tp.team_id FROM tournament_participants tp
                JOIN teams t ON t.id = tp.team_id
                WHERE tp.tournament_id = @id AND t.name = @teamName
                LIMIT 1
                """,
                new { id, teamName = req.WinnerTeamName });
            if (byName.HasValue) return byName;
        }

        var stageCounts = await conn.QuerySingleAsync<dynamic>(
            """
            SELECT
                COUNT(*)::int AS total,
                COUNT(*) FILTER (WHERE status = 'completed')::int AS completed
            FROM tournament_stages
            WHERE tournament_id = @id
            """,
            new { id });

        int stageTotal = (int)stageCounts.total;
        int stageCompleted = (int)stageCounts.completed;
        if (stageTotal == 0 || stageTotal != stageCompleted) return null;

        return await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT m.winner_id
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            JOIN tournament_stages s ON s.id = v.stage_id
            WHERE s.tournament_id = @id
              AND s.stage_order = (
                  SELECT MAX(stage_order) FROM tournament_stages WHERE tournament_id = @id
              )
              AND m.status = 'completed'
              AND m.winner_id IS NOT NULL
              AND m.bracket_type = 'final'
            ORDER BY m.round_index DESC, m.match_number DESC
            LIMIT 1
            """,
            new { id });
    }

    private static async Task ApplyPostUpdateEffectsAsync(
        IDbConnection conn, Guid id, UpdateTournamentRequest req, dynamic? updated,
        TournamentWinnerService winnerService, PlacementResolutionService placementResolution,
        HttpContext ctx, CancellationToken ct)
    {
        if (req.Status is not null && req.Status != "completed")
        {
            try
            {
                using var txClear = conn.BeginTransaction();
                await winnerService.ClearWinnerAsync(conn, txClear, id, reopenCompleted: false,
                    reason: "tournament status changed away from completed", ct);
                txClear.Commit();
            }
            catch { /* best effort: stale winner clear must not block status change */ }
        }

        if (req.Status != "completed" || updated is null) return;

        var resolvedWinnerId = await ResolveCompletionWinnerIdAsync(conn, id, req);
        if (resolvedWinnerId.HasValue)
        {
            try
            {
                using var txWinner = conn.BeginTransaction();
                await winnerService.SetWinnerAsync(conn, txWinner, id, resolvedWinnerId.Value,
                    reason: "tournament status changed to completed", ct);
                txWinner.Commit();
            }
            catch { /* trigger still blocks; winner set must not block status change */ }
        }

        try { await placementResolution.ResolveAsync(id, force: false, ct); }
        catch (Exception ex)
        {
            ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("PrizeDistribution")
                .LogWarning(ex, "Placement resolution failed for tournament {TournamentId}; manual resolve available.", id);
        }
    }

    private static async Task RescheduleCheckinJobAsync(HttpContext ctx, Guid id, dynamic updated, CancellationToken ct)
    {
        var jobScheduler = ctx.RequestServices.GetRequiredService<Esportra.Api.ScheduledJobs.JobSchedulingService>();
        bool checkInRequired = (bool)(updated.check_in_required ?? false);
        bool autoRemove = (bool)(updated.auto_remove_unchecked ?? false);
        DateTime? deadline = updated.check_in_deadline is not null
            ? (DateTime)updated.check_in_deadline
            : null;
        if (checkInRequired && autoRemove && deadline.HasValue && deadline.Value > DateTime.UtcNow)
            await jobScheduler.ScheduleTournamentCheckinDeadlineAsync(id, deadline.Value, ct);
        else
            await jobScheduler.CancelTournamentCheckinDeadlineAsync(id, ct);
    }

    private static async Task<bool> CanViewTournamentPublicDataAsync(IDbConnection conn, HttpContext ctx, Guid tournamentId)
    {
        var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT organizer_id, status::text AS status, is_public
            FROM public.tournaments
            WHERE id = @tournamentId AND deleted_at IS NULL
            """,
            new { tournamentId });
        if (tournament is null)
            return false;

        // Draft, private, and public tournaments are all viewable via direct link (slug or id).
        // Discovery/browse remains gated separately by is_public on list endpoints.
        return true;
    }

    private static async Task<bool> CanManageTournamentParticipantsAsync(
        IDbConnection conn, UserContext userCtx, Guid tournamentId)
    {
        if (userCtx.IsSuperAdmin)
            return true;

        return await StaffAuthHelper.CanActOnTournamentAsync(
            conn, userCtx.UserIdGuid, tournamentId, StaffAuthHelper.PermTeamsManage);
    }

    private static bool TryParseStorageRef(string receiptRef, out string bucket, out string path)
    {
        bucket = string.Empty;
        path = string.Empty;

        var trimmed = receiptRef.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        const string publicMarker = "/storage/v1/object/public/";
        var publicIdx = trimmed.IndexOf(publicMarker, StringComparison.OrdinalIgnoreCase);
        if (publicIdx >= 0)
            return TryParseStorageRef(trimmed[(publicIdx + publicMarker.Length)..], out bucket, out path);

        const string objectMarker = "/storage/v1/object/";
        var objectIdx = trimmed.IndexOf(objectMarker, StringComparison.OrdinalIgnoreCase);
        if (objectIdx >= 0)
            return TryParseStorageRef(trimmed[(objectIdx + objectMarker.Length)..], out bucket, out path);

        var slash = trimmed.IndexOf('/');
        if (slash <= 0 || slash >= trimmed.Length - 1)
            return false;

        bucket = trimmed[..slash];
        path = trimmed[(slash + 1)..];
        return !string.IsNullOrWhiteSpace(bucket) && !string.IsNullOrWhiteSpace(path);
    }

    private static string InferReceiptContentType(string storagePath)
    {
        var ext = Path.GetExtension(storagePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }
}

// ── Request records ───────────────────────────────────────────────────────────

public sealed record CreateTournamentRequest(
    string Name,
    string Game,
    DateTime StartDate,
    int MaxTeams,
    string? Description = null,
    string? Slug = null,
    string? Format = null,
    string? GameMode = null,
    int? TeamSize = null,
    decimal? EntryFee = null,
    decimal? PrizePool = null,
    DateTime? EndDate = null,
    DateTime? RegistrationDeadline = null,
    string? Status = null,
    string? BannerUrl = null,
    string? LogoUrl = null,
    string? OrganizationId = null,
    string? VenueId = null,
    string? Region = null,
    bool? IsPublic = false,
    bool? CheckInRequired = false,
    DateTime? CheckInDeadline = null,
    bool? AutoRemoveUnchecked = false,
    string? Rewards = null,
    string? StreamUrl = null,
    object? Settings = null,
    List<StageRequest>? Stages = null,
    string? Rules = null,
    List<string>? MapPoolIds = null,
    string? PaymentInstructions = null,
    string? Currency = null,
    string? ServerRegion = null,
    string? TournamentType = null,
    int? ReservedInviteSlots = null,
    int? InviteExpiryDays = null,
    object? PrizeDistribution = null,
    string? PayoutMethod = null,
    string? ManualPayoutNotes = null);

public sealed record StageRequest(
    string Name,
    string Format,
    int? StageOrder = null,
    int? BestOf = 1,
    int? Capacity = null,
    int? AdvancementCount = null,
    string? BoMode = null,
    Dictionary<string, int>? RoundBoOverrides = null,
    object? Config = null,
    string? StartsAt = null,
    string? EndsAt = null);

public sealed record UpdateTournamentRequest(
    string? Name = null,
    string? Description = null,
    string? Game = null,
    string? Format = null,
    string? GameMode = null,
    string? Status = null,
    int? MaxTeams = null,
    int? TeamSize = null,
    decimal? EntryFee = null,
    decimal? PrizePool = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    DateTime? RegistrationDeadline = null,
    string? BannerUrl = null,
    string? LogoUrl = null,
    string? Region = null,
    bool? IsPublic = null,
    bool? CheckInRequired = null,
    DateTime? CheckInDeadline = null,
    string? Rewards = null,
    string? StreamUrl = null,
    string? Rules = null,
    DateTime? DeletedAt = null,
    bool ClearDeletedAt = false,
    object? Settings = null,
    string? PaymentInstructions = null,
    string? Currency = null,
    string? WinnerTeamName = null,
    int? ReservedInviteSlots = null,
    int? InviteExpiryDays = null,
    object? PrizeDistribution = null,
    string? PayoutMethod = null,
    string? ManualPayoutNotes = null);

public sealed record RegisterTournamentRequest(
    string? TeamId = null,
    string? ParticipantType = null,
    string? TeamCaptainId = null,
    string? TeamName = null,
    string? TeamMembers = null,
    string? RosterLineup = null,
    string? RosterId = null,
    string? RosterName = null,
    string? TeamContactEmail = null,
    string? Status = null,
    decimal? EntryFeeAmount = null,
    bool? EntryFeePaid = null,
    string? PaymentReceiptUrl = null);
public sealed record UpdateBannerRequest(string? Url);
public sealed record PaymentRejectionRequest(string? Reason = null);

// ── Organizer Dispute request records ────────────────────────────────────────

public sealed record AddDisputeCommentRequest(string Comment, bool IsInternal = false, string? AttachmentUrl = null);
public sealed record UpdateDisputeRequest(string? Status = null, string? UpdatedAt = null, string? AssignedToUserId = null, string? ResolutionNotes = null);
public sealed class ResolveDisputeRequest2
{
    public string? Status { get; init; }
    public string? ResolutionNotes { get; init; }
    [JsonPropertyName("resolution_notes")]
    public string? ResolutionNotesSnake { get; init; }
    public string? ReportId { get; init; }
    [JsonPropertyName("report_id")]
    public string? ReportIdSnake { get; init; }
}
public sealed record BanParticipantRequest(string ParticipantId, string? UserId = null, string? BanReason = null);
public sealed record AddMapToPoolRequest(Guid MapId);
public sealed record CreateAnnouncementRequest(string Title, string Content);
public sealed record UpdateAnnouncementRequest(string? Title = null, string? Content = null);
public sealed record CreateDisputeRequest(
    Guid TournamentId,
    string Title,
    string Description,
    Guid? MatchId = null,
    Guid? TeamId = null,
    string? EvidenceUrl = null,
    string? Reason = null);
public sealed record NotifyAdminsRequest(
    Guid DisputeId,
    string? Message = null,
    string? Type = null,
    string? Title = null,
    string? Link = null);

// ── Tournament Staff request records ─────────────────────────────────────────

public sealed record InviteTournamentStaffRequest(
    string UserEmail,
    string Role,
    string[]? Permissions = null);

public sealed record UpdateTournamentStaffRequest(
    string Role,
    string[]? Permissions = null);

public sealed record RespondToStaffInviteRequest(bool Accept);

// ── BR Game Data request records ─────────────────────────────────────────────

public sealed record BRGameDataRequest(JsonElement Games);

public sealed record BRSubmitEvidenceRequest(
    int GameNumber,
    string TeamId,
    string ImageUrl,
    int? Placement = null,
    int? Kills = null);

public sealed record MockGenerateRequest(int? Count = null);

// Internal deserialization helpers for BR evidence merge
internal sealed class BRGameDataInternal
{
    public int gameNumber { get; set; }
    public string status { get; set; } = "pending";
    public List<object>? results { get; set; }
    public string? lobbyCode { get; set; }
    public List<BREvidenceItem>? evidence { get; set; }
}

internal sealed class BREvidenceItem
{
    public string teamId { get; set; } = "";
    public string teamName { get; set; } = "";
    public string imageUrl { get; set; } = "";
    public string submittedBy { get; set; } = "";
    public string submittedAt { get; set; } = "";
    public int? placement { get; set; }
    public int? kills { get; set; }
    public bool reviewed { get; set; }
}

// ── Mock team name generator ──────────────────────────────────────────────────
internal static class MockTeamNames
{
    private static readonly string[] _pool =
    [
        "Shadow Wolves", "Iron Fist", "Ghost Protocol", "Storm Riders", "Neon Strike",
        "Titan Force", "Dark Matter", "Echo Squad", "Venom Vipers", "Steel Phoenix",
        "Apex Hunters", "Cyber Syndicate", "Rogue Elements", "Night Stalkers", "Blade Runners",
        "Thunder Clap", "Void Walkers", "Circuit Breakers", "Solar Flare", "Crimson Tide",
        "Silent Storm", "Alpha Protocol", "Zero Hour", "Phase Shift", "Fracture Point",
        "Orbital Strike", "Black Horizon", "Quantum Flux", "Reaper Squad", "Ice Breakers",
        "Nova Surge", "Static Charge", "War Machine", "Red Signal", "Overwatch Protocol",
        "Deep Impact", "Code Red", "Vector Prime", "Hex Runners", "Signal Lost",
        "Binary Kings", "Override", "Flux State", "Warpzone Elite", "Crossfire Unit",
        "Digital Ghosts", "Null Pointer", "Stack Overflow", "Kernel Panic", "Buffer Overflow",
        "Cache Miss", "Stack Smash", "Heap Spray", "Race Condition", "Deadlock",
        "Memory Leak", "Segfault", "Off By One", "Bit Flip", "Overflow Error",
    ];

    public static List<string> Generate(int count)
    {
        var pool = _pool.ToList();
        var result = new List<string>(count);
        var rng = Random.Shared;

        while (result.Count < count)
        {
            if (pool.Count == 0)
            {
                // If we've exhausted the pool, recycle with a numeric suffix
                pool = _pool.Select((n, i) => $"{n} {(result.Count / _pool.Length) + 2}").ToList();
            }
            var idx = rng.Next(pool.Count);
            result.Add(pool[idx]);
            pool.RemoveAt(idx);
        }

        return result;
    }
}

