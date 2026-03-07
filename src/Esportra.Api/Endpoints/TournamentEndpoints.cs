using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
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
    public static void MapTournamentEndpoints(this WebApplication app)
    {
        // ── GET /api/tournaments ───────────────────────────────────────────────
        // Replaces useTournaments N+1: participant count in a correlated subquery.
        app.MapGet("/api/tournaments", async (
            string?              status,
            string?              game,
            string?              q,
            int                  limit  = 50,
            int                  offset = 0,
            IDbConnectionFactory db     = null!,
            HybridCache          cache  = null!,
            CancellationToken    ct     = default) =>
        {
            var cacheKey = $"tournaments:{status}:{game}:{q}:{limit}:{offset}";
            return await cache.GetOrCreateAsync(
                cacheKey,
                async (_) =>
                {
                    using var conn = db.CreateConnection();

                    var rows = await conn.QueryAsync<dynamic>(
                        """
                        SELECT t.id, t.name, t.slug, t.game, t.status, t.format,
                               t.start_date, t.end_date, t.registration_deadline,
                               t.max_teams, t.min_teams, t.team_size,
                               t.entry_fee, t.prize_pool,
                               t.banner_url, t.logo_url, t.is_public,
                               t.organizer_id, t.venue_id, t.description,
                               t.created_at, t.updated_at,
                               (SELECT COUNT(*) FROM tournament_participants tp
                                WHERE tp.tournament_id = t.id) AS current_participants,
                               o.name   AS organizer_name,
                               o.slug   AS organization_slug,
                               p.username      AS organizer_username,
                               p.full_name     AS organizer_full_name
                        FROM tournaments t
                        LEFT JOIN organizations o ON o.owner_id = t.organizer_id
                        LEFT JOIN profiles      p ON p.id       = t.organizer_id
                        WHERE t.is_public = TRUE
                          AND t.deleted_at IS NULL
                          AND (@status IS NULL OR t.status = @status)
                          AND (@game   IS NULL OR t.game   ILIKE '%' || @game || '%')
                          AND (@q      IS NULL OR t.name   ILIKE '%' || @q   || '%')
                        ORDER BY t.start_date ASC
                        LIMIT @limit OFFSET @offset
                        """,
                        new { status, game, q, limit, offset });

                    return Results.Ok(rows);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                cancellationToken: ct);
        });

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
                       (SELECT COUNT(*) FROM tournament_participants tp WHERE tp.tournament_id = t.id) AS current_participants,
                       o.name  AS organizer_name, o.slug AS organization_slug,
                       p.username AS organizer_username
                FROM tournaments t
                LEFT JOIN organizations o ON o.owner_id = t.organizer_id
                LEFT JOIN profiles      p ON p.id       = t.organizer_id
                WHERE t.deleted_at IS NULL
                  AND (t.slug = @slugOrId
                    OR t.id::text = @slugOrId
                    OR t.slug ILIKE @slugOrId)
                LIMIT 1
                """,
                new { slugOrId });

            if (tournament is null) return Results.NotFound();

            string tournamentId = tournament.id;

            // Participants + stages in parallel
            var participantsTask = conn.QueryAsync<dynamic>(
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

            var stagesTask = conn.QueryAsync<dynamic>(
                "SELECT * FROM tournament_stages WHERE tournament_id = @tournamentId ORDER BY stage_order",
                new { tournamentId });

            await Task.WhenAll(participantsTask, stagesTask);

            // Permission check for organizer/staff
            var userCtx = ctx.Items["UserContext"] as UserContext;
            bool isOrganizer = userCtx is not null && userCtx.UserId == (string?)tournament.organizer_id;

            string[]? staffPermissions = null;
            if (userCtx is not null && !isOrganizer)
            {
                staffPermissions = (await conn.QueryAsync<string>(
                    """
                    SELECT unnest(permissions) FROM organization_staff
                    WHERE organization_id = (SELECT organization_id FROM tournaments WHERE id = @tid)
                      AND user_id = @userId AND is_active = TRUE
                    UNION
                    SELECT unnest(permissions) FROM tournament_staff
                    WHERE tournament_id = @tid AND user_id = @userId AND is_active = TRUE
                    """,
                    new { tid = tournamentId, userId = userCtx.UserId })).ToArray();
            }

            return Results.Ok(new
            {
                tournament,
                participants    = await participantsTask,
                stages          = await stagesTask,
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
                        rewards, stream_url, settings, organizer_id
                    ) VALUES (
                        @name, @description, @slug, @game, @format, @maxTeams, 2, @teamSize,
                        @entryFee, @prizePool, @startDate, @endDate, @registrationDeadline,
                        'draft', @bannerUrl, @logoUrl, @organizationId, @venueId, @isPublic,
                        @checkInRequired, @checkInDeadline, @autoRemoveUnchecked,
                        @rewards, @streamUrl, @settings::jsonb, @organizerId
                    )
                    RETURNING *
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
                        bannerUrl            = req.BannerUrl,
                        logoUrl              = req.LogoUrl,
                        organizationId       = req.OrganizationId,
                        venueId              = req.VenueId,
                        isPublic             = req.IsPublic ?? true,
                        checkInRequired      = req.CheckInRequired ?? false,
                        checkInDeadline      = req.CheckInDeadline,
                        autoRemoveUnchecked  = req.AutoRemoveUnchecked ?? false,
                        rewards              = req.Rewards,
                        streamUrl            = req.StreamUrl,
                        settings             = req.Settings is not null
                            ? JsonSerializer.Serialize(req.Settings)
                            : "{}",
                        organizerId          = userCtx.UserId,
                    },
                    tx);

                string tournamentId = tournament.id;

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
                        req.MapPoolIds.Select(m => new { tournamentId, mapId = m }),
                        tx);
                }

                tx.Commit();

                // Invalidate tournament list cache
                await cache.RemoveAsync("tournaments:*", ct);

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
            string                        id,
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
            var organizerId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT organizer_id FROM tournaments WHERE id = @id", new { id });
            if (organizerId is null) return Results.NotFound();
            if (organizerId != userCtx.UserId && !userCtx.Roles.Contains("admin"))
                return Results.Forbid();

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE tournaments SET
                    name                 = COALESCE(@name, name),
                    description          = COALESCE(@description, description),
                    game                 = COALESCE(@game, game),
                    status               = COALESCE(@status, status),
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
                    updated_at           = NOW()
                WHERE id = @id
                RETURNING *
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
                });

            await cache.RemoveAsync($"tournaments:*", ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/me/registration-status ────────────────────────
        // Batch: ?ids=id1,id2,id3  →  { id1: true, id2: false, ... }
        // Replaces useTournamentRegistrationStatus N+1 effect.
        app.MapGet("/api/tournaments/me/registration-status", async (
            string               ids,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (idList.Length == 0) return Results.Ok(new { });

            using var conn = db.CreateConnection();

            // Get the user's team IDs first
            var teamIds = (await conn.QueryAsync<string>(
                "SELECT team_id FROM team_members WHERE user_id = @userId AND is_active = TRUE",
                new { userId = userCtx.UserId })).ToArray();

            // Single query: registered tournament IDs via user_id OR any team
            IEnumerable<string> registeredIds;
            if (teamIds.Length > 0)
            {
                registeredIds = await conn.QueryAsync<string>(
                    """
                    SELECT DISTINCT tournament_id FROM tournament_participants
                    WHERE tournament_id = ANY(@ids)
                      AND (user_id = @userId OR team_id = ANY(@teamIds))
                    """,
                    new { ids = idList, userId = userCtx.UserId, teamIds });
            }
            else
            {
                registeredIds = await conn.QueryAsync<string>(
                    "SELECT tournament_id FROM tournament_participants WHERE tournament_id = ANY(@ids) AND user_id = @userId",
                    new { ids = idList, userId = userCtx.UserId });
            }

            var registeredSet = registeredIds.ToHashSet();
            var statusMap     = idList.ToDictionary(id => id, id => registeredSet.Contains(id));

            return Results.Ok(statusMap);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/register ────────────────────────────────
        app.MapPost("/api/tournaments/{id}/register", async (
            string                           id,
            [FromBody] RegisterTournamentRequest req,
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Guard: tournament must be open
            var tournStatus = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT status FROM tournaments WHERE id = @id", new { id });
            if (tournStatus is null)    return Results.NotFound();
            if (tournStatus != "open")  return Results.BadRequest(new { error = "Tournament is not accepting registrations." });

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO tournament_participants
                    (tournament_id, user_id, team_id, status, participant_type)
                VALUES (@tournamentId, @userId, @teamId, 'registered', @participantType)
                ON CONFLICT (tournament_id, user_id) DO NOTHING
                RETURNING *
                """,
                new
                {
                    tournamentId    = id,
                    userId          = userCtx.UserId,
                    teamId          = req.TeamId,
                    participantType = req.TeamId is not null ? "team" : "solo",
                });

            return Results.Ok(row);
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/tournaments/{id}/register — withdraw ──────────────────
        app.MapDelete("/api/tournaments/{id}/register", async (
            string               id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM tournament_participants WHERE tournament_id = @id AND user_id = @userId",
                new { id, userId = userCtx.UserId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }

    // ── Slug helper ───────────────────────────────────────────────────────────
    // Simple slug: lowercase, replace non-alphanumeric with hyphens, collapse.
    private static string Slugify(string input) =>
        System.Text.RegularExpressions.Regex.Replace(
            input.ToLowerInvariant().Trim(),
            @"[^a-z0-9]+", "-").Trim('-');
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
    string?    BannerUrl            = null,
    string?    LogoUrl              = null,
    string?    OrganizationId       = null,
    string?    VenueId              = null,
    bool?      IsPublic             = true,
    bool?      CheckInRequired      = false,
    DateTime?  CheckInDeadline      = null,
    bool?      AutoRemoveUnchecked  = false,
    string?    Rewards              = null,
    string?    StreamUrl            = null,
    object?    Settings             = null,
    List<StageRequest>?  Stages     = null,
    List<string>? MapPoolIds        = null);

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
    bool?     IsPublic             = null,
    bool?     CheckInRequired      = null,
    DateTime? CheckInDeadline      = null,
    string?   Rewards              = null,
    string?   StreamUrl            = null);

public sealed record RegisterTournamentRequest(string? TeamId = null);
