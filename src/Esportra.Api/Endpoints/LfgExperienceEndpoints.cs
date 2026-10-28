using Dapper;
using System.Data;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>Authenticated LFG inbox and saved-listing operations.</summary>
public static class LfgExperienceEndpoints
{
    private static UserContext? User(HttpContext ctx) => ctx.Items["UserContext"] as UserContext;

    public static async Task<IResult> GetRosterAsync(Guid id, HttpContext ctx, IDbConnectionFactory db)
    {
        var user = User(ctx);
        if (user is null) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var listing = await conn.QuerySingleOrDefaultAsync<LfgRosterListing>(
            """
            SELECT team_id, game_slug FROM public.lfg_listings
            WHERE id = @id AND team_id IS NOT NULL
              AND intent::text IN ('looking_for_player', 'competitive_roster')
              AND (user_id = @userId OR (status = 'published' AND (expires_at IS NULL OR expires_at > now())))
            """, new { id, userId = user.UserIdGuid });
        if (listing is null) return Results.NotFound();

        var rosterIds = (await conn.QueryAsync<Guid>(
            "SELECT id FROM public.team_rosters WHERE team_id = @teamId AND lower(game) = lower(@game) ORDER BY id LIMIT 2",
            new { teamId = listing.TeamId, game = listing.GameSlug })).ToArray();
        if (rosterIds.Length == 0) return Results.Ok(new { status = "missing", members = Array.Empty<LfgRosterMember>() });
        if (rosterIds.Length > 1) return Results.Ok(new { status = "ambiguous", members = Array.Empty<LfgRosterMember>() });

        var members = await conn.QueryAsync<LfgRosterMember>(
            """
            SELECT rm.user_id, p.username, p.avatar_url, p.avatar_seed, p.avatar_style,
                   rm.roster_role::text AS roster_role, COALESCE(rm.is_starter, false) AS is_starter,
                   CASE WHEN visible.can_view THEN stats.competitive_tier END AS competitive_tier,
                   CASE WHEN visible.can_view THEN stats.competitive_tier_name END AS competitive_tier_name,
                   CASE WHEN visible.can_view THEN stats.kd_ratio END AS kd_ratio,
                   CASE WHEN visible.can_view THEN stats.win_rate END AS win_rate,
                   CASE WHEN visible.can_view THEN stats.matches_analyzed END AS matches_analyzed
            FROM public.team_roster_members rm
            JOIN public.team_members tm ON tm.team_id = @teamId AND tm.user_id = rm.user_id AND tm.is_active = true
            JOIN public.profiles p ON p.id = rm.user_id
            LEFT JOIN public.player_game_stats stats ON stats.user_id = rm.user_id AND stats.game_slug = @game
              AND (@game <> 'valorant' OR EXISTS (SELECT 1 FROM public.riot_accounts linked WHERE linked.user_id = rm.user_id AND linked.puuid = stats.provider_player_id))
            CROSS JOIN LATERAL (SELECT rm.user_id = @userId OR EXISTS (
                SELECT 1 FROM public.player_game_profiles pgp
                WHERE pgp.user_id = rm.user_id AND pgp.game_slug = @game AND pgp.profile_visible = true
            ) OR EXISTS (
                SELECT 1 FROM public.lfg_listings own
                WHERE own.user_id = rm.user_id AND own.game_slug = @game AND own.intent::text = 'looking_for_team' AND own.status = 'published'
                  AND (own.expires_at IS NULL OR own.expires_at > now())
            ) AS can_view) visible
            WHERE rm.roster_id = @rosterId AND rm.is_active = true
            ORDER BY rm.display_order, rm.created_at, rm.user_id
            """, new { teamId = listing.TeamId, game = listing.GameSlug, rosterId = rosterIds[0], userId = user.UserIdGuid });
        return Results.Ok(new { status = "available", members });
    }

    public static async Task<IResult> GetInterestsAsync(
        HttpContext ctx, IDbConnectionFactory db, [FromQuery] string? direction,
        [FromQuery] string? game, [FromQuery] string? status,
        [FromQuery] int limit = 20, [FromQuery] int offset = 0)
    {
        var user = User(ctx);
        if (user is null) return Results.Unauthorized();
        if (direction is not ("received" or "sent")) return Results.BadRequest(new { error = "direction must be received or sent." });
        if (status is not null and not ("expressed" or "accepted" or "declined" or "withdrawn" or "blocked"))
            return Results.BadRequest(new { error = "Invalid status." });
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<LfgInboxRow>(
            """
            SELECT i.id, i.listing_id, i.from_user_id, i.to_user_id, i.status::text AS status,
                   i.message, i.created_at, i.updated_at, l.game_slug, l.intent::text AS intent,
                   l.status::text AS listing_status, l.expires_at, l.user_id AS listing_owner_id,
                   COALESCE(t.name, owner_profile.username) AS listing_name,
                   p.username AS counterpart_username, p.avatar_seed AS counterpart_avatar_seed,
                   p.avatar_url AS counterpart_avatar_url, p.avatar_style AS counterpart_avatar_style,
                   COALESCE(i.conversation_id, prior.id) AS conversation_id
            FROM public.lfg_interests i
            JOIN public.lfg_listings l ON l.id = i.listing_id
            LEFT JOIN public.teams t ON t.id = l.team_id
            LEFT JOIN public.profiles owner_profile ON owner_profile.id = l.user_id
            LEFT JOIN public.profiles p ON p.id = CASE WHEN @direction = 'sent' THEN i.to_user_id ELSE i.from_user_id END
            LEFT JOIN LATERAL (
                SELECT c.id FROM public.conversations c
                JOIN public.conversation_participants a ON a.conversation_id = c.id AND a.user_id = i.from_user_id AND a.is_active
                JOIN public.conversation_participants b ON b.conversation_id = c.id AND b.user_id = i.to_user_id AND b.is_active
                WHERE c.type = 'direct' AND i.status = 'accepted'
                  AND NOT EXISTS (SELECT 1 FROM public.conversation_participants extra
                      WHERE extra.conversation_id = c.id AND extra.user_id NOT IN (i.from_user_id, i.to_user_id))
                ORDER BY c.created_at, c.id LIMIT 1
            ) prior ON true
            WHERE ((@direction = 'sent' AND i.from_user_id = @userId)
                OR (@direction = 'received' AND i.to_user_id = @userId))
              AND (@game IS NULL OR l.game_slug = @game)
              AND (@status IS NULL OR i.status::text = @status)
            ORDER BY i.created_at DESC, i.id DESC LIMIT @limit OFFSET @offset
            """, new { userId = user.UserIdGuid, direction, game, status, limit = Math.Clamp(limit, 1, 50), offset = Math.Max(offset, 0) });
        return Results.Ok(rows);
    }

    public static async Task<IResult> GetSavedAsync(HttpContext ctx, IDbConnectionFactory db,
        [FromQuery] string? game, [FromQuery] int limit = 20, [FromQuery] int offset = 0)
    {
        var user = User(ctx);
        if (user is null) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<LfgListingRow>(
            """
            WITH active_v AS (SELECT id FROM public.game_catalog_versions WHERE is_active = TRUE LIMIT 1)
            SELECT l.id, l.user_id, l.team_id, l.game_slug, l.intent::text AS intent,
                l.status::text AS status, l.mode_key, l.region_key, l.preferred_servers,
                l.min_rank_sort_order, l.max_rank_sort_order, l.preferred_roles, l.preferred_agents,
                l.languages, l.description, l.expires_at, l.created_at, l.updated_at,
                p.username, p.avatar_seed, p.avatar_url AS profile_avatar_url, p.banner_url AS profile_banner_url, p.avatar_style,
                t.name AS team_name, t.tag AS team_tag, t.logo_url AS team_logo_url,
                reg.display_name AS region_display_name, min_rt.display_name AS min_rank_display_name,
                min_rt.icon_url AS min_rank_icon_url, max_rt.display_name AS max_rank_display_name,
                max_rt.icon_url AS max_rank_icon_url, pgs.competitive_tier, pgs.competitive_tier_name,
                pgs.win_count, pgs.loss_count, pgs.win_rate, pgs.kd_ratio, (CASE WHEN pgs.popular_agents IS NULL THEN NULL WHEN pgs.headshot_data_complete THEN pgs.popular_agents WHEN jsonb_typeof(pgs.popular_agents) = 'array' THEN (SELECT COALESCE(jsonb_agg(item - 'hs_pct'), '[]'::jsonb) FROM jsonb_array_elements(pgs.popular_agents) item) ELSE '[]'::jsonb END)::text AS popular_agents, pgs.sync_status AS stats_sync_status,
                pgs.matches_analyzed, pgs.synced_at AS riot_synced_at, pgs.player_card_id,
                ra.game_name AS riot_game_name, ra.tag_line AS riot_tag_line,
                (SELECT count(*)::int FROM public.team_members tm WHERE tm.team_id = l.team_id AND tm.is_active) AS team_member_count,
                true AS is_saved, vi.id AS viewer_interest_id, vi.status::text AS viewer_interest_status,
                COALESCE(vi.conversation_id, (SELECT c.id FROM public.conversations c
                    JOIN public.conversation_participants a ON a.conversation_id = c.id AND a.user_id = vi.from_user_id AND a.is_active
                    JOIN public.conversation_participants b ON b.conversation_id = c.id AND b.user_id = vi.to_user_id AND b.is_active
                    WHERE c.type = 'direct' AND vi.status = 'accepted' AND NOT EXISTS (
                        SELECT 1 FROM public.conversation_participants extra WHERE extra.conversation_id = c.id
                        AND extra.user_id NOT IN (vi.from_user_id, vi.to_user_id))
                    ORDER BY c.created_at, c.id LIMIT 1)) AS viewer_conversation_id
            FROM public.lfg_saved_listings s JOIN public.lfg_listings l ON l.id = s.listing_id
            JOIN public.profiles p ON p.id = l.user_id LEFT JOIN public.teams t ON t.id = l.team_id
            LEFT JOIN public.lfg_interests vi ON vi.listing_id = l.id AND vi.from_user_id = @userId
            LEFT JOIN public.player_game_stats pgs ON pgs.user_id = l.user_id AND pgs.game_slug = l.game_slug
              AND (l.game_slug <> 'valorant' OR EXISTS (SELECT 1 FROM public.riot_accounts linked WHERE linked.user_id = l.user_id AND linked.puuid = pgs.provider_player_id))
            LEFT JOIN public.riot_accounts ra ON ra.user_id = l.user_id
            LEFT JOIN public.game_catalog_regions reg ON reg.region_key = l.region_key AND reg.game_slug = l.game_slug AND reg.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_rank_tiers min_rt ON min_rt.sort_order = l.min_rank_sort_order AND min_rt.game_slug = l.game_slug AND min_rt.version_id = (SELECT id FROM active_v)
            LEFT JOIN public.game_catalog_rank_tiers max_rt ON max_rt.sort_order = l.max_rank_sort_order AND max_rt.game_slug = l.game_slug AND max_rt.version_id = (SELECT id FROM active_v)
            WHERE s.user_id = @userId AND l.status = 'published'
              AND (l.expires_at IS NULL OR l.expires_at > now()) AND (@game IS NULL OR l.game_slug = @game)
            ORDER BY s.created_at DESC, s.listing_id DESC LIMIT @limit OFFSET @offset
            """, new { userId = user.UserIdGuid, game, limit = Math.Clamp(limit, 1, 50), offset = Math.Max(offset, 0) });
        return Results.Ok(rows);
    }

    public static async Task<IResult> SaveAsync(Guid id, HttpContext ctx, IDbConnectionFactory db)
    {
        var user = User(ctx);
        if (user is null) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var visible = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM public.lfg_listings WHERE id = @id AND status = 'published' AND (expires_at IS NULL OR expires_at > now()))", new { id });
        if (!visible) return Results.NotFound();
        await conn.ExecuteAsync("INSERT INTO public.lfg_saved_listings (user_id, listing_id) VALUES (@userId, @id) ON CONFLICT DO NOTHING", new { userId = user.UserIdGuid, id });
        return Results.NoContent();
    }

    public static async Task<IResult> UnsaveAsync(Guid id, HttpContext ctx, IDbConnectionFactory db)
    {
        var user = User(ctx);
        if (user is null) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM public.lfg_saved_listings WHERE user_id = @userId AND listing_id = @id", new { userId = user.UserIdGuid, id });
        return Results.NoContent();
    }

    public static async Task<IResult> AcceptAsync(Guid interestId, HttpContext ctx, IDbConnectionFactory db)
    {
        var user = User(ctx);
        if (user is null) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var row = await conn.QuerySingleOrDefaultAsync<LfgInterestRow>(
            "SELECT id, listing_id, from_user_id, to_user_id, status::text AS status, message, created_at, updated_at FROM public.lfg_interests WHERE id = @interestId AND to_user_id = @userId FOR UPDATE",
            new { interestId, userId = user.UserIdGuid }, tx);
        if (row is null || row.Status is not ("expressed" or "accepted")) return Results.NotFound();
        if (row.Status == "accepted")
        {
            var priorId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT conversation_id FROM public.lfg_interests WHERE id = @interestId", new { interestId }, tx);
            if (priorId.HasValue)
            {
                tx.Commit();
                return Results.Ok(new { interest = row, conversation_id = priorId });
            }
        }
        var pair = new { userId = user.UserIdGuid, otherId = row.FromUserId };
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(LEAST(@userId::text, @otherId::text) || ':' || GREATEST(@userId::text, @otherId::text), 0))", pair, tx);
        var conversation = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT c.id FROM public.conversations c
            JOIN public.conversation_participants a ON a.conversation_id = c.id AND a.user_id = @userId AND a.is_active
            JOIN public.conversation_participants b ON b.conversation_id = c.id AND b.user_id = @otherId AND b.is_active
            WHERE c.type = 'direct' AND NOT EXISTS (SELECT 1 FROM public.conversation_participants extra
                WHERE extra.conversation_id = c.id AND extra.user_id NOT IN (@userId, @otherId))
            ORDER BY c.created_at, c.id LIMIT 1
            """, pair, tx);
        if (conversation is null)
        {
            conversation = await conn.QuerySingleAsync<Guid>("INSERT INTO public.conversations (type, created_by) VALUES ('direct', @userId) RETURNING id", pair, tx);
            await conn.ExecuteAsync("INSERT INTO public.conversation_participants (conversation_id, user_id) VALUES (@conversation, @userId), (@conversation, @otherId) ON CONFLICT DO NOTHING", new { conversation, pair.userId, pair.otherId }, tx);
        }
        var updated = await conn.QuerySingleAsync<LfgInterestRow>(
            "UPDATE public.lfg_interests SET status = 'accepted', conversation_id = @conversation, updated_at = now() WHERE id = @interestId RETURNING id, listing_id, from_user_id, to_user_id, status::text AS status, message, created_at, updated_at",
            new { interestId, conversation }, tx);
        if (row.Status == "expressed")
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@recipient, 'lfg_interest_accepted', 'Your request was accepted',
                    'A conversation is ready.', @link,
                    jsonb_build_object('interest_id', @interestId::text, 'conversation_id', @conversation::text), FALSE)
                """, new { recipient = row.FromUserId, link = $"/inbox/{conversation}", interestId, conversation }, tx);
        tx.Commit();
        return Results.Ok(new { interest = updated, conversation_id = conversation });
    }
}

public sealed record LfgRosterListing(Guid TeamId, string GameSlug);

public sealed record LfgRosterMember(Guid UserId, string? Username, string? AvatarUrl, string? AvatarSeed,
    string? AvatarStyle, string? RosterRole, bool IsStarter, int? CompetitiveTier,
    string? CompetitiveTierName, decimal? KdRatio, decimal? WinRate, int? MatchesAnalyzed);

public sealed record LfgInboxRow
{
    public Guid Id { get; init; }
    public Guid ListingId { get; init; }
    public Guid FromUserId { get; init; }
    public Guid ToUserId { get; init; }
    public string Status { get; init; } = "";
    public string? Message { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string GameSlug { get; init; } = "";
    public string Intent { get; init; } = "";
    public string ListingStatus { get; init; } = "";
    public DateTimeOffset? ExpiresAt { get; init; }
    public Guid ListingOwnerId { get; init; }
    public string? ListingName { get; init; }
    public string? CounterpartUsername { get; init; }
    public string? CounterpartAvatarSeed { get; init; }
    public string? CounterpartAvatarUrl { get; init; }
    public string? CounterpartAvatarStyle { get; init; }
    public Guid? ConversationId { get; init; }
}
