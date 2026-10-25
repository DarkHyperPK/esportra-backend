using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// LFG (Looking For Group) listings and interest flow (PROJ-065 Phase 1, PROJ-075 T3/T4/T5).
/// GET    /api/competitive/lfg                                — discovery (enriched)
/// POST   /api/competitive/lfg                               — create listing
/// GET    /api/competitive/lfg/mine                          — own listings (enriched)
/// GET    /api/competitive/lfg/{id}                          — listing detail (NEW)
/// PUT    /api/competitive/lfg/{id}                          — partial update
/// POST   /api/competitive/lfg/{id}/publish                  — publishes draft OR paused
/// POST   /api/competitive/lfg/{id}/pause
/// POST   /api/competitive/lfg/{id}/close
/// GET    /api/competitive/lfg/{id}/interests                — interests for a listing (NEW)
/// POST   /api/competitive/lfg/{id}/interest                 — express interest
/// DELETE /api/competitive/lfg/{id}/interest                 — withdraw interest
/// POST   /api/competitive/lfg/interests/{interestId}/accept
/// POST   /api/competitive/lfg/interests/{interestId}/decline
/// </summary>
public static class LfgEndpoints
{
    private static readonly HashSet<string> ValidIntents =
    [
        "duo", "group", "teammates", "looking_for_team",
        "looking_for_player", "competitive_roster", "casual",
    ];

    public static void MapLfgEndpoints(this WebApplication app)
    {
        // Static segments registered before parameterized to make intent explicit.
        app.MapGet("/api/competitive/lfg/mine", GetMineAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/interests/{interestId}/accept", AcceptInterestAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/interests/{interestId}/decline", DeclineInterestAsync)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/competitive/lfg", DiscoverAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg", CreateListingAsync)
            .RequireAuthorization("Authenticated");

        // Parameterized routes: register {id}/interests before {id} so the static
        // "interests" segment wins over any potential guid collision.
        app.MapGet("/api/competitive/lfg/{id}/interests", GetListingInterestsAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/lfg/{id}", GetListingAsync)
            .RequireAuthorization("Authenticated");

        app.MapPut("/api/competitive/lfg/{id}", UpdateListingAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/{id}/publish", PublishListingAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/{id}/pause", PauseListingAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/{id}/close", CloseListingAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/{id}/interest", ExpressInterestAsync)
            .RequireAuthorization("Authenticated");
        app.MapDelete("/api/competitive/lfg/{id}/interest", WithdrawInterestAsync)
            .RequireAuthorization("Authenticated");
    }

    // ── GET /api/competitive/lfg (discovery) ──────────────────────────────────

    private static async Task<IResult> DiscoverAsync(
        [FromQuery] string? game,
        [FromQuery] string? intent,
        [FromQuery] string? region_key,
        [FromQuery] string? preferred_roles,
        [FromQuery] int? min_rank,
        [FromQuery] int? max_rank,
        [FromQuery] int limit,
        [FromQuery] int offset,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(game))
            return Results.BadRequest(new { error = "game query parameter is required." });

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 50);
        offset = Math.Max(offset, 0);

        var conditions = new List<string>
        {
            "l.game_slug = @game",
            "l.status = 'published'",
            "l.user_id != @callerId",
            "(l.expires_at > now() OR l.expires_at IS NULL)",
        };
        var p = new DynamicParameters();
        p.Add("game", game);
        p.Add("callerId", userCtx.UserIdGuid);
        p.Add("limit", limit);
        p.Add("offset", offset);

        if (!string.IsNullOrWhiteSpace(intent))
        {
            if (!ValidIntents.Contains(intent))
                return Results.BadRequest(new { error = $"Invalid intent. Allowed: {string.Join(", ", ValidIntents)}." });
            conditions.Add("l.intent = @intent::lfg_intent");
            p.Add("intent", intent);
        }

        if (!string.IsNullOrWhiteSpace(region_key))
        {
            conditions.Add("l.region_key = @regionKey");
            p.Add("regionKey", region_key);
        }

        if (!string.IsNullOrWhiteSpace(preferred_roles))
        {
            var roles = preferred_roles
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (roles.Length > 0)
            {
                conditions.Add("@roles::text[] && l.preferred_roles");
                p.Add("roles", roles);
            }
        }

        if (min_rank.HasValue)
        {
            conditions.Add("l.min_rank_sort_order >= @minRank");
            p.Add("minRank", min_rank.Value);
        }

        if (max_rank.HasValue)
        {
            conditions.Add("l.max_rank_sort_order <= @maxRank");
            p.Add("maxRank", max_rank.Value);
        }

        var where = string.Join(" AND ", conditions);
        using var conn = db.CreateConnection();

        var rows = await conn.QueryAsync<LfgListingRow>(
            $"""
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT l.id, l.user_id, l.game_slug, l.intent, l.mode_key,
                   l.region_key, l.preferred_servers, l.min_rank_sort_order,
                   l.max_rank_sort_order, l.preferred_roles, l.languages,
                   l.description, l.expires_at, l.status, l.created_at, l.updated_at,
                   pr.username, pr.avatar_seed,
                   reg.display_name AS region_display_name,
                   min_rt.display_name AS min_rank_display_name,
                   min_rt.icon_url    AS min_rank_icon_url,
                   max_rt.display_name AS max_rank_display_name,
                   max_rt.icon_url    AS max_rank_icon_url
            FROM public.lfg_listings l
            JOIN public.profiles pr ON pr.id = l.user_id
            LEFT JOIN public.game_catalog_regions reg
                ON reg.region_key = l.region_key
               AND reg.game_slug = l.game_slug
               AND reg.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_rank_tiers min_rt
                ON min_rt.sort_order = l.min_rank_sort_order
               AND min_rt.game_slug = l.game_slug
               AND min_rt.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_rank_tiers max_rt
                ON max_rt.sort_order = l.max_rank_sort_order
               AND max_rt.game_slug = l.game_slug
               AND max_rt.version_id = (SELECT id FROM active_v)
            WHERE {where}
            ORDER BY l.created_at DESC
            LIMIT @limit OFFSET @offset
            """,
            p);

        return Results.Ok(rows);
    }

    // ── POST /api/competitive/lfg (create listing) ────────────────────────────

    private static async Task<IResult> CreateListingAsync(
        [FromBody] CreateLfgListingRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.GameSlug))
            return Results.BadRequest(new { error = "game_slug is required." });

        if (!ValidIntents.Contains(req.Intent ?? ""))
            return Results.BadRequest(new { error = $"Invalid intent. Allowed: {string.Join(", ", ValidIntents)}." });

        if (req.Description is { Length: > 280 })
            return Results.BadRequest(new { error = "description must be 280 characters or fewer." });

        using var conn = db.CreateConnection();

        var gameExists = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.game_catalog_game_aliases a
                JOIN public.game_catalog_versions v ON v.id = a.version_id
                WHERE v.is_active = TRUE AND LOWER(a.alias) = LOWER(@game)
            )
            """,
            new { game = req.GameSlug });
        if (!gameExists)
            return Results.BadRequest(new { error = "Unknown game." });

        if (req.PreferredRoles is { Length: > 0 })
        {
            var validRoleKeys = (await conn.QueryAsync<string>(
                """
                SELECT role_key FROM public.game_catalog_game_roles gr
                JOIN public.game_catalog_versions v ON v.id = gr.version_id
                WHERE v.is_active = TRUE AND gr.game_slug = @game
                """,
                new { game = req.GameSlug })).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var badRole = req.PreferredRoles.FirstOrDefault(r => !validRoleKeys.Contains(r));
            if (badRole is not null)
                return Results.BadRequest(new { error = $"Unknown role '{badRole}' for this game." });
        }

        var listingLimit = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int FROM public.lfg_listings
            WHERE user_id = @userId AND game_slug = @game AND status = 'published'
            """,
            new { userId = userCtx.UserIdGuid, game = req.GameSlug });
        if (listingLimit >= 3)
            return Results.Json(new { error = "LISTING_LIMIT_REACHED" }, statusCode: 429);

        var duplicate = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.lfg_listings
                WHERE user_id = @userId AND game_slug = @game
                  AND intent = @intent::lfg_intent
                  AND status IN ('draft', 'published')
                  AND created_at >= now() - INTERVAL '24 hours'
            )
            """,
            new { userId = userCtx.UserIdGuid, game = req.GameSlug, intent = req.Intent });
        if (duplicate)
            return Results.Conflict(new { error = "LISTING_ALREADY_ACTIVE" });

        var expiresAt = req.ExpiresAt ?? DateTimeOffset.UtcNow.AddDays(7);
        if (expiresAt <= DateTimeOffset.UtcNow)
            return Results.BadRequest(new { error = "expires_at must be in the future." });

        var row = await conn.QuerySingleOrDefaultAsync<LfgListingRow>(
            """
            INSERT INTO public.lfg_listings
                (user_id, game_slug, intent, mode_key, region_key, preferred_servers,
                 min_rank_sort_order, max_rank_sort_order, preferred_roles, languages,
                 description, expires_at, status)
            VALUES
                (@userId, @gameSlug, @intent::lfg_intent, @modeKey, @regionKey,
                 @preferredServers, @minRank, @maxRank, @preferredRoles, @languages,
                 @description, @expiresAt, 'draft'::lfg_listing_status)
            RETURNING *
            """,
            new
            {
                userId = userCtx.UserIdGuid,
                gameSlug = req.GameSlug,
                intent = req.Intent,
                modeKey = req.ModeKey,
                regionKey = req.RegionKey,
                preferredServers = req.PreferredServers ?? Array.Empty<string>(),
                minRank = req.MinRankSortOrder,
                maxRank = req.MaxRankSortOrder,
                preferredRoles = req.PreferredRoles ?? Array.Empty<string>(),
                languages = req.Languages ?? Array.Empty<string>(),
                description = req.Description,
                expiresAt,
            });

        return Results.Ok(row);
    }

    // ── GET /api/competitive/lfg/mine ─────────────────────────────────────────

    private static async Task<IResult> GetMineAsync(
        [FromQuery] int limit,
        [FromQuery] int offset,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        limit = Math.Clamp(limit == 0 ? 50 : limit, 1, 100);
        offset = Math.Max(offset, 0);

        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<LfgListingRow>(
            """
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT l.id, l.user_id, l.game_slug, l.intent, l.mode_key,
                   l.region_key, l.preferred_servers, l.min_rank_sort_order,
                   l.max_rank_sort_order, l.preferred_roles, l.languages,
                   l.description, l.expires_at, l.status, l.created_at, l.updated_at,
                   pr.username, pr.avatar_seed,
                   reg.display_name AS region_display_name,
                   min_rt.display_name AS min_rank_display_name,
                   min_rt.icon_url    AS min_rank_icon_url,
                   max_rt.display_name AS max_rank_display_name,
                   max_rt.icon_url    AS max_rank_icon_url
            FROM public.lfg_listings l
            JOIN public.profiles pr ON pr.id = l.user_id
            LEFT JOIN public.game_catalog_regions reg
                ON reg.region_key = l.region_key
               AND reg.game_slug = l.game_slug
               AND reg.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_rank_tiers min_rt
                ON min_rt.sort_order = l.min_rank_sort_order
               AND min_rt.game_slug = l.game_slug
               AND min_rt.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_rank_tiers max_rt
                ON max_rt.sort_order = l.max_rank_sort_order
               AND max_rt.game_slug = l.game_slug
               AND max_rt.version_id = (SELECT id FROM active_v)
            WHERE l.user_id = @userId
            ORDER BY l.created_at DESC
            LIMIT @limit OFFSET @offset
            """,
            new { userId = userCtx.UserIdGuid, limit, offset });

        return Results.Ok(rows);
    }

    // ── GET /api/competitive/lfg/{id} ────────────────────────────────────────

    private static async Task<IResult> GetListingAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<LfgListingRow>(
            """
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT l.id, l.user_id, l.game_slug, l.intent, l.mode_key,
                   l.region_key, l.preferred_servers, l.min_rank_sort_order,
                   l.max_rank_sort_order, l.preferred_roles, l.languages,
                   l.description, l.expires_at, l.status, l.created_at, l.updated_at,
                   pr.username, pr.avatar_seed,
                   reg.display_name AS region_display_name,
                   min_rt.display_name AS min_rank_display_name,
                   min_rt.icon_url    AS min_rank_icon_url,
                   max_rt.display_name AS max_rank_display_name,
                   max_rt.icon_url    AS max_rank_icon_url
            FROM public.lfg_listings l
            JOIN public.profiles pr ON pr.id = l.user_id
            LEFT JOIN public.game_catalog_regions reg
                ON reg.region_key = l.region_key
               AND reg.game_slug = l.game_slug
               AND reg.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_rank_tiers min_rt
                ON min_rt.sort_order = l.min_rank_sort_order
               AND min_rt.game_slug = l.game_slug
               AND min_rt.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_rank_tiers max_rt
                ON max_rt.sort_order = l.max_rank_sort_order
               AND max_rt.game_slug = l.game_slug
               AND max_rt.version_id = (SELECT id FROM active_v)
            WHERE l.id = @id
            """,
            new { id });

        if (row is null) return Results.NotFound();

        // Non-owners may only see published listings.
        var isOwner = row.UserId == userCtx.UserIdGuid;
        if (!isOwner && row.Status != "published") return Results.NotFound();

        return Results.Ok(row);
    }

    // ── GET /api/competitive/lfg/{id}/interests ───────────────────────────────

    private static async Task<IResult> GetListingInterestsAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        // Verify listing exists and caller owns it.
        var listing = await conn.QuerySingleOrDefaultAsync<(Guid UserId, string GameSlug)>(
            "SELECT user_id, game_slug FROM public.lfg_listings WHERE id = @id",
            new { id });

        if (listing == default) return Results.NotFound();
        if (listing.UserId != userCtx.UserIdGuid) return Results.Forbid();

        var interests = await conn.QueryAsync<LfgInterestEnrichedRow>(
            """
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT i.id, i.listing_id, i.from_user_id, i.status, i.message, i.created_at,
                   pr.username, pr.avatar_seed,
                   pgp.rank_tier_key, pgp.rank_source, pgp.preferred_roles, pgp.region_key,
                   grt.display_name AS rank_tier_display_name,
                   grt.icon_url     AS rank_tier_icon_url,
                   reg.display_name AS region_display_name
            FROM public.lfg_interests i
            JOIN public.profiles pr ON pr.id = i.from_user_id
            LEFT JOIN public.player_game_profiles pgp
                ON pgp.user_id = i.from_user_id
               AND pgp.game_slug = @gameSlug
            LEFT JOIN public.game_catalog_rank_tiers grt
                ON grt.tier_key = pgp.rank_tier_key
               AND grt.game_slug = @gameSlug
               AND grt.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_regions reg
                ON reg.region_key = pgp.region_key
               AND reg.game_slug = @gameSlug
               AND reg.version_id = (SELECT id FROM active_v)
            WHERE i.listing_id = @listingId
            ORDER BY i.created_at DESC
            """,
            new { listingId = id, gameSlug = listing.GameSlug });

        return Results.Ok(interests);
    }

    // ── PUT /api/competitive/lfg/{id} ─────────────────────────────────────────

    private static async Task<IResult> UpdateListingAsync(
        Guid id,
        [FromBody] UpdateLfgListingRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.ExpiresAt.HasValue && req.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            return Results.BadRequest(new { error = "expires_at must be in the future." });

        if (req.Description is { Length: > 280 })
            return Results.BadRequest(new { error = "description must be 280 characters or fewer." });

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<LfgListingRow>(
            """
            UPDATE public.lfg_listings SET
                mode_key             = COALESCE(@modeKey, mode_key),
                region_key           = COALESCE(@regionKey, region_key),
                preferred_servers    = COALESCE(@preferredServers, preferred_servers),
                min_rank_sort_order  = COALESCE(@minRank, min_rank_sort_order),
                max_rank_sort_order  = COALESCE(@maxRank, max_rank_sort_order),
                preferred_roles      = COALESCE(@preferredRoles, preferred_roles),
                languages            = COALESCE(@languages, languages),
                description          = COALESCE(@description, description),
                expires_at           = COALESCE(@expiresAt, expires_at),
                updated_at           = now()
            WHERE id = @id AND user_id = @userId
              AND status IN ('draft', 'published')
            RETURNING *
            """,
            new
            {
                id,
                userId = userCtx.UserIdGuid,
                modeKey = req.ModeKey,
                regionKey = req.RegionKey,
                preferredServers = req.PreferredServers,
                minRank = req.MinRankSortOrder,
                maxRank = req.MaxRankSortOrder,
                preferredRoles = req.PreferredRoles,
                languages = req.Languages,
                description = req.Description,
                expiresAt = req.ExpiresAt,
            });

        if (row is null)
            return Results.BadRequest(new { error = "LISTING_NOT_EDITABLE" });

        return Results.Ok(row);
    }

    // ── POST /api/competitive/lfg/{id}/publish ────────────────────────────────

    private static async Task<IResult> PublishListingAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var listing = await conn.QuerySingleOrDefaultAsync<(string? Status, DateTimeOffset ExpiresAt)>(
            "SELECT status, expires_at FROM public.lfg_listings WHERE id = @id AND user_id = @userId",
            new { id, userId = userCtx.UserIdGuid });

        if (listing.Status is null) return Results.NotFound();
        if (listing.Status is not ("draft" or "paused")) return Results.BadRequest(new { error = "LISTING_NOT_EDITABLE" });
        if (listing.ExpiresAt <= DateTimeOffset.UtcNow)
            return Results.BadRequest(new { error = "expires_at must be in the future." });

        // Atomic publish: the UPDATE only fires if the user has < 3 published listings,
        // preventing the TOCTOU race between the limit check and the status transition.
        var row = await conn.QuerySingleOrDefaultAsync<LfgListingRow>(
            """
            UPDATE public.lfg_listings
            SET status = 'published'::lfg_listing_status, updated_at = now()
            WHERE id = @id AND user_id = @userId
              AND (
                  SELECT COUNT(*) FROM public.lfg_listings
                  WHERE user_id = @userId AND status = 'published'
              ) < 3
            RETURNING *
            """,
            new { id, userId = userCtx.UserIdGuid });

        // If the row was not updated, distinguish: does the listing exist, or was the cap hit?
        if (row is null)
        {
            var capCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*)::int FROM public.lfg_listings WHERE user_id = @userId AND status = 'published'",
                new { userId = userCtx.UserIdGuid });
            if (capCount >= 3)
                return Results.Json(new { error = "LISTING_LIMIT_REACHED" }, statusCode: 429);
            return Results.Problem("Could not publish listing. Please try again.");
        }

        return Results.Ok(row);
    }

    // ── POST /api/competitive/lfg/{id}/pause ──────────────────────────────────

    private static async Task<IResult> PauseListingAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<LfgListingRow>(
            """
            UPDATE public.lfg_listings
            SET status = 'paused'::lfg_listing_status, updated_at = now()
            WHERE id = @id AND user_id = @userId AND status = 'published'
            RETURNING *
            """,
            new { id, userId = userCtx.UserIdGuid });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    // ── POST /api/competitive/lfg/{id}/close ──────────────────────────────────

    private static async Task<IResult> CloseListingAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<LfgListingRow>(
            """
            UPDATE public.lfg_listings
            SET status = 'closed'::lfg_listing_status, updated_at = now()
            WHERE id = @id AND user_id = @userId
            RETURNING *
            """,
            new { id, userId = userCtx.UserIdGuid });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    // ── POST /api/competitive/lfg/{id}/interest ───────────────────────────────

    private static async Task<IResult> ExpressInterestAsync(
        Guid id,
        [FromBody] ExpressInterestRequest? req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var listing = await conn.QuerySingleOrDefaultAsync<(Guid UserId, string? Status)>(
            "SELECT user_id, status FROM public.lfg_listings WHERE id = @id",
            new { id });

        if (listing.Status is null || listing.Status != "published")
            return Results.NotFound(new { error = "LISTING_NOT_PUBLISHED" });

        if (listing.UserId == userCtx.UserIdGuid)
            return Results.BadRequest(new { error = "OWN_LISTING" });

        var alreadyInterested = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.lfg_interests
                WHERE listing_id = @listingId AND from_user_id = @fromUserId
            )
            """,
            new { listingId = id, fromUserId = userCtx.UserIdGuid });

        if (alreadyInterested)
            return Results.Conflict(new { error = "ALREADY_INTERESTED" });

        var row = await conn.QuerySingleOrDefaultAsync<LfgInterestRow>(
            """
            INSERT INTO public.lfg_interests (listing_id, from_user_id, to_user_id, status, message)
            VALUES (@listingId, @fromUserId, @toUserId, 'expressed'::lfg_interest_status, @message)
            RETURNING *
            """,
            new
            {
                listingId = id,
                fromUserId = userCtx.UserIdGuid,
                toUserId = listing.UserId,
                message = req?.Message,
            });

        return Results.Ok(row);
    }

    // ── DELETE /api/competitive/lfg/{id}/interest ─────────────────────────────

    private static async Task<IResult> WithdrawInterestAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var updated = await conn.ExecuteAsync(
            """
            UPDATE public.lfg_interests
            SET status = 'withdrawn'::lfg_interest_status, updated_at = now()
            WHERE listing_id = @listingId AND from_user_id = @userId
              AND status = 'expressed'
            """,
            new { listingId = id, userId = userCtx.UserIdGuid });

        return updated == 0 ? Results.NotFound() : Results.Ok(new { success = true });
    }

    // ── POST /api/competitive/lfg/interests/{interestId}/accept ──────────────

    private static async Task<IResult> AcceptInterestAsync(
        Guid interestId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<LfgInterestRow>(
            """
            UPDATE public.lfg_interests
            SET status = 'accepted'::lfg_interest_status, updated_at = now()
            WHERE id = @id AND to_user_id = @userId
              AND status = 'expressed'
            RETURNING *
            """,
            new { id = interestId, userId = userCtx.UserIdGuid });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    // ── POST /api/competitive/lfg/interests/{interestId}/decline ─────────────

    private static async Task<IResult> DeclineInterestAsync(
        Guid interestId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<LfgInterestRow>(
            """
            UPDATE public.lfg_interests
            SET status = 'declined'::lfg_interest_status, updated_at = now()
            WHERE id = @id AND to_user_id = @userId
              AND status = 'expressed'
            RETURNING *
            """,
            new { id = interestId, userId = userCtx.UserIdGuid });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }
}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record CreateLfgListingRequest(
    [property: JsonPropertyName("game_slug")] string GameSlug,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("mode_key")] string? ModeKey = null,
    [property: JsonPropertyName("region_key")] string? RegionKey = null,
    [property: JsonPropertyName("preferred_servers")] string[]? PreferredServers = null,
    [property: JsonPropertyName("min_rank_sort_order")] int? MinRankSortOrder = null,
    [property: JsonPropertyName("max_rank_sort_order")] int? MaxRankSortOrder = null,
    [property: JsonPropertyName("preferred_roles")] string[]? PreferredRoles = null,
    [property: JsonPropertyName("languages")] string[]? Languages = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null);

public sealed record UpdateLfgListingRequest(
    [property: JsonPropertyName("mode_key")] string? ModeKey = null,
    [property: JsonPropertyName("region_key")] string? RegionKey = null,
    [property: JsonPropertyName("preferred_servers")] string[]? PreferredServers = null,
    [property: JsonPropertyName("min_rank_sort_order")] int? MinRankSortOrder = null,
    [property: JsonPropertyName("max_rank_sort_order")] int? MaxRankSortOrder = null,
    [property: JsonPropertyName("preferred_roles")] string[]? PreferredRoles = null,
    [property: JsonPropertyName("languages")] string[]? Languages = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null);

public sealed record ExpressInterestRequest(
    [property: JsonPropertyName("message")] string? Message = null);
