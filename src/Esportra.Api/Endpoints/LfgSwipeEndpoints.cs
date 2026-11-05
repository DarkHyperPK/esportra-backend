using System.Data;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Esportra.Api.Hubs;

namespace Esportra.Api.Endpoints;

/// <summary>
/// LFG Queue Now — live swipe matching (Valorant).
/// POST   /api/competitive/lfg/swipe/session             — enter the queue
/// DELETE /api/competitive/lfg/swipe/session             — leave the queue (returns session stats)
/// POST   /api/competitive/lfg/swipe/session/heartbeat   — keep presence alive
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
        [FromBody] StartSwipeSessionRequest? req, HttpContext ctx, IDbConnectionFactory db)
    {
        if (CallerId(ctx) is not { } userId) return Results.Unauthorized();
        if (req is null || req.SlotsNeeded is < 1 or > 4)
            return Results.BadRequest(new { error = "slots_needed must be between 1 and 4." });

        using var conn = db.CreateConnection();
        var profile = await conn.QuerySingleOrDefaultAsync<(bool Linked, int? Tier, string? Region)>(
            """
            SELECT EXISTS (SELECT 1 FROM public.riot_accounts WHERE user_id = @userId) AS linked,
                   (SELECT pgs.competitive_tier FROM public.player_game_stats pgs
                    WHERE pgs.user_id = @userId AND pgs.game_slug = 'valorant'
                      AND EXISTS (SELECT 1 FROM public.riot_accounts l WHERE l.user_id = pgs.user_id AND l.puuid = pgs.provider_player_id)) AS tier,
                   (SELECT lower(region) FROM public.riot_accounts WHERE user_id = @userId) AS region
            """, new { userId });
        if (!profile.Linked) return Results.UnprocessableEntity(new { error = "RIOT_NOT_LINKED" });
        if (profile.Tier is null) return Results.UnprocessableEntity(new { error = "NOT_RANKED" });
        if (string.IsNullOrWhiteSpace(profile.Region)) return Results.UnprocessableEntity(new { error = "REGION_UNKNOWN" });

        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            new { key = $"lfg-swipe-session:{userId}" }, tx);
        // A stale or ended session must not carry old likes into the new one (actions cascade).
        await conn.ExecuteAsync(
            $"DELETE FROM public.lfg_swipe_sessions WHERE user_id = @userId AND game_slug = 'valorant' AND NOT {LfgSwipeSql.Active("lfg_swipe_sessions")}",
            new { userId }, tx);
        var session = await conn.QuerySingleAsync<LfgSwipeSessionDto>(
            """
            INSERT INTO public.lfg_swipe_sessions (user_id, game_slug, region, competitive_tier, mic_on, slots_needed)
            VALUES (@userId, 'valorant', @region, @tier, @micOn, @slots)
            ON CONFLICT (user_id, game_slug) DO UPDATE SET
                mic_on = EXCLUDED.mic_on, slots_needed = EXCLUDED.slots_needed,
                competitive_tier = EXCLUDED.competitive_tier, region = EXCLUDED.region, last_heartbeat = now()
            RETURNING id, game_slug, region, competitive_tier, mic_on, slots_needed, started_at, last_heartbeat
            """, new { userId, region = profile.Region, tier = profile.Tier, micOn = req.MicOn, slots = req.SlotsNeeded }, tx);
        tx.Commit();
        return Results.Ok(session);
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
        if (sessionId is null) return Results.NotFound(new { error = "NO_ACTIVE_SESSION" });
        return Results.Ok(new { pendingLikes = await CountPendingLikesAsync(conn, myId, sessionId.Value) });
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
        HttpContext ctx, IDbConnectionFactory db, [FromQuery] int? limit, [FromQuery(Name = "tier_range")] int? tierRange)
    {
        if (CallerId(ctx) is not { } myId) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await HasActiveSessionAsync(conn, myId)) return Results.NotFound(new { error = "NO_ACTIVE_SESSION" });
        var cards = await conn.QueryAsync<LfgSwipeCardDto>(LfgSwipeSql.Cards(),
            new { myId, limit = Math.Clamp(limit ?? 5, 1, 10), tierRange = Math.Clamp(tierRange ?? 4, 1, 8) });
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
        conn.ExecuteScalarAsync<int>(
            $"""
            SELECT count(*)::int FROM public.lfg_swipe_actions a
            JOIN public.lfg_swipe_sessions s ON s.id = a.session_id AND {LfgSwipeSql.Active("s")}
            WHERE a.direction = 'like' AND a.target_user_id = @myId
              AND NOT EXISTS (SELECT 1 FROM public.lfg_swipe_actions mine
                              WHERE mine.session_id = @mySession AND mine.target_user_id = s.user_id)
            """, new { myId, mySession });

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
        var sessionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            $"SELECT id FROM public.lfg_swipe_sessions s WHERE s.user_id = @myId AND s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")}",
            new { myId });
        if (sessionId is null) return Results.NotFound(new { error = "NO_ACTIVE_SESSION" });
        // Selecting from profiles means an unknown id is a no-op rather than an FK error.
        await conn.ExecuteAsync(
            """
            INSERT INTO public.lfg_swipe_actions (session_id, target_user_id, direction)
            SELECT @sessionId, p.id, 'pass' FROM public.profiles p WHERE p.id = @targetUserId
            ON CONFLICT (session_id, target_user_id) DO NOTHING
            """, new { sessionId, targetUserId });
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
        var outcome = await LfgSwipeMatchmaker.SwipeRightAsync(conn, myId, targetUserId);
        if (outcome is null) return Results.NotFound(new { error = "TARGET_NOT_ACTIVE" });
        await LfgSwipeMatchmaker.PushAsync(notifHub, outcome.Pushes);
        return Results.Ok(outcome.Result);
    }

    private static async Task<bool> HasActiveSessionAsync(IDbConnection conn, Guid myId) =>
        await conn.ExecuteScalarAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM public.lfg_swipe_sessions s WHERE s.user_id = @myId AND s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")})",
            new { myId });
}

public sealed record StartSwipeSessionRequest(
    [property: JsonPropertyName("mic_on")] bool MicOn,
    [property: JsonPropertyName("slots_needed")] int SlotsNeeded);
