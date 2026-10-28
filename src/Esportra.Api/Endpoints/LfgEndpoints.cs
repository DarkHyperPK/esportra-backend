using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;
using Hangfire;
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
    private static bool ValidPreferredAgents(string[]? agents) => agents is null ||
        (agents.Length <= 5 && agents.All(agent => Guid.TryParse(agent, out _)) &&
         agents.Distinct(StringComparer.OrdinalIgnoreCase).Count() == agents.Length);

    private static readonly HashSet<string> ValidIntents =
    [
        "looking_for_team", "looking_for_player", "competitive_roster",
    ];

    public static void MapLfgEndpoints(this WebApplication app)
    {
        // Static segments registered before parameterized to make intent explicit.
        app.MapGet("/api/competitive/lfg/mine", GetMineAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/lfg/interests/mine", LfgExperienceEndpoints.GetInterestsAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/lfg/saved", LfgExperienceEndpoints.GetSavedAsync)
            .RequireAuthorization("Authenticated");
        app.MapPut("/api/competitive/lfg/{id:guid}/saved", LfgExperienceEndpoints.SaveAsync)
            .RequireAuthorization("Authenticated");
        app.MapDelete("/api/competitive/lfg/{id:guid}/saved", LfgExperienceEndpoints.UnsaveAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/interests/{interestId}/accept", LfgExperienceEndpoints.AcceptAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/interests/{interestId}/decline", DeclineInterestAsync)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/competitive/game-stats/me", GetMyGameStatsAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/game-stats/valorant/matches", ValorantArchiveEndpoints.GetMatchesAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/game-stats/sync", SyncRiotStatsAsync)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/competitive/lfg", DiscoverAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg", CreateListingAsync)
            .RequireAuthorization("Authenticated");

        // Parameterized routes: register {id}/interests before {id} so the static
        // "interests" segment wins over any potential guid collision.
        app.MapGet("/api/competitive/lfg/{id}/interests", GetListingInterestsAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/lfg/{id:guid}/roster", LfgExperienceEndpoints.GetRosterAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/lfg/{id}", GetListingAsync)
            .RequireAuthorization("Authenticated");

        app.MapPut("/api/competitive/lfg/{id}", UpdateListingAsync)
            .RequireAuthorization("Authenticated");
        app.MapDelete("/api/competitive/lfg/{id}", DeleteListingAsync)
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
        [FromQuery] string? q,
        [FromQuery] string? language,
        [FromQuery] string? server,
        [FromQuery] bool? saved_only,
        [FromQuery] int? min_rank,
        [FromQuery] int? max_rank,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0)
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
            "(lower(l.game_slug) <> 'valorant' OR ra.user_id IS NOT NULL)",
            "(l.expires_at > now() OR l.expires_at IS NULL)",
        };
        var p = new DynamicParameters();
        p.Add("game", game);
        p.Add("callerId", userCtx.UserIdGuid);
        p.Add("limit", limit);
        p.Add("offset", offset);

        if (!string.IsNullOrWhiteSpace(q))
        {
            if (q.Length > 100) return Results.BadRequest(new { error = "Search is too long." });
            conditions.Add("(pr.username ILIKE @search OR t.name ILIKE @search OR l.description ILIKE @search)");
            p.Add("search", $"%{q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%");
        }
        if (!string.IsNullOrWhiteSpace(language))
        {
            conditions.Add("@language = ANY(l.languages)");
            p.Add("language", language);
        }
        if (!string.IsNullOrWhiteSpace(server))
        {
            conditions.Add("@server = ANY(l.preferred_servers)");
            p.Add("server", server);
        }
        if (saved_only == true)
            conditions.Add("EXISTS (SELECT 1 FROM public.lfg_saved_listings s WHERE s.listing_id = l.id AND s.user_id = @callerId)");

        if (!string.IsNullOrWhiteSpace(intent))
        {
            var intents = intent.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var i in intents)
            {
                if (!ValidIntents.Contains(i))
                    return Results.BadRequest(new { error = $"Invalid intent '{i}'. Allowed: {string.Join(", ", ValidIntents)}." });
            }
            if (intents.Length == 1)
            {
                conditions.Add("l.intent = @intent::lfg_intent");
                p.Add("intent", intents[0]);
            }
            else
            {
                conditions.Add("l.intent::text = ANY(@intents)");
                p.Add("intents", intents);
            }
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
            conditions.Add("(CASE WHEN l.intent = 'looking_for_team' THEN CASE WHEN l.game_slug = 'valorant' AND pgs.competitive_tier >= 3 THEN pgs.competitive_tier - 3 END ELSE l.min_rank_sort_order END) >= @minRank");
            p.Add("minRank", min_rank.Value);
        }

        if (max_rank.HasValue)
        {
            conditions.Add("(CASE WHEN l.intent = 'looking_for_team' THEN CASE WHEN l.game_slug = 'valorant' AND pgs.competitive_tier >= 3 THEN pgs.competitive_tier - 3 END ELSE l.max_rank_sort_order END) <= @maxRank");
            p.Add("maxRank", max_rank.Value);
        }

        var where = string.Join(" AND ", conditions);
        using var conn = db.CreateConnection();

        var rows = await conn.QueryAsync<LfgListingRow>(
            $"""
            WITH active_v AS (
                SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1
            )
            SELECT l.id, l.user_id, l.team_id, l.game_slug, l.intent::text AS intent, l.mode_key,
                   l.region_key, l.preferred_servers, l.min_rank_sort_order,
                   l.max_rank_sort_order, l.preferred_roles, l.preferred_agents, l.languages,
                   l.description, l.expires_at, l.status::text AS status, l.created_at, l.updated_at,
                   pr.username, pr.avatar_seed, pr.avatar_url AS profile_avatar_url, pr.banner_url AS profile_banner_url, pr.avatar_style,
                   EXISTS (SELECT 1 FROM public.lfg_saved_listings s WHERE s.listing_id = l.id AND s.user_id = @callerId) AS is_saved,
                   vi.id AS viewer_interest_id, vi.status::text AS viewer_interest_status,
                   COALESCE(vi.conversation_id, (SELECT c.id FROM public.conversations c
                     JOIN public.conversation_participants a ON a.conversation_id = c.id AND a.user_id = vi.from_user_id AND a.is_active
                     JOIN public.conversation_participants b ON b.conversation_id = c.id AND b.user_id = vi.to_user_id AND b.is_active
                     WHERE c.type = 'direct' AND vi.status = 'accepted'
                       AND NOT EXISTS (SELECT 1 FROM public.conversation_participants extra
                         WHERE extra.conversation_id = c.id AND extra.user_id NOT IN (vi.from_user_id, vi.to_user_id))
                     ORDER BY c.created_at, c.id LIMIT 1)) AS viewer_conversation_id,
                   reg.display_name AS region_display_name,
                   min_rt.display_name AS min_rank_display_name,
                   min_rt.icon_url    AS min_rank_icon_url,
                   max_rt.display_name AS max_rank_display_name,
                   max_rt.icon_url    AS max_rank_icon_url,
                   pgs.competitive_tier, pgs.competitive_tier_name,
                   pgs.win_count, pgs.loss_count, pgs.win_rate, pgs.kd_ratio,
                   CASE WHEN pgs.headshot_data_complete THEN pgs.hs_pct END AS hs_pct,
                   (CASE WHEN pgs.popular_agents IS NULL THEN NULL WHEN pgs.headshot_data_complete THEN pgs.popular_agents WHEN jsonb_typeof(pgs.popular_agents) = 'array' THEN (SELECT COALESCE(jsonb_agg(item - 'hs_pct'), '[]'::jsonb) FROM jsonb_array_elements(pgs.popular_agents) item) ELSE '[]'::jsonb END)::text AS popular_agents, pgs.sync_status AS stats_sync_status,
                   pgs.matches_analyzed, pgs.synced_at AS riot_synced_at, pgs.player_card_id,
                   ra.game_name AS riot_game_name, ra.tag_line AS riot_tag_line,
                   t.name AS team_name, t.logo_url AS team_logo_url, t.tag AS team_tag,
                   (SELECT COUNT(*)::int FROM public.team_members tm
                    WHERE tm.team_id = l.team_id AND tm.is_active = TRUE) AS team_member_count,
                   (SELECT COUNT(*)::int FROM public.team_members tm2
                    JOIN public.riot_accounts ra2 ON ra2.user_id = tm2.user_id
                    WHERE tm2.team_id = l.team_id AND tm2.is_active = TRUE) AS team_verified_count
            FROM public.lfg_listings l
            JOIN public.profiles pr ON pr.id = l.user_id
            LEFT JOIN public.player_game_stats pgs
                ON pgs.user_id = l.user_id AND pgs.game_slug = l.game_slug
               AND (l.game_slug <> 'valorant' OR EXISTS (SELECT 1 FROM public.riot_accounts linked WHERE linked.user_id = l.user_id AND linked.puuid = pgs.provider_player_id))
            LEFT JOIN public.riot_accounts ra ON ra.user_id = l.user_id
            LEFT JOIN public.teams t ON t.id = l.team_id
            LEFT JOIN public.lfg_interests vi ON vi.listing_id = l.id AND vi.from_user_id = @callerId
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

        if (string.Equals(req.GameSlug, "valorant", StringComparison.OrdinalIgnoreCase) && !ValidPreferredAgents(req.PreferredAgents))
            return Results.BadRequest(new { error = "Choose up to five distinct agents." });

        if (req.Intent != "looking_for_team" && req.MinRankSortOrder.HasValue && req.MaxRankSortOrder.HasValue && req.MinRankSortOrder > req.MaxRankSortOrder)
            return Results.BadRequest(new { error = "minimum rank must not exceed maximum rank." });

        using var conn = db.CreateConnection();

        if (string.Equals(req.GameSlug, "valorant", StringComparison.OrdinalIgnoreCase) &&
            !await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.riot_accounts WHERE user_id = @userId)", new { userId = userCtx.UserIdGuid }))
            return Results.BadRequest(new { error = "NO_LINKED_RIOT_ACCOUNT" });

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

            validRoleKeys.UnionWith(["coach", "manager"]);
            var badRole = req.PreferredRoles.FirstOrDefault(r => !validRoleKeys.Contains(r));
            if (badRole is not null)
                return Results.BadRequest(new { error = $"Unknown role '{badRole}' for this game." });
        }

        var listingLimit = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)::int FROM public.lfg_listings
            WHERE user_id = @userId AND game_slug = @game
              AND status IN ('draft', 'published', 'paused')
            """,
            new { userId = userCtx.UserIdGuid, game = req.GameSlug });
        if (listingLimit >= 3)
            return Results.BadRequest(new { error = "LISTING_LIMIT_REACHED", max = 3 });

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

        var isTeamIntent = req.Intent is "looking_for_player" or "competitive_roster";
        if (isTeamIntent && !req.TeamId.HasValue)
            return Results.BadRequest(new { error = "team_id is required for team listings." });

        if (req.TeamId.HasValue)
        {
            var isStaff = await conn.ExecuteScalarAsync<bool>(
                $"""
                SELECT EXISTS(
                    SELECT 1 FROM public.teams t
                    WHERE t.id = @teamId AND {TeamKindSql.RealTeamWhere} AND (t.owner_id = @userId OR EXISTS (
                        SELECT 1 FROM public.team_members tm
                        WHERE tm.team_id = t.id AND tm.user_id = @userId AND tm.is_active = TRUE
                          AND tm.role IN ('captain', 'manager')))
                )
                """,
                new { teamId = req.TeamId.Value, userId = userCtx.UserIdGuid });
            if (!isStaff)
                return Results.Forbid();
        }

        var row = await conn.QuerySingleOrDefaultAsync<LfgListingRow>(
            """
            INSERT INTO public.lfg_listings
                (user_id, team_id, game_slug, intent, mode_key, region_key, preferred_servers,
                 min_rank_sort_order, max_rank_sort_order, preferred_roles, preferred_agents,
                 languages, description, expires_at, status)
            VALUES
                (@userId, @teamId, @gameSlug, @intent::lfg_intent, @modeKey, @regionKey,
                 @preferredServers, @minRank, @maxRank, @preferredRoles, @preferredAgents,
                 @languages, @description, @expiresAt, 'draft'::lfg_listing_status)
            RETURNING id, team_id, user_id, game_slug, intent::text AS intent, mode_key,
                      region_key, preferred_servers, min_rank_sort_order, max_rank_sort_order,
                      preferred_roles, preferred_agents, languages, description, expires_at,
                      status::text AS status, created_at, updated_at
            """,
            new
            {
                userId = userCtx.UserIdGuid,
                teamId = req.TeamId,
                gameSlug = req.GameSlug,
                intent = req.Intent,
                modeKey = req.ModeKey,
                regionKey = req.RegionKey,
                preferredServers = req.PreferredServers ?? Array.Empty<string>(),
                minRank = req.Intent == "looking_for_team" ? null : req.MinRankSortOrder,
                maxRank = req.Intent == "looking_for_team" ? null : req.MaxRankSortOrder,
                preferredRoles = NormalizeStaffRoles(req.PreferredRoles) ?? Array.Empty<string>(),
                preferredAgents = req.PreferredAgents ?? Array.Empty<string>(),
                languages = req.Languages ?? Array.Empty<string>(),
                description = req.Description,
                expiresAt,
            });

        return Results.Ok(row);
    }

    // ── GET /api/competitive/lfg/mine ─────────────────────────────────────────

    private static async Task<IResult> GetMineAsync(
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
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
            SELECT l.id, l.user_id, l.team_id, l.game_slug, l.intent::text AS intent, l.mode_key,
                   l.region_key, l.preferred_servers, l.min_rank_sort_order,
                   l.max_rank_sort_order, l.preferred_roles, l.preferred_agents, l.languages,
                   l.description, l.expires_at, l.status::text AS status, l.created_at, l.updated_at,
                   pr.username, pr.avatar_seed, pr.avatar_url AS profile_avatar_url, pr.banner_url AS profile_banner_url, pr.avatar_style,
                   reg.display_name AS region_display_name,
                   min_rt.display_name AS min_rank_display_name,
                   min_rt.icon_url    AS min_rank_icon_url,
                   max_rt.display_name AS max_rank_display_name,
                   max_rt.icon_url    AS max_rank_icon_url,
                   pgs.competitive_tier, pgs.competitive_tier_name,
                   pgs.win_count, pgs.loss_count, pgs.win_rate, pgs.kd_ratio,
                   CASE WHEN pgs.headshot_data_complete THEN pgs.hs_pct END AS hs_pct,
                   (CASE WHEN pgs.popular_agents IS NULL THEN NULL WHEN pgs.headshot_data_complete THEN pgs.popular_agents WHEN jsonb_typeof(pgs.popular_agents) = 'array' THEN (SELECT COALESCE(jsonb_agg(item - 'hs_pct'), '[]'::jsonb) FROM jsonb_array_elements(pgs.popular_agents) item) ELSE '[]'::jsonb END)::text AS popular_agents, pgs.sync_status AS stats_sync_status,
                   pgs.matches_analyzed, pgs.synced_at AS riot_synced_at, pgs.player_card_id,
                   ra.game_name AS riot_game_name, ra.tag_line AS riot_tag_line,
                   t.name AS team_name, t.logo_url AS team_logo_url, t.tag AS team_tag,
                   (SELECT COUNT(*)::int FROM public.team_members tm
                    WHERE tm.team_id = l.team_id AND tm.is_active = TRUE) AS team_member_count,
                   (SELECT COUNT(*)::int FROM public.team_members tm2
                    JOIN public.riot_accounts ra2 ON ra2.user_id = tm2.user_id
                    WHERE tm2.team_id = l.team_id AND tm2.is_active = TRUE) AS team_verified_count
            FROM public.lfg_listings l
            JOIN public.profiles pr ON pr.id = l.user_id
            LEFT JOIN public.player_game_stats pgs
                ON pgs.user_id = l.user_id AND pgs.game_slug = l.game_slug
               AND (l.game_slug <> 'valorant' OR EXISTS (SELECT 1 FROM public.riot_accounts linked WHERE linked.user_id = l.user_id AND linked.puuid = pgs.provider_player_id))
            LEFT JOIN public.riot_accounts ra ON ra.user_id = l.user_id
            LEFT JOIN public.teams t ON t.id = l.team_id
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
            SELECT l.id, l.user_id, l.team_id, l.game_slug, l.intent::text AS intent, l.mode_key,
                   l.region_key, l.preferred_servers, l.min_rank_sort_order,
                   l.max_rank_sort_order, l.preferred_roles, l.preferred_agents, l.languages,
                   l.description, l.expires_at, l.status::text AS status, l.created_at, l.updated_at,
                   pr.username, pr.avatar_seed, pr.avatar_url AS profile_avatar_url, pr.banner_url AS profile_banner_url, pr.avatar_style,
                   reg.display_name AS region_display_name,
                   min_rt.display_name AS min_rank_display_name,
                   min_rt.icon_url    AS min_rank_icon_url,
                   max_rt.display_name AS max_rank_display_name,
                   max_rt.icon_url    AS max_rank_icon_url,
                   pgs.competitive_tier, pgs.competitive_tier_name,
                   pgs.win_count, pgs.loss_count, pgs.win_rate, pgs.kd_ratio,
                   CASE WHEN pgs.headshot_data_complete THEN pgs.hs_pct END AS hs_pct,
                   (CASE WHEN pgs.popular_agents IS NULL THEN NULL WHEN pgs.headshot_data_complete THEN pgs.popular_agents WHEN jsonb_typeof(pgs.popular_agents) = 'array' THEN (SELECT COALESCE(jsonb_agg(item - 'hs_pct'), '[]'::jsonb) FROM jsonb_array_elements(pgs.popular_agents) item) ELSE '[]'::jsonb END)::text AS popular_agents, pgs.sync_status AS stats_sync_status,
                   pgs.matches_analyzed, pgs.synced_at AS riot_synced_at, pgs.player_card_id,
                   ra.game_name AS riot_game_name, ra.tag_line AS riot_tag_line,
                   t.name AS team_name, t.logo_url AS team_logo_url, t.tag AS team_tag,
                   (SELECT COUNT(*)::int FROM public.team_members tm
                    WHERE tm.team_id = l.team_id AND tm.is_active = TRUE) AS team_member_count,
                   (SELECT COUNT(*)::int FROM public.team_members tm2
                    JOIN public.riot_accounts ra2 ON ra2.user_id = tm2.user_id
                    WHERE tm2.team_id = l.team_id AND tm2.is_active = TRUE) AS team_verified_count
            FROM public.lfg_listings l
            JOIN public.profiles pr ON pr.id = l.user_id
            LEFT JOIN public.player_game_stats pgs
                ON pgs.user_id = l.user_id AND pgs.game_slug = l.game_slug
               AND (l.game_slug <> 'valorant' OR EXISTS (SELECT 1 FROM public.riot_accounts linked WHERE linked.user_id = l.user_id AND linked.puuid = pgs.provider_player_id))
            LEFT JOIN public.riot_accounts ra ON ra.user_id = l.user_id
            LEFT JOIN public.teams t ON t.id = l.team_id
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
            WHERE l.id = @id AND (l.user_id = @viewerId OR lower(l.game_slug) <> 'valorant'
                OR EXISTS (SELECT 1 FROM public.riot_accounts linked WHERE linked.user_id = l.user_id))
            """,
            new { id, viewerId = userCtx.UserIdGuid });

        if (row is null) return Results.NotFound();

        // Non-owners may only see published listings.
        var isOwner = row.UserId == userCtx.UserIdGuid;
        if (!isOwner && (row.Status != "published" || row.ExpiresAt <= DateTimeOffset.UtcNow)) return Results.NotFound();

        var viewer = await conn.QuerySingleOrDefaultAsync<LfgViewerState>(
            """
            SELECT EXISTS (SELECT 1 FROM public.lfg_saved_listings s WHERE s.user_id = @userId AND s.listing_id = @id) AS is_saved,
                   i.id AS viewer_interest_id, i.status::text AS viewer_interest_status,
                   COALESCE(i.conversation_id, (SELECT c.id FROM public.conversations c
                     JOIN public.conversation_participants a ON a.conversation_id = c.id AND a.user_id = i.from_user_id AND a.is_active
                     JOIN public.conversation_participants b ON b.conversation_id = c.id AND b.user_id = i.to_user_id AND b.is_active
                     WHERE c.type = 'direct' AND i.status = 'accepted'
                       AND NOT EXISTS (SELECT 1 FROM public.conversation_participants extra
                         WHERE extra.conversation_id = c.id AND extra.user_id NOT IN (i.from_user_id, i.to_user_id))
                     ORDER BY c.created_at, c.id LIMIT 1)) AS viewer_conversation_id
            FROM (SELECT 1) x LEFT JOIN public.lfg_interests i
              ON i.listing_id = @id AND i.from_user_id = @userId
            """, new { id, userId = userCtx.UserIdGuid });
        row = row with
        {
            IsSaved = viewer?.IsSaved ?? false,
            ViewerInterestId = viewer?.ViewerInterestId,
            ViewerInterestStatus = viewer?.ViewerInterestStatus,
            ViewerConversationId = viewer?.ViewerConversationId
        };

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
            SELECT i.id, i.listing_id, i.from_user_id, i.status::text AS status, i.message, i.created_at,
                   pr.username, pr.avatar_seed, pr.avatar_url AS profile_avatar_url, pr.avatar_style,
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
        var editable = await conn.QuerySingleOrDefaultAsync<EditableLfgListing>(
            "SELECT game_slug, intent::text AS intent FROM public.lfg_listings WHERE id = @id AND user_id = @userId AND status IN ('draft', 'published', 'paused')",
            new { id, userId = userCtx.UserIdGuid });
        if (editable is null) return Results.BadRequest(new { error = "LISTING_NOT_EDITABLE" });
        var gameSlug = editable.GameSlug;
        var isPlayer = editable.Intent == "looking_for_team";
        if (!isPlayer && req.MinRankSortOrder.HasValue && req.MaxRankSortOrder.HasValue && req.MinRankSortOrder > req.MaxRankSortOrder)
            return Results.BadRequest(new { error = "minimum rank must not exceed maximum rank." });
        if (string.Equals(gameSlug, "valorant", StringComparison.OrdinalIgnoreCase) && !ValidPreferredAgents(req.PreferredAgents))
            return Results.BadRequest(new { error = "Choose up to five distinct agents." });
        if (req.PreferredRoles is { Length: > 0 })
        {
            var validRoles = (await conn.QueryAsync<string>(
                "SELECT r.role_key FROM public.game_catalog_game_roles r JOIN public.game_catalog_versions v ON v.id = r.version_id WHERE v.is_active AND r.game_slug = @gameSlug",
                new { gameSlug })).ToHashSet(StringComparer.OrdinalIgnoreCase);
            validRoles.UnionWith(["coach", "manager"]);
            if (req.PreferredRoles.Any(role => !validRoles.Contains(role)))
                return Results.BadRequest(new { error = "Unknown role for this game." });
        }
        var row = await conn.QuerySingleOrDefaultAsync<LfgListingRow>(
            """
            UPDATE public.lfg_listings SET
                mode_key             = CASE WHEN @clearMode THEN NULL ELSE COALESCE(@modeKey, mode_key) END,
                region_key           = CASE WHEN @clearRegion THEN NULL ELSE COALESCE(@regionKey, region_key) END,
                preferred_servers    = COALESCE(@preferredServers, preferred_servers),
                min_rank_sort_order  = CASE WHEN @clearMinRank THEN NULL ELSE COALESCE(@minRank, min_rank_sort_order) END,
                max_rank_sort_order  = CASE WHEN @clearMaxRank THEN NULL ELSE COALESCE(@maxRank, max_rank_sort_order) END,
                preferred_roles      = COALESCE(@preferredRoles, preferred_roles),
                preferred_agents     = COALESCE(@preferredAgents, preferred_agents),
                languages            = COALESCE(@languages, languages),
                description          = CASE WHEN @clearDescription THEN NULL ELSE COALESCE(@description, description) END,
                expires_at           = COALESCE(@expiresAt, expires_at),
                updated_at           = now()
            WHERE id = @id AND user_id = @userId
              AND status IN ('draft', 'published', 'paused')
            RETURNING *
            """,
            new
            {
                id,
                userId = userCtx.UserIdGuid,
                modeKey = req.ModeKey,
                regionKey = req.RegionKey,
                preferredServers = req.PreferredServers,
                minRank = isPlayer ? null : req.MinRankSortOrder,
                maxRank = isPlayer ? null : req.MaxRankSortOrder,
                preferredRoles = NormalizeStaffRoles(req.PreferredRoles),
                preferredAgents = req.PreferredAgents,
                languages = req.Languages,
                description = req.Description,
                expiresAt = req.ExpiresAt,
                clearMode = req.ClearMode,
                clearRegion = req.ClearRegion,
                clearMinRank = isPlayer || req.ClearMinRank,
                clearMaxRank = isPlayer || req.ClearMaxRank,
                clearDescription = req.ClearDescription,
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

        var listing = await conn.QuerySingleOrDefaultAsync<(string? Status, DateTimeOffset ExpiresAt, string GameSlug)>(
            "SELECT status::text AS status, expires_at, game_slug FROM public.lfg_listings WHERE id = @id AND user_id = @userId",
            new { id, userId = userCtx.UserIdGuid });

        if (listing.Status is null) return Results.NotFound();
        if (string.Equals(listing.GameSlug, "valorant", StringComparison.OrdinalIgnoreCase) &&
            !await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.riot_accounts WHERE user_id = @userId)", new { userId = userCtx.UserIdGuid }))
            return Results.BadRequest(new { error = "NO_LINKED_RIOT_ACCOUNT" });
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
              AND (lower(game_slug) <> 'valorant' OR EXISTS (SELECT 1 FROM public.riot_accounts linked WHERE linked.user_id = @userId))
              AND (
                  SELECT COUNT(*) FROM public.lfg_listings
                  WHERE user_id = @userId AND status = 'published'
              ) < 3
            RETURNING id, team_id, user_id, game_slug, intent::text AS intent, mode_key,
                      region_key, preferred_servers, min_rank_sort_order, max_rank_sort_order,
                      preferred_roles, languages, description, expires_at,
                      status::text AS status, created_at, updated_at
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
            RETURNING id, team_id, user_id, game_slug, intent::text AS intent, mode_key,
                      region_key, preferred_servers, min_rank_sort_order, max_rank_sort_order,
                      preferred_roles, languages, description, expires_at,
                      status::text AS status, created_at, updated_at
            """,
            new { id, userId = userCtx.UserIdGuid });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    // ── DELETE /api/competitive/lfg/{id} ─────────────────────────────────────

    private static async Task<IResult> DeleteListingAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var deleted = await conn.ExecuteAsync(
            "DELETE FROM public.lfg_listings WHERE id = @id AND user_id = @userId",
            new { id, userId = userCtx.UserIdGuid });

        return deleted == 0 ? Results.NotFound() : Results.Ok(new { success = true });
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
            RETURNING id, team_id, user_id, game_slug, intent::text AS intent, mode_key,
                      region_key, preferred_servers, min_rank_sort_order, max_rank_sort_order,
                      preferred_roles, languages, description, expires_at,
                      status::text AS status, created_at, updated_at
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

        if (req?.Message is { Length: > 500 }) return Results.BadRequest(new { error = "message must be 500 characters or fewer." });
        using var conn = db.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            new { key = $"lfg-interest:{id}:{userCtx.UserIdGuid}" }, tx);
        var listing = await conn.QuerySingleOrDefaultAsync<(Guid UserId, string? Status)>(
            "SELECT user_id, status::text AS status FROM public.lfg_listings WHERE id = @id AND (expires_at IS NULL OR expires_at > now()) AND (lower(game_slug) <> 'valorant' OR EXISTS (SELECT 1 FROM public.riot_accounts linked WHERE linked.user_id = public.lfg_listings.user_id)) FOR SHARE",
            new { id }, tx);
        if (listing.Status != "published") return Results.NotFound(new { error = "LISTING_NOT_PUBLISHED" });
        if (listing.UserId == userCtx.UserIdGuid) return Results.BadRequest(new { error = "OWN_LISTING" });
        var existing = await conn.QuerySingleOrDefaultAsync<LfgInterestRow>("""
            SELECT id, listing_id, from_user_id, to_user_id, status::text AS status, message, created_at, updated_at
            FROM public.lfg_interests WHERE listing_id = @id AND from_user_id = @userId FOR UPDATE
            """, new { id, userId = userCtx.UserIdGuid }, tx);
        if (existing is not null && existing.Status is not ("expressed" or "withdrawn"))
            return Results.Conflict(new { error = "REQUEST_CLOSED" });
        LfgInterestRow row;
        if (existing is null)
            row = await conn.QuerySingleAsync<LfgInterestRow>("""
                INSERT INTO public.lfg_interests (listing_id, from_user_id, to_user_id, status, message)
                VALUES (@id, @userId, @ownerId, 'expressed', @message)
                RETURNING id, listing_id, from_user_id, to_user_id, status::text AS status, message, created_at, updated_at
                """, new { id, userId = userCtx.UserIdGuid, ownerId = listing.UserId, message = req?.Message }, tx);
        else if (existing.Status == "withdrawn")
        {
            // A fresh ID makes stale notification actions unable to respond to the new request.
            row = await conn.QuerySingleAsync<LfgInterestRow>("""
                UPDATE public.lfg_interests SET id = gen_random_uuid(), status = 'expressed', message = @message,
                    created_at = now(), updated_at = now(), conversation_id = NULL
                WHERE id = @interestId
                RETURNING id, listing_id, from_user_id, to_user_id, status::text AS status, message, created_at, updated_at
                """, new { interestId = existing.Id, message = req?.Message }, tx);
        }
        else row = existing;
        var fromUsername = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT username FROM public.profiles WHERE id = @userId", new { userId = userCtx.UserIdGuid }, tx);
        // Repair requests created by older versions before their notification was sent.
        await conn.ExecuteAsync("""
            INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
            SELECT @ownerId, 'lfg_interest_received', @title, @message, @link,
                jsonb_build_object('interest_id', @interestId::text, 'listing_id', @listingId::text, 'from_username', @fromUsername), FALSE
            WHERE NOT EXISTS (SELECT 1 FROM public.notifications WHERE user_id = @ownerId
                AND type::text = 'lfg_interest_received' AND data->>'interest_id' = @interestId::text)
            """, new
        {
            ownerId = listing.UserId,
            title = $"{fromUsername ?? "Someone"} is interested in your listing",
            message = row.Message ?? "They want to connect.",
            link = $"/competitive/lfg/{id}",
            interestId = row.Id,
            listingId = id,
            fromUsername = fromUsername ?? ""
        }, tx);
        tx.Commit();
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
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            new { key = $"lfg-interest:{id}:{userCtx.UserIdGuid}" }, tx);
        var row = await conn.QuerySingleOrDefaultAsync<LfgInterestRow>("""
            UPDATE public.lfg_interests SET status = 'withdrawn', updated_at = now()
            WHERE listing_id = @listingId AND from_user_id = @userId AND status = 'expressed'
            RETURNING id, listing_id, from_user_id, to_user_id, status::text AS status, message, created_at, updated_at
            """, new { listingId = id, userId = userCtx.UserIdGuid }, tx);
        if (row is null) return Results.NotFound();
        await conn.ExecuteAsync("""
            UPDATE public.notifications SET data = data - 'interest_id', is_read = true
            WHERE user_id = @ownerId AND type::text = 'lfg_interest_received' AND data->>'interest_id' = @interestId::text
            """, new { ownerId = row.ToUserId, interestId = row.Id }, tx);
        tx.Commit();
        return Results.Ok(new { success = true });
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
            RETURNING id, listing_id, from_user_id, to_user_id, status::text AS status, message, created_at, updated_at
            """,
            new { id = interestId, userId = userCtx.UserIdGuid });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    // ── GET /api/competitive/game-stats/me ───────────────────────────────────
    // Returns the calling user's Riot link status + synced Valorant stats.
    // Always returns 200 so the frontend can gate on riot_linked / sync_status.

    internal static async Task<IResult> GetMyGameStatsAsync(
        [FromQuery] string? game,
        HttpContext ctx,
        IDbConnectionFactory db,
        IBackgroundJobClient backgroundJobs,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(game))
            return Results.BadRequest(new { error = "game query parameter is required." });

        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<GameStatsRaw>(
            """
            SELECT
                (ra.puuid IS NOT NULL)      AS riot_linked,
                ra.puuid                    AS riot_puuid,
                ra.game_name               AS riot_game_name,
                ra.tag_line                AS riot_tag_line,
                pgs.sync_status            AS sync_status,
                pgs.synced_at              AS synced_at,
                pgs.updated_at             AS updated_at,
                pgs.competitive_tier       AS competitive_tier,
                pgs.competitive_tier_name  AS competitive_tier_name,
                pgs.win_count              AS win_count,
                pgs.loss_count             AS loss_count,
                pgs.win_rate               AS win_rate,
                pgs.kd_ratio               AS kd_ratio,
                pgs.matches_analyzed       AS matches_analyzed,
                (CASE WHEN pgs.popular_agents IS NULL THEN NULL WHEN pgs.headshot_data_complete THEN pgs.popular_agents WHEN jsonb_typeof(pgs.popular_agents) = 'array' THEN (SELECT COALESCE(jsonb_agg(item - 'hs_pct'), '[]'::jsonb) FROM jsonb_array_elements(pgs.popular_agents) item) ELSE '[]'::jsonb END)::text   AS popular_agents,
                CASE WHEN pgs.headshot_data_complete THEN pgs.hs_pct ELSE NULL END AS hs_pct,
                pgs.player_card_id         AS player_card_id
            FROM (SELECT @userId::uuid AS uid) AS u
            LEFT JOIN public.riot_accounts ra ON ra.user_id = u.uid
            LEFT JOIN public.player_game_stats pgs
                ON pgs.user_id = u.uid AND pgs.game_slug = @game
               AND (lower(@game) <> 'valorant' OR pgs.provider_player_id = ra.puuid)
            """,
            new { userId = userCtx.UserIdGuid, game });

        if (row is null)
            return Results.Ok(new { riot_linked = false });

        // Auto-sync: if Riot is linked but stats are missing or stale (>1h), enqueue background sync.
        // Cooldown via updated_at prevents re-enqueue storms after failed syncs (sync_error path
        // keeps synced_at old) — a fresh attempt at most every 10 minutes per user.
        var responseSyncStatus = row.SyncStatus;
        if (row.RiotLinked && string.Equals(game, "valorant", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.UtcNow;
            var lastSyncedAt = GameStatsRaw.ToDto(row.SyncedAt);
            var lastUpdatedAt = GameStatsRaw.ToDto(row.UpdatedAt);
            var syncAgeHours = lastSyncedAt is null ? double.MaxValue : (now - lastSyncedAt.Value).TotalHours;
            var lastTouchAgeMinutes = lastUpdatedAt is null
                ? double.MaxValue
                : (now - lastUpdatedAt.Value).TotalMinutes;
            var needsSync = syncAgeHours >= 1 || row.SyncStatus is "error" or "partial" or "pending" or "syncing" or null;
            if (needsSync && lastTouchAgeMinutes >= 10)
            {
                // Stamp 'pending' synchronously so the caller (and its 5s poll) sees the sync
                // kicking off immediately, instead of a stale null status.
                var claimed = await conn.ExecuteAsync(
                    """
                    INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, sync_status)
                    VALUES (@userId, 'valorant', @puuid, 'pending')
                    ON CONFLICT (user_id, game_slug) DO UPDATE SET
                        sync_status = 'pending', updated_at = now()
                    WHERE player_game_stats.updated_at <= now() - interval '10 minutes'
                       OR (player_game_stats.provider_player_id IS DISTINCT FROM @puuid
                           AND player_game_stats.sync_status NOT IN ('pending', 'syncing'))
                    """,
                    new { userId = userCtx.UserIdGuid, puuid = row.RiotPuuid ?? "" });

                if (claimed > 0)
                {
                    try
                    {
                        backgroundJobs.Enqueue<ValorantStatsSyncService>(
                            s => s.SyncForUserAsync(userCtx.UserIdGuid, CancellationToken.None));
                        responseSyncStatus = "pending";
                    }
                    catch
                    {
                        await conn.ExecuteAsync("UPDATE public.player_game_stats SET sync_status = 'error', updated_at = now() WHERE user_id = @userId AND game_slug = 'valorant' AND sync_status = 'pending'", new { userId = userCtx.UserIdGuid });
                        responseSyncStatus = "error";
                    }
                }
                else
                    responseSyncStatus = await conn.QuerySingleOrDefaultAsync<string>("SELECT sync_status FROM public.player_game_stats WHERE user_id = @userId AND game_slug = 'valorant'", new { userId = userCtx.UserIdGuid });
            }
        }

        System.Text.Json.JsonElement? parsedAgents = null;
        if (!string.IsNullOrEmpty(row.PopularAgents))
        {
            try { parsedAgents = System.Text.Json.JsonDocument.Parse(row.PopularAgents).RootElement; }
            catch { /* malformed JSONB — leave null */ }
        }

        var syncedAtDto = GameStatsRaw.ToDto(row.SyncedAt);
        var actStats = row.RiotLinked && string.Equals(game, "valorant", StringComparison.OrdinalIgnoreCase)
            ? await ValorantArchiveEndpoints.GetActsAsync(conn, userCtx.UserIdGuid) : [];

        return Results.Ok(new
        {
            riot_linked = row.RiotLinked,
            act_stats = actStats,
            riot_game_name = row.RiotGameName,
            riot_tag_line = row.RiotTagLine,
            sync_status = responseSyncStatus,
            synced_at = syncedAtDto,
            competitive_tier = row.CompetitiveTier,
            competitive_tier_name = row.CompetitiveTierName,
            win_count = row.WinCount,
            loss_count = row.LossCount,
            win_rate = row.WinRate,
            kd_ratio = row.KdRatio,
            matches_analyzed = row.MatchesAnalyzed,
            hs_pct = row.HsPct,
            player_card_id = row.PlayerCardId,
            popular_agents = (object?)parsedAgents,
        });
    }

    // ── POST /api/competitive/game-stats/sync ────────────────────────────────

    private static async Task<IResult> SyncRiotStatsAsync(
        HttpContext ctx,
        IDbConnectionFactory db,
        IBackgroundJobClient backgroundJobs,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var hasPuuid = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM public.riot_accounts WHERE user_id = @userId)",
            new { userId = userCtx.UserIdGuid });
        if (!hasPuuid)
            return Results.BadRequest(new { error = "NO_LINKED_RIOT_ACCOUNT" });

        var lastSyncRaw = await conn.QuerySingleOrDefaultAsync<DateTime?>(
            "SELECT synced_at FROM public.player_game_stats WHERE user_id = @userId AND game_slug = 'valorant'",
            new { userId = userCtx.UserIdGuid });
        var lastSync = GameStatsRaw.ToDto(lastSyncRaw);

        if (lastSync.HasValue && (DateTimeOffset.UtcNow - lastSync.Value).TotalMinutes < 5)
            return Results.Ok(new { status = "fresh", synced_at = lastSync.Value });

        backgroundJobs.Enqueue<ValorantStatsSyncService>(
            s => s.SyncForUserAsync(userCtx.UserIdGuid, CancellationToken.None));

        return Results.Ok(new { status = "queued" });
    }
    private static string[]? NormalizeStaffRoles(string[]? roles) => roles?.Select(role =>
        role.Equals("coach", StringComparison.OrdinalIgnoreCase) ? "coach" :
        role.Equals("manager", StringComparison.OrdinalIgnoreCase) ? "manager" : role).ToArray();

}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record CreateLfgListingRequest(
    [property: JsonPropertyName("game_slug")] string GameSlug,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("team_id")] Guid? TeamId = null,
    [property: JsonPropertyName("mode_key")] string? ModeKey = null,
    [property: JsonPropertyName("region_key")] string? RegionKey = null,
    [property: JsonPropertyName("preferred_servers")] string[]? PreferredServers = null,
    [property: JsonPropertyName("min_rank_sort_order")] int? MinRankSortOrder = null,
    [property: JsonPropertyName("max_rank_sort_order")] int? MaxRankSortOrder = null,
    [property: JsonPropertyName("preferred_roles")] string[]? PreferredRoles = null,
    [property: JsonPropertyName("preferred_agents")] string[]? PreferredAgents = null,
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
    [property: JsonPropertyName("preferred_agents")] string[]? PreferredAgents = null,
    [property: JsonPropertyName("languages")] string[]? Languages = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null,
    [property: JsonPropertyName("clear_mode")] bool ClearMode = false,
    [property: JsonPropertyName("clear_region")] bool ClearRegion = false,
    [property: JsonPropertyName("clear_min_rank")] bool ClearMinRank = false,
    [property: JsonPropertyName("clear_max_rank")] bool ClearMaxRank = false,
    [property: JsonPropertyName("clear_description")] bool ClearDescription = false);

public sealed record ExpressInterestRequest(
    [property: JsonPropertyName("message")] string? Message = null);

file sealed record LfgViewerState(bool IsSaved, Guid? ViewerInterestId, string? ViewerInterestStatus, Guid? ViewerConversationId);

// Internal DTO for GetMyGameStatsAsync — Dapper maps nullable columns to null.
// Internal DTO for GetMyGameStatsAsync — non-positional so Dapper maps by property
// (positional primary constructors must exactly match reader column types or Dapper
// throws "no matching constructor"). Timestamptz columns read as System.DateTime via
// Npgsql, so DateTime? is the exact type; converted to DateTimeOffset via ToDto()
// below for the serialized response.
file sealed record GameStatsRaw
{
    public bool RiotLinked { get; init; }
    public string? RiotPuuid { get; init; }
    public string? RiotGameName { get; init; }
    public string? RiotTagLine { get; init; }
    public string? SyncStatus { get; init; }
    public DateTime? SyncedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int? CompetitiveTier { get; init; }
    public string? CompetitiveTierName { get; init; }
    public int? WinCount { get; init; }
    public int? LossCount { get; init; }
    public decimal? WinRate { get; init; }
    public decimal? KdRatio { get; init; }
    public int? MatchesAnalyzed { get; init; }
    public string? PopularAgents { get; init; }
    public decimal? HsPct { get; init; }
    public string? PlayerCardId { get; init; }

    /// <summary>
    /// Npgsql reads timestamptz as DateTime. Normalize to a DateTimeOffset without
    /// shifting the instant, regardless of the Kind Npgsql attached to the value.
    /// </summary>
    public static DateTimeOffset? ToDto(DateTime? value)
    {
        if (value is null) return null;
        var dt = value.Value;
        return dt.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(dt, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(dt),
            _ => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
        };
    }
}

file sealed record EditableLfgListing
{
    public string GameSlug { get; init; } = "";
    public string Intent { get; init; } = "";
}
