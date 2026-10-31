using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Services;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Esportra.Api.Middleware;
using Esportra.Api.ScoreboardOcr;
using Esportra.Infrastructure.Integrations;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Scrims and Practice (PROJ-068 Phase 4).
///
/// Scrim listings:
/// POST   /api/scrims/listings                       — create listing
/// GET    /api/scrims/listings?team_id={id}          — team's listings (staff)
/// PUT    /api/scrims/listings/{id}                  — update listing
/// POST   /api/scrims/listings/{id}/pause            — pause
/// POST   /api/scrims/listings/{id}/close            — close
/// GET    /api/scrims/discover?game={slug}&...       — public discovery
///
/// Scrim offers:
/// POST   /api/scrims/listings/{id}/offer            — make offer
/// GET    /api/scrims/listings/{id}/offers           — offers for a listing (staff)
/// GET    /api/scrims/offers/mine?team_id={id}       — offers sent by team
/// POST   /api/scrims/offers/{id}/accept             — accept offer (creates session)
/// POST   /api/scrims/offers/{id}/decline            — decline offer
/// POST   /api/scrims/offers/{id}/withdraw           — withdraw offer (offering team)
///
/// Scrim sessions:
/// GET    /api/scrims/sessions?team_id={id}          — team's sessions
/// GET    /api/scrims/sessions/{id}                  — session detail
/// POST   /api/scrims/sessions/{id}/start            — start
/// POST   /api/scrims/sessions/{id}/complete         — complete
/// POST   /api/scrims/sessions/{id}/cancel           — cancel
/// POST   /api/scrims/sessions/{id}/report-result    — report result
/// GET    /api/scrims/sessions/{id}/results          — result reports
/// POST   /api/scrims/sessions/{id}/dispute          — open dispute
///
/// Practice:
/// POST   /api/practice/sessions                     — create practice session
/// GET    /api/practice/sessions?team_id={id}        — team's practice sessions
/// GET    /api/practice/sessions/{id}                — detail
/// PUT    /api/practice/sessions/{id}                — update
/// POST   /api/practice/sessions/{id}/complete       — mark complete
/// POST   /api/practice/sessions/{id}/cancel         — cancel
/// GET    /api/practice/sessions/{id}/attendance     — list attendance
/// PUT    /api/practice/sessions/{id}/attendance     — upsert attendance for all members
/// PUT    /api/practice/attendance/{id}              — update own attendance status
/// </summary>
public static class ScrimEndpoints
{
    public static void MapScrimEndpoints(this WebApplication app)
    {
        // ── Scrim Listings ──────────────────────────────────────────────────────
        app.MapPost("/api/scrims/listings", CreateListingAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/listings", GetTeamListingsAsync).RequireAuthorization("Authenticated");
        app.MapPut("/api/scrims/listings/{id:guid}", UpdateListingAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/listings/{id:guid}/pause", PauseListingAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/listings/{id:guid}/reopen", ReopenListingAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/listings/{id:guid}/close", CloseListingAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/discover", DiscoverAsync).AllowAnonymous();

        // ── Scrim Offers ────────────────────────────────────────────────────────
        app.MapPost("/api/scrims/listings/{id:guid}/offer", MakeOfferAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/listings/{id:guid}/offers", GetListingOffersAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/offers/mine", GetMyOffersAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/offers/{id:guid}/accept", AcceptOfferAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/offers/{id:guid}/decline", DeclineOfferAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/offers/{id:guid}/withdraw", WithdrawOfferAsync).RequireAuthorization("Authenticated");

        // ── Scrim Sessions ──────────────────────────────────────────────────────
        app.MapGet("/api/scrims/sessions", GetTeamSessionsAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/sessions/{id:guid}", GetSessionAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/start", StartSessionAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/complete", CompleteSessionAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/cancel", CancelSessionAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/report-result", ReportResultAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/sessions/{id:guid}/results", GetSessionResultsAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/dispute", OpenDisputeAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/dispute/evidence", SubmitEvidenceAsync).RequireAuthorization("Authenticated");

        // ── Scrim Sessions — PROJ-077 new endpoints ─────────────────────────────
        app.MapPatch("/api/scrims/sessions/{id:guid}/lobby", UpdateLobbyAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/confirm-result", ConfirmResultAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/checkin", CheckInAsync).RequireAuthorization("Authenticated");

        // ── Scrim Slots (PROJ-078) ──────────────────────────────────────────────
        app.MapGet("/api/scrims/listings/{id:guid}/slots", GetSlotsAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/listings/{id:guid}/slots", CreateSlotsAsync).RequireAuthorization("Authenticated");

        // ── Scrim History + Riot Scan (PROJ-078) ─────────────────────────────────
        app.MapGet("/api/scrims/sessions/{id:guid}/recent-matches", GetSessionRecentMatchesAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/reports/parse-screenshot", ParseScrimScreenshotAsync)
            .RequireAuthorization("Authenticated")
            .DisableAntiforgery()
            .WithMetadata(new RateLimitPolicyMetadata("screenshotOcr"));
        app.MapGet("/api/scrims/teams/{teamId:guid}/history", GetTeamScrimHistoryAsync).AllowAnonymous();
        app.MapPatch("/api/scrims/teams/{teamId:guid}/history/privacy", UpdateHistoryPrivacyAsync).RequireAuthorization("Authenticated");

        // ── Practice ────────────────────────────────────────────────────────────
        app.MapPost("/api/practice/sessions", CreatePracticeAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/practice/sessions", GetTeamPracticeAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/practice/sessions/{id:guid}", GetPracticeAsync).RequireAuthorization("Authenticated");
        app.MapPut("/api/practice/sessions/{id:guid}", UpdatePracticeAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/practice/sessions/{id:guid}/complete", CompletePracticeAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/practice/sessions/{id:guid}/cancel", CancelPracticeAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/practice/sessions/{id:guid}/attendance", GetAttendanceAsync).RequireAuthorization("Authenticated");
        app.MapPut("/api/practice/sessions/{id:guid}/attendance", UpsertAttendanceAsync).RequireAuthorization("Authenticated");
        app.MapPut("/api/practice/attendance/{id:guid}", UpdateOwnAttendanceAsync).RequireAuthorization("Authenticated");
    }


    // ── POST /api/scrims/listings ──────────────────────────────────────────────

    private static async Task<IResult> CreateListingAsync(
        [FromBody] CreateScrimListingRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.TeamId == Guid.Empty) return Results.BadRequest(new { error = "team_id is required." });
        if (req.Notes?.Length > 500) return Results.BadRequest(new { error = "notes max 500 chars." });

        var scheduleType = req.ScheduleType ?? "one_off";
        if (scheduleType is not ("one_off" or "recurring"))
            return Results.UnprocessableEntity(new { error = "schedule_type must be 'one_off' or 'recurring'." });

        if (scheduleType == "recurring")
        {
            if (req.ScheduleUntil is null)
                return Results.UnprocessableEntity(new { error = "schedule_until is required for recurring listings." });
            if (req.SlotDurationMinutes is null)
                return Results.UnprocessableEntity(new { error = "slot_duration_minutes is required for recurring listings." });
            if (req.SlotDurationMinutes <= 0)
                return Results.UnprocessableEntity(new { error = "slot_duration_minutes must be positive." });
        }
        else
        {
            if (req.ScheduleUntil is not null)
                return Results.UnprocessableEntity(new { error = "schedule_until is only valid for recurring listings." });
            if (req.SlotDurationMinutes is not null)
                return Results.UnprocessableEntity(new { error = "slot_duration_minutes is only valid for recurring listings." });
        }

        using var conn = db.CreateConnection();

        if (!await IsTeamStaffAsync(conn, req.TeamId, userCtx.UserIdGuid)) return Results.Forbid();

        var row = await conn.QuerySingleAsync<ScrimListingRow>(
            """
            INSERT INTO public.scrim_listings
                (team_id, game_slug, mode_key, region_key, min_rank_key, max_rank_key,
                 preferred_days, preferred_time_start, preferred_time_end, timezone, notes,
                 schedule_type, schedule_until, slot_duration_minutes)
            VALUES
                (@teamId, @gameSlug, @modeKey, @regionKey, @minRankKey, @maxRankKey,
                 @preferredDays, @preferredTimeStart::time, @preferredTimeEnd::time, @timezone, @notes,
                 @scheduleType, @scheduleUntil, @slotDurationMinutes)
            RETURNING *
            """,
            new
            {
                teamId = req.TeamId,
                gameSlug = req.GameSlug ?? "",
                modeKey = req.ModeKey,
                regionKey = req.RegionKey,
                minRankKey = req.MinRankKey,
                maxRankKey = req.MaxRankKey,
                preferredDays = req.PreferredDays ?? Array.Empty<int>(),
                preferredTimeStart = req.PreferredTimeStart,
                preferredTimeEnd = req.PreferredTimeEnd,
                timezone = req.Timezone,
                notes = req.Notes,
                scheduleType,
                scheduleUntil = req.ScheduleUntil,
                slotDurationMinutes = req.SlotDurationMinutes,
            });

        // AC16: Auto-generate slots for recurring listings with preferred availability set.
        if (scheduleType == "recurring")
            await GenerateSlotsAsync(conn, row.Id);

        return Results.Created($"/api/scrims/listings/{row.Id}", row);
    }

    // ── GET /api/scrims/listings ───────────────────────────────────────────────

    private static async Task<IResult> GetTeamListingsAsync(
        [FromQuery] Guid? team_id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (team_id is null) return Results.BadRequest(new { error = "team_id is required." });

        using var conn = db.CreateConnection();

        if (!await IsTeamStaffAsync(conn, team_id.Value, userCtx.UserIdGuid)) return Results.Forbid();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        offset = Math.Max(offset, 0);

        var rows = await conn.QueryAsync<ScrimListingRow>(
            """
            SELECT sl.*, t.name AS team_name
            FROM public.scrim_listings sl JOIN public.teams t ON t.id = sl.team_id
            WHERE sl.team_id = @teamId
            ORDER BY sl.created_at DESC LIMIT @limit OFFSET @offset
            """,
            new { teamId = team_id.Value, limit, offset });

        return Results.Ok(rows);
    }

    // ── PUT /api/scrims/listings/{id} ──────────────────────────────────────────

    private static async Task<IResult> UpdateListingAsync(
        Guid id,
        [FromBody] UpdateScrimListingRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.Notes?.Length > 500) return Results.BadRequest(new { error = "notes max 500 chars." });

        using var conn = db.CreateConnection();

        var teamId = await conn.ExecuteScalarAsync<Guid?>("SELECT team_id FROM public.scrim_listings WHERE id = @id", new { id });
        if (teamId is null) return Results.NotFound();
        if (!await IsTeamStaffAsync(conn, teamId.Value, userCtx.UserIdGuid)) return Results.Forbid();

        var updated = await conn.QuerySingleOrDefaultAsync<ScrimListingRow>(
            """
            UPDATE public.scrim_listings
            SET mode_key             = COALESCE(@modeKey, mode_key),
                region_key           = COALESCE(@regionKey, region_key),
                min_rank_key         = COALESCE(@minRankKey, min_rank_key),
                max_rank_key         = COALESCE(@maxRankKey, max_rank_key),
                preferred_days       = COALESCE(@preferredDays, preferred_days),
                preferred_time_start = COALESCE(@preferredTimeStart::time, preferred_time_start),
                preferred_time_end   = COALESCE(@preferredTimeEnd::time, preferred_time_end),
                timezone             = COALESCE(@timezone, timezone),
                notes                = COALESCE(@notes, notes),
                updated_at           = now()
            WHERE id = @id RETURNING *
            """,
            new
            {
                id,
                modeKey = req.ModeKey,
                regionKey = req.RegionKey,
                minRankKey = req.MinRankKey,
                maxRankKey = req.MaxRankKey,
                preferredDays = req.PreferredDays,
                preferredTimeStart = req.PreferredTimeStart,
                preferredTimeEnd = req.PreferredTimeEnd,
                timezone = req.Timezone,
                notes = req.Notes,
            });

        // AC16: Regenerate slots when preferred availability is updated on a recurring listing.
        // ON CONFLICT DO NOTHING makes this idempotent — existing future slots are untouched.
        if (updated is not null && updated.ScheduleType == "recurring")
            await GenerateSlotsAsync(conn, id);

        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static Task<IResult> PauseListingAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
        => TransitionListingAsync(id, ctx, db, "paused", ["open"]);

    private static Task<IResult> ReopenListingAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
        => TransitionListingAsync(id, ctx, db, "open", ["paused"]);

    private static async Task<IResult> CloseListingAsync(
        Guid id, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub, CancellationToken ct)
    {
        var result = await TransitionListingAsync(id, ctx, db, "closed", ["open", "paused"]);

        // Only notify on successful close.
        if (result is IStatusCodeHttpResult { StatusCode: not 200 }) return result;

        // Gap 9: Notify teams with pending offers that the listing was closed.
        using var conn = db.CreateConnection();
        var offerTeamIds = (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT offering_team_id FROM public.scrim_offers
            WHERE listing_id = @listingId AND status = 'pending'
            """,
            new { listingId = id })).ToList();

        foreach (var offerTeamId in offerTeamIds)
        {
            var staffIds = (await conn.QueryAsync<Guid>(
                """
                SELECT user_id FROM public.team_members
                WHERE team_id = @teamId AND role IN ('captain', 'manager') AND is_active = TRUE
                """,
                new { teamId = offerTeamId })).ToList();

            foreach (var staffId in staffIds)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.notifications (user_id, type, title, message, is_read)
                    VALUES (@userId, 'scrim_offer_declined'::notification_type, 'Scrim Listing Closed',
                            'A scrim listing you offered on was closed.', FALSE)
                    """,
                    new { userId = staffId });
            }

            await TeamNotifications.PushAsync(hub, staffIds, "scrim_offer_declined", "Scrim Listing Closed", "A scrim listing you offered on was closed.");
        }

        return result;
    }

    // ── GET /api/scrims/discover ───────────────────────────────────────────────

    private static async Task<IResult> DiscoverAsync(
        [FromQuery] string? game,
        [FromQuery] string? mode_key,
        [FromQuery] string? region_key,
        [FromQuery] int? min_rank_sort_order,
        [FromQuery] int? max_rank_sort_order,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game))
            return Results.BadRequest(new { error = "game query parameter is required." });

        var actualLimit = Math.Clamp(limit is null or 0 ? 20 : limit.Value, 1, 50);
        var actualOffset = Math.Max(offset ?? 0, 0);

        using var conn = db.CreateConnection();


        // Build rank-filter conditions using a CTE to resolve sort_order from tier_key.
        // The filter applies the overlap predicate:
        //   listing.max_rank_sort_order >= minRankSortOrder  (listing reaches at least the min)
        //   listing.min_rank_sort_order <= maxRankSortOrder  (listing starts before or at the max)
        // Listings with NULL rank keys are excluded only when both filter params are supplied.
        var rankConditions = "";
        if (min_rank_sort_order.HasValue)
        {
            rankConditions +=
                """

                  AND (max_rt.sort_order IS NULL OR max_rt.sort_order >= @minRankSortOrder)
                """;
        }

        if (max_rank_sort_order.HasValue)
        {
            rankConditions +=
                """

                  AND (min_rt.sort_order IS NULL OR min_rt.sort_order <= @maxRankSortOrder)
                """;
        }

        var rows = await conn.QueryAsync<ScrimListingRow>(
            $"""
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT sl.*, t.name AS team_name, t.logo_url AS team_logo_url, t.tag AS team_tag
            FROM public.scrim_listings sl
            JOIN public.teams t ON t.id = sl.team_id
            LEFT JOIN public.game_catalog_rank_tiers min_rt
                ON min_rt.tier_key = sl.min_rank_key
               AND min_rt.game_slug = sl.game_slug
               AND min_rt.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_rank_tiers max_rt
                ON max_rt.tier_key = sl.max_rank_key
               AND max_rt.game_slug = sl.game_slug
               AND max_rt.version_id = (SELECT id FROM active_v)
            WHERE sl.status = 'open' AND sl.game_slug = @game
              AND (@modeKey IS NULL OR sl.mode_key = @modeKey)
              AND (@regionKey IS NULL OR sl.region_key = @regionKey){rankConditions}
            ORDER BY sl.created_at DESC LIMIT @actualLimit OFFSET @actualOffset
            """,
            new
            {
                game,
                modeKey = mode_key,
                regionKey = region_key,
                minRankSortOrder = min_rank_sort_order,
                maxRankSortOrder = max_rank_sort_order,
                actualLimit,
                actualOffset,
            });

        return Results.Ok(rows);
    }

    // ── POST /api/scrims/listings/{id}/offer ───────────────────────────────────

    private static async Task<IResult> MakeOfferAsync(
        Guid id,
        [FromBody] MakeScrimOfferRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.OfferingTeamId == Guid.Empty) return Results.BadRequest(new { error = "offering_team_id is required." });
        if (req.Notes?.Length > 300) return Results.BadRequest(new { error = "notes max 300 chars." });

        using var conn = db.CreateConnection();


        var listing = await conn.QuerySingleOrDefaultAsync<ScrimListingRef>(
            "SELECT team_id, status FROM public.scrim_listings WHERE id = @id", new { id });
        if (listing is null) return Results.NotFound();
        if (listing.Status != "open")
            return Results.BadRequest(new { error = "Listing is not open." });

        if (!await IsTeamStaffAsync(conn, req.OfferingTeamId, userCtx.UserIdGuid)) return Results.Forbid();

        var row = await conn.QuerySingleAsync<ScrimOfferRow>(
            """
            INSERT INTO public.scrim_offers
                (listing_id, offering_team_id, proposed_at, proposed_duration_minutes, notes, slot_id)
            VALUES (@listingId, @offeringTeamId, @proposedAt, @proposedDurationMinutes, @notes, @slotId)
            RETURNING *
            """,
            new
            {
                listingId = id,
                offeringTeamId = req.OfferingTeamId,
                proposedAt = req.ProposedAt ?? DateTimeOffset.UtcNow,
                proposedDurationMinutes = req.ProposedDurationMinutes,
                notes = req.Notes,
                slotId = req.SlotId,
            });

        // Notify listing team staff.
        var staffIds = await conn.QueryAsync<Guid>(
            """
            SELECT tm.user_id FROM public.team_members tm
            WHERE tm.team_id = @teamId AND tm.role IN ('captain', 'manager') AND tm.is_active = TRUE
            """,
            new { teamId = listing.TeamId });

        var offerTeamName = await conn.ExecuteScalarAsync<string?>(
            "SELECT name FROM public.teams WHERE id = @id", new { id = req.OfferingTeamId });

        foreach (var staffId in staffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, is_read)
                VALUES (@userId, 'scrim_offer_received', 'Scrim Offer',
                        @message, FALSE)
                """,
                new { userId = staffId, message = $"{offerTeamName ?? "A team"} sent a scrim offer." });
        }

        return Results.Created($"/api/scrims/offers/{row.Id}", row);
    }

    // ── GET /api/scrims/listings/{id}/offers ───────────────────────────────────

    private static async Task<IResult> GetListingOffersAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var teamId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT team_id FROM public.scrim_listings WHERE id = @id", new { id });
        if (teamId is null) return Results.NotFound();
        if (!await IsTeamStaffAsync(conn, teamId.Value, userCtx.UserIdGuid)) return Results.Forbid();

        var rows = await conn.QueryAsync<ScrimOfferRow>(
            """
            SELECT so.*, t.name AS offering_team_name, t.logo_url AS offering_team_logo
            FROM public.scrim_offers so JOIN public.teams t ON t.id = so.offering_team_id
            WHERE so.listing_id = @listingId
            ORDER BY so.created_at DESC
            """,
            new { listingId = id });

        return Results.Ok(rows);
    }

    // ── GET /api/scrims/offers/mine ────────────────────────────────────────────

    private static async Task<IResult> GetMyOffersAsync(
        [FromQuery] Guid? team_id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (team_id is null) return Results.BadRequest(new { error = "team_id is required." });

        using var conn = db.CreateConnection();

        if (!await IsTeamStaffAsync(conn, team_id.Value, userCtx.UserIdGuid)) return Results.Forbid();

        var rows = await conn.QueryAsync<ScrimOfferRow>(
            """
            SELECT so.*, sl.game_slug AS listing_game_slug, t.name AS listing_team_name
            FROM public.scrim_offers so
            JOIN public.scrim_listings sl ON sl.id = so.listing_id
            JOIN public.teams t ON t.id = sl.team_id
            WHERE so.offering_team_id = @teamId
            ORDER BY so.created_at DESC
            """,
            new { teamId = team_id.Value });

        return Results.Ok(rows);
    }

    // ── POST /api/scrims/offers/{id}/accept ────────────────────────────────────

    private static async Task<IResult> AcceptOfferAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<NotificationHub> hub,
        IBackgroundJobClient backgroundJobs,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        using var tx = conn.BeginTransaction();

        try
        {
            // Fetch offer + listing team first so authorization is checked before any mutation.
            var offerRow = await conn.QuerySingleOrDefaultAsync<ScrimOfferForAccept>(
                """
                SELECT so.id, so.listing_id, so.offering_team_id,
                       so.proposed_at, so.proposed_duration_minutes, so.status,
                       sl.team_id AS listing_team_id, sl.schedule_type, so.slot_id
                FROM public.scrim_offers so
                JOIN public.scrim_listings sl ON sl.id = so.listing_id
                WHERE so.id = @id
                """,
                new { id }, tx);

            if (offerRow is null || offerRow.Status != "pending")
                return Results.BadRequest(new { error = "Offer not found or already processed." });

            if (!await IsTeamStaffAsync(conn, offerRow.ListingTeamId, userCtx.UserIdGuid))
            {
                tx.Rollback();
                return Results.Forbid();
            }

            // Optimistic update — prevents double-accept under concurrent requests.
            var accepted = await conn.ExecuteScalarAsync<Guid?>(
                """
                UPDATE public.scrim_offers
                SET status = 'accepted', updated_at = now()
                WHERE id = @id AND status = 'pending'
                RETURNING id
                """,
                new { id }, tx);

            if (accepted is null)
                return Results.BadRequest(new { error = "Offer not found or already processed." });

            // Create scrim session.
            var session = await conn.QuerySingleAsync<ScrimSessionRow>(
                """
                INSERT INTO public.scrim_sessions
                    (team1_id, team2_id, game_slug, scheduled_at, duration_minutes, status)
                SELECT sl.team_id, @team2Id, sl.game_slug,
                       @scheduledAt, @durationMinutes, 'scheduled'
                FROM public.scrim_listings sl WHERE sl.id = @listingId
                RETURNING *
                """,
                new
                {
                    team2Id = offerRow.OfferingTeamId,
                    listingId = offerRow.ListingId,
                    scheduledAt = offerRow.ProposedAt ?? DateTimeOffset.UtcNow,
                    durationMinutes = offerRow.ProposedDurationMinutes,
                }, tx);

            // D7: Recurring listings stay 'open' so more sessions can be created.
            // One-off listings close on acceptance (existing behaviour).
            if (offerRow.ScheduleType != "recurring")
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE public.scrim_listings
                    SET status = 'closed'::scrim_listing_status, updated_at = now()
                    WHERE id = @listingId
                    """,
                    new { listingId = offerRow.ListingId }, tx);
            }

            // DEFECT-4b: Lock slot atomically to prevent race conditions on concurrent accepts.
            // Only applies when the offer was linked to a specific recurring slot.
            if (offerRow.SlotId.HasValue)
            {
                var slotLocked = await conn.ExecuteScalarAsync<Guid?>(
                    """
                    UPDATE public.scrim_slots
                    SET status = 'booked', booked_by_team_id = @team2Id, session_id = @sessionId
                    WHERE id = @slotId AND status = 'available'
                    RETURNING id
                    """,
                    new
                    {
                        slotId = offerRow.SlotId.Value,
                        team2Id = offerRow.OfferingTeamId,
                        sessionId = session.Id,
                    },
                    tx);

                if (slotLocked is null)
                {
                    tx.Rollback();
                    return Results.Conflict(new { error = "This slot has just been booked. Please select another time." });
                }
            }

            tx.Commit();

            // D3: Enqueue Hangfire delayed jobs for session lifecycle automation.
            // Jobs are enqueued post-commit so they only fire for committed sessions.
            if (session.ScheduledAt.HasValue)
            {
                var scheduledAt = new DateTimeOffset(session.ScheduledAt.Value, TimeSpan.Zero);
                var reminderId = backgroundJobs.Schedule<ScrimReminderJob>(
                    j => j.RunAsync(session.Id, CancellationToken.None),
                    scheduledAt.AddMinutes(-30));
                var windowId = backgroundJobs.Schedule<ScrimCheckinWindowJob>(
                    j => j.RunAsync(session.Id, CancellationToken.None),
                    scheduledAt.AddMinutes(-15));
                var timeoutId = backgroundJobs.Schedule<ScrimCheckinTimeoutJob>(
                    j => j.RunAsync(session.Id, CancellationToken.None),
                    scheduledAt.AddMinutes(15));

                // Store job IDs so they can be deleted if the session is cancelled.
                var jobIds = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reminder = reminderId,
                    window = windowId,
                    timeout = timeoutId,
                });
                await conn.ExecuteAsync(
                    "UPDATE public.scrim_sessions SET hangfire_job_ids = @jobIds::jsonb WHERE id = @sessionId",
                    new { jobIds, sessionId = session.Id });
            }

            // M5: open inbox conversation between team captains (best-effort, post-commit).
            await OpenScrimConversationAsync(
                conn, offerRow.ListingTeamId, offerRow.OfferingTeamId, session,
                loggerFactory.CreateLogger(nameof(ScrimEndpoints)));

            // Notify offering team staff (after commit — outbox pattern).
            var staffIds = await conn.QueryAsync<Guid>(
                """
                SELECT user_id FROM public.team_members
                WHERE team_id = @teamId AND role IN ('captain', 'manager') AND is_active = TRUE
                """,
                new { teamId = offerRow.OfferingTeamId });

            var listingTeamName = await conn.ExecuteScalarAsync<string?>(
                "SELECT name FROM public.teams WHERE id = @id", new { id = offerRow.ListingTeamId });

            var staffIdList = staffIds.ToList();
            foreach (var staffId in staffIdList)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.notifications (user_id, type, title, message, is_read)
                    VALUES (@userId, 'scrim_offer_accepted', 'Scrim Offer Accepted', @message, FALSE)
                    """,
                    new { userId = staffId, message = $"{listingTeamName ?? "A team"} accepted your scrim offer." });
            }

            // Gap 1: real-time push after commit.
            await TeamNotifications.PushAsync(hub, staffIdList, "scrim_offer_accepted", "Scrim Offer Accepted", $"{listingTeamName ?? "A team"} accepted your scrim offer.");

            return Results.Ok(session);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── POST /api/scrims/offers/{id}/decline ───────────────────────────────────

    private static async Task<IResult> DeclineOfferAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub, CancellationToken ct)
        => await ChangeOfferStatusAsync(id, ctx, db, hub, "declined", requiresListingTeam: true);

    // ── POST /api/scrims/offers/{id}/withdraw ──────────────────────────────────

    private static async Task<IResult> WithdrawOfferAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub, CancellationToken ct)
        => await ChangeOfferStatusAsync(id, ctx, db, hub, "withdrawn", requiresListingTeam: false);

    // ── GET /api/scrims/sessions ───────────────────────────────────────────────

    private static async Task<IResult> GetTeamSessionsAsync(
        [FromQuery] Guid? team_id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (team_id is null) return Results.BadRequest(new { error = "team_id is required." });

        using var conn = db.CreateConnection();

        if (!await IsTeamStaffAsync(conn, team_id.Value, userCtx.UserIdGuid)) return Results.Forbid();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        offset = Math.Max(offset, 0);

        var rows = await conn.QueryAsync<ScrimSessionRow>(
            """
            SELECT ss.*,
                   t1.name AS team1_name, t1.logo_url AS team1_logo,
                   t2.name AS team2_name, t2.logo_url AS team2_logo
            FROM public.scrim_sessions ss
            JOIN public.teams t1 ON t1.id = ss.team1_id
            JOIN public.teams t2 ON t2.id = ss.team2_id
            WHERE ss.team1_id = @teamId OR ss.team2_id = @teamId
            ORDER BY ss.created_at DESC LIMIT @limit OFFSET @offset
            """,
            new { teamId = team_id.Value, limit, offset });

        return Results.Ok(rows);
    }

    // ── GET /api/scrims/sessions/{id} ──────────────────────────────────────────

    private static async Task<IResult> GetSessionAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        // Fetch session + raw veto tokens (tokens are excluded from ScrimSessionRow mapping).
        // my_role: caller's team_members.role — used by the frontend to gate the check-in button.
        var row = await conn.QuerySingleOrDefaultAsync<ScrimSessionWithTokens>(
            """
            SELECT ss.*,
                   t1.name AS team1_name, t1.logo_url AS team1_logo,
                   t2.name AS team2_name, t2.logo_url AS team2_logo,
                   ss.veto_team1_token, ss.veto_team2_token,
                   (SELECT tm.role FROM public.team_members tm
                    WHERE tm.team_id IN (ss.team1_id, ss.team2_id)
                      AND tm.user_id = @userId
                      AND tm.is_active = TRUE
                    LIMIT 1) AS my_role
            FROM public.scrim_sessions ss
            JOIN public.teams t1 ON t1.id = ss.team1_id
            JOIN public.teams t2 ON t2.id = ss.team2_id
            WHERE ss.id = @id
              AND EXISTS (
                SELECT 1 FROM public.team_members tm
                WHERE tm.team_id IN (ss.team1_id, ss.team2_id)
                  AND tm.user_id = @userId
                  AND tm.role IN ('captain', 'manager')
                  AND tm.is_active = TRUE
              )
            """,
            new { id, userId = userCtx.UserIdGuid });

        if (row is null) return Results.NotFound();

        // Derive veto_my_token based on caller's team — raw tokens never sent.
        string? vetoMyToken = null;
        if (row.VetoSessionId.HasValue)
        {
            var callerTeamId = await conn.ExecuteScalarAsync<Guid?>(
                """
                SELECT team_id FROM public.team_members
                WHERE user_id = @userId
                  AND team_id IN (@t1, @t2)
                  AND is_active = TRUE
                LIMIT 1
                """,
                new { userId = userCtx.UserIdGuid, t1 = row.Team1Id, t2 = row.Team2Id });

            vetoMyToken = callerTeamId == row.Team1Id
                ? row.VetoTeam1Token
                : callerTeamId == row.Team2Id
                    ? row.VetoTeam2Token
                    : null;
        }

        // Fetch the most recent open dispute for this session (null if no dispute).
        var dispute = await conn.QuerySingleOrDefaultAsync<ScrimDisputeRow>(
            """
            SELECT * FROM public.scrim_disputes
            WHERE session_id = @id
            ORDER BY created_at DESC
            LIMIT 1
            """,
            new { id });

        // Return session row with derived VetoMyToken (raw tokens excluded) + dispute.
        var response = row.ToSessionRow(vetoMyToken, dispute);
        return Results.Ok(response);
    }

    // ── Session lifecycle ──────────────────────────────────────────────────────

    private static Task<IResult> StartSessionAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
        => TransitionSessionAsync(id, ctx, db, "in_progress", ["scheduled"]);

    private static Task<IResult> CompleteSessionAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
        => TransitionSessionAsync(id, ctx, db, "completed", ["in_progress"]);

    private static async Task<IResult> CancelSessionAsync(
        Guid id, HttpContext ctx, IDbConnectionFactory db, IBackgroundJobClient backgroundJobs, CancellationToken ct)
    {
        var result = await TransitionSessionAsync(id, ctx, db, "cancelled", ["scheduled", "in_progress"]);

        // Delete Hangfire jobs on cancel so they don't fire for a cancelled session.
        if (result is IStatusCodeHttpResult { StatusCode: 200 })
        {
            using var conn = db.CreateConnection();
            var jobIds = await conn.ExecuteScalarAsync<string?>(
                "SELECT hangfire_job_ids FROM public.scrim_sessions WHERE id = @id", new { id });

            if (!string.IsNullOrEmpty(jobIds))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(jobIds);
                    var root = doc.RootElement;
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                            backgroundJobs.Delete(prop.Value.GetString()!);
                    }
                }
                catch
                {
                    // Non-critical: if deletion fails, jobs are idempotent and will no-op.
                }
            }
        }

        return result;
    }

    // ── POST /api/scrims/sessions/{id}/report-result ───────────────────────────

    private static async Task<IResult> ReportResultAsync(
        Guid id,
        [FromBody] ReportScrimResultRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<NotificationHub> hub,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.ReportingTeamId == Guid.Empty) return Results.BadRequest(new { error = "reporting_team_id is required." });
        if (req.Notes?.Length > 500) return Results.BadRequest(new { error = "notes max 500 chars." });

        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionRef>(
            "SELECT team1_id, team2_id, status FROM public.scrim_sessions WHERE id = @id", new { id });
        if (session is null) return Results.NotFound();

        var isParty = req.ReportingTeamId == session.Team1Id || req.ReportingTeamId == session.Team2Id;
        if (!isParty) return Results.BadRequest(new { error = "Reporting team is not a party to this session." });
        if (!await IsTeamStaffAsync(conn, req.ReportingTeamId, userCtx.UserIdGuid)) return Results.Forbid();

        try
        {
            var report = await conn.QuerySingleAsync<ScrimResultReportRow>(
                """
                INSERT INTO public.scrim_result_reports
                    (session_id, reporting_team_id, team1_score, team2_score, notes, screenshots_urls)
                VALUES (@sessionId, @reportingTeamId, @team1Score, @team2Score, @notes, @screenshotsUrls)
                ON CONFLICT (session_id, reporting_team_id) DO UPDATE SET
                    team1_score = @team1Score, team2_score = @team2Score,
                    notes = COALESCE(@notes, scrim_result_reports.notes),
                    screenshots_urls = @screenshotsUrls,
                    submitted_at = now()
                RETURNING *
                """,
                new
                {
                    sessionId = id,
                    reportingTeamId = req.ReportingTeamId,
                    team1Score = req.Team1Score,
                    team2Score = req.Team2Score,
                    notes = req.Notes,
                    screenshotsUrls = req.ScreenshotUrls ?? Array.Empty<string>(),
                });

            // Update session flags and scores.
            var isTeam1 = req.ReportingTeamId == session.Team1Id;
            var flagCol = isTeam1 ? "result_reported_by_team1 = TRUE" : "result_reported_by_team2 = TRUE";
            await conn.ExecuteAsync(
                $"""
                UPDATE public.scrim_sessions
                SET {flagCol}, team1_score = @t1, team2_score = @t2, updated_at = now()
                WHERE id = @id
                """,
                new { id, t1 = req.Team1Score, t2 = req.Team2Score });

            // Gap 9+1: Notify other team that a result was submitted.
            var otherTeamId = isTeam1 ? session.Team2Id : session.Team1Id;
            var otherStaffIds = (await conn.QueryAsync<Guid>(
                """
                SELECT user_id FROM public.team_members
                WHERE team_id = @teamId AND role IN ('captain', 'manager') AND is_active = TRUE
                """,
                new { teamId = otherTeamId })).ToList();

            foreach (var staffId in otherStaffIds)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.notifications (user_id, type, title, message, is_read)
                    VALUES (@userId, 'scrim_result_submitted'::notification_type, 'Scrim Result Submitted',
                            'The opposing team submitted a result report.', FALSE)
                    """,
                    new { userId = staffId });
            }

            await TeamNotifications.PushAsync(hub, otherStaffIds, "scrim_result_submitted", "Scrim Result Submitted", "The opposing team submitted a result report.");

            return Results.Ok(report);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.BadRequest(new { error = "Result already reported by this team." });
        }
    }

    // ── GET /api/scrims/sessions/{id}/results ──────────────────────────────────

    private static async Task<IResult> GetSessionResultsAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionParties>(
            "SELECT team1_id, team2_id FROM public.scrim_sessions WHERE id = @id", new { id });
        if (session is null) return Results.NotFound();

        var isStaff = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members tm
                WHERE tm.team_id IN (@t1, @t2)
                  AND tm.user_id = @userId
                  AND tm.role IN ('captain', 'manager')
                  AND tm.is_active = TRUE
            )
            """,
            new { t1 = session.Team1Id, t2 = session.Team2Id, userId = userCtx.UserIdGuid });
        if (!isStaff) return Results.Forbid();

        var rows = await conn.QueryAsync<ScrimResultReportRow>(
            """
            SELECT srr.*, t.name AS reporting_team_name
            FROM public.scrim_result_reports srr
            JOIN public.teams t ON t.id = srr.reporting_team_id
            WHERE srr.session_id = @sessionId
            """,
            new { sessionId = id });

        return Results.Ok(rows);
    }

    // ── POST /api/scrims/sessions/{id}/dispute ─────────────────────────────────

    private static async Task<IResult> OpenDisputeAsync(
        Guid id,
        [FromBody] OpenDisputeRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<NotificationHub> hub,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.RaisedByTeamId == Guid.Empty) return Results.BadRequest(new { error = "raised_by_team_id is required." });
        if (string.IsNullOrWhiteSpace(req.Reason)) return Results.BadRequest(new { error = "reason is required." });
        if (req.Reason.Length > 1000) return Results.BadRequest(new { error = "reason max 1000 chars." });
        if (req.EvidenceUrls?.Length > 5)
            return Results.UnprocessableEntity(new { error = "evidence_urls max 5 URLs." });
        if (req.EvidenceUrls is not null)
        {
            foreach (var url in req.EvidenceUrls)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return Results.UnprocessableEntity(new { error = $"Invalid URL: {url}" });
            }
        }

        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionParties>(
            "SELECT team1_id, team2_id FROM public.scrim_sessions WHERE id = @id", new { id });
        if (session is null) return Results.NotFound();

        var isParty = req.RaisedByTeamId == session.Team1Id || req.RaisedByTeamId == session.Team2Id;
        if (!isParty) return Results.BadRequest(new { error = "Team is not a party to this session." });
        if (!await IsTeamStaffAsync(conn, req.RaisedByTeamId, userCtx.UserIdGuid)) return Results.Forbid();

        var dispute = await conn.QuerySingleAsync<ScrimDisputeRow>(
            """
            INSERT INTO public.scrim_disputes (session_id, raised_by_team_id, reason, evidence_urls)
            VALUES (@sessionId, @raisedByTeamId, @reason, @evidenceUrls)
            RETURNING *
            """,
            new
            {
                sessionId = id,
                raisedByTeamId = req.RaisedByTeamId,
                reason = req.Reason,
                evidenceUrls = req.EvidenceUrls ?? Array.Empty<string>(),
            });

        // Mark session as disputed.
        await conn.ExecuteAsync(
            "UPDATE public.scrim_sessions SET status = 'disputed', updated_at = now() WHERE id = @id",
            new { id });

        // Gap 9+1: Notify the other team that a dispute was raised.
        var otherTeamId = req.RaisedByTeamId == session.Team1Id ? session.Team2Id : session.Team1Id;
        var otherStaffIds = (await conn.QueryAsync<Guid>(
            """
            SELECT user_id FROM public.team_members
            WHERE team_id = @teamId AND role IN ('captain', 'manager') AND is_active = TRUE
            """,
            new { teamId = otherTeamId })).ToList();

        foreach (var staffId in otherStaffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, is_read)
                VALUES (@userId, 'scrim_dispute_raised'::notification_type, 'Scrim Dispute Raised',
                        'The opposing team raised a dispute on your scrim session.', FALSE)
                """,
                new { userId = staffId });
        }

        await TeamNotifications.PushAsync(hub, otherStaffIds, "scrim_dispute_raised", "Scrim Dispute Raised", "The opposing team raised a dispute on your scrim session.");

        return Results.Created($"/api/scrims/sessions/{id}/dispute", dispute);
    }

    // ── POST /api/scrims/sessions/{id}/dispute/evidence ───────────────────────

    private static async Task<IResult> SubmitEvidenceAsync(
        Guid id,
        [FromBody] SubmitScrimEvidenceRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        var urls = req.ResponderEvidenceUrls ?? Array.Empty<string>();
        if (urls.Length > 5)
            return Results.UnprocessableEntity(new { error = "responder_evidence_urls max 5 URLs." });
        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Results.UnprocessableEntity(new { error = $"Invalid URL: {url}" });
        }

        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionParties>(
            "SELECT team1_id, team2_id FROM public.scrim_sessions WHERE id = @id", new { id });
        if (session is null) return Results.NotFound();

        // Caller must be captain/manager on one of the two parties.
        var isStaff = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE team_id IN (@t1, @t2)
                  AND user_id = @userId
                  AND role IN ('captain', 'manager')
                  AND is_active = TRUE
            )
            """,
            new { t1 = session.Team1Id, t2 = session.Team2Id, userId = userCtx.UserIdGuid });
        if (!isStaff) return Results.Forbid();

        var updated = await conn.QuerySingleOrDefaultAsync<ScrimDisputeRow>(
            """
            UPDATE public.scrim_disputes
            SET responder_evidence_urls = @urls
            WHERE session_id = @sessionId
              AND status IN ('open', 'escalated')
            RETURNING *
            """,
            new { sessionId = id, urls });

        if (updated is null)
            return Results.NotFound(new { error = "No open dispute found for this session." });

        return Results.Ok(updated);
    }

    // ── PATCH /api/scrims/sessions/{id}/lobby ─────────────────────────────────

    private static async Task<IResult> UpdateLobbyAsync(
        Guid id,
        [FromBody] UpdateLobbyRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<NotificationHub> hub,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.LobbyCode is null && req.ServerInfo is null)
            return Results.BadRequest(new { error = "At least one of lobby_code or server_info must be provided." });

        using var conn = db.CreateConnection();


        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionRef>(
            "SELECT team1_id, team2_id, status FROM public.scrim_sessions WHERE id = @id",
            new { id });
        if (session is null) return Results.NotFound();

        if (session.Status is "in_progress" or "completed" or "cancelled" or "disputed")
            return Results.Conflict(new { error = $"Lobby cannot be updated when session is '{session.Status}'." });

        var isStaff = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE team_id IN (@t1, @t2)
                  AND user_id = @userId
                  AND role IN ('captain', 'manager')
                  AND is_active = TRUE
            )
            """,
            new { t1 = session.Team1Id, t2 = session.Team2Id, userId = userCtx.UserIdGuid });
        if (!isStaff) return Results.Forbid();

        var updated = await conn.QuerySingleOrDefaultAsync<ScrimSessionRow>(
            """
            UPDATE public.scrim_sessions
            SET lobby_code  = COALESCE(@lobbyCode, lobby_code),
                server_info = COALESCE(@serverInfo, server_info),
                updated_at  = now()
            WHERE id = @id
            RETURNING *
            """,
            new { id, lobbyCode = req.LobbyCode, serverInfo = req.ServerInfo });

        if (updated is null) return Results.NotFound();

        // Notify the other team's staff (the team that did NOT make the update).
        var callerTeamId = await conn.ExecuteScalarAsync<Guid?>(
            """
            SELECT team_id FROM public.team_members
            WHERE user_id = @userId
              AND team_id IN (@t1, @t2)
              AND is_active = TRUE
            LIMIT 1
            """,
            new { userId = userCtx.UserIdGuid, t1 = session.Team1Id, t2 = session.Team2Id });

        var otherTeamId = callerTeamId == session.Team1Id ? session.Team2Id : session.Team1Id;
        var otherStaffIds = (await conn.QueryAsync<Guid>(
            """
            SELECT user_id FROM public.team_members
            WHERE team_id = @teamId AND role IN ('captain', 'manager') AND is_active = TRUE
            """,
            new { teamId = otherTeamId })).ToList();

        foreach (var staffId in otherStaffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, is_read)
                VALUES (@userId, 'scrim_lobby_updated'::notification_type,
                        'Lobby Updated', 'The opposing team updated the lobby details.', FALSE)
                """,
                new { userId = staffId });
        }

        await TeamNotifications.PushAsync(hub, otherStaffIds, "scrim_lobby_updated",
            "Lobby Updated", "The opposing team updated the lobby details.");

        return Results.Ok(updated);
    }

    // ── POST /api/scrims/sessions/{id}/confirm-result ─────────────────────────

    private static async Task<IResult> ConfirmResultAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<NotificationHub> hub,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();


        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionRef>(
            "SELECT team1_id, team2_id, status FROM public.scrim_sessions WHERE id = @id",
            new { id });
        if (session is null) return Results.NotFound();

        var isStaff = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE team_id IN (@t1, @t2)
                  AND user_id = @userId
                  AND role IN ('captain', 'manager')
                  AND is_active = TRUE
            )
            """,
            new { t1 = session.Team1Id, t2 = session.Team2Id, userId = userCtx.UserIdGuid });
        if (!isStaff) return Results.Forbid();

        // Fetch ALL result reports for this session — both teams may have filed one.
        var reports = (await conn.QueryAsync<ScrimResultReportRow>(
            "SELECT * FROM public.scrim_result_reports WHERE session_id = @sessionId ORDER BY submitted_at DESC",
            new { sessionId = id })).ToList();

        if (reports.Count == 0)
            return Results.NotFound(new { error = "No result report found for this session." });

        // Idempotency: if any report is already confirmed, return 200 with it.
        var alreadyConfirmed = reports.FirstOrDefault(r => r.ConfirmedAt is not null);
        if (alreadyConfirmed is not null)
            return Results.Ok(alreadyConfirmed);

        // Security: derive the caller's team from their membership in either party.
        var callerTeamId = await conn.ExecuteScalarAsync<Guid?>(
            """
            SELECT team_id FROM public.team_members
            WHERE user_id = @userId
              AND team_id IN (@t1, @t2)
              AND is_active = TRUE
            LIMIT 1
            """,
            new { userId = userCtx.UserIdGuid, t1 = session.Team1Id, t2 = session.Team2Id });

        if (callerTeamId is null) return Results.Forbid();

        // Find the report filed by the OPPOSING team. The confirming team cannot
        // confirm their own report.
        var report = reports.FirstOrDefault(r => r.ReportingTeamId != callerTeamId);
        if (report is null)
            return Results.Forbid(/* caller has only filed their own report; nothing to confirm yet */);

        using var tx = conn.BeginTransaction();
        try
        {
            // Target the specific opposing-team report by reporting_team_id to avoid
            // accidental multi-row update when both teams have filed reports.
            var confirmedReport = await conn.QuerySingleOrDefaultAsync<ScrimResultReportRow>(
                """
                UPDATE public.scrim_result_reports
                SET confirmed_by_team_id = @confirmedByTeamId,
                    confirmed_at         = now()
                WHERE session_id       = @sessionId
                  AND reporting_team_id = @reportingTeamId
                  AND confirmed_at IS NULL
                RETURNING *
                """,
                new { sessionId = id, confirmedByTeamId = callerTeamId, reportingTeamId = report.ReportingTeamId }, tx);

            if (confirmedReport is null)
                return Results.Conflict(new { error = "Result was already confirmed." });

            // Write canonical scores to the session and mark it completed.
            await conn.ExecuteAsync(
                """
                UPDATE public.scrim_sessions
                SET team1_score = @t1Score,
                    team2_score = @t2Score,
                    status      = 'completed'::scrim_session_status,
                    updated_at  = now()
                WHERE id = @sessionId
                """,
                new { sessionId = id, t1Score = confirmedReport.Team1Score, t2Score = confirmedReport.Team2Score }, tx);

            tx.Commit();

            // Notify both teams after commit.
            var allStaffIds = (await conn.QueryAsync<Guid>(
                """
                SELECT user_id FROM public.team_members
                WHERE team_id IN (@t1, @t2) AND role IN ('captain', 'manager') AND is_active = TRUE
                """,
                new { t1 = session.Team1Id, t2 = session.Team2Id })).ToList();

            foreach (var staffId in allStaffIds)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.notifications (user_id, type, title, message, is_read)
                    VALUES (@userId, 'scrim_result_confirmed'::notification_type,
                            'Result Confirmed', 'The scrim result has been confirmed by both teams.', FALSE)
                    """,
                    new { userId = staffId });
            }

            await TeamNotifications.PushAsync(hub, allStaffIds, "scrim_result_confirmed",
                "Result Confirmed", "The scrim result has been confirmed by both teams.");

            return Results.Ok(confirmedReport);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── POST /api/scrims/sessions/{id}/checkin ────────────────────────────────

    private static async Task<IResult> CheckInAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<NotificationHub> hub,
        ScrimVetoService vetoService,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionCheckinRef>(
            """
            SELECT team1_id, team2_id, status,
                   team1_checked_in_at, team2_checked_in_at,
                   checkin_window_opened_at, game_slug,
                   (SELECT name FROM public.teams WHERE id = ss.team1_id) AS team1_name,
                   (SELECT name FROM public.teams WHERE id = ss.team2_id) AS team2_name
            FROM public.scrim_sessions ss WHERE id = @id
            """,
            new { id });

        if (session is null) return Results.NotFound();
        if (session.Status is "completed" or "cancelled" or "disputed")
            return Results.Conflict(new { error = $"Cannot check in when session is '{session.Status}'." });

        // D4: Check-in is strict — reject if the window is not yet open.
        if (session.CheckinWindowOpenedAt is null)
            return Results.Conflict(new { error = "Check-in window is not open yet. The window opens 15 minutes before the session." });

        var isStaff = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE team_id IN (@t1, @t2)
                  AND user_id = @userId
                  AND role IN ('captain', 'manager')
                  AND is_active = TRUE
            )
            """,
            new { t1 = session.Team1Id, t2 = session.Team2Id, userId = userCtx.UserIdGuid });
        if (!isStaff) return Results.Forbid();

        // Identify which team the caller belongs to.
        var callerTeamId = await conn.ExecuteScalarAsync<Guid?>(
            """
            SELECT team_id FROM public.team_members
            WHERE user_id = @userId
              AND team_id IN (@t1, @t2)
              AND is_active = TRUE
            LIMIT 1
            """,
            new { userId = userCtx.UserIdGuid, t1 = session.Team1Id, t2 = session.Team2Id });

        if (callerTeamId is null) return Results.Forbid();

        var isTeam1 = callerTeamId == session.Team1Id;

        // Idempotency: if already checked in, return 200 immediately.
        if (isTeam1 && session.Team1CheckedInAt is not null)
            return Results.Ok(await FetchSessionWithTeamsAsync(conn, id));
        if (!isTeam1 && session.Team2CheckedInAt is not null)
            return Results.Ok(await FetchSessionWithTeamsAsync(conn, id));

        // Set the relevant timestamp.
        var checkinCol = isTeam1
            ? "team1_checked_in_at = now()"
            : "team2_checked_in_at = now()";

        var updatedSession = await conn.QuerySingleOrDefaultAsync<ScrimSessionCheckinRef>(
            $"""
            UPDATE public.scrim_sessions
            SET {checkinCol}, updated_at = now()
            WHERE id = @id
            RETURNING team1_id, team2_id, status,
                      team1_checked_in_at, team2_checked_in_at
            """,
            new { id });

        if (updatedSession is null) return Results.NotFound();

        var bothCheckedIn = updatedSession.Team1CheckedInAt is not null
                         && updatedSession.Team2CheckedInAt is not null;

        if (bothCheckedIn)
        {
            await conn.ExecuteAsync(
                """
                UPDATE public.scrim_sessions
                SET status = 'in_progress'::scrim_session_status, updated_at = now()
                WHERE id = @id
                """,
                new { id });

            // Best-effort veto creation for eligible games (D4: proceed on failure).
            await vetoService.TryCreateVetoSessionAsync(
                id, session.GameSlug, session.Team1Name ?? "Team 1", session.Team2Name ?? "Team 2", ct);
        }

        // Notify both teams of the check-in state change.
        var allStaffIds = (await conn.QueryAsync<Guid>(
            """
            SELECT user_id FROM public.team_members
            WHERE team_id IN (@t1, @t2) AND role IN ('captain', 'manager') AND is_active = TRUE
            """,
            new { t1 = session.Team1Id, t2 = session.Team2Id })).ToList();

        foreach (var staffId in allStaffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, is_read)
                VALUES (@userId, 'scrim_checkin_updated'::notification_type,
                        'Check-in Update', 'A team checked in for the scrim session.', FALSE)
                """,
                new { userId = staffId });
        }

        await TeamNotifications.PushAsync(hub, allStaffIds, "scrim_checkin_updated",
            "Check-in Update", "A team checked in for the scrim session.");

        if (bothCheckedIn)
        {
            await TeamNotifications.PushAsync(hub, allStaffIds, "scrim_session_started",
                "Session Started", "Both teams have checked in — your scrim is now in progress.");

            // Notify veto is ready if a veto session was created.
            var vetoSessionId = await conn.ExecuteScalarAsync<Guid?>(
                "SELECT veto_session_id FROM public.scrim_sessions WHERE id = @id", new { id });

            if (vetoSessionId.HasValue)
            {
                foreach (var staffId in allStaffIds)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO public.notifications (user_id, type, title, message, is_read)
                        VALUES (@userId, 'scrim_veto_ready'::notification_type,
                                'Map Veto Ready', 'The map veto is ready. Check the session for your team link.', FALSE)
                        """,
                        new { userId = staffId });
                }

                await TeamNotifications.PushAsync(hub, allStaffIds, "scrim_veto_ready",
                    "Map Veto Ready", "The map veto is ready. Check the session for your team link.");
            }
        }

        return Results.Ok(await FetchSessionWithTeamsAsync(conn, id));
    }

    private static Task<ScrimSessionRow?> FetchSessionWithTeamsAsync(
        System.Data.IDbConnection conn, Guid id) =>
        conn.QuerySingleOrDefaultAsync<ScrimSessionRow>(
            """
            SELECT ss.*,
                   t1.name AS team1_name, t1.logo_url AS team1_logo,
                   t2.name AS team2_name, t2.logo_url AS team2_logo
            FROM public.scrim_sessions ss
            JOIN public.teams t1 ON t1.id = ss.team1_id
            JOIN public.teams t2 ON t2.id = ss.team2_id
            WHERE ss.id = @id
            """,
            new { id });

    // ── GET /api/scrims/listings/{id}/slots ───────────────────────────────────

    private static async Task<IResult> GetSlotsAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var listing = await conn.QuerySingleOrDefaultAsync<ScrimListingRef>(
            "SELECT team_id, status FROM public.scrim_listings WHERE id = @id", new { id });
        if (listing is null) return Results.NotFound();
        if (listing.Status != "open") return Results.NotFound();

        limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        offset = Math.Max(offset, 0);

        var rows = await conn.QueryAsync<ScrimSlotRow>(
            """
            SELECT id, listing_id, starts_at, ends_at, status, created_at,
                   booked_by_team_id, session_id
            FROM public.scrim_slots
            WHERE listing_id = @listingId
              AND status != 'cancelled'
              AND starts_at > now()
            ORDER BY starts_at
            LIMIT @limit OFFSET @offset
            """,
            new { listingId = id, limit, offset });

        return Results.Ok(rows);
    }

    // ── POST /api/scrims/listings/{id}/slots ──────────────────────────────────

    private static async Task<IResult> CreateSlotsAsync(
        Guid id,
        [FromBody] CreateSlotsRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.Slots is null || req.Slots.Length == 0)
            return Results.BadRequest(new { error = "At least one slot is required." });
        if (req.Slots.Length > 100)
            return Results.BadRequest(new { error = "Maximum 100 slots per request." });

        using var conn = db.CreateConnection();

        var listing = await conn.QuerySingleOrDefaultAsync<ScrimListingTypeRef>(
            "SELECT team_id, status, schedule_type FROM public.scrim_listings WHERE id = @id", new { id });
        if (listing is null) return Results.NotFound();
        if (listing.ScheduleType != "recurring")
            return Results.BadRequest(new { error = "Slots can only be created for recurring listings." });
        if (!await IsTeamStaffAsync(conn, listing.TeamId, userCtx.UserIdGuid)) return Results.Forbid();

        var created = new List<ScrimSlotRow>();
        foreach (var slot in req.Slots)
        {
            if (slot.StartsAt >= slot.EndsAt)
                return Results.UnprocessableEntity(new { error = "starts_at must be before ends_at for each slot." });

            // ON CONFLICT DO NOTHING: idempotent via UNIQUE(listing_id, starts_at).
            var row = await conn.QuerySingleOrDefaultAsync<ScrimSlotRow>(
                """
                INSERT INTO public.scrim_slots (listing_id, starts_at, ends_at)
                VALUES (@listingId, @startsAt, @endsAt)
                ON CONFLICT (listing_id, starts_at) DO NOTHING
                RETURNING id, listing_id, starts_at, ends_at, status, created_at, booked_by_team_id, session_id
                """,
                new { listingId = id, startsAt = slot.StartsAt, endsAt = slot.EndsAt });

            if (row is not null) created.Add(row);
        }

        return Results.Ok(new { created = created.Count, slots = created });
    }

    // ── GET /api/scrims/sessions/{id}/recent-matches ───────────────────────────

    private static async Task<IResult> GetSessionRecentMatchesAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        RiotApiClient riotApi,
        HybridCache cache,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionRef>(
            "SELECT team1_id, team2_id, status FROM public.scrim_sessions WHERE id = @id", new { id });
        if (session is null) return Results.NotFound();
        if (session.Status != "in_progress")
            return Results.Conflict(new { error = "Recent matches are only available while the session is in progress." });

        var isStaff = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE team_id IN (@t1, @t2)
                  AND user_id = @userId
                  AND role IN ('captain', 'manager')
                  AND is_active = TRUE
            )
            """,
            new { t1 = session.Team1Id, t2 = session.Team2Id, userId = userCtx.UserIdGuid });
        if (!isStaff) return Results.Forbid();

        var callerTeamId = await conn.ExecuteScalarAsync<Guid?>(
            """
            SELECT team_id FROM public.team_members
            WHERE user_id = @userId AND team_id IN (@t1, @t2) AND is_active = TRUE LIMIT 1
            """,
            new { userId = userCtx.UserIdGuid, t1 = session.Team1Id, t2 = session.Team2Id });
        if (callerTeamId is null) return Results.Forbid();

        var callerAccounts = (await conn.QueryAsync<dynamic>(
            """
            SELECT ra.puuid, ra.region FROM riot_accounts ra
            JOIN team_members tm ON tm.user_id = ra.user_id
            WHERE tm.team_id = @teamId AND ra.puuid IS NOT NULL AND tm.is_active = TRUE
            """,
            new { teamId = callerTeamId.Value })).AsList();

        if (callerAccounts.Count == 0)
            return Results.Ok(new { matches = Array.Empty<object>(), reason = "No linked Riot accounts found for your team" });

        var allAccounts = (await conn.QueryAsync<dynamic>(
            """
            SELECT ra.puuid, tm.team_id FROM riot_accounts ra
            JOIN team_members tm ON tm.user_id = ra.user_id
            WHERE tm.team_id IN (@t1, @t2) AND ra.puuid IS NOT NULL AND tm.is_active = TRUE
            """,
            new { t1 = session.Team1Id, t2 = session.Team2Id })).AsList();

        var team1Puuids = allAccounts
            .Where(a => (Guid)a.team_id == session.Team1Id).Select(a => (string)a.puuid).ToHashSet();
        var team2Puuids = allAccounts
            .Where(a => (Guid)a.team_id == session.Team2Id).Select(a => (string)a.puuid).ToHashSet();

        var shard = await RiotShards.ResolveAsync(riotApi, cache, (string)callerAccounts[0].puuid, (string?)callerAccounts[0].region, ct);
        var matchIds = await CollectScrimMatchIdsAsync(callerAccounts, shard, riotApi, ct);

        if (matchIds.Count == 0)
            return Results.Ok(new { matches = Array.Empty<object>(), reason = "No recent matches in Riot history" });

        var log = loggerFactory.CreateLogger("ScrimEndpoints.RecentMatches");
        var candidates = new List<object>();
        foreach (var riotMatchId in matchIds.Take(20))
        {
            var (detStatus, detBody) = await riotApi.ProxyAsync(shard, $"/val/match/v1/matches/{riotMatchId}", ct);
            if (detStatus != 200) continue;
            try
            {
                using var doc = JsonDocument.Parse(detBody);
                var candidate = BuildScrimCandidate(doc.RootElement, riotMatchId, callerAccounts, team1Puuids, team2Puuids);
                if (candidate is not null) candidates.Add(candidate);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to parse Riot match {MatchId}", riotMatchId);
            }
        }

        return Results.Ok(new { matches = candidates });
    }

    // ── POST /api/scrims/sessions/{id}/reports/parse-screenshot ───────────────

    private static async Task<IResult> ParseScrimScreenshotAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        ScoreboardOcrClient ocr,
        ScoreboardScreenshotStore store,
        RiotIdRefreshService riotIds,
        IRiotMatchLookup riotMatches,
        HybridCache cache,
        CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext userCtx) return Results.Unauthorized();
        if (!ocr.IsConfigured)
            return Results.Json(new { error = "Screenshot reading is not available right now." }, statusCode: 503);

        var ocrEnabled = await cache.GetOrCreateAsync(
            "ocr:kill-switch",
            async _ =>
            {
                using var c = db.CreateConnection();
                return await c.ExecuteScalarAsync<string>(
                    "SELECT value FROM system_settings WHERE key = 'scoreboard_ocr_enabled'") ?? "false";
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) },
            cancellationToken: ct);
        if (ocrEnabled != "true")
            return Results.Json(new { error = "Screenshot reading is not available right now.", ocrEnabled = false }, statusCode: 503);

        if (!ctx.Request.HasFormContentType)
            return Results.BadRequest(new { error = "Send the screenshot as multipart form data." });
        var form = await ctx.Request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["reportedByTeamId"].FirstOrDefault(), out var reportingTeamId))
            return Results.BadRequest(new { error = "Invalid competitor ID" });
        var file = form.Files.GetFile("file");
        if (file is null || file.Length is 0 or > ScreenshotValidator.MaxBytes)
            return Results.BadRequest(new { error = "Upload one screenshot of 10 MB or less." });

        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionForOcr>(
            "SELECT team1_id, team2_id, status, game_slug FROM public.scrim_sessions WHERE id = @id", new { id });
        if (session is null) return Results.NotFound();
        if (session.Status != "in_progress")
            return Results.Conflict(new { error = "Screenshots can only be uploaded while the session is in progress." });
        if (session.GameSlug?.Contains("valorant", StringComparison.OrdinalIgnoreCase) != true)
            return Results.BadRequest(new { error = "Screenshot reading currently supports Valorant only." });

        var isParty = reportingTeamId == session.Team1Id || reportingTeamId == session.Team2Id;
        if (!isParty) return Results.BadRequest(new { error = "Reporting team is not a party to this session." });
        if (!await IsTeamStaffAsync(conn, reportingTeamId, userCtx.UserIdGuid))
            return Results.Json(new { error = "Only a captain or manager of this team can submit a report." }, statusCode: 403);

        var bytes = await ScoreboardOcrEndpoints.ReadAllAsync(file, ct);
        var check = ScreenshotValidator.Validate(file.FileName, bytes);
        if (!check.Ok) return Results.BadRequest(new { error = check.Error });

        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            budget.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var userIds = await ScoreboardRosterLoader.LoadUserIdsAsync(conn, session.Team1Id, session.Team2Id);
                await riotIds.RefreshUsersAsync(userIds, TimeSpan.FromHours(6), budget.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException && !ct.IsCancellationRequested) { }
        }

        var rosters = await ScoreboardRosterLoader.LoadAsync(conn, session.Team1Id, session.Team2Id);
        var azureEnabled = await cache.GetOrCreateAsync(
            "ocr:engine-setting",
            async _ =>
            {
                using var c = db.CreateConnection();
                return await c.ExecuteScalarAsync<string>(
                    "SELECT value FROM system_settings WHERE key = 'azure_ocr_enabled'") ?? "false";
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60) },
            cancellationToken: ct);
        var engine = azureEnabled == "true" ? "azure" : "paddle";
        var call = await ocr.ParseValorantAsync(bytes, $"screenshot{check.Extension}", check.ContentType, rosters, engine, ct);
        if (call.Status == OcrCallStatus.Unreadable) return Results.UnprocessableEntity(new { error = call.UserMessage });
        if (call.Status != OcrCallStatus.Success) return Results.Json(new { error = call.UserMessage }, statusCode: 502);

        var parseId = Guid.NewGuid();
        var path = ScoreboardScreenshotStore.BuildPath(id, parseId, check.Extension);
        if (!await store.UploadAsync(path, bytes, check.ContentType, ct))
            return Results.Json(new { error = "Could not store the screenshot. Please try again." }, statusCode: 502);

        using var result = JsonDocument.Parse(call.ResultJson!);
        var verification = ScoreboardVerification.FromParse(result.RootElement, rosters);

        var linked = await ScoreboardRosterLoader.LoadLinkedAccountsAsync(conn, session.Team1Id, session.Team2Id);
        var reportingLabel = reportingTeamId == session.Team1Id ? "team1" : "team2";
        var puuids = linked
            .OrderBy(a => a.Team == reportingLabel ? 0 : 1)
            .Select(a => a.Puuid).Distinct().Take(3).ToList();
        var query = ScoreboardRiotMatcher.QueryFrom(result.RootElement, DateTimeOffset.UtcNow, ScoreboardRiotMatcher.MatchWindow);
        var riot = await ScoreboardRiotMatcher.FindAsync(riotMatches, puuids, query, TimeSpan.FromSeconds(6), ct);
        object? candidate = null;
        if (riot.Match is { } riotMatch)
        {
            verification = ScoreboardVerification.ApplyRiotMatch(
                verification, linked, riotMatch, [], ScoreboardRiotMatcher.ScoreFrom(result.RootElement), DateTimeOffset.UtcNow);
            if (ScoreboardRiotMatcher.SameGameAllySide(result.RootElement, riotMatch) is { } allySide)
                candidate = ScoreboardRiotMatcher.Candidate(riotMatch, allySide, ScoreboardRiotMatcher.AllyTeamOf(result.RootElement), reportingLabel, linked);
        }

        return Results.Ok(new
        {
            parseId,
            screenshotUrl = store.PublicUrl(path),
            mapId = await ResolveScrimMapIdAsync(conn, result.RootElement),
            team1Id = session.Team1Id,
            team2Id = session.Team2Id,
            result = result.RootElement.Clone(),
            verification = System.Text.Json.JsonSerializer.SerializeToElement(verification, OcrVerificationJson.Options),
            riot = new
            {
                status = riot.Status,
                confident = candidate is not null,
                candidate,
                match = riot.Match is null ? null : ScoreboardRiotMatcher.Payload(riot.Match, linked),
            },
        });
    }

    // ── GET /api/scrims/teams/{teamId}/history ─────────────────────────────────

    private static async Task<IResult> GetTeamScrimHistoryAsync(
        Guid teamId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var offset = (page - 1) * pageSize;

        using var conn = db.CreateConnection();

        var teamRow = await conn.QuerySingleOrDefaultAsync<TeamPrivacyRow>(
            "SELECT scrim_history_public FROM public.teams WHERE id = @teamId", new { teamId });
        if (teamRow is null) return Results.NotFound();

        var callerCtx = ctx.Items["UserContext"] as UserContext;
        bool computeResult;

        if (!teamRow.ScrimHistoryPublic)
        {
            if (callerCtx is null) return Results.Unauthorized();
            var isMember = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM public.team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE)",
                new { teamId, userId = callerCtx.UserIdGuid });
            if (!isMember) return Results.Forbid();
            computeResult = true;
        }
        else
        {
            computeResult = callerCtx is not null && await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM public.team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE)",
                new { teamId, userId = callerCtx?.UserIdGuid ?? Guid.Empty });
        }

        var total = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM public.scrim_sessions
            WHERE (team1_id = @teamId OR team2_id = @teamId) AND status = 'completed'
            """,
            new { teamId });

        var rows = (await conn.QueryAsync<ScrimHistoryRow>(
            """
            SELECT ss.id, ss.scheduled_at, ss.game_slug,
                   ss.team1_id, t1.name AS team1_name,
                   ss.team2_id, t2.name AS team2_name,
                   ss.team1_score, ss.team2_score,
                   ss.updated_at AS completed_at,
                   pvs.selected_map_id AS raw_map_id
            FROM public.scrim_sessions ss
            JOIN public.teams t1 ON t1.id = ss.team1_id
            JOIN public.teams t2 ON t2.id = ss.team2_id
            LEFT JOIN public.public_veto_sessions pvs ON pvs.id = ss.veto_session_id
            WHERE (ss.team1_id = @teamId OR ss.team2_id = @teamId)
              AND ss.status = 'completed'
            ORDER BY ss.updated_at DESC
            LIMIT @pageSize OFFSET @offset
            """,
            new { teamId, pageSize, offset })).ToList();

        return Results.Ok(new
        {
            items = rows.Select(row => new
            {
                id = row.Id,
                scheduled_at = row.ScheduledAt,
                game_slug = row.GameSlug,
                team1_id = row.Team1Id,
                team1_name = row.Team1Name,
                team2_id = row.Team2Id,
                team2_name = row.Team2Name,
                team1_score = row.Team1Score,
                team2_score = row.Team2Score,
                result = DeriveScrimResult(teamId, row, computeResult),
                map_name = string.IsNullOrWhiteSpace(row.RawMapId) ? null : ValorantMapIds.Resolve(row.RawMapId),
                completed_at = row.CompletedAt,
            }),
            total,
            page,
            pageSize,
        });
    }

    // ── PATCH /api/scrims/teams/{teamId}/history/privacy ──────────────────────

    private static async Task<IResult> UpdateHistoryPrivacyAsync(
        Guid teamId,
        [FromBody] UpdateHistoryPrivacyRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM public.teams WHERE id = @teamId)", new { teamId });
        if (!exists) return Results.NotFound();
        if (!await IsTeamStaffAsync(conn, teamId, userCtx.UserIdGuid)) return Results.Forbid();

        await conn.ExecuteAsync(
            "UPDATE public.teams SET scrim_history_public = @isPublic WHERE id = @teamId",
            new { teamId, isPublic = req.Public });

        return Results.Ok(new { scrimHistoryPublic = req.Public });
    }

    // ── Scrim scan helpers ─────────────────────────────────────────────────────

    private static async Task<List<string>> CollectScrimMatchIdsAsync(
        List<dynamic> accounts, string shard, RiotApiClient riotApi, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var account in accounts)
        {
            var (status, body) = await riotApi.ProxyAsync(shard, $"/val/match/v1/matchlists/by-puuid/{(string)account.puuid}", ct);
            if (status != 200) continue;
            using var doc = JsonDocument.Parse(body);
            foreach (var entry in doc.RootElement.GetProperty("history").EnumerateArray())
            {
                var matchId = entry.GetProperty("matchId").GetString()!;
                if (seen.Add(matchId)) ordered.Add(matchId);
            }
        }
        return ordered;
    }

    private static object? BuildScrimCandidate(
        JsonElement root, string riotMatchId, List<dynamic> accounts,
        HashSet<string> team1Puuids, HashSet<string> team2Puuids)
    {
        foreach (var account in accounts)
        {
            var result = RiotMatchCandidates.Build(root, riotMatchId, (string)account.puuid, team1Puuids, team2Puuids);
            if (result is not null) return result;
        }
        return null;
    }

    private static string? DeriveScrimResult(Guid teamId, ScrimHistoryRow row, bool compute)
    {
        if (!compute || row.Team1Score is null || row.Team2Score is null) return null;
        var (mine, theirs) = teamId == row.Team1Id
            ? (row.Team1Score.Value, row.Team2Score.Value)
            : (row.Team2Score.Value, row.Team1Score.Value);
        return mine > theirs ? "win" : mine < theirs ? "loss" : "draw";
    }

    private static async Task<Guid?> ResolveScrimMapIdAsync(System.Data.IDbConnection conn, JsonElement result)
    {
        if (!result.TryGetProperty("map", out var map) || !map.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Object) return null;
        var name = value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return await conn.QueryFirstOrDefaultAsync<Guid?>(
            """
            SELECT id FROM game_maps
            WHERE game ILIKE '%valorant%' AND is_active = TRUE AND lower(map_name) = lower(@name)
            LIMIT 1
            """,
            new { name });
    }

    // ── Practice endpoints ─────────────────────────────────────────────────────

    private static async Task<IResult> CreatePracticeAsync(
        [FromBody] CreatePracticeSessionRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.TeamId == Guid.Empty) return Results.BadRequest(new { error = "team_id is required." });
        if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "title is required." });
        if (req.Title.Length > 200) return Results.BadRequest(new { error = "title max 200 chars." });
        if (req.Notes?.Length > 500) return Results.BadRequest(new { error = "notes max 500 chars." });

        using var conn = db.CreateConnection();
        if (!await IsTeamStaffAsync(conn, req.TeamId, userCtx.UserIdGuid)) return Results.Forbid();

        var row = await conn.QuerySingleAsync<PracticeSessionRow>(
            """
            INSERT INTO public.practice_sessions
                (team_id, game_slug, title, scheduled_at, duration_minutes, notes, created_by)
            VALUES (@teamId, @gameSlug, @title, @scheduledAt, @durationMinutes, @notes, @createdBy)
            RETURNING *
            """,
            new
            {
                teamId = req.TeamId,
                gameSlug = req.GameSlug ?? "",
                title = req.Title,
                scheduledAt = req.ScheduledAt,
                durationMinutes = req.DurationMinutes,
                notes = req.Notes,
                createdBy = userCtx.UserIdGuid,
            });

        return Results.Created($"/api/practice/sessions/{row.Id}", row);
    }

    private static async Task<IResult> GetTeamPracticeAsync(
        [FromQuery] Guid? team_id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (team_id is null) return Results.BadRequest(new { error = "team_id is required." });

        using var conn = db.CreateConnection();
        var isMember = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM public.team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE)",
            new { teamId = team_id.Value, userId = userCtx.UserIdGuid });
        if (!isMember) return Results.Forbid();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        offset = Math.Max(offset, 0);

        var rows = await conn.QueryAsync<PracticeSessionRow>(
            """
            SELECT ps.*, t.name AS team_name
            FROM public.practice_sessions ps JOIN public.teams t ON t.id = ps.team_id
            WHERE ps.team_id = @teamId
            ORDER BY ps.created_at DESC LIMIT @limit OFFSET @offset
            """,
            new { teamId = team_id.Value, limit, offset });

        return Results.Ok(rows);
    }

    private static async Task<IResult> GetPracticeAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<PracticeSessionRow>(
            """
            SELECT ps.*, t.name AS team_name
            FROM public.practice_sessions ps JOIN public.teams t ON t.id = ps.team_id
            WHERE ps.id = @id AND EXISTS(
                SELECT 1 FROM public.team_members tm
                WHERE tm.team_id = ps.team_id AND tm.user_id = @userId AND tm.is_active = TRUE
            )
            """,
            new { id, userId = userCtx.UserIdGuid });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    private static async Task<IResult> UpdatePracticeAsync(
        Guid id,
        [FromBody] UpdatePracticeSessionRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var teamId = await conn.ExecuteScalarAsync<Guid?>("SELECT team_id FROM public.practice_sessions WHERE id = @id", new { id });
        if (teamId is null) return Results.NotFound();
        if (!await IsTeamStaffAsync(conn, teamId.Value, userCtx.UserIdGuid)) return Results.Forbid();

        var updated = await conn.QuerySingleOrDefaultAsync<PracticeSessionRow>(
            """
            UPDATE public.practice_sessions
            SET title            = COALESCE(@title, title),
                scheduled_at     = COALESCE(@scheduledAt, scheduled_at),
                duration_minutes = COALESCE(@durationMinutes, duration_minutes),
                notes            = COALESCE(@notes, notes),
                updated_at       = now()
            WHERE id = @id RETURNING *
            """,
            new { id, title = req.Title, scheduledAt = req.ScheduledAt, durationMinutes = req.DurationMinutes, notes = req.Notes });

        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static Task<IResult> CompletePracticeAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
        => TransitionPracticeAsync(id, ctx, db, "completed", ["scheduled"]);

    private static Task<IResult> CancelPracticeAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
        => TransitionPracticeAsync(id, ctx, db, "cancelled", ["scheduled"]);

    private static async Task<IResult> GetAttendanceAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var teamId = await conn.ExecuteScalarAsync<Guid?>("SELECT team_id FROM public.practice_sessions WHERE id = @id", new { id });
        if (teamId is null) return Results.NotFound();

        var isMember = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM public.team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE)",
            new { teamId = teamId.Value, userId = userCtx.UserIdGuid });
        if (!isMember) return Results.Forbid();

        var rows = await conn.QueryAsync<PracticeAttendanceRow>(
            """
            SELECT pa.*, pr.username, pr.avatar_seed
            FROM public.practice_attendance pa JOIN public.profiles pr ON pr.id = pa.user_id
            WHERE pa.session_id = @sessionId
            """,
            new { sessionId = id });

        return Results.Ok(rows);
    }

    private static async Task<IResult> UpsertAttendanceAsync(
        Guid id,
        [FromBody] UpsertAttendanceRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var teamId = await conn.ExecuteScalarAsync<Guid?>("SELECT team_id FROM public.practice_sessions WHERE id = @id", new { id });
        if (teamId is null) return Results.NotFound();
        if (!await IsTeamStaffAsync(conn, teamId.Value, userCtx.UserIdGuid)) return Results.Forbid();

        foreach (var entry in req.Entries ?? [])
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.practice_attendance (session_id, user_id, status)
                VALUES (@sessionId, @userId, @status::practice_attendance_status)
                ON CONFLICT (session_id, user_id) DO UPDATE SET status = @status::practice_attendance_status
                """,
                new { sessionId = id, userId = entry.UserId, status = entry.Status });
        }

        return Results.Ok(new { success = true });
    }

    private static async Task<IResult> UpdateOwnAttendanceAsync(
        Guid id,
        [FromBody] UpdateOwnAttendanceRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        var status = req.Status?.ToLower();
        if (status is not ("confirmed" or "excused"))
            return Results.BadRequest(new { error = "status must be 'confirmed' or 'excused'." });

        using var conn = db.CreateConnection();

        var updated = await conn.QuerySingleOrDefaultAsync<PracticeAttendanceRow>(
            """
            UPDATE public.practice_attendance
            SET status = @status::practice_attendance_status
            WHERE id = @id AND user_id = @userId
            RETURNING *
            """,
            new { id, userId = userCtx.UserIdGuid, status });

        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    // ── Transition helpers ─────────────────────────────────────────────────────

    private static async Task<IResult> TransitionListingAsync(
        Guid id, HttpContext ctx, IDbConnectionFactory db, string target, string[] from)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<ScrimListingRef>(
            "SELECT team_id, status FROM public.scrim_listings WHERE id = @id", new { id });
        if (row is null) return Results.NotFound();
        if (!await IsTeamStaffAsync(conn, row.TeamId, userCtx.UserIdGuid)) return Results.Forbid();
        if (!from.Contains(row.Status))
            return Results.BadRequest(new { error = $"Cannot transition from '{row.Status}' to '{target}'." });

        var updated = await conn.QuerySingleOrDefaultAsync<ScrimListingRow>(
            "UPDATE public.scrim_listings SET status = @target::scrim_listing_status, updated_at = now() WHERE id = @id RETURNING *",
            new { id, target });
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> TransitionSessionAsync(
        Guid id, HttpContext ctx, IDbConnectionFactory db, string target, string[] from)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<ScrimSessionRef>(
            "SELECT team1_id, team2_id, status FROM public.scrim_sessions WHERE id = @id", new { id });
        if (row is null) return Results.NotFound();

        var isStaff = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members tm
                WHERE tm.team_id IN (@t1, @t2)
                  AND tm.user_id = @userId
                  AND tm.role IN ('captain', 'manager')
                  AND tm.is_active = TRUE
            )
            """,
            new { t1 = row.Team1Id, t2 = row.Team2Id, userId = userCtx.UserIdGuid });
        if (!isStaff) return Results.Forbid();

        if (!from.Contains(row.Status))
            return Results.BadRequest(new { error = $"Cannot transition from '{row.Status}' to '{target}'." });

        var updated = await conn.QuerySingleOrDefaultAsync<ScrimSessionRow>(
            "UPDATE public.scrim_sessions SET status = @target::scrim_session_status, updated_at = now() WHERE id = @id RETURNING *",
            new { id, target });
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> TransitionPracticeAsync(
        Guid id, HttpContext ctx, IDbConnectionFactory db, string target, string[] from)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<PracticeSessionRef>(
            "SELECT team_id, status FROM public.practice_sessions WHERE id = @id", new { id });
        if (row is null) return Results.NotFound();
        if (!await IsTeamStaffAsync(conn, row.TeamId, userCtx.UserIdGuid)) return Results.Forbid();
        if (!from.Contains(row.Status))
            return Results.BadRequest(new { error = $"Cannot transition from '{row.Status}' to '{target}'." });

        var updated = await conn.QuerySingleOrDefaultAsync<PracticeSessionRow>(
            "UPDATE public.practice_sessions SET status = @target::practice_session_status, updated_at = now() WHERE id = @id RETURNING *",
            new { id, target });
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<IResult> ChangeOfferStatusAsync(
        Guid id, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub, string target, bool requiresListingTeam)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var offer = await conn.QuerySingleOrDefaultAsync<ScrimOfferRef>(
            """
            SELECT so.listing_id, so.offering_team_id, sl.team_id AS listing_team_id
            FROM public.scrim_offers so JOIN public.scrim_listings sl ON sl.id = so.listing_id
            WHERE so.id = @id AND so.status = 'pending'
            """,
            new { id });
        if (offer is null) return Results.NotFound();

        var checkTeamId = requiresListingTeam ? offer.ListingTeamId : offer.OfferingTeamId;
        if (!await IsTeamStaffAsync(conn, checkTeamId, userCtx.UserIdGuid)) return Results.Forbid();

        var updated = await conn.QuerySingleOrDefaultAsync<ScrimOfferRow>(
            "UPDATE public.scrim_offers SET status = @target::scrim_offer_status, updated_at = now() WHERE id = @id RETURNING *",
            new { id, target });
        if (updated is null) return Results.NotFound();

        // Gap 9+1: Notify the affected team and push real-time.
        Guid notifyTeamId;
        string notifType;
        string notifTitle;
        string notifMessage;

        if (target == "declined")
        {
            // Listing team declined → notify offering team.
            notifyTeamId = offer.OfferingTeamId;
            notifType = "scrim_offer_declined";
            notifTitle = "Scrim Offer Declined";
            notifMessage = "Your scrim offer was declined.";
        }
        else if (target == "withdrawn")
        {
            // Offering team withdrew → notify listing team.
            notifyTeamId = offer.ListingTeamId;
            notifType = "scrim_offer_withdrawn";
            notifTitle = "Scrim Offer Withdrawn";
            notifMessage = "A scrim offer was withdrawn.";
        }
        else
        {
            return Results.Ok(updated);
        }

        var staffIds = (await conn.QueryAsync<Guid>(
            """
            SELECT user_id FROM public.team_members
            WHERE team_id = @teamId AND role IN ('captain', 'manager') AND is_active = TRUE
            """,
            new { teamId = notifyTeamId })).ToList();

        foreach (var staffId in staffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, is_read)
                VALUES (@userId, @notifType::notification_type, @notifTitle, @notifMessage, FALSE)
                """,
                new { userId = staffId, notifType, notifTitle, notifMessage });
        }

        await TeamNotifications.PushAsync(hub, staffIds, notifType, notifTitle, notifMessage);

        return Results.Ok(updated);
    }

    /// <summary>
    /// After a scrim session is committed, opens a direct inbox conversation between
    /// the primary captain of each team and sends a system message with session context.
    /// Failures are logged and swallowed — the session acceptance must not roll back.
    /// </summary>
    private static async Task OpenScrimConversationAsync(
        System.Data.IDbConnection conn,
        Guid listingTeamId,
        Guid offeringTeamId,
        ScrimSessionRow session,
        ILogger logger)
    {
        try
        {
            var listingCaptain = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT user_id FROM public.team_members
                WHERE team_id = @teamId AND role = 'captain' AND is_active = TRUE
                ORDER BY user_id LIMIT 1
                """, new { teamId = listingTeamId });

            var offeringCaptain = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT user_id FROM public.team_members
                WHERE team_id = @teamId AND role = 'captain' AND is_active = TRUE
                ORDER BY user_id LIMIT 1
                """, new { teamId = offeringTeamId });

            if (listingCaptain is null || offeringCaptain is null) return;

            var conversation = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT c.id FROM public.conversations c
                JOIN public.conversation_participants a
                    ON a.conversation_id = c.id AND a.user_id = @userId AND a.is_active
                JOIN public.conversation_participants b
                    ON b.conversation_id = c.id AND b.user_id = @otherId AND b.is_active
                WHERE c.type = 'direct'
                  AND NOT EXISTS (
                    SELECT 1 FROM public.conversation_participants extra
                    WHERE extra.conversation_id = c.id
                      AND extra.user_id NOT IN (@userId, @otherId))
                ORDER BY c.created_at, c.id LIMIT 1
                """,
                new { userId = listingCaptain.Value, otherId = offeringCaptain.Value });

            if (conversation is null)
            {
                conversation = await conn.QuerySingleAsync<Guid>(
                    "INSERT INTO public.conversations (type, created_by) VALUES ('direct', @userId) RETURNING id",
                    new { userId = listingCaptain.Value });
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.conversation_participants (conversation_id, user_id)
                    VALUES (@c, @a), (@c, @b) ON CONFLICT DO NOTHING
                    """,
                    new { c = conversation.Value, a = listingCaptain.Value, b = offeringCaptain.Value });
            }

            var scheduledStr = session.ScheduledAt?.ToString("yyyy-MM-dd HH:mm UTC") ?? "TBD";
            var content = $"Scrim booked: {session.GameSlug ?? "Unknown"} — {scheduledStr} — Match Room: /competitive/scrims/sessions/{session.Id}";
            await conn.ExecuteAsync(
                """
                INSERT INTO public.messages (conversation_id, sender_id, content, message_type)
                VALUES (@conversationId, @senderId, @content, 'system')
                """,
                new { conversationId = conversation.Value, senderId = listingCaptain.Value, content });
            await conn.ExecuteAsync(
                "UPDATE public.conversations SET updated_at = now() WHERE id = @id",
                new { id = conversation.Value });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to open scrim inbox conversation for session {SessionId}.", session.Id);
        }
    }

    /// <summary>
    /// Generates available scrim slots for a recurring listing based on its preferred availability.
    /// Idempotent — ON CONFLICT (listing_id, starts_at) DO NOTHING.
    /// Only generates future slots within the next 4 weeks (or schedule_until if sooner).
    /// </summary>
    private static async Task GenerateSlotsAsync(System.Data.IDbConnection conn, Guid listingId)
    {
        var listing = await conn.QuerySingleOrDefaultAsync<ScrimListingRow>(
            "SELECT * FROM public.scrim_listings WHERE id = @listingId",
            new { listingId });

        if (listing is null
            || listing.ScheduleType != "recurring"
            || listing.PreferredDays is not { Length: > 0 }
            || listing.PreferredTimeStart is null
            || listing.PreferredTimeEnd is null
            || listing.SlotDurationMinutes is null or <= 0)
            return;

        var tz = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(listing.Timezone))
        {
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(listing.Timezone); }
            catch { /* unrecognised IANA id — fall back to UTC */ }
        }

        var nowUtc = DateTime.UtcNow;
        var endUtc = nowUtc.AddDays(28);
        if (listing.ScheduleUntil.HasValue && listing.ScheduleUntil.Value < endUtc)
            endUtc = listing.ScheduleUntil.Value;

        var preferredDays = listing.PreferredDays.ToHashSet();
        var slotDuration = TimeSpan.FromMinutes(listing.SlotDurationMinutes.Value);
        var timeStart = listing.PreferredTimeStart.Value.ToTimeSpan();
        var timeEnd = listing.PreferredTimeEnd.Value.ToTimeSpan();

        if (timeEnd <= timeStart || slotDuration <= TimeSpan.Zero) return;

        var nowInTz = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var date = nowInTz.Date;

        while (true)
        {
            DateTime dayUtcStart;
            try
            {
                dayUtcStart = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(date, DateTimeKind.Unspecified), tz);
            }
            catch { date = date.AddDays(1); continue; }

            if (dayUtcStart >= endUtc) break;

            var dotNetDow = (int)date.DayOfWeek; // 0=Sunday … 6=Saturday (matches frontend convention)
            if (preferredDays.Contains(dotNetDow))
            {
                for (var t = timeStart; t + slotDuration <= timeEnd; t += slotDuration)
                {
                    DateTime utcStart, utcEnd;
                    try
                    {
                        utcStart = TimeZoneInfo.ConvertTimeToUtc(
                            DateTime.SpecifyKind(date.Add(t), DateTimeKind.Unspecified), tz);
                        utcEnd = TimeZoneInfo.ConvertTimeToUtc(
                            DateTime.SpecifyKind(date.Add(t + slotDuration), DateTimeKind.Unspecified), tz);
                    }
                    catch { continue; } // skip ambiguous DST transitions

                    if (utcStart <= nowUtc) continue; // only future slots

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO public.scrim_slots (listing_id, starts_at, ends_at)
                        VALUES (@listingId, @startsAt, @endsAt)
                        ON CONFLICT (listing_id, starts_at) DO NOTHING
                        """,
                        new { listingId, startsAt = utcStart, endsAt = utcEnd });
                }
            }

            date = date.AddDays(1);
        }
    }

    private static async Task<bool> IsTeamStaffAsync(System.Data.IDbConnection conn, Guid teamId, Guid userId)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE team_id = @teamId AND user_id = @userId
                  AND role IN ('captain', 'manager') AND is_active = TRUE
            )
            """,
            new { teamId, userId });
    }

    // ── Private auth records ───────────────────────────────────────────────────

    private sealed record ScrimSessionForOcr(Guid Team1Id, Guid Team2Id, string Status, string? GameSlug);
    private sealed record ScrimHistoryRow(
        Guid Id, DateTime? ScheduledAt, string? GameSlug,
        Guid Team1Id, string? Team1Name, Guid Team2Id, string? Team2Name,
        int? Team1Score, int? Team2Score,
        DateTime CompletedAt, string? RawMapId);
    private sealed record TeamPrivacyRow(bool ScrimHistoryPublic);

    private sealed record ScrimListingRef(Guid TeamId, string Status);
    private sealed record ScrimSessionRef(Guid Team1Id, Guid Team2Id, string Status);
    private sealed record ScrimSessionParties(Guid Team1Id, Guid Team2Id);
    private sealed record ScrimSessionCheckinRef(
        Guid Team1Id, Guid Team2Id, string Status,
        DateTimeOffset? Team1CheckedInAt, DateTimeOffset? Team2CheckedInAt,
        DateTimeOffset? CheckinWindowOpenedAt = null,
        string? GameSlug = null,
        string? Team1Name = null,
        string? Team2Name = null);
    private sealed record ScrimOfferForAccept(
        Guid Id, Guid ListingId, Guid OfferingTeamId,
        DateTimeOffset? ProposedAt, int? ProposedDurationMinutes,
        string Status, Guid ListingTeamId, string ScheduleType = "one_off",
        Guid? SlotId = null);
    private sealed record ScrimOfferRef(Guid ListingId, Guid OfferingTeamId, Guid ListingTeamId);
    private sealed record PracticeSessionRef(Guid TeamId, string Status);
    private sealed record ScrimListingTypeRef(Guid TeamId, string Status, string ScheduleType);

    /// <summary>
    /// Projection for GetSessionAsync that includes raw veto tokens.
    /// Tokens are never returned in API responses — VetoMyToken is derived per caller.
    /// Column order matches scrim_sessions DB creation order for Dapper positional materialization.
    /// </summary>
    private sealed record ScrimSessionWithTokens(
        Guid Id, Guid Team1Id, Guid Team2Id, string? GameSlug,
        string? ModeKey, DateTime? ScheduledAt, int? DurationMinutes,
        string? ServerInfo, string? LobbyCode, string Status,
        bool ResultReportedByTeam1, bool ResultReportedByTeam2,
        int? Team1Score, int? Team2Score, DateTime CreatedAt, DateTime UpdatedAt,
        DateTime? Team1CheckedInAt, DateTime? Team2CheckedInAt,
        string? HangfireJobIds, DateTime? CheckinWindowOpenedAt,
        Guid? VetoSessionId,
        string? VetoTeam1Token,
        string? VetoTeam2Token,
        string? Team1Name = null, string? Team1Logo = null,
        string? Team2Name = null, string? Team2Logo = null,
        string? MyRole = null)
    {
        public ScrimSessionRow ToSessionRow(string? vetoMyToken, ScrimDisputeRow? dispute = null) => new(
            Id: Id, Team1Id: Team1Id, Team2Id: Team2Id, GameSlug: GameSlug,
            ModeKey: ModeKey, ScheduledAt: ScheduledAt, DurationMinutes: DurationMinutes,
            ServerInfo: ServerInfo, LobbyCode: LobbyCode, Status: Status,
            ResultReportedByTeam1: ResultReportedByTeam1, ResultReportedByTeam2: ResultReportedByTeam2,
            Team1Score: Team1Score, Team2Score: Team2Score, CreatedAt: CreatedAt, UpdatedAt: UpdatedAt,
            Team1CheckedInAt: Team1CheckedInAt, Team2CheckedInAt: Team2CheckedInAt,
            HangfireJobIds: HangfireJobIds, CheckinWindowOpenedAt: CheckinWindowOpenedAt,
            VetoSessionId: VetoSessionId, VetoTeam1Token: VetoTeam1Token, VetoTeam2Token: VetoTeam2Token,
            Team1Name: Team1Name, Team1Logo: Team1Logo, Team2Name: Team2Name, Team2Logo: Team2Logo,
            VetoMyToken: vetoMyToken, MyRole: MyRole, Dispute: dispute);
    }
}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record CreateScrimListingRequest(
    [property: JsonPropertyName("team_id")] Guid TeamId,
    [property: JsonPropertyName("game_slug")] string? GameSlug = null,
    [property: JsonPropertyName("mode_key")] string? ModeKey = null,
    [property: JsonPropertyName("region_key")] string? RegionKey = null,
    [property: JsonPropertyName("min_rank_key")] string? MinRankKey = null,
    [property: JsonPropertyName("max_rank_key")] string? MaxRankKey = null,
    [property: JsonPropertyName("preferred_days")] int[]? PreferredDays = null,
    [property: JsonPropertyName("preferred_time_start")] string? PreferredTimeStart = null,
    [property: JsonPropertyName("preferred_time_end")] string? PreferredTimeEnd = null,
    [property: JsonPropertyName("timezone")] string? Timezone = null,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("schedule_type")] string? ScheduleType = null,
    [property: JsonPropertyName("schedule_until")] DateTimeOffset? ScheduleUntil = null,
    [property: JsonPropertyName("slot_duration_minutes")] int? SlotDurationMinutes = null);

public sealed record UpdateLobbyRequest(
    [property: JsonPropertyName("lobby_code")] string? LobbyCode = null,
    [property: JsonPropertyName("server_info")] string? ServerInfo = null);

public sealed record UpdateScrimListingRequest(
    [property: JsonPropertyName("mode_key")] string? ModeKey = null,
    [property: JsonPropertyName("region_key")] string? RegionKey = null,
    [property: JsonPropertyName("min_rank_key")] string? MinRankKey = null,
    [property: JsonPropertyName("max_rank_key")] string? MaxRankKey = null,
    [property: JsonPropertyName("preferred_days")] int[]? PreferredDays = null,
    [property: JsonPropertyName("preferred_time_start")] string? PreferredTimeStart = null,
    [property: JsonPropertyName("preferred_time_end")] string? PreferredTimeEnd = null,
    [property: JsonPropertyName("timezone")] string? Timezone = null,
    [property: JsonPropertyName("notes")] string? Notes = null);

public sealed record MakeScrimOfferRequest(
    [property: JsonPropertyName("offering_team_id")] Guid OfferingTeamId,
    [property: JsonPropertyName("proposed_at")] DateTimeOffset? ProposedAt = null,
    [property: JsonPropertyName("proposed_duration_minutes")] int? ProposedDurationMinutes = null,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("slot_id")] Guid? SlotId = null);

public sealed record ReportScrimResultRequest(
    [property: JsonPropertyName("reporting_team_id")] Guid ReportingTeamId,
    [property: JsonPropertyName("team1_score")] int Team1Score,
    [property: JsonPropertyName("team2_score")] int Team2Score,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("screenshot_urls")] string[]? ScreenshotUrls = null);

public sealed record OpenDisputeRequest(
    [property: JsonPropertyName("raised_by_team_id")] Guid RaisedByTeamId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("evidence_urls")] string[]? EvidenceUrls = null);

public sealed record SubmitScrimEvidenceRequest(
    [property: JsonPropertyName("responder_evidence_urls")] string[]? ResponderEvidenceUrls = null);

public sealed record CreatePracticeSessionRequest(
    [property: JsonPropertyName("team_id")] Guid TeamId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("game_slug")] string? GameSlug = null,
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset? ScheduledAt = null,
    [property: JsonPropertyName("duration_minutes")] int? DurationMinutes = null,
    [property: JsonPropertyName("notes")] string? Notes = null);

public sealed record UpdatePracticeSessionRequest(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset? ScheduledAt = null,
    [property: JsonPropertyName("duration_minutes")] int? DurationMinutes = null,
    [property: JsonPropertyName("notes")] string? Notes = null);

public sealed record AttendanceEntry(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("status")] string Status);

public sealed record UpsertAttendanceRequest(
    [property: JsonPropertyName("entries")] AttendanceEntry[]? Entries = null);

public sealed record UpdateOwnAttendanceRequest(
    [property: JsonPropertyName("status")] string? Status = null);

public sealed record SlotInput(
    [property: JsonPropertyName("starts_at")] DateTimeOffset StartsAt,
    [property: JsonPropertyName("ends_at")] DateTimeOffset EndsAt);

public sealed record CreateSlotsRequest(
    [property: JsonPropertyName("slots")] SlotInput[]? Slots = null);

public sealed record UpdateHistoryPrivacyRequest(
    [property: JsonPropertyName("public")] bool Public);
