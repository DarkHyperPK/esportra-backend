using System.Data;
using System.Text.Json;
using Dapper;
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
                        LEFT JOIN organizations o ON o.id = t.organization_id
                        LEFT JOIN profiles      p ON p.id = t.organizer_id
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
                       (SELECT COUNT(*) FROM tournament_participants tp WHERE tp.tournament_id = t.id AND tp.status = 'checked_in') AS checked_in_count,
                       o.name       AS organization_name, o.slug AS organization_slug,
                       o.logo_url   AS organization_logo,  o.owner_id AS organization_owner_id,
                       p.username   AS organizer_username,  p.avatar_url AS organizer_avatar,
                       v.name       AS venue_name
                FROM tournaments t
                LEFT JOIN organizations o ON o.id = t.organization_id
                LEFT JOIN profiles      p ON p.id = t.organizer_id
                LEFT JOIN venues        v ON v.id = t.venue_id
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
            var participants = await conn.QueryAsync<dynamic>(
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
            bool isOrganizer = userCtx is not null && userCtx.UserIdGuid == organizerId;

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
                    new { tid = tournamentId, userId = userCtx.UserIdGuid })).ToArray();
            }

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
            IDistributedCache                 distCache,
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
                        organizerId          = userCtx.UserIdGuid,
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

                // Invalidate tournament list cache (HybridCache doesn't support wildcards)
                // Remove known cache key patterns explicitly
                try { await distCache.RemoveAsync("tournaments:::::50:0", ct); } catch { /* best effort */ }

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
            IDistributedCache             distCache,
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

            try { await distCache.RemoveAsync("tournaments:::::50:0", ct); } catch { /* best effort */ }
            return updated is null ? Results.NotFound() : Results.Ok(updated);
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
                        WHERE tp.user_id = @userId OR tp.team_id = ANY(@teamIds)
                        """,
                        new { userId = userCtx.UserIdGuid, teamIds });
                }
                else
                {
                    rows = await conn.QueryAsync(
                        "SELECT id, tournament_id FROM tournament_participants WHERE user_id = @userId",
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
                      AND (user_id = @userId OR team_id = ANY(@teamIds))
                    """,
                    new { ids = idList, userId = userCtx.UserIdGuid, teamIds });
            }
            else
            {
                registeredIdGuids = await conn.QueryAsync<Guid>(
                    "SELECT tournament_id FROM tournament_participants WHERE tournament_id = ANY(@ids) AND user_id = @userId",
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

            // Guard: tournament must be open and not at capacity
            var tourn = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT status, max_teams FROM tournaments WHERE id = @id", new { id });
            if (tourn is null)    return Results.NotFound();
            if ((string)tourn.status != "open")
                return Results.BadRequest(new { error = "Tournament is not accepting registrations." });

            // Check capacity
            int? maxTeams = (int?)tourn.max_teams;
            if (maxTeams.HasValue)
            {
                var currentCount = await conn.QuerySingleAsync<int>(
                    "SELECT COUNT(*) FROM tournament_participants WHERE tournament_id = @id", new { id });
                if (currentCount >= maxTeams.Value)
                    return Results.BadRequest(new { error = "Tournament has reached maximum capacity." });
            }

            // Check existing registration (no unique constraint, so check manually)
            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id FROM tournament_participants WHERE tournament_id = @id AND user_id = @userId",
                new { id, userId = userCtx.UserIdGuid });
            if (existing is not null)
                return Results.Conflict(new { error = "You are already registered for this tournament." });

            Guid? teamIdGuid = req.TeamId is not null ? Guid.Parse(req.TeamId) : null;

            var row = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO tournament_participants
                    (tournament_id, user_id, team_id, status, participant_type)
                VALUES (@tournamentId, @userId, @teamId, 'registered', @participantType)
                RETURNING *
                """,
                new
                {
                    tournamentId    = id,
                    userId          = userCtx.UserIdGuid,
                    teamId          = teamIdGuid,
                    participantType = teamIdGuid is not null ? "team" : "solo",
                });

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
            await conn.ExecuteAsync(
                "DELETE FROM tournament_participants WHERE tournament_id = @id AND (user_id = @userId OR team_captain_id = @userId)",
                new { id, userId = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{id}/check-in ────────────────────────────────
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
                  AND status = 'registered'
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

            // 4. Get user's registration (solo or via team)
            var registration = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT tp.*, t.name AS team_name, t.logo_url AS team_logo
                FROM tournament_participants tp
                LEFT JOIN teams t ON t.id = tp.team_id
                WHERE tp.tournament_id = @id
                  AND (tp.user_id = @userId OR tp.team_captain_id = @userId
                       OR (tp.team_id = ANY(@teamIds) AND tp.participant_type = 'team'))
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
                       p.username, p.full_name, p.avatar_url, p.riot_tag, p.faceit_nickname
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
            string               id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var removed = await conn.ExecuteAsync(
                """
                UPDATE tournament_participants
                SET status = 'cancelled'
                WHERE tournament_id = @id
                  AND checked_in_at IS NULL
                  AND status IN ('pending', 'approved', 'registered')
                """,
                new { id });

            return Results.Ok(new { removedCount = removed });
        }).RequireAuthorization("Organizer");

        // ── POST /api/tournaments/{id}/ban-participant ──────────────────────────
        app.MapPost("/api/tournaments/{id}/ban-participant", async (
            string                               id,
            [FromBody] BanParticipantRequest     req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Get participant info
            var participant = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT user_id, team_id FROM tournament_participants WHERE id = @pid",
                new { pid = req.ParticipantId });
            if (participant is null)
                return Results.NotFound(new { error = "Participant not found" });

            // Determine ban target
            string? banUserId = null, banTeamId = null;
            if (participant.team_id is not null) banTeamId = participant.team_id;
            else if (participant.user_id is not null) banUserId = participant.user_id;
            else banUserId = req.UserId;

            if (banUserId is null && banTeamId is null)
                return Results.BadRequest(new { error = "Cannot determine ban target" });

            // Insert ban
            await conn.ExecuteAsync(
                """
                INSERT INTO tournament_bans (tournament_id, participant_id, user_id, team_id, ban_reason, banned_by, banned_at, is_active)
                VALUES (@tournamentId, @participantId, @userId, @teamId, @banReason, @bannedBy, NOW(), TRUE)
                """,
                new { tournamentId = id, participantId = req.ParticipantId, userId = banUserId, teamId = banTeamId, banReason = req.BanReason, bannedBy = userCtx.UserIdGuid });

            // Delete registration
            string? deleteError = null;
            try
            {
                await conn.ExecuteAsync(
                    "DELETE FROM tournament_participants WHERE id = @pid",
                    new { pid = req.ParticipantId });
            }
            catch (Exception ex)
            {
                deleteError = ex.Message;
            }

            return Results.Ok(new { success = true, deleteError });
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/{id}/participants/{pid} — single participant ───
        app.MapGet("/api/tournaments/{id}/participants/{pid}", async (
            string               id,
            string               pid,
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
            string               id,
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
            string               id,
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
            string               id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT * FROM tournament_participants WHERE tournament_id = @id",
                new { id });
            return Results.Ok(rows);
        });

        // ── GET /api/tournaments/{id}/match-proofs ───────────────────────────
        app.MapGet("/api/tournaments/{id}/match-proofs", async (
            string               id,
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
            string               id,
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
            return Results.Ok(rows);
        });

        // ── GET /api/teams — search teams by name ───────────────────────────────
        app.MapGet("/api/teams", async (
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
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tournament Staff
    // ══════════════════════════════════════════════════════════════════════════

    private static void MapTournamentStaffEndpoints(WebApplication app)
    {
        // ── GET /api/tournaments/{tournamentId}/staff ────────────────────────
        app.MapGet("/api/tournaments/{tournamentId}/staff", async (
            string               tournamentId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Only organizer or existing staff can view staff list
            var hasAccess = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tournaments WHERE id = @tid AND organizer_id = @userId
                    UNION ALL
                    SELECT 1 FROM tournament_staff WHERE tournament_id = @tid AND user_id = @userId AND status = 'active'
                )
                """,
                new { tid = tournamentId, userId = userCtx.UserIdGuid });
            if (!hasAccess && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT ts.*,
                       jsonb_build_object(
                           'full_name', p.full_name,
                           'username', p.username,
                           'email', p.email,
                           'avatar_url', p.avatar_url
                       ) AS profiles
                FROM tournament_staff ts
                LEFT JOIN profiles p ON p.id = ts.user_id
                WHERE ts.tournament_id = @tournamentId
                ORDER BY ts.created_at ASC
                """,
                new { tournamentId });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{tournamentId}/staff — invite ──────────────
        app.MapPost("/api/tournaments/{tournamentId}/staff", async (
            string                                tournamentId,
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

            // Check for existing staff record
            var existing = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT id FROM tournament_staff WHERE tournament_id = @tid AND user_id = @userId",
                new { tid = tournamentId, userId });

            var permissionsJson = JsonSerializer.Serialize(req.Permissions ?? Array.Empty<string>());

            if (existing is not null)
            {
                // Re-invite: update existing record
                await conn.ExecuteAsync(
                    """
                    UPDATE tournament_staff
                    SET role = @role, permissions = @permissions::jsonb,
                        assigned_by = @assignedBy, status = 'pending',
                        accepted_at = NULL, responded_at = NULL, updated_at = NOW()
                    WHERE id = @id
                    """,
                    new { id = existing, role = req.Role, permissions = permissionsJson,
                          assignedBy = userCtx.UserIdGuid });
            }
            else
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO tournament_staff
                        (tournament_id, user_id, role, permissions, assigned_by, status)
                    VALUES
                        (@tournamentId, @userId, @role, @permissions::jsonb, @assignedBy, 'pending')
                    """,
                    new { tournamentId, userId, role = req.Role, permissions = permissionsJson,
                          assignedBy = userCtx.UserIdGuid });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── PUT /api/tournaments/staff/{staffId} — update role/permissions ───
        app.MapPut("/api/tournaments/staff/{staffId}", async (
            string                                  staffId,
            [FromBody] UpdateTournamentStaffRequest  req,
            HttpContext                              ctx,
            IDbConnectionFactory                    db,
            CancellationToken                       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is organizer of the tournament that owns this staff record
            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tournament_staff ts
                    JOIN tournaments t ON t.id = ts.tournament_id
                    WHERE ts.id = @staffId AND t.organizer_id = @userId
                )
                """,
                new { staffId, userId = userCtx.UserIdGuid });
            if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            var permissionsJson = JsonSerializer.Serialize(req.Permissions ?? Array.Empty<string>());
            await conn.ExecuteAsync(
                """
                UPDATE tournament_staff
                SET role = @role, permissions = @permissions::jsonb, updated_at = NOW()
                WHERE id = @staffId
                """,
                new { staffId, role = req.Role, permissions = permissionsJson });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/tournaments/staff/{staffId} — remove ────────────────
        app.MapDelete("/api/tournaments/staff/{staffId}", async (
            string               staffId,
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
                    SELECT 1 FROM tournament_staff ts
                    JOIN tournaments t ON t.id = ts.tournament_id
                    WHERE ts.id = @staffId AND t.organizer_id = @userId
                )
                """,
                new { staffId, userId = userCtx.UserIdGuid });
            if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            await conn.ExecuteAsync(
                "DELETE FROM tournament_staff WHERE id = @staffId",
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
                SELECT ts.*,
                       jsonb_build_object(
                           'id', t.id, 'name', t.name, 'game', t.game,
                           'start_date', t.start_date
                       ) AS tournament,
                       jsonb_build_object(
                           'full_name', p.full_name,
                           'username', p.username,
                           'email', p.email
                       ) AS organizer_profile
                FROM tournament_staff ts
                JOIN tournaments t ON t.id = ts.tournament_id
                LEFT JOIN profiles p ON p.id = ts.assigned_by
                WHERE ts.user_id = @userId AND ts.status = 'pending'
                ORDER BY ts.created_at DESC
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
                SELECT ts.*,
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
                FROM tournament_staff ts
                JOIN tournaments t ON t.id = ts.tournament_id
                LEFT JOIN profiles p ON p.id = ts.assigned_by
                WHERE ts.user_id = @userId AND ts.status = 'active'
                ORDER BY ts.updated_at DESC
                """,
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/staff/{inviteId}/respond ───────────────────
        app.MapPost("/api/tournaments/staff/{inviteId}/respond", async (
            string                                    inviteId,
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
                "SELECT user_id FROM tournament_staff WHERE id = @inviteId AND status = 'pending'",
                new { inviteId });
            if (inviteUserId is null) return Results.NotFound(new { error = "Invite not found or already responded." });
            if (inviteUserId != userCtx.UserId) return Results.Forbid();

            var now = DateTime.UtcNow;
            await conn.ExecuteAsync(
                """
                UPDATE tournament_staff
                SET status = @status, accepted_at = @acceptedAt, responded_at = @respondedAt
                WHERE id = @inviteId AND status = 'pending'
                """,
                new
                {
                    inviteId,
                    status     = req.Accept ? "active" : "revoked",
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
                SELECT td.id, td.title, td.description, td.status, td.dispute_reason,
                       td.resolution_notes, td.evidence_url, td.created_at, td.updated_at,
                       td.tournament_id, td.match_id, td.raised_by_user_id,
                       t.name AS tournament_name,
                       COALESCE(p.full_name, p.username, 'Unknown') AS raised_by_name,
                       CASE WHEN bm.id IS NOT NULL THEN jsonb_build_object(
                           'match_number', bm.match_number,
                           'round_index', bm.round_index,
                           'best_of', bm.best_of,
                           'bracket_type', bm.bracket_type,
                           'scheduled_time', bm.scheduled_time,
                           'team1_score', bm.team1_score,
                           'team2_score', bm.team2_score,
                           'team1_name', t1.name,
                           'team2_name', t2.name
                       ) ELSE NULL END AS match
                FROM tournament_disputes td
                JOIN tournaments t ON t.id = td.tournament_id
                LEFT JOIN profiles p ON p.id = td.raised_by_user_id
                LEFT JOIN brkt_matches bm ON bm.id = td.match_id
                LEFT JOIN teams t1 ON t1.id = bm.team1_id
                LEFT JOIN teams t2 ON t2.id = bm.team2_id
                WHERE t.organizer_id = @userId
                   OR EXISTS (
                       SELECT 1 FROM tournament_staff ts
                       WHERE ts.tournament_id = td.tournament_id
                         AND ts.user_id = @userId AND ts.status = 'active'
                   )
                ORDER BY td.created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizer/disputes/{disputeId}/comments ─────────────────
        app.MapGet("/api/organizer/disputes/{disputeId}/comments", async (
            string               disputeId,
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
            string                            disputeId,
            [FromBody] AddDisputeCommentRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            IHubContext<NotificationHub>      notifHub,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Insert comment
            await conn.ExecuteAsync(
                """
                INSERT INTO dispute_comments (dispute_id, user_id, comment, is_internal, attachment_url)
                VALUES (@disputeId, @userId, @comment, FALSE, @attachmentUrl)
                """,
                new { disputeId, userId = userCtx.UserIdGuid, comment = req.Comment ?? "", attachmentUrl = req.AttachmentUrl });

            // Auto-promote to in_review if open
            var currentStatus = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT status FROM tournament_disputes WHERE id = @disputeId",
                new { disputeId });

            if (currentStatus == "open")
            {
                await conn.ExecuteAsync(
                    "UPDATE tournament_disputes SET status = 'in_review', updated_at = NOW() WHERE id = @disputeId",
                    new { disputeId });
            }
            else
            {
                await conn.ExecuteAsync(
                    "UPDATE tournament_disputes SET updated_at = NOW() WHERE id = @disputeId",
                    new { disputeId });
            }

            return Results.Ok(new { success = true, autoPromoted = currentStatus == "open" });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizer/disputes/{disputeId}/resolve ─────────────────
        app.MapPost("/api/organizer/disputes/{disputeId}/resolve", async (
            string                              disputeId,
            [FromBody] ResolveDisputeRequest2   req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            IHubContext<NotificationHub>        notifHub,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Update dispute
            await conn.ExecuteAsync(
                """
                UPDATE tournament_disputes
                SET status = @status, resolution_notes = @notes,
                    assigned_to_user_id = @userId, updated_at = NOW()
                WHERE id = @disputeId
                """,
                new { disputeId, status = req.Status, notes = req.ResolutionNotes, userId = userCtx.UserIdGuid });

            // Send notification to filer
            var dispute = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT raised_by_user_id, title FROM tournament_disputes WHERE id = @disputeId",
                new { disputeId });

            if (dispute is not null)
            {
                string filerId = dispute.raised_by_user_id;
                string title = dispute.title ?? "Your dispute";
                var notifType = req.Status == "resolved" ? "dispute_resolved" : "dispute_rejected";
                var notifTitle = req.Status == "resolved" ? "Dispute Resolved" : "Dispute Rejected";
                var notifMsg = req.Status == "resolved"
                    ? $"Your dispute \"{title}\" has been resolved by the organizer."
                    : $"Your dispute \"{title}\" has been rejected by the organizer.";

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

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Additional Organizer Utility Endpoints
    // ══════════════════════════════════════════════════════════════════════════

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
public sealed record UpdateBannerRequest(string Url);

// ── Organizer Dispute request records ────────────────────────────────────────

public sealed record AddDisputeCommentRequest(string Comment, string? AttachmentUrl = null);
public sealed record ResolveDisputeRequest2(string Status, string? ResolutionNotes = null);
public sealed record BanParticipantRequest(string ParticipantId, string? UserId = null, string? BanReason = null);

// ── Tournament Staff request records ─────────────────────────────────────────

public sealed record InviteTournamentStaffRequest(
    string    UserEmail,
    string    Role,
    string[]? Permissions = null);

public sealed record UpdateTournamentStaffRequest(
    string    Role,
    string[]? Permissions = null);

public sealed record RespondToStaffInviteRequest(bool Accept);
