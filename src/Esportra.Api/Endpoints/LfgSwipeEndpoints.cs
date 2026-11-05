using System.Data;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Esportra.Api.Hubs;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Squad Up — live swipe matching (Valorant).
/// POST   /api/competitive/lfg/swipe/session             — start a Squad Up session
/// DELETE /api/competitive/lfg/swipe/session             — end the session (returns session stats)
/// POST   /api/competitive/lfg/swipe/session/heartbeat   — keep presence alive (tab open)
/// POST   /api/competitive/lfg/swipe/session/active      — "Still looking?" (player is active)
/// GET    /api/competitive/lfg/swipe/session/stats       — swipes / matches / duration
/// GET    /api/competitive/lfg/swipe/cards               — next cards
/// GET    /api/competitive/lfg/swipe/pending-likes       — unseen likes
/// POST   /api/competitive/lfg/swipe/{id}/right|left
/// GET    /api/competitive/lfg/swipe/matches             — matches of the last 24h
/// </summary>
public static class LfgSwipeEndpoints
{
    public static void MapLfgSwipeEndpoints(this WebApplication app)
    {
        // Static segments first so they win over {targetUserId:guid}.
        app.MapPost("/api/competitive/lfg/swipe/session", StartSessionAsync).RequireAuthorization("Authenticated");
        app.MapDelete("/api/competitive/lfg/swipe/session", EndSessionAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/swipe/session/heartbeat", HeartbeatAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/swipe/session/active", StillHereAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/lfg/swipe/session/stats", GetStatsAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/lfg/swipe/cards", GetCardsAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/lfg/swipe/pending-likes", PendingLikesAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/lfg/swipe/matches", GetMatchesAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/swipe/{targetUserId:guid}/right", SwipeRightAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/lfg/swipe/{targetUserId:guid}/left", SwipeLeftAsync).RequireAuthorization("Authenticated");
    }

    private static Guid? CallerId(HttpContext ctx) => (ctx.Items["UserContext"] as UserContext)?.UserIdGuid;

    // ── POST session ─────────────────────────────────────────────────────────

    private static async Task<IResult> StartSessionAsync(
        [FromBody] StartSwipeSessionRequest? req, HttpContext ctx, IDbConnectionFactory db,
        ValorantSyncRequestService sync, IHubContext<NotificationHub> notifHub, ILoggerFactory loggerFactory)
    {
        if (CallerId(ctx) is not { } userId) return Results.Unauthorized();
        if (req is null || req.SlotsNeeded is < 1 or > 4)
            return Results.BadRequest(new { error = "slots_needed must be between 1 and 4." });

        using var conn = db.CreateConnection();
        var player = await SquadUpEligibility.LoadAsync(conn, userId);
        var (filters, filterError) = SquadUpEligibility.Normalise(req.Preferences);
        if (filters is null) return Results.BadRequest(new { error = filterError });
        var (note, noteError) = SquadUpEligibility.NormaliseNote(req.Note);
        if (noteError is not null) return Results.BadRequest(new { error = noteError });
        if (player.Linked && player.Tier is null && player.StatsSyncStatus is null or "pending" or "syncing")
            await sync.RequestAsync(userId, CancellationToken.None);
        var refused = await SquadUpEligibility.CheckAsync(conn, userId, player, filters);
        if (refused is not null) return refused;

        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            new { key = $"lfg-swipe-session:{userId}" }, tx);
        // A stale or ended session must not carry old likes into the new one (actions cascade).
        await conn.ExecuteAsync(
            $"DELETE FROM public.lfg_swipe_sessions WHERE user_id = @userId AND game_slug = @game AND NOT {LfgSwipeSql.Active("lfg_swipe_sessions")}",
            new { userId, game = filters.Game }, tx);
        var alreadyLive = await conn.ExecuteScalarAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM public.lfg_swipe_sessions s WHERE s.user_id = @userId AND s.game_slug = @game AND {LfgSwipeSql.Active("s")})",
            new { userId, game = filters.Game }, tx);
        var sessionAreas = filters.Areas ?? [filters.Area];
        var session = await conn.QuerySingleAsync<LfgSwipeSessionDto>(
            """
            INSERT INTO public.lfg_swipe_sessions
                (user_id, game_slug, region, competitive_tier, mic_on, slots_needed, mode, country_code, area, areas, note, min_tier, max_tier)
            VALUES (@userId, @game, @region, @tier, @micOn, @slots, @mode, @country, @area, @areas::text[], @note, @minTier, @maxTier)
            ON CONFLICT (user_id, game_slug) DO UPDATE SET
                mic_on = EXCLUDED.mic_on, slots_needed = EXCLUDED.slots_needed,
                competitive_tier = EXCLUDED.competitive_tier, region = EXCLUDED.region, mode = EXCLUDED.mode,
                country_code = EXCLUDED.country_code, area = EXCLUDED.area, areas = EXCLUDED.areas, note = EXCLUDED.note,
                min_tier = EXCLUDED.min_tier, max_tier = EXCLUDED.max_tier,
                last_heartbeat = now(), last_activity_at = now()
            RETURNING id, game_slug, region, competitive_tier, mic_on, slots_needed, started_at, last_heartbeat,
                      mode, country_code, area, areas, note, min_tier, max_tier
            """, new
            {
                userId,
                game = filters.Game,
                region = player.Region,
                tier = player.Tier,
                micOn = req.MicOn,
                slots = req.SlotsNeeded,
                mode = filters.Mode,
                country = player.Country,
                area = filters.Area,
                areas = sessionAreas,
                note,
                minTier = filters.MinTier,
                maxTier = filters.MaxTier,
            }, tx);
        if (!alreadyLive) await SquadUpStats.BumpAsync(conn, session.Region, SquadUpCounter.SessionsStarted, tx);
        tx.Commit();
        await PushPoolUpdatedAsync(notifHub, conn, loggerFactory.CreateLogger("SquadUp"), userId, session.GameSlug, session.Mode, session.Region, sessionAreas);
        return Results.Ok(session);
    }

    /// <summary>"Still looking?" — the player is here. Keeps an idle-but-open tab in the pool for another 10 minutes.</summary>
    private static async Task<IResult> StillHereAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var touched = await TouchActivityAsync(conn, myId);
        return touched ? Results.NoContent() : Results.NotFound(new { error = "NO_ACTIVE_SESSION" });
    }

    /// <summary>Records that the player did something. Heartbeats do not count, so an idle tab drops out.</summary>
    internal static async Task<bool> TouchActivityAsync(IDbConnection conn, Guid myId)
    {
        var session = await conn.ExecuteAsync(
            $"UPDATE public.lfg_swipe_sessions SET last_activity_at = now() WHERE user_id = @myId AND game_slug = 'valorant' AND {LfgSwipeSql.Active("lfg_swipe_sessions")}",
            new { myId });
        var lobby = await conn.ExecuteAsync(
            $"UPDATE public.squad_up_lobbies l SET last_activity_at = now() WHERE l.host_id = @myId AND {SquadUpLobbySql.Open("l")}",
            new { myId });
        return session + lobby > 0;
    }

    // ── DELETE session / heartbeat / stats ───────────────────────────────────

    private static async Task<IResult> EndSessionAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var stats = await conn.QuerySingleOrDefaultAsync<LfgSwipeStatsDto>(LfgSwipeSql.Stats, new { myId }, tx);
        await conn.ExecuteAsync(
            "DELETE FROM public.lfg_swipe_sessions WHERE user_id = @myId AND game_slug = 'valorant'", new { myId }, tx);
        tx.Commit();
        return stats is null ? Results.NoContent() : Results.Ok(stats);
    }

    /// <summary>
    /// Keeps the player's presence alive while the tab is open: their Duo session and the lobby they host.
    /// 404 when neither is live, which is how the page learns the player dropped out.
    /// </summary>
    private static async Task<IResult> HeartbeatAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var sessionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            $"""
            UPDATE public.lfg_swipe_sessions SET last_heartbeat = now()
            WHERE user_id = @myId AND game_slug = 'valorant' AND {LfgSwipeSql.Active("lfg_swipe_sessions")}
            RETURNING id
            """, new { myId });
        var hostingLobby = await conn.ExecuteAsync(
            $"UPDATE public.squad_up_lobbies l SET last_heartbeat = now() WHERE l.host_id = @myId AND {SquadUpLobbySql.Open("l")}",
            new { myId }) > 0;
        if (sessionId is null && !hostingLobby) return Results.NotFound(new { error = "NO_ACTIVE_SESSION" });
        var pendingLikes = sessionId is null ? 0 : await CountPendingLikesAsync(conn, myId, sessionId.Value);
        return Results.Ok(new { pendingLikes, inSession = sessionId is not null, hostingLobby });
    }

    private static async Task<IResult> GetStatsAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var stats = await conn.QuerySingleOrDefaultAsync<LfgSwipeStatsDto>(LfgSwipeSql.Stats, new { myId });
        return stats is null ? Results.NotFound(new { error = "NO_SESSION" }) : Results.Ok(stats);
    }

    // ── GET cards / pending-likes / matches ──────────────────────────────────

    private static async Task<IResult> GetCardsAsync(
        HttpContext ctx, IDbConnectionFactory db, [FromQuery] int? limit)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await HasActiveSessionAsync(conn, myId)) return Results.NotFound(new { error = "NO_ACTIVE_SESSION" });
        var cards = await conn.QueryAsync<LfgSwipeCardDto>(LfgSwipeSql.Cards(),
            new { myId, limit = Math.Clamp(limit ?? 5, 1, 10) });
        return Results.Ok(cards);
    }

    private static async Task<IResult> PendingLikesAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var sessionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            $"SELECT id FROM public.lfg_swipe_sessions s WHERE s.user_id = @myId AND s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")}",
            new { myId });
        if (sessionId is null) return Results.NotFound(new { error = "NO_ACTIVE_SESSION" });
        return Results.Ok(new { count = await CountPendingLikesAsync(conn, myId, sessionId.Value) });
    }

    private static Task<int> CountPendingLikesAsync(IDbConnection conn, Guid myId, Guid mySession) =>
        conn.ExecuteScalarAsync<int>(LfgSwipeSql.PendingLikes(), new { myId, mySession });

    private static async Task<IResult> GetMatchesAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        return Results.Ok(await conn.QueryAsync<LfgSwipeMatchDto>(LfgSwipeSql.Matches(), new { myId }));
    }

    // ── POST swipe ───────────────────────────────────────────────────────────

    private static async Task<IResult> SwipeLeftAsync(Guid targetUserId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        if (targetUserId == myId) return Results.BadRequest(new { error = "SELF_SWIPE" });
        using var conn = db.CreateConnection();
        var session = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Region)?>(
            $"SELECT id, region FROM public.lfg_swipe_sessions s WHERE s.user_id = @myId AND s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")}",
            new { myId });
        if (session is not { } current) return Results.NotFound(new { error = "NO_ACTIVE_SESSION" });
        var sessionId = current.Id;
        await TouchActivityAsync(conn, myId);
        // Selecting from profiles means an unknown id is a no-op rather than an FK error.
        var recorded = await conn.ExecuteAsync(
            """
            INSERT INTO public.lfg_swipe_actions (session_id, target_user_id, direction)
            SELECT @sessionId, p.id, 'pass' FROM public.profiles p WHERE p.id = @targetUserId
            ON CONFLICT (session_id, target_user_id) DO NOTHING
            """, new { sessionId, targetUserId });
        if (recorded > 0) await SquadUpStats.BumpAsync(conn, current.Region, SquadUpCounter.SwipesLeft);
        return Results.NoContent();
    }

    private static async Task<IResult> SwipeRightAsync(
        Guid targetUserId, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> notifHub)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        if (targetUserId == myId) return Results.BadRequest(new { error = "SELF_SWIPE" });
        using var conn = db.CreateConnection();
        var caller = await conn.QuerySingleOrDefaultAsync<Guid?>(
            $"SELECT id FROM public.lfg_swipe_sessions s WHERE s.user_id = @myId AND s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")}",
            new { myId });
        if (caller is null) return Results.NotFound(new { error = "NO_ACTIVE_SESSION" });
        await TouchActivityAsync(conn, myId);
        var outcome = await LfgSwipeMatchmaker.SwipeRightAsync(conn, myId, targetUserId);
        if (outcome is null) return Results.NotFound(new { error = "TARGET_NOT_ACTIVE" });
        await LfgSwipeMatchmaker.PushAsync(notifHub, outcome.Pushes);
        await LfgSwipeMatchmaker.PushLikeAsync(notifHub, outcome.LikePush);
        return Results.Ok(outcome.Result);
    }

    private static async Task<bool> HasActiveSessionAsync(IDbConnection conn, Guid myId) =>
        await conn.ExecuteScalarAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM public.lfg_swipe_sessions s WHERE s.user_id = @myId AND s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")})",
            new { myId });

    /// <summary>
    /// After a new/updated session commits, notify all currently-active compatible peers so their
    /// card decks refresh without a manual reload. Filters by game+region+areas overlap (tier is
    /// not checked here — the Cards query handles that). Fire-after-commit; non-critical.
    /// </summary>
    private static async Task PushPoolUpdatedAsync(
        IHubContext<NotificationHub> hub, IDbConnection conn, ILogger logger,
        Guid newUserId, string game, string mode, string region, string[] areas)
    {
        try
        {
            var peers = (await conn.QueryAsync<Guid>(
                $"""
                SELECT DISTINCT s.user_id
                FROM public.lfg_swipe_sessions s
                WHERE s.game_slug = @game AND s.mode = @mode AND s.region = @region AND s.areas && @areas::text[]
                  AND s.user_id <> @newUserId
                  AND {LfgSwipeSql.Active("s")}
                """, new { game, mode, region, areas, newUserId })).ToList();
            foreach (var peerId in peers)
            {
                await hub.Clients.Group(NotificationHub.UserGroup(peerId.ToString()))
                    .SendAsync(NotificationHubEvents.SquadUpPoolUpdated);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SquadUpPoolUpdated push failed for session {UserId}; peers will see the new card on their next deck refill.", newUserId);
        }
    }
}

public sealed record StartSwipeSessionRequest(
    [property: JsonPropertyName("mic_on")] bool MicOn,
    [property: JsonPropertyName("slots_needed")] int SlotsNeeded,
    [property: JsonPropertyName("game")] string? Game = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("area")] string? Area = null,
    [property: JsonPropertyName("min_tier")] int? MinTier = null,
    [property: JsonPropertyName("max_tier")] int? MaxTier = null,
    [property: JsonPropertyName("note")] string? Note = null,
    [property: JsonPropertyName("areas")] string[]? Areas = null)
{
    [JsonIgnore]
    public SquadUpPreferences Preferences => new(Game, Mode, Area, MinTier, MaxTier, Areas);
}
