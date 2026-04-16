using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
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
        { "draft", "open" };

    /// Typed DTOfor tournament list rows — required so HybridCache (System.Text.Json) can
    /// serialize/deserialize the cached results. Dapper dynamic (ExpandoObject) is NOT
    /// serializable by STJ and causes 500s when HybridCache tries to write to Redis.
    private sealed record TournamentListRow(
        Guid      Id,
        string    Name,
        string?   Slug,
        string    Game,
        string    Status,
        string?   Format,
        DateTime? StartDate,
        DateTime? EndDate,
        DateTime? RegistrationDeadline,
        int?      MaxTeams,
        int?      MinTeams,
        int?      TeamSize,
        decimal?  EntryFee,
        decimal?  PrizePool,
        string?   BannerUrl,
        string?   LogoUrl,
        bool      IsPublic,
        Guid      OrganizerId,
        Guid?     VenueId,
        string?   Description,
        DateTime  CreatedAt,
        DateTime? UpdatedAt,
        string?   Region,
        string?   Currency,
        long      CurrentParticipants,
        string?   OrganizerName,
        string?   OrganizationSlug,
        string?   OrganizerUsername,
        string?   OrganizerFullName,
        string?   WinnerTeamName = null,
        string?   VenueCity = null,
        string?   VenueCountry = null,
        string?   GameBackgroundImage = null
    );

    private const string TournamentListSql = """
        SELECT t.id, t.name, t.slug, t.game, t.status::text AS status, t.format,
               t.start_date, t.end_date, t.registration_deadline,
               t.max_teams, t.min_teams, t.team_size,
               t.entry_fee, t.prize_pool,
               t.banner_url, t.logo_url, t.is_public,
               t.organizer_id, t.venue_id, t.description,
               t.created_at, t.updated_at, t.region, t.currency,
               (SELECT COUNT(*) FROM tournament_participants tp
                WHERE tp.tournament_id = t.id AND tp.status NOT IN ('rejected', 'cancelled')) AS current_participants,
               o.name   AS organizer_name,
               o.slug   AS organization_slug,
               p.username      AS organizer_username,
               p.full_name     AS organizer_full_name,
               wt.name  AS winner_team_name,
               v.city   AS venue_city,
               v.country AS venue_country,
               gm.background_image AS game_background_image
        FROM tournaments t
        LEFT JOIN organizations o  ON o.id  = t.organization_id
        LEFT JOIN profiles      p  ON p.id  = t.organizer_id
        LEFT JOIN teams        wt  ON wt.id = t.winner_id
        LEFT JOIN venues        v  ON v.id  = t.venue_id
        LEFT JOIN games_metadata gm ON LOWER(gm.game_name) = LOWER(t.game)
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
        ORDER BY t.start_date ASC
        LIMIT @limit OFFSET @offset
        """;

    public static void MapTournamentEndpoints(this WebApplication app)
    {
        // ── GET /api/tournaments ───────────────────────────────────────────────
        // Replaces useTournaments N+1: participant count in a correlated subquery.
        app.MapGet("/api/tournaments", async (
            string?              status,
            string?              game,
            string?              q,
            string?              organizer_id,
            string?              ids,
            bool?                is_online,
            string?              city,
            string?              country,
            string?              region,
            int                  limit  = 50,
            int                  offset = 0,
            IDbConnectionFactory db     = null!,
            HybridCache          cache  = null!,
            CancellationToken    ct     = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
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
                    SELECT t.id, t.name, t.slug, t.game, t.status::text AS status, t.format,
                           t.start_date, t.end_date, t.registration_deadline,
                           t.max_teams, t.min_teams, t.team_size,
                           t.entry_fee, t.prize_pool,
                           t.banner_url, t.logo_url, t.is_public,
                           t.organizer_id, t.venue_id, t.description,
                           t.created_at, t.updated_at, t.region, t.currency,
                           (SELECT COUNT(*) FROM tournament_participants tp
                            WHERE tp.tournament_id = t.id AND tp.status NOT IN ('rejected', 'cancelled')) AS current_participants,
                           o.name   AS organizer_name,
                           o.slug   AS organization_slug,
                           p.username      AS organizer_username,
                           p.full_name     AS organizer_full_name,
                           wt.name  AS winner_team_name,
                           v.city   AS venue_city,
                           v.country AS venue_country,
                           gm.background_image AS game_background_image
                    FROM tournaments t
                    LEFT JOIN organizations o  ON o.id  = t.organization_id
                    LEFT JOIN profiles      p  ON p.id  = t.organizer_id
                    LEFT JOIN teams        wt  ON wt.id = t.winner_id
                    LEFT JOIN venues        v  ON v.id  = t.venue_id
                    LEFT JOIN games_metadata gm ON LOWER(gm.game_name) = LOWER(t.game)
                    WHERE t.id = ANY(@idList) AND t.deleted_at IS NULL
                    ORDER BY t.start_date ASC
                    """,
                    new { idList })).AsList();
                return Results.Json(rows2, s_snakeCase);
            }

            var cacheKey = $"tournaments:{status}:{game}:{q}:{organizer_id}:{is_online}:{city}:{country}:{region}:{limit}:{offset}";
            Guid? organizerGuid = Guid.TryParse(organizer_id, out var g) ? g : null;
            var rows = await cache.GetOrCreateAsync<List<TournamentListRow>>(
                cacheKey,
                async (_) =>
                {
                    using var conn = db.CreateConnection();
                    return (await conn.QueryAsync<TournamentListRow>(
                        TournamentListSql,
                        new { status, game, q, organizerGuid, isOnline = is_online, city, country, region, limit, offset })).AsList();
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                tags: ["tournament-list"],
                cancellationToken: ct);
            return Results.Json(rows, s_snakeCase);
        });

        // ── GET /api/tournaments/filters — distinct values from actual content ─
        app.MapGet("/api/tournaments/filters", async (
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
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
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            var rows = await cache.GetOrCreateAsync<List<TournamentListRow>>(
                "tournaments:upcoming",
                async (_) =>
                {
                    using var conn = db.CreateConnection();
                    return (await conn.QueryAsync<TournamentListRow>(
                        """
                        SELECT t.id, t.name, t.slug, t.game, t.status::text AS status, t.format,
                               t.start_date, t.end_date, t.registration_deadline,
                               t.max_teams, t.min_teams, t.team_size,
                               t.entry_fee, t.prize_pool,
                               t.banner_url, t.logo_url, t.is_public,
                               t.organizer_id, t.venue_id, t.description,
                               t.created_at, t.updated_at, t.region, t.currency,
                               (SELECT COUNT(*) FROM tournament_participants tp
                                WHERE tp.tournament_id = t.id AND tp.status NOT IN ('rejected', 'cancelled')) AS current_participants,
                               o.name AS organizer_name,
                               o.slug AS organization_slug,
                               p.username   AS organizer_username,
                               p.full_name  AS organizer_full_name,
                               wt.name AS winner_team_name,
                               v.city   AS venue_city,
                               v.country AS venue_country
                        FROM tournaments t
                        LEFT JOIN organizations o ON o.id = t.organization_id
                        LEFT JOIN profiles      p ON p.id = t.organizer_id
                        LEFT JOIN teams        wt ON wt.id = t.winner_id
                        LEFT JOIN venues        v ON v.id  = t.venue_id
                        WHERE t.is_public = TRUE
                          AND t.deleted_at IS NULL
                          AND t.status::text IN ('published', 'open', 'check_in')
                        ORDER BY t.start_date ASC
                        LIMIT 100
                        """)).AsList();
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                cancellationToken: ct);
            return Results.Json(rows, s_snakeCase);
        }); // Public

        // ── GET /api/tournaments/{slugOrId} ────────────────────────────────────
        // Replaces useTournamentDashboard — consolidated tournament + participants + stages.
        app.MapGet("/api/tournaments/{slugOrId}", async (
            string               slugOrId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            // Support slug OR uuid lookup (with ILIKE slug fallback)
            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT t.*,
                       (SELECT COUNT(*) FROM tournament_participants tp WHERE tp.tournament_id = t.id AND tp.status NOT IN ('rejected', 'cancelled')) AS current_participants,
                       (SELECT COUNT(*) FROM tournament_participants tp WHERE tp.tournament_id = t.id AND tp.status = 'checked_in') AS checked_in_count,
                       o.name       AS organization_name, o.slug AS organization_slug,
                       o.logo_url   AS organization_logo,  o.owner_id AS organization_owner_id,
                       p.username   AS organizer_username,  p.avatar_url AS organizer_avatar,
                       v.name       AS venue_name,
                       wt.name      AS winner_team_name,    wt.logo_url AS winner_team_logo,
                       gm.background_image AS game_background_image
                FROM tournaments t
                LEFT JOIN organizations o  ON o.id  = t.organization_id
                LEFT JOIN profiles      p  ON p.id  = t.organizer_id
                LEFT JOIN venues        v  ON v.id  = t.venue_id
                LEFT JOIN teams        wt  ON wt.id = t.winner_id
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
            var organizerId  = (Guid)tournament.organizer_id;

            // Fetch participants and stages sequentially (Npgsql connections are NOT thread-safe)
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

            // Permission check for organizer/staff
            var userCtx = ctx.Items["UserContext"] as UserContext;
            bool isOrganizer = false;
            if (userCtx is not null)
            {
                isOrganizer = userCtx.UserIdGuid == organizerId;
                // Also check if user owns the organization
                if (!isOrganizer && tournament.organization_id is not null)
                {
                    isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId)",
                        new { orgId = (Guid)tournament.organization_id, userId = userCtx.UserIdGuid });
                }
            }

            string[]? staffPermissions = null;
            if (userCtx is not null && !isOrganizer)
            {
                staffPermissions = (await conn.QueryAsync<string>(
                    """
                    SELECT DISTINCT unnest(os.permissions)
                    FROM organization_staff os
                    JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                    WHERE sta.tournament_id = @tid
                      AND os.user_id = @userId AND os.status = 'active'
                    """,
                    new { tid = tournamentId, userId = userCtx.UserIdGuid })).ToArray();
            }

            // Organizers see all participants (for payment management); others see only active
            var participants = isOrganizer
                ? allParticipants
                : allParticipants.Where(p => {
                    string status = (string)p.status;
                    return status != "rejected" && status != "cancelled";
                });

            return Results.Ok(new
            {
                tournament,
                participants,
                stages,
                isOrganizer,
                staffPermissions,
            });
        });

        // ── POST /api/tournaments ──────────────────────────────────────────────
        // Handles slug uniqueness + stages + map pool in one transaction.
        app.MapPost("/api/tournaments", async (
            [FromBody] CreateTournamentRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            HybridCache                       cache,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx   = conn.BeginTransaction();

            try
            {
                // Unique slug: base + numeric suffix on conflict
                var slug       = req.Slug ?? Slugify(req.Name);
                var uniqueSlug = slug;
                var exists     = await conn.QuerySingleOrDefaultAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM tournaments WHERE slug = @slug)", new { slug }, tx);
                if (exists)
                    uniqueSlug = $"{slug}-{DateTime.UtcNow.Ticks % 9999:x4}";

                var tournament = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO tournaments (
                        name, description, slug, game, format, max_teams, min_teams, team_size,
                        entry_fee, prize_pool, start_date, end_date, registration_deadline,
                        status, banner_url, logo_url, organization_id, venue_id, is_public,
                        check_in_required, check_in_deadline, auto_remove_unchecked,
                        rewards, stream_url, settings, organizer_id, rules, payment_instructions, region, currency, server_region
                    ) VALUES (
                        @name, @description, @slug, @game, @format, @maxTeams, 2, @teamSize,
                        @entryFee, @prizePool, @startDate, @endDate, @registrationDeadline,
                        @status::tournament_status, @bannerUrl, @logoUrl, @organizationId, @venueId, @isPublic,
                        @checkInRequired, @checkInDeadline, @autoRemoveUnchecked,
                        @rewards, @streamUrl, @settings::jsonb, @organizerId, @rules, @paymentInstructions, @region, @currency, @serverRegion
                    )
                    RETURNING id, name, description, slug, game, format, max_teams, min_teams, team_size,
                             entry_fee, prize_pool, start_date, end_date, registration_deadline,
                             status, banner_url, logo_url, organization_id, venue_id, is_public,
                             check_in_required, check_in_deadline, auto_remove_unchecked,
                             rewards, stream_url, settings, organizer_id, created_at, rules, payment_instructions, region, currency
                    """,
                    new
                    {
                        name                 = req.Name,
                        description          = req.Description,
                        slug                 = uniqueSlug,
                        game                 = req.Game,
                        format               = req.Format ?? "single_elimination",
                        maxTeams             = req.MaxTeams,
                        teamSize             = req.TeamSize ?? 1,
                        entryFee             = req.EntryFee ?? 0m,
                        prizePool            = req.PrizePool ?? 0m,
                        startDate            = req.StartDate,
                        endDate              = req.EndDate ?? req.StartDate.AddHours(2),
                        registrationDeadline = req.RegistrationDeadline ?? req.StartDate.AddDays(-1),
                        status               = AllowedCreateStatuses.Contains(req.Status ?? "") ? req.Status! : "open",
                        bannerUrl            = req.BannerUrl,
                        logoUrl              = req.LogoUrl,
                        organizationId       = Guid.TryParse(req.OrganizationId, out var orgGuid) ? orgGuid : (Guid?)null,
                        venueId              = Guid.TryParse(req.VenueId, out var venGuid) ? venGuid : (Guid?)null,
                        isPublic             = req.IsPublic ?? true,
                        checkInRequired      = req.CheckInRequired ?? false,
                        checkInDeadline      = req.CheckInDeadline,
                        autoRemoveUnchecked  = req.AutoRemoveUnchecked ?? false,
                        rewards              = req.Rewards,
                        streamUrl            = req.StreamUrl,
                        settings             = req.Settings is not null
                            ? JsonSerializer.Serialize(req.Settings)
                            : "{}",
                        organizerId          = userCtx.UserIdGuid,
                        rules                = req.Rules,
                        paymentInstructions  = req.PaymentInstructions,
                        region               = req.Region,
                        currency             = req.Currency ?? "USD",
                        serverRegion         = req.ServerRegion,
                    },
                    tx);

                var tournamentId = (Guid)tournament.id;

                // Stages
                if (req.Stages is { Count: > 0 })
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO tournament_stages
                            (tournament_id, name, format, stage_order, best_of, capacity, advancement_count)
                        VALUES
                            (@tournamentId, @name, @format, @stageOrder, @bestOf, @capacity, @advancementCount)
                        """,
                        req.Stages.Select((s, i) => new
                        {
                            tournamentId,
                            name              = s.Name,
                            format            = s.Format,
                            stageOrder        = s.StageOrder ?? i,
                            bestOf            = s.BestOf ?? 1,
                            capacity          = s.Capacity,
                            advancementCount  = s.AdvancementCount,
                        }),
                        tx);
                }

                // Map pool
                if (req.MapPoolIds is { Count: > 0 })
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO tournament_map_pools (tournament_id, map_id) VALUES (@tournamentId, @mapId)",
                        req.MapPoolIds
                            .Where(m => Guid.TryParse(m, out _))
                            .Select(m => new { tournamentId, mapId = Guid.Parse(m) }),
                        tx);
                }

                tx.Commit();

                // Invalidate all tournament list cache entries
                try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }

                return Results.Ok(tournament);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Organizer");

        // ── PUT /api/tournaments/{id} ──────────────────────────────────────────
        app.MapPut("/api/tournaments/{id}", async (
            Guid                          id,
            [FromBody] UpdateTournamentRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory          db,
            HybridCache                   cache,
            CancellationToken             ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Only organizer or admin can update
            var organizerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT organizer_id FROM tournaments WHERE id = @id", new { id });
            if (organizerId is null) return Results.NotFound();
            if (organizerId != userCtx.UserIdGuid && !userCtx.Roles.Contains("admin"))
                return Results.Forbid();

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE tournaments SET
                    name                 = COALESCE(@name, name),
                    description          = COALESCE(@description, description),
                    game                 = COALESCE(@game, game),
                    status               = CASE WHEN @status IS NOT NULL THEN @status::tournament_status ELSE status END,
                    max_teams            = COALESCE(@maxTeams, max_teams),
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
                    deleted_at           = CASE WHEN @clearDeletedAt THEN NULL ELSE COALESCE(@deletedAt, deleted_at) END,
                    updated_at           = NOW()
                WHERE id = @id
                RETURNING id, name, description, slug, game, format, max_teams, min_teams, team_size,
                         entry_fee, prize_pool, start_date, end_date, registration_deadline,
                         status, banner_url, logo_url, organization_id, venue_id, is_public,
                         check_in_required, check_in_deadline, auto_remove_unchecked,
                         rewards, stream_url, rules, payment_instructions, region, currency, settings, organizer_id, created_at, updated_at
                """,
                new
                {
                    id,
                    name                 = req.Name,
                    description          = req.Description,
                    game                 = req.Game,
                    status               = req.Status,
                    maxTeams             = req.MaxTeams,
                    entryFee             = req.EntryFee,
                    prizePool            = req.PrizePool,
                    startDate            = req.StartDate,
                    endDate              = req.EndDate,
                    registrationDeadline = req.RegistrationDeadline,
                    bannerUrl            = req.BannerUrl,
                    logoUrl              = req.LogoUrl,
                    isPublic             = req.IsPublic,
                    checkInRequired      = req.CheckInRequired,
                    checkInDeadline      = req.CheckInDeadline,
                    rewards              = req.Rewards,
                    streamUrl            = req.StreamUrl,
                    rules                = req.Rules,
                    paymentInstructions  = req.PaymentInstructions,
                    region               = req.Region,
                    currency             = req.Currency,
                    settings             = req.Settings is not null
                                             ? System.Text.Json.JsonSerializer.Serialize(req.Settings)
                                             : null,
                    deletedAt            = req.DeletedAt,
                    clearDeletedAt       = req.ClearDeletedAt,
                });


            // Clear winner_id when reopening a completed tournament
            if (req.Status is not null && req.Status != "completed")
            {
                try
                {
                    using var txClear = conn.BeginTransaction();
                    await conn.ExecuteAsync(
                        "SET LOCAL request.jwt.claim.role = 'service_role'",
                        transaction: txClear);
                    await conn.ExecuteAsync(
                        "UPDATE tournaments SET winner_id = NULL WHERE id = @id AND winner_id IS NOT NULL",
                        new { id },
                        transaction: txClear);
                    txClear.Commit();
                }
                catch { /* best effort */ }
            }

            // Auto-set winner_id when tournament is marked completed
            // Only pick winner from the grand final (last match of last stage)
            // and only if ALL stages are completed
            if (req.Status == "completed" && updated is not null)
            {
                Guid? resolvedWinnerId = null;

                // 1. BR tournaments: frontend sends winner_team_name, look up team by name
                if (!string.IsNullOrWhiteSpace(req.WinnerTeamName))
                {
                    resolvedWinnerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                        """
                        SELECT tp.team_id FROM tournament_participants tp
                        JOIN teams t ON t.id = tp.team_id
                        WHERE tp.tournament_id = @id AND t.name = @teamName
                        LIMIT 1
                        """,
                        new { id, teamName = req.WinnerTeamName });
                }

                // 2. Bracket tournaments: find winner from grand final match
                if (resolvedWinnerId is null)
                {
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

                    if (stageTotal > 0 && stageTotal == stageCompleted)
                    {
                        resolvedWinnerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
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
                }

                if (resolvedWinnerId.HasValue)
                {
                    try
                    {
                        using var tx = conn.BeginTransaction();
                        await conn.ExecuteAsync(
                            "SET LOCAL request.jwt.claim.role = 'service_role'",
                            transaction: tx);
                        await conn.ExecuteAsync(
                            "UPDATE tournaments SET winner_id = @winnerId WHERE id = @id",
                            new { id, winnerId = resolvedWinnerId.Value },
                            transaction: tx);
                        tx.Commit();
                    }
                    catch
                    {
                        // If trigger still blocks, don't fail the status change
                    }
                }
            }

            try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/tournaments/{id} — permanent delete (organizer only, must be soft-deleted first)
        app.MapDelete("/api/tournaments/{id}", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            HybridCache          cache,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT organizer_id, deleted_at FROM tournaments WHERE id = @id", new { id });
            if (row is null) return Results.NotFound();
            if ((Guid)row.organizer_id != userCtx.UserIdGuid && !userCtx.Roles.Contains("admin"))
                return Results.Forbid();
            if (row.deleted_at is null)
                return Results.BadRequest(new { error = "Tournament must be moved to trash before it can be permanently deleted." });

            await conn.ExecuteAsync("DELETE FROM tournaments WHERE id = @id", new { id });
            try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { /* best effort */ }
            return Results.Ok(new { deleted = true });
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/me/history ─────────────────────────────────
        // Returns tournaments the current user has participated in.
        app.MapGet("/api/tournaments/me/history", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
                """,
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/me/registration-status ────────────────────────
        // With ?ids=id1,id2,id3 → { id1: true, id2: false, ... } (batch check)
        // Without ids           → [{ tournament_id: "...", id: "..." }, ...] (all registrations)
        app.MapGet("/api/tournaments/me/registration-status", async (
            string?              ids,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
                          AND tp.status NOT IN ('cancelled', 'rejected')
                        """,
                        new { userId = userCtx.UserIdGuid, teamIds });
                }
                else
                {
                    rows = await conn.QueryAsync(
                        "SELECT id, tournament_id FROM tournament_participants WHERE user_id = @userId AND status NOT IN ('cancelled', 'rejected')",
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
                    "SELECT tournament_id FROM tournament_participants WHERE tournament_id = ANY(@ids) AND user_id = @userId AND status NOT IN ('cancelled', 'rejected')",
                    new { ids = idList, userId = userCtx.UserIdGuid });
            }

            var registeredSet = registeredIdGuids.Select(g => g.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var statusMap     = idList.ToDictionary(id => id, id => registeredSet.Contains(id));

            return Results.Ok(statusMap);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/register ────────────────────────────────
        app.MapPost("/api/tournaments/{id}/register", async (
            Guid                             id,
            [FromBody] RegisterTournamentRequest req,
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var txn = conn.BeginTransaction();

            // Lock tournament row to prevent race condition on capacity check
            var tourn = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT status, max_teams, entry_fee, payment_instructions, game FROM tournaments WHERE id = @id FOR UPDATE",
                new { id }, txn);
            if (tourn is null)    { txn.Rollback(); return Results.NotFound(); }
            if ((string)tourn.status is not "open" and not "published")
            {   txn.Rollback(); return Results.BadRequest(new { error = "Tournament is not accepting registrations." }); }

            // Check capacity (0 or null = unlimited)
            int? maxTeams = (int?)tourn.max_teams;
            if (maxTeams.HasValue && maxTeams.Value > 0)
            {
                var currentCount = await conn.QuerySingleAsync<int>(
                    "SELECT COUNT(*) FROM tournament_participants WHERE tournament_id = @id AND status NOT IN ('rejected', 'cancelled')",
                    new { id }, txn);
                if (currentCount >= maxTeams.Value)
                {   txn.Rollback(); return Results.BadRequest(new { error = "Tournament has reached maximum capacity." }); }
            }

            // Check existing registration (exclude cancelled/rejected/disqualified)
            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id FROM tournament_participants WHERE tournament_id = @id AND user_id = @userId AND status NOT IN ('cancelled', 'rejected', 'disqualified')",
                new { id, userId = userCtx.UserIdGuid }, txn);
            if (existing is not null)
            {   txn.Rollback(); return Results.Conflict(new { error = "You are already registered for this tournament." }); }

            Guid? teamIdGuid      = req.TeamId is not null ? Guid.Parse(req.TeamId) : null;
            Guid? captainIdGuid   = req.TeamCaptainId is not null ? Guid.Parse(req.TeamCaptainId) : null;
            Guid? rosterIdGuid    = req.RosterId is not null ? Guid.Parse(req.RosterId) : null;
            var   participantType = teamIdGuid is not null ? "team" : "solo";

            // Auto-create a virtual team for solo participants so that
            // brkt_matches.team1_id / team2_id always references teams(id).
            if (participantType == "solo")
            {
                var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT username, avatar_url FROM profiles WHERE id = @uid",
                    new { uid = userCtx.UserIdGuid }, txn);

                var vtId  = Guid.NewGuid();
                var vtTag = $"solo-{vtId:N}";

                await conn.ExecuteAsync(
                    """
                    INSERT INTO teams (id, name, tag, game, owner_id, is_solo, max_members, logo_url)
                    VALUES (@vtId, @name, @tag, @game, @ownerId, true, 1, @logo)
                    """,
                    new
                    {
                        vtId,
                        name = (string?)profile?.username ?? "Solo Player",
                        tag  = vtTag,
                        game = (string)tourn.game,
                        ownerId = userCtx.UserIdGuid,
                        logo = (string?)profile?.avatar_url,
                    }, txn);

                await conn.ExecuteAsync(
                    """
                    INSERT INTO team_members (team_id, user_id, role, is_active)
                    VALUES (@teamId, @userId, 'captain', true)
                    ON CONFLICT DO NOTHING
                    """,
                    new { teamId = vtId, userId = userCtx.UserIdGuid }, txn);

                teamIdGuid = vtId;
            }

            // Determine if this is a paid tournament
            decimal tournEntryFee = (decimal)(tourn.entry_fee ?? 0m);
            bool isPaid = tournEntryFee > 0;

            // Server determines registration status — never trust user-supplied value
            var   regStatus       = isPaid ? "pending" : "approved";
            var   paymentStatus   = isPaid ? "pending" : "not_required";
            var   entryFeePaid    = !isPaid; // free = already paid; paid = not yet

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO tournament_participants
                    (tournament_id, user_id, team_id, team_captain_id, team_name,
                     team_members, team_contact_email, roster_id, roster_name,
                     status, participant_type, entry_fee_amount, entry_fee_paid,
                     payment_status, payment_receipt_url)
                VALUES (@tournamentId, @userId, @teamId, @teamCaptainId, @teamName,
                        @teamMembers::jsonb, @teamContactEmail, @rosterId, @rosterName,
                        @regStatus::registration_status, @participantType::registration_type,
                        @entryFeeAmount, @entryFeePaid,
                        @paymentStatus, @paymentReceiptUrl)
                RETURNING id, tournament_id, user_id, team_id, team_captain_id, team_name,
                         team_members, team_contact_email, roster_id, roster_name,
                         status, participant_type, entry_fee_amount, entry_fee_paid,
                         payment_status, payment_receipt_url, created_at
                """,
                new
                {
                    tournamentId     = id,
                    userId           = userCtx.UserIdGuid,
                    teamId           = teamIdGuid,
                    teamCaptainId    = captainIdGuid ?? userCtx.UserIdGuid,
                    teamName         = req.TeamName,
                    teamMembers      = req.TeamMembers is not null ? $"[\"{req.TeamMembers.Replace(",", "\",\"")}\"]" : "[]",
                    teamContactEmail = req.TeamContactEmail,
                    rosterId         = rosterIdGuid,
                    rosterName       = req.RosterName,
                    regStatus,
                    participantType,
                    entryFeeAmount   = tournEntryFee,
                    entryFeePaid     = entryFeePaid,
                    paymentStatus    = paymentStatus,
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
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                 id,
            Guid                 participantId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify requester is the tournament organizer
            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOrganizer) return Results.Forbid();

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
                    new {
                        userId  = (Guid)participant.user_id,
                        title   = $"💰 Payment Confirmed!",
                        message = $"You're officially in! Your payment for {(string)participant.tournament_name} has been approved. Time to prepare for battle!",
                        data    = $"{{\"tournament_id\":\"{id}\"}}"
                    });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/participants/{participantId}/reject-payment ──
        app.MapPost("/api/tournaments/{id}/participants/{participantId}/reject-payment", async (
            Guid                                    id,
            Guid                                    participantId,
            [FromBody] PaymentRejectionRequest       req,
            HttpContext                              ctx,
            IDbConnectionFactory                    db,
            CancellationToken                       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify requester is the tournament organizer
            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOrganizer) return Results.Forbid();

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
                    new {
                        userId  = (Guid)participant.user_id,
                        title   = "❌ Payment Not Accepted",
                        message = $"Your payment for {(string)participant.tournament_name} was not accepted. Reason: {req.Reason ?? "No reason provided."} — You can resubmit if eligible.",
                        data    = $"{{\"tournament_id\":\"{id}\"}}"
                    });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/upload-receipt ────────────────────────────
        app.MapPost("/api/tournaments/{id}/upload-receipt", async (
            Guid                                    id,
            HttpContext                              ctx,
            IDbConnectionFactory                    db,
            IConfiguration                          config,
            CancellationToken                       ct) =>
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
                "SELECT id FROM tournament_participants WHERE tournament_id = @tournamentId AND (user_id = @userId OR team_captain_id = @userId) AND status NOT IN ('cancelled', 'rejected')",
                new { tournamentId = id, userId = userCtx.UserIdGuid });
            if (participant is null) return Results.NotFound(new { error = "You are not registered for this tournament." });

            // Upload to Supabase storage
            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/') ?? config["SupabaseUrl"]?.TrimEnd('/');
            var serviceKey  = config["Supabase:ServiceKey"] ?? config["Supabase:ServiceRoleKey"] ?? config["SupabaseServiceRoleKey"];
            if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceKey))
                return Results.Json(new { error = "File storage is temporarily unavailable. Please try again later." }, statusCode: 500);

            var ext         = Path.GetExtension(file.FileName) ?? ".jpg";
            var storagePath = $"{id}/{userCtx.UserIdGuid}{ext}";
            var bucket      = "tournaments.payment.receipts";

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceKey);
            http.DefaultRequestHeaders.Add("apikey", serviceKey);
            http.DefaultRequestHeaders.Add("x-upsert", "true");

            using var stream  = file.OpenReadStream();
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
        // Returns the public receipt URL — only organizers can call this endpoint
        app.MapGet("/api/tournaments/{id}/participants/{participantId}/receipt", async (
            Guid                 id,
            Guid                 participantId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            IConfiguration       config,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOrganizer) return Results.Forbid();

            var receiptRef = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT payment_receipt_url FROM tournament_participants WHERE id = @participantId AND tournament_id = @id",
                new { participantId, id });
            if (string.IsNullOrWhiteSpace(receiptRef)) return Results.NotFound(new { error = "No receipt found." });

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/') ?? config["SupabaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(supabaseUrl))
                return Results.Json(new { error = "File storage is temporarily unavailable. Please try again later." }, statusCode: 500);

            var publicUrl = $"{supabaseUrl}/storage/v1/object/public/{receiptRef}";
            return Results.Ok(new { url = publicUrl });
        }).RequireAuthorization("Authenticated");

        app.MapPost("/api/tournaments/{id}/check-in", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

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

        // ── GET /api/tournaments/{id}/my-status ────────────────────────────────
        // Consolidated endpoint: returns ban status, registration, team info for current user.
        app.MapGet("/api/tournaments/{id}/my-status", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
                  AND tp.status NOT IN ('cancelled', 'rejected')
                  AND (tp.user_id = @userId OR tp.team_captain_id = @userId
                       OR (tp.team_id = ANY(@teamIds) AND tp.participant_type = 'team'))
                ORDER BY CASE WHEN tp.user_id = @userId THEN 0 ELSE 1 END, tp.created_at DESC
                LIMIT 1
                """,
                new { id, userId = userCtx.UserIdGuid, teamIds = userTeamIds });

            // 5. Get user's captain teams for registration options
            var captainTeams = await conn.QueryAsync<dynamic>(
                """
                SELECT t.id, t.name, t.logo_url
                FROM teams t
                WHERE t.owner_id = @userId
                   OR t.id IN (SELECT team_id FROM team_members WHERE user_id = @userId AND role = 'captain' AND is_active = TRUE)
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(new
            {
                userBan       = userBan is not null ? new { banReason = (string)userBan.ban_reason } : null,
                teamBan       = teamBan is not null ? new { banReason = (string)teamBan.ban_reason, teamId = ((Guid)teamBan.team_id).ToString() } : null,
                registration,
                captainTeams,
                userTeamIds   = userTeamIds.Select(g => g.ToString()),
            });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/tournaments/{id}/banner ────────────────────────────────────
        app.MapPut("/api/tournaments/{id}/banner", async (
            Guid                            id,
            [FromBody] UpdateBannerRequest  req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOrganizer = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOrganizer) return Results.Forbid();

            await conn.ExecuteAsync(
                "UPDATE tournaments SET banner_url = @url, updated_at = NOW() WHERE id = @id",
                new { id, url = req.Url });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/registrations — organizer: all registrations ───
        app.MapGet("/api/tournaments/{id}/registrations", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify organizer
            var isOrganizer = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOrganizer) return Results.Forbid();

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
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller owns this tournament
            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

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
        }).RequireAuthorization("Organizer");

        // ── POST /api/tournaments/{id}/ban-participant ──────────────────────────
        app.MapPost("/api/tournaments/{id}/ban-participant", async (
            Guid                                 id,
            [FromBody] BanParticipantRequest     req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var participantId = Guid.Parse(req.ParticipantId);

            // Verify caller owns this tournament
            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            // Get participant info
            var participant = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT user_id, team_id FROM tournament_participants WHERE id = @pid",
                new { pid = participantId });
            if (participant is null)
                return Results.NotFound(new { error = "Participant not found" });

            // Determine ban target (must be Guid for uuid columns)
            Guid? banUserId = null, banTeamId = null;
            if (participant.team_id is not null) banTeamId = (Guid)participant.team_id;
            else if (participant.user_id is not null) banUserId = (Guid)participant.user_id;
            else if (req.UserId is not null) banUserId = Guid.Parse(req.UserId);

            if (banUserId is null && banTeamId is null)
                return Results.BadRequest(new { error = "Cannot determine ban target" });

            // Insert ban
            await conn.ExecuteAsync(
                """
                INSERT INTO tournament_bans (tournament_id, participant_id, user_id, team_id, ban_reason, banned_by, banned_at, is_active)
                VALUES (@tournamentId, @participantId, @userId, @teamId, @banReason, @bannedBy, NOW(), TRUE)
                """,
                new { tournamentId = id, participantId, userId = banUserId, teamId = banTeamId, banReason = req.BanReason, bannedBy = userCtx.UserIdGuid });

            // Mark participant as disqualified (soft delete)
            await conn.ExecuteAsync(
                "UPDATE tournament_participants SET status = 'disqualified' WHERE id = @pid AND status NOT IN ('cancelled', 'rejected')",
                new { pid = participantId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/{id}/participants/{pid} — single participant ───
        app.MapGet("/api/tournaments/{id}/participants/{pid}", async (
            Guid                 id,
            Guid                 pid,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM tournament_participants WHERE id = @pid AND tournament_id = @id",
                new { pid, id });
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/{id}/stages ─────────────────────────────────
        app.MapGet("/api/tournaments/{id}/stages", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT * FROM tournament_stages WHERE tournament_id = @id ORDER BY stage_order",
                new { id });
            return Results.Ok(rows);
        });

        // ── GET /api/tournaments/{id}/bracket-versions ───────────────────────
        app.MapGet("/api/tournaments/{id}/bracket-versions", async (
            Guid                 id,
            string?              status,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
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
            Guid                 id,
            string?              status,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            // Build optional status filter — exclude rejected/cancelled by default
            var statusFilter = !string.IsNullOrEmpty(status)
                ? "AND tp.status::text = @status"
                : "AND tp.status NOT IN ('rejected', 'cancelled')";

            // Fetch participants with team member roster details
            var flat = await conn.QueryAsync<dynamic>(
                $"""
                SELECT tp.id, tp.tournament_id, tp.user_id, tp.team_id,
                       tp.participant_type::text AS participant_type,
                       tp.status::text AS status, tp.created_at,
                       t.name AS team_name, t.logo_url AS team_logo_url,
                       tm.user_id AS member_user_id,
                       p.username AS member_username,
                       sp.username AS solo_username,
                       sp.full_name AS solo_full_name,
                       sp.riot_tag AS solo_riot_tag,
                       sp.avatar_url AS solo_avatar_url
                FROM tournament_participants tp
                LEFT JOIN teams t ON t.id = tp.team_id
                LEFT JOIN team_members tm ON tm.team_id = tp.team_id AND tm.is_active = true
                LEFT JOIN profiles p ON p.id = tm.user_id
                LEFT JOIN profiles sp ON sp.id = tp.user_id
                WHERE tp.tournament_id = @id {statusFilter}
                ORDER BY tp.created_at ASC
                LIMIT 500
                """, new { id, status });

            // Group by participant to nest members
            var grouped = flat
                .GroupBy(r => (Guid)r.id)
                .Select(g =>
                {
                    var first = g.First();
                    var members = g
                        .Where(m => m.member_user_id is not null)
                        .Select(m => new { user_id = (Guid)m.member_user_id, username = (string)m.member_username })
                        .Distinct()
                        .ToList();

                    return new
                    {
                        id = (Guid)first.id,
                        tournament_id = (Guid)first.tournament_id,
                        user_id = first.user_id as Guid?,
                        team_id = first.team_id as Guid?,
                        participant_type = first.participant_type as string,
                        status = (string)first.status,
                        created_at = first.created_at,
                        team_name = first.team_name as string,
                        team_logo_url = first.team_logo_url as string,
                        team_members = string.Join(", ", members.Select(m => m.username)),
                        members,
                        solo_username = first.solo_username as string,
                        solo_full_name = first.solo_full_name as string,
                        solo_riot_tag = first.solo_riot_tag as string,
                        solo_avatar_url = first.solo_avatar_url as string,
                    };
                })
                .ToList();

            return Results.Ok(grouped);
        });

        // ── GET /api/tournaments/{id}/match-proofs ───────────────────────────
        app.MapGet("/api/tournaments/{id}/match-proofs", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT match_id, image_url FROM tournament_match_results WHERE tournament_id = @id AND image_url IS NOT NULL",
                new { id });
            return Results.Ok(rows);
        });

        // ── GET /api/tournaments/{id}/match-games ────────────────────────────
        app.MapGet("/api/tournaments/{id}/match-games", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
                """,
                new { id });
            DapperJsonbHelper.FixJsonb(rows);
            return Results.Ok(rows);
        });

        // ── GET /api/teams/search — search teams by name ─────────────────────────
        app.MapGet("/api/teams/search", async (
            string?              name,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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

        // GET /api/tournaments/{id}/br-games — read BR game data (any authenticated user)
        app.MapGet("/api/tournaments/{id}/br-games", async (
            Guid                 id,
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
            Guid                 id,
            [FromBody] BRGameDataRequest req,
            HttpContext           ctx,
            IDbConnectionFactory  db) =>
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
            Guid                 id,
            [FromBody] BRSubmitEvidenceRequest req,
            HttpContext           ctx,
            IDbConnectionFactory  db) =>
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
            Guid                 tournamentId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Only organizer or existing org staff can view staff list
            var hasAccess = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tournaments WHERE id = @tid AND organizer_id = @userId
                    UNION ALL
                    SELECT 1 FROM organization_staff os
                    JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                    WHERE sta.tournament_id = @tid AND os.user_id = @userId AND os.status = 'active'
                )
                """,
                new { tid = tournamentId, userId = userCtx.UserIdGuid });
            if (!hasAccess && !userCtx.Roles.Contains("admin")) return Results.Forbid();

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
            Guid                                  tournamentId,
            [FromBody] InviteTournamentStaffRequest req,
            HttpContext                             ctx,
            IDbConnectionFactory                   db,
            CancellationToken                      ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Only organizer can invite staff
            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @tid AND organizer_id = @userId)",
                new { tid = tournamentId, userId = userCtx.UserIdGuid });
            if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();

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
                    new { id = orgStaffId, role = req.Role,
                          permissions = req.Permissions ?? Array.Empty<string>(),
                          assignedBy = userCtx.UserIdGuid });
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
                    new { orgId, userId, role = req.Role,
                          permissions = req.Permissions ?? Array.Empty<string>(),
                          assignedBy = userCtx.UserIdGuid })).ToString();
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
        }).RequireAuthorization("Organizer");

        // ── PUT /api/tournaments/staff/{staffId} — update role/permissions ───
        app.MapPut("/api/tournaments/staff/{staffId}", async (
            Guid                                    staffId,
            [FromBody] UpdateTournamentStaffRequest  req,
            HttpContext                              ctx,
            IDbConnectionFactory                    db,
            CancellationToken                       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is organizer of a tournament this org staff is assigned to
            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM organization_staff os
                    JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                    JOIN tournaments t ON t.id = sta.tournament_id
                    WHERE os.id = @staffId AND t.organizer_id = @userId
                )
                """,
                new { staffId, userId = userCtx.UserIdGuid });
            if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            await conn.ExecuteAsync(
                """
                UPDATE organization_staff
                SET role = @role, permissions = @permissions::text[], updated_at = NOW()
                WHERE id = @staffId
                """,
                new { staffId, role = req.Role, permissions = req.Permissions ?? Array.Empty<string>() });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/tournaments/staff/{staffId} — remove ────────────────
        app.MapDelete("/api/tournaments/staff/{staffId}", async (
            Guid                 staffId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM organization_staff os
                    JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                    JOIN tournaments t ON t.id = sta.tournament_id
                    WHERE os.id = @staffId AND t.organizer_id = @userId
                )
                """,
                new { staffId, userId = userCtx.UserIdGuid });
            if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            // Remove tournament assignments then the org staff record
            await conn.ExecuteAsync(
                "DELETE FROM staff_tournament_assignments WHERE organization_staff_id = @staffId",
                new { staffId });
            await conn.ExecuteAsync(
                "DELETE FROM organization_staff WHERE id = @staffId",
                new { staffId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/staff/my-invites ────────────────────────────
        app.MapGet("/api/tournaments/staff/my-invites", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/staff/my-assignments ────────────────────────
        app.MapGet("/api/tournaments/staff/my-assignments", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT os.id, sta.tournament_id, os.user_id, os.role,
                       os.permissions, os.status, os.assigned_by,
                       os.created_at, os.updated_at, os.accepted_at,
                       sta.id AS organization_staff_id,
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
                JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                JOIN tournaments t ON t.id = sta.tournament_id
                LEFT JOIN profiles p ON p.id = os.assigned_by
                WHERE os.user_id = @userId AND os.status = 'active'
                ORDER BY os.updated_at DESC
                """,
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/staff/{inviteId}/respond ───────────────────
        app.MapPost("/api/tournaments/staff/{inviteId}/respond", async (
            Guid                                      inviteId,
            [FromBody] RespondToStaffInviteRequest     req,
            HttpContext                                ctx,
            IDbConnectionFactory                      db,
            CancellationToken                         ct) =>
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
                    status     = req.Accept ? "active" : "declined",
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
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
                       ) ELSE '[]'::jsonb END AS riot_accounts
                FROM tournament_disputes td
                JOIN tournaments t ON t.id = td.tournament_id
                LEFT JOIN profiles p ON p.id = td.raised_by_user_id
                LEFT JOIN teams td_team ON td_team.id = td.team_id
                LEFT JOIN brkt_matches bm ON bm.id = td.match_id
                LEFT JOIN teams t1 ON t1.id = bm.team1_id
                LEFT JOIN teams t2 ON t2.id = bm.team2_id
                WHERE (t.organizer_id = @userId
                   OR EXISTS (
                       SELECT 1 FROM organization_staff os
                       JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                       WHERE sta.tournament_id = td.tournament_id
                         AND os.user_id = @userId AND os.status = 'active'
                   ))
                  AND td.dispute_reason NOT IN ('ban_appeal', 'general_support')
                ORDER BY td.created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });

            DapperJsonbHelper.FixJsonb(rows);
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizer/disputes/{disputeId} ──────────────────────────
        app.MapGet("/api/organizer/disputes/{disputeId}", async (
            Guid                 disputeId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, status, dispute_reason, resolution_notes, updated_at FROM tournament_disputes WHERE id = @disputeId",
                new { disputeId });
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/organizer/disputes/{disputeId} ──────────────────────────
        app.MapPut("/api/organizer/disputes/{disputeId}", async (
            Guid                 disputeId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var req = await System.Text.Json.JsonSerializer.DeserializeAsync<UpdateDisputeRequest>(
                ctx.Request.Body, s_snakeCase, ct);
            if (req is null) return Results.BadRequest("Invalid body");

            using var conn = db.CreateConnection();

            // Verify caller is the tournament organizer for this dispute
            var isOwner = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tournament_disputes d
                    JOIN tournaments t ON t.id = d.tournament_id
                    WHERE d.id = @disputeId AND t.organizer_id = @userId
                )
                """,
                new { disputeId, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

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
            Guid                 disputeId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                              disputeId,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            IHubContext<NotificationHub>      notifHub,
            IHubContext<MatchHub>             matchHub,
            CancellationToken                 ct) =>
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
            Guid                                disputeId,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            IHubContext<NotificationHub>        notifHub,
            ILoggerFactory                      loggerFactory,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var req = await System.Text.Json.JsonSerializer.DeserializeAsync<ResolveDisputeRequest2>(
                ctx.Request.Body, s_snakeCase, ct);
            if (req is null) return Results.BadRequest("Invalid body");

            var logger = loggerFactory.CreateLogger("DisputeResolve");
            try
            {
            using var conn = db.CreateConnection();

            // Verify caller is the tournament organizer for this dispute
            var isOwner = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tournament_disputes d
                    JOIN tournaments t ON t.id = d.tournament_id
                    WHERE d.id = @disputeId AND t.organizer_id = @userId
                )
                """,
                new { disputeId, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            // Update dispute status
            await conn.ExecuteAsync(
                """
                UPDATE tournament_disputes
                SET status = @status::text, resolution_notes = @notes,
                    assigned_to_user_id = @userId, updated_at = NOW()
                WHERE id = @disputeId
                """,
                new { disputeId, status = req.Status, notes = req.ResolutionNotes, userId = userCtx.UserIdGuid });

            // If resolving with an accepted report: enforce scores on the match
            if (req.Status == "resolved" && req.ReportId is not null
                && Guid.TryParse(req.ReportId, out var reportId))
            {
                var report = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT mrr.match_id, mrr.team1_score, mrr.team2_score,
                           mrr.reported_by_team_id,
                           bm.team1_id, bm.team2_id
                    FROM match_result_reports mrr
                    JOIN brkt_matches bm ON bm.id = mrr.match_id
                    WHERE mrr.id = @reportId
                    """,
                    new { reportId });

                if (report is not null)
                {
                    Guid matchId = (Guid)report.match_id;
                    int t1Score  = (int)report.team1_score;
                    int t2Score  = (int)report.team2_score;

                    // Determine winner
                    Guid? winnerId = t1Score > t2Score ? (Guid?)report.team1_id
                                  : t2Score > t1Score ? (Guid?)report.team2_id
                                  : null;

                    // Enforce scores + winner on match
                    await conn.ExecuteAsync(
                        """
                        UPDATE brkt_matches
                        SET team1_score = @t1, team2_score = @t2,
                            winner_team_id = @winner, status = 'completed', updated_at = NOW()
                        WHERE id = @matchId
                        """,
                        new { t1 = t1Score, t2 = t2Score, winner = winnerId, matchId });

                    // Mark report as accepted, others for this match as rejected
                    await conn.ExecuteAsync(
                        """
                        UPDATE match_result_reports SET status = 'accepted'  WHERE id = @reportId;
                        UPDATE match_result_reports SET status = 'rejected'
                          WHERE match_id = @matchId AND id != @reportId AND status = 'disputed';
                        """,
                        new { reportId, matchId });

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
                        new { team1Id = (Guid)report.team1_id, team2Id = (Guid)report.team2_id,
                              t1Score, t2Score });

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
                                userId  = captainId,
                                message = $"The organizer has made the final call — match result: {captain.own_score} – {captain.opp_score} for your team.",
                                data    = System.Text.Json.JsonSerializer.Serialize(new { match_id = matchId, dispute_id = disputeId }),
                            });
                        await notifHub.Clients.Group($"user:{captainId}")
                            .SendAsync("NewNotification", new { type = "result_accepted" }, ct);
                    }
                }
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
                var notifType  = req.Status == "resolved" ? "dispute_resolved" : "dispute_rejected";
                var notifTitle = req.Status == "resolved"
                    ? "✅ Dispute Resolved"
                    : "❌ Dispute Rejected";
                var notifMsg   = req.Status == "resolved"
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
                        userId  = filerId,
                        type    = notifType,
                        title   = notifTitle,
                        message = notifMsg,
                        data    = System.Text.Json.JsonSerializer.Serialize(new { dispute_id = disputeId }),
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var tournaments = await conn.QueryAsync<dynamic>(
                """
                SELECT DISTINCT t.id, t.name, t.slug, t.game, t.status::text AS status,
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                 id,
            IDbConnectionFactory db,
            [FromQuery] int      limit = 50,
            [FromQuery] int      offset = 0,
            CancellationToken    ct = default) =>
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
        // Player-facing: get current user's own filed disputes
        app.MapGet("/api/disputes/mine", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
                       t.name AS tournament_name, t.slug AS tournament_slug
                FROM public.tournament_disputes td
                LEFT JOIN public.tournaments t ON t.id = td.tournament_id
                WHERE td.raised_by_user_id = @userId
                ORDER BY td.created_at DESC
                """, new { userId = userCtx.UserIdGuid });

            return Results.Ok(disputes);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/disputes/{disputeId} ────────────────────────────────────
        // Player-facing: get single dispute details (ownership check)
        app.MapGet("/api/disputes/{disputeId}", async (
            Guid                 disputeId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
                var isOrgOrStaff = await conn.QuerySingleOrDefaultAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM tournaments t
                        WHERE t.id = @tid AND (t.organizer_id = @uid OR EXISTS (
                            SELECT 1 FROM organization_staff os
                            JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                            WHERE sta.tournament_id = @tid AND os.user_id = @uid AND os.status = 'active'
                        ))
                    )
                    """, new { tid = tournamentId, uid = userCtx.UserIdGuid });
                if (!isOrgOrStaff) return Results.Forbid();
            }

            return Results.Ok(dispute);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/disputes/{disputeId}/comments ───────────────────────────
        app.MapGet("/api/disputes/{disputeId}/comments", async (
            Guid                 disputeId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                              disputeId,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            IHubContext<MatchHub>             matchHub,
            CancellationToken                 ct) =>
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
            Guid                              disputeId,
            [FromBody] UpdateDisputeRequest   req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/tournaments/{id}/bans/{banId} ─────────────────────────
        app.MapDelete("/api/tournaments/{id}/bans/{banId}", async (
            Guid                 id,
            Guid                 banId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/{id}/participants/me ─────────────────────────
        app.MapGet("/api/tournaments/{id}/participants/me", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var maps = await conn.QueryAsync<dynamic>(
                """
                SELECT gm.id::text as id, gm.game, gm.map_name, gm.map_image_url, gm.is_active
                FROM public.tournament_map_pools tmp
                JOIN public.game_maps gm ON gm.id = tmp.map_id
                WHERE tmp.tournament_id = @id
                ORDER BY gm.map_name ASC
                """, new { id });
            return Results.Ok(maps);
        });

        // ── POST /api/tournaments/{id}/map-pool ───────────────────────────────
        app.MapPost("/api/tournaments/{id}/map-pool", async (
            Guid                         id,
            [FromBody] AddMapToPoolRequest req,
            HttpContext                   ctx,
            IDbConnectionFactory          db,
            CancellationToken             ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller owns this tournament
            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            await conn.ExecuteAsync(
                "INSERT INTO tournament_map_pools (tournament_id, map_id) VALUES (@id, @mapId) ON CONFLICT DO NOTHING",
                new { id, mapId = req.MapId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/tournaments/{id}/map-pool/{mapId} ─────────────────────
        app.MapDelete("/api/tournaments/{id}/map-pool/{mapId}", async (
            Guid                 id,
            Guid                 mapId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller owns this tournament
            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            await conn.ExecuteAsync(
                "DELETE FROM tournament_map_pools WHERE tournament_id = @id AND map_id = @mapId",
                new { id, mapId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/{id}/match-reports ───────────────────────────
        app.MapGet("/api/tournaments/{id}/match-reports", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                                 id,
            [FromBody] CreateAnnouncementRequest req,
            HttpContext                          ctx,
            IDbConnectionFactory                 db,
            IHubContext<NotificationHub>          notifHub,
            CancellationToken                    ct) =>
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
            Guid                                  id,
            Guid                                  announcementId,
            [FromBody] UpdateAnnouncementRequest  req,
            HttpContext                           ctx,
            IDbConnectionFactory                  db,
            CancellationToken                     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOrganizer = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOrganizer) return Results.Forbid();

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
            Guid                 id,
            Guid                 announcementId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOrganizer = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND organizer_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOrganizer) return Results.Forbid();

            var rows = await conn.ExecuteAsync(
                "DELETE FROM tournament_announcements WHERE id = @announcementId AND tournament_id = @id",
                new { announcementId, id });
            return rows == 0
                ? Results.NotFound(new { error = "Announcement not found" })
                : Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournament-participants/{id} ─────────────────────────────
        app.MapGet("/api/tournament-participants/{id}", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            HttpContext                     ctx,
            IDbConnectionFactory            db,
            Esportra.Core.Alerts.AdminAlertService alertService,
            CancellationToken               ct) =>
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
                    userId       = userCtx.UserIdGuid,
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
            HttpContext                    ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>(ct);
            if (body is null) return Results.BadRequest("Invalid body");

            Guid disputeId = body.TryGetValue("dispute_id", out var did) ? did.GetGuid()
                           : body.TryGetValue("disputeId", out did) ? did.GetGuid()
                           : body.TryGetValue("DisputeId", out did) ? did.GetGuid() : Guid.Empty;
            if (disputeId == Guid.Empty) return Results.BadRequest(new { error = "dispute_id is required" });

            string? type    = body.TryGetValue("type", out var tv) ? tv.GetString() : null;
            string? title   = body.TryGetValue("title", out var ttl) ? ttl.GetString() : null;
            string? message = body.TryGetValue("message", out var msg) ? msg.GetString() : null;
            string? link    = body.TryGetValue("link", out var lnk) ? lnk.GetString() : null;

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
                    adminIds  = adminIds.ToArray(),
                    type      = type ?? "dispute_filed",
                    title     = title ?? "🚨 New Dispute Filed",
                    message   = message ?? "A new dispute requires admin review and resolution.",
                    link      = link ?? "",
                    disputeId,
                });
            return Results.Ok(new { notified = adminIds.Count });
        }).RequireAuthorization("Authenticated");
    }

    // ── GET /api/tournaments/by-slug/{slug} — fetch by slug or id ────────────
    // Replaces ManageBracketPage's supabase.from('tournaments') call
    // Already handled by GET /api/tournaments/{id} if id-based,
    // but ManageBracketPage needs slug support with org join.

    // ── POST /api/profiles/resolve-players — batch resolve player tags ───────
    // Replaces TournamentManage's 4 parallel profile lookups

    // ── POST /api/rosters/{rosterId}/members — get roster members via RPC ────
    // Replaces TournamentManage's supabase.rpc('get_roster_members')
}

// ── Request records ───────────────────────────────────────────────────────────

public sealed record CreateTournamentRequest(
    string     Name,
    string     Game,
    DateTime   StartDate,
    int        MaxTeams,
    string?    Description          = null,
    string?    Slug                 = null,
    string?    Format               = null,
    int?       TeamSize             = null,
    decimal?   EntryFee             = null,
    decimal?   PrizePool            = null,
    DateTime?  EndDate              = null,
    DateTime?  RegistrationDeadline = null,
    string?    Status               = null,
    string?    BannerUrl            = null,
    string?    LogoUrl              = null,
    string?    OrganizationId       = null,
    string?    VenueId              = null,
    string?    Region               = null,
    bool?      IsPublic             = true,
    bool?      CheckInRequired      = false,
    DateTime?  CheckInDeadline      = null,
    bool?      AutoRemoveUnchecked  = false,
    string?    Rewards              = null,
    string?    StreamUrl            = null,
    object?    Settings             = null,
    List<StageRequest>?  Stages     = null,
    string?       Rules             = null,
    List<string>? MapPoolIds        = null,
    string?    PaymentInstructions  = null,
    string?    Currency             = null,
    string?    ServerRegion         = null,
    string?    TournamentType       = null);

public sealed record StageRequest(
    string  Name,
    string  Format,
    int?    StageOrder       = null,
    int?    BestOf           = 1,
    int?    Capacity         = null,
    int?    AdvancementCount = null);

public sealed record UpdateTournamentRequest(
    string?   Name                 = null,
    string?   Description          = null,
    string?   Game                 = null,
    string?   Status               = null,
    int?      MaxTeams             = null,
    decimal?  EntryFee             = null,
    decimal?  PrizePool            = null,
    DateTime? StartDate            = null,
    DateTime? EndDate              = null,
    DateTime? RegistrationDeadline = null,
    string?   BannerUrl            = null,
    string?   LogoUrl              = null,
    string?   Region               = null,
    bool?     IsPublic             = null,
    bool?     CheckInRequired      = null,
    DateTime? CheckInDeadline      = null,
    string?   Rewards              = null,
    string?   StreamUrl            = null,
    string?   Rules                = null,
    DateTime? DeletedAt            = null,
    bool      ClearDeletedAt       = false,
    object?   Settings             = null,
    string?   PaymentInstructions  = null,
    string?   Currency             = null,
    string?   WinnerTeamName       = null);

public sealed record RegisterTournamentRequest(
    string? TeamId            = null,
    string? ParticipantType   = null,
    string? TeamCaptainId     = null,
    string? TeamName          = null,
    string? TeamMembers       = null,
    string? RosterId          = null,
    string? RosterName        = null,
    string? TeamContactEmail  = null,
    string? Status            = null,
    decimal? EntryFeeAmount   = null,
    bool?   EntryFeePaid      = null,
    string? PaymentReceiptUrl = null);
public sealed record UpdateBannerRequest(string? Url);
public sealed record PaymentRejectionRequest(string? Reason = null);

// ── Organizer Dispute request records ────────────────────────────────────────

public sealed record AddDisputeCommentRequest(string Comment, bool IsInternal = false, string? AttachmentUrl = null);
public sealed record UpdateDisputeRequest(string? Status = null, string? UpdatedAt = null, string? AssignedToUserId = null, string? ResolutionNotes = null);
public sealed record ResolveDisputeRequest2(string Status, string? ResolutionNotes = null, string? ReportId = null);
public sealed record BanParticipantRequest(string ParticipantId, string? UserId = null, string? BanReason = null);
public sealed record AddMapToPoolRequest(Guid MapId);
public sealed record CreateAnnouncementRequest(string Title, string Content);
public sealed record UpdateAnnouncementRequest(string? Title = null, string? Content = null);
public sealed record CreateDisputeRequest(
    Guid    TournamentId,
    string  Title,
    string  Description,
    Guid?   MatchId     = null,
    Guid?   TeamId      = null,
    string? EvidenceUrl = null,
    string? Reason      = null);
public sealed record NotifyAdminsRequest(
    Guid DisputeId,
    string? Message = null,
    string? Type    = null,
    string? Title   = null,
    string? Link    = null);

// ── Tournament Staff request records ─────────────────────────────────────────

public sealed record InviteTournamentStaffRequest(
    string    UserEmail,
    string    Role,
    string[]? Permissions = null);

public sealed record UpdateTournamentStaffRequest(
    string    Role,
    string[]? Permissions = null);

public sealed record RespondToStaffInviteRequest(bool Accept);

// ── BR Game Data request records ─────────────────────────────────────────────

public sealed record BRGameDataRequest(JsonElement Games);

public sealed record BRSubmitEvidenceRequest(
    int     GameNumber,
    string  TeamId,
    string  ImageUrl,
    int?    Placement = null,
    int?    Kills     = null);

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
