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
                   COALESCE(sum(swipes_left), 0)::int AS swipes_left, COALESCE(sum(matches), 0)::int AS matches
            FROM public.squad_up_daily_stats
            WHERE day >= (now() AT TIME ZONE 'utc')::date - (@range - 1)
            GROUP BY day ORDER BY day
            """, new { range })).ToList();
        var flag = await conn.QuerySingleOrDefaultAsync<SquadUpFlagRow>(
            "SELECT id, is_enabled FROM public.feature_flags WHERE key = 'squad_up'");
        var rights = series.Sum(d => d.SwipesRight);
        var matches = series.Sum(d => d.Matches);
        return Results.Ok(new
        {
            flag = flag is null ? null : new { id = flag.Id, enabled = flag.IsEnabled },
            live = new { activePlayers = live.Sum(r => r.Players), byRegion = live },
            totals = new
            {
                days = range,
                sessionsStarted = series.Sum(d => d.SessionsStarted),
                swipesRight = rights,
                swipesLeft = series.Sum(d => d.SwipesLeft),
                matches,
                matchRate = rights == 0 ? 0m : Math.Round(100m * matches / rights, 1),
            },
            series,
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

public sealed record SquadUpRegionRow
{
    public string Region { get; init; } = "";
    public int Players { get; init; }
    public int AvgTier { get; init; }
}

public sealed record SquadUpDayRow
{
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
    public int CompetitiveTier { get; init; }
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
