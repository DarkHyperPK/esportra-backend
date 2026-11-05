using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Admin Centre endpoints for Squad Up (live teammate finder).
/// GET  /api/admin/squad-up/overview?days=14            — live pool, totals, daily series, flag state (squad_up:view)
/// GET  /api/admin/squad-up/sessions?limit=&amp;offset=      — players in Squad Up right now (squad_up:view)
/// GET  /api/admin/squad-up/matches?limit=               — most recent matches (squad_up:view)
/// POST /api/admin/squad-up/sessions/{userId}/end       — remove a player from Squad Up (squad_up:manage; audited)
/// Turning the feature on or off is the existing feature-flag toggle (key: squad_up).
/// </summary>
/// <summary>Seeded by 20261105130200_squad_up_admin_permissions.sql (super_admin bypasses via IsSuperAdmin).</summary>
internal static class SquadUpPermissions
{
    public const string View = "squad_up:view";
    public const string Manage = "squad_up:manage";
}

public static class SquadUpAdminEndpoints
{
    public static void MapSquadUpAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/squad-up/overview", OverviewAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/admin/squad-up/sessions", SessionsAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/admin/squad-up/matches", MatchesAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/admin/squad-up/sessions/{userId:guid}/end", EndSessionAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/admin/squad-up/lobbies", LobbiesAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/admin/squad-up/lobbies/{id:guid}/close", CloseLobbyAsync).RequireAuthorization("Authenticated");
    }

    private static bool Allowed(HttpContext ctx, string permission) =>
        ctx.Items["UserContext"] is UserContext user && (user.IsSuperAdmin || user.Permissions.Contains(permission));

    private static async Task<IResult> OverviewAsync(
        [FromQuery] int? days, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is null) return Results.Unauthorized();
        if (!Allowed(ctx, SquadUpPermissions.View)) return Results.Forbid();
        var range = Math.Clamp(days ?? 14, 1, 90);
        using var conn = db.CreateConnection();
        var live = (await conn.QueryAsync<SquadUpRegionRow>(
            $"""
            SELECT s.region, count(*)::int AS players, COALESCE(round(avg(s.competitive_tier))::int, 0) AS avg_tier
            FROM public.lfg_swipe_sessions s
            WHERE s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")}
            GROUP BY s.region ORDER BY players DESC, s.region
            """)).ToList();
        var series = (await conn.QueryAsync<SquadUpDayRow>(
            """
            SELECT to_char(day, 'YYYY-MM-DD') AS day, COALESCE(sum(sessions_started), 0)::int AS sessions_started, COALESCE(sum(swipes_right), 0)::int AS swipes_right,
                   COALESCE(sum(swipes_left), 0)::int AS swipes_left, COALESCE(sum(matches), 0)::int AS matches,
                   COALESCE(sum(lobbies_opened), 0)::int AS lobbies_opened, COALESCE(sum(lobbies_filled), 0)::int AS lobbies_filled
            FROM public.squad_up_daily_stats
            WHERE day >= (now() AT TIME ZONE 'utc')::date - (@range - 1)
            GROUP BY day ORDER BY day
            """, new { range })).ToList();
        var flag = await conn.QuerySingleOrDefaultAsync<SquadUpFlagRow>(
            "SELECT id, is_enabled FROM public.feature_flags WHERE key = 'squad_up'");
        var reports = await conn.QuerySingleAsync<SquadUpReportCountsRow>(
            """
            SELECT count(*) FILTER (WHERE status = 'open')::int AS open,
                   count(*) FILTER (WHERE created_at > now() - interval '7 days')::int AS last7_days,
                   COALESCE(round(avg(extract(epoch FROM (resolved_at - created_at)) / 3600) FILTER (WHERE status = 'resolved'))::int, 0) AS avg_resolution_hours
            FROM public.squad_up_reports
            """);
        var byReason = await conn.QueryAsync<SquadUpReasonCountRow>(
            """
            SELECT reason, count(*)::int AS count FROM public.squad_up_reports
            WHERE created_at >= now() - make_interval(days => @range) GROUP BY reason ORDER BY count DESC
            """, new { range });
        var pool = await conn.QuerySingleAsync<SquadUpPoolRow>(
            $"""
            SELECT count(*) FILTER (WHERE s.mode = 'competitive')::int AS competitive,
                   count(*) FILTER (WHERE s.mode = 'unrated')::int AS unrated,
                   (SELECT count(*) FROM public.squad_up_lobbies l WHERE {SquadUpLobbySql.Open("l")})::int AS open_lobbies
            FROM public.lfg_swipe_sessions s WHERE {LfgSwipeSql.Active("s")}
            """);
        var rights = series.Sum(d => d.SwipesRight);
        var matches = series.Sum(d => d.Matches);
        return Results.Ok(new
        {
            flag = flag is null ? null : new { id = flag.Id, enabled = flag.IsEnabled },
            live = new { activePlayers = live.Sum(r => r.Players), byRegion = live, pool.Competitive, pool.Unrated, pool.OpenLobbies },
            totals = new
            {
                days = range,
                sessionsStarted = series.Sum(d => d.SessionsStarted),
                swipesRight = rights,
                swipesLeft = series.Sum(d => d.SwipesLeft),
                matches,
                matchRate = rights == 0 ? 0m : Math.Round(100m * matches / rights, 1),
                lobbiesOpened = series.Sum(d => d.LobbiesOpened),
                lobbiesFilled = series.Sum(d => d.LobbiesFilled),
            },
            series,
            reports = new { reports.Open, reports.Last7Days, reports.AvgResolutionHours, byReason },
        });
    }

    private static async Task<IResult> SessionsAsync(
        [FromQuery] int? limit, [FromQuery] int? offset, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is null) return Results.Unauthorized();
        if (!Allowed(ctx, SquadUpPermissions.View)) return Results.Forbid();
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<SquadUpSessionRow>(
            $"""
            SELECT s.user_id, p.username, s.region, s.competitive_tier, s.mic_on, s.slots_needed, s.started_at, s.last_heartbeat,
                   s.mode, s.country_code, s.area, s.note,
                   (SELECT count(*) FROM public.lfg_swipe_actions a WHERE a.session_id = s.id AND a.direction = 'like')::int AS likes_sent,
                   (SELECT count(*) FROM public.lfg_swipe_actions a WHERE a.session_id = s.id AND a.direction = 'pass')::int AS passes_sent
            FROM public.lfg_swipe_sessions s
            JOIN public.profiles p ON p.id = s.user_id
            WHERE s.game_slug = 'valorant' AND {LfgSwipeSql.Active("s")}
            ORDER BY s.started_at DESC
            LIMIT @limit OFFSET @offset
            """, new { limit = Math.Clamp(limit ?? 50, 1, 200), offset = Math.Max(offset ?? 0, 0) });
        return Results.Ok(rows);
    }

    private static async Task<IResult> MatchesAsync(
        [FromQuery] int? limit, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is null) return Results.Unauthorized();
        if (!Allowed(ctx, SquadUpPermissions.View)) return Results.Forbid();
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<SquadUpMatchRow>(
            """
            SELECT m.id, m.matched_at, m.conversation_id, m.user1_id, u1.username AS user1_username,
                   m.user2_id, u2.username AS user2_username
            FROM public.lfg_swipe_matches m
            JOIN public.profiles u1 ON u1.id = m.user1_id
            JOIN public.profiles u2 ON u2.id = m.user2_id
            ORDER BY m.matched_at DESC LIMIT @limit
            """, new { limit = Math.Clamp(limit ?? 50, 1, 200) });
        return Results.Ok(rows);
    }

    private static async Task<IResult> LobbiesAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is null) return Results.Unauthorized();
        if (!Allowed(ctx, SquadUpPermissions.View)) return Results.Forbid();
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<SquadUpAdminLobbyRow>(
            $"""
            SELECT l.id, l.host_id, p.username AS host_username, l.title, l.mode, l.region, l.country_code, l.area, l.open_slots, l.status, l.created_at,
                   (SELECT count(*) FROM public.squad_up_lobby_members m WHERE m.lobby_id = l.id AND m.status = 'accepted')::int AS accepted_count,
                   (SELECT count(*) FROM public.squad_up_lobby_members m WHERE m.lobby_id = l.id AND m.status = 'requested')::int AS pending_count
            FROM public.squad_up_lobbies l JOIN public.profiles p ON p.id = l.host_id
            WHERE {SquadUpLobbySql.Open("l")} OR (l.status = 'full' AND l.created_at > now() - interval '6 hours')
            ORDER BY l.created_at DESC LIMIT 200
            """);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CloseLobbyAsync(Guid id, HttpContext ctx, IDbConnectionFactory db,
        Microsoft.AspNetCore.SignalR.IHubContext<Esportra.Api.Hubs.NotificationHub> hub)
    {
        if (ctx.Items["UserContext"] is null) return Results.Unauthorized();
        if (!Allowed(ctx, SquadUpPermissions.Manage)) return Results.Forbid();
        using var conn = db.CreateConnection();
        var closed = await conn.ExecuteAsync(
            "UPDATE public.squad_up_lobbies SET status = 'closed', closed_at = now() WHERE id = @id AND status IN ('open', 'full')", new { id });
        if (closed == 0) return Results.NotFound();
        await SquadUpLobbyMembership.PushUpdatedAsync(conn, hub, id);
        return Results.NoContent();
    }

    private static async Task<IResult> EndSessionAsync(Guid userId, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is null) return Results.Unauthorized();
        if (!Allowed(ctx, SquadUpPermissions.Manage)) return Results.Forbid();
        using var conn = db.CreateConnection();
        var removed = await conn.ExecuteAsync(
            "DELETE FROM public.lfg_swipe_sessions WHERE user_id = @userId AND game_slug = 'valorant'", new { userId });
        return removed == 0 ? Results.NotFound(new { error = "NO_SESSION" }) : Results.NoContent();
    }
}

public sealed record SquadUpReportCountsRow
{
    public int Open { get; init; }
    public int Last7Days { get; init; }
    public int AvgResolutionHours { get; init; }
}

public sealed record SquadUpReasonCountRow
{
    public string Reason { get; init; } = "";
    public int Count { get; init; }
}

public sealed record SquadUpRegionRow
{
    public string Region { get; init; } = "";
    public int Players { get; init; }
    public int AvgTier { get; init; }
}

public sealed record SquadUpPoolRow
{
    public int Competitive { get; init; }
    public int Unrated { get; init; }
    public int OpenLobbies { get; init; }
}

public sealed record SquadUpAdminLobbyRow
{
    public Guid Id { get; init; }
    public Guid HostId { get; init; }
    public string? HostUsername { get; init; }
    public string Title { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Region { get; init; } = "";
    public string? CountryCode { get; init; }
    public string? Area { get; init; }
    public int OpenSlots { get; init; }
    public int AcceptedCount { get; init; }
    public int PendingCount { get; init; }
    public string Status { get; init; } = "";
    public DateTime CreatedAt { get; init; }
}

public sealed record SquadUpDayRow
{
    public int LobbiesOpened { get; init; }
    public int LobbiesFilled { get; init; }
    public string Day { get; init; } = "";
    public int SessionsStarted { get; init; }
    public int SwipesRight { get; init; }
    public int SwipesLeft { get; init; }
    public int Matches { get; init; }
}

public sealed record SquadUpFlagRow
{
    public Guid Id { get; init; }
    public bool IsEnabled { get; init; }
}

public sealed record SquadUpSessionRow
{
    public Guid UserId { get; init; }
    public string? Username { get; init; }
    public string Region { get; init; } = "";
    public string Mode { get; init; } = "";
    public string? CountryCode { get; init; }
    public string? Area { get; init; }
    public string? Note { get; init; }
    public int? CompetitiveTier { get; init; }
    public bool MicOn { get; init; }
    public int SlotsNeeded { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime LastHeartbeat { get; init; }
    public int LikesSent { get; init; }
    public int PassesSent { get; init; }
}

public sealed record SquadUpMatchRow
{
    public Guid Id { get; init; }
    public DateTime MatchedAt { get; init; }
    public Guid? ConversationId { get; init; }
    public Guid User1Id { get; init; }
    public string? User1Username { get; init; }
    public Guid User2Id { get; init; }
    public string? User2Username { get; init; }
}
