using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Core.Broadcast;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastScreenEndpoints
{
    private static readonly HashSet<string> Overlays = new(StringComparer.Ordinal)
    {
        "agent-select", "banners", "bracket", "brb", "break-clock", "casters", "champion", "duel", "economy",
        "ending", "event-bug", "halftime", "holding", "ingame", "killfeed", "lower-third", "map-breakdown",
        "map-result", "map-veto", "match-intro", "minimap-frame", "moments", "observed-player", "player-cams",
        "player-cards", "player-profile", "poll", "replay", "round-history", "round-recap", "schedule", "scoreboard",
        "scorebug", "series", "spike-timer", "sponsor", "standings", "starting-soon", "stinger", "team-compare",
        "team-roster-a", "team-roster-b", "ticker", "toast", "win-probability"
    };

    public static void MapBroadcastScreenEndpoints(this WebApplication app) =>
        app.MapPost("/api/broadcast/shows/{showId:guid}/screens/import", Import)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));

    private static async Task<IResult> Import(Guid showId, [FromBody] ImportScreensRequest input, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (input.MatchId == Guid.Empty || input.Screens is not { Length: > 0 and <= 45 } ||
            input.Screens.Any(screen => screen is null || !Overlays.Contains(screen.Overlay ?? "") ||
                string.IsNullOrWhiteSpace(screen.Title) || screen.Title.Length > 120))
            return Results.BadRequest(new { error = "Choose 1–45 available screens with names up to 120 characters." });
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var show = await conn.QuerySingleOrDefaultAsync<ShowScope>("SELECT org_id AS OrgId, status AS Status FROM broadcast_shows WHERE id = @showId FOR UPDATE", new { showId }, tx);
        if (show is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid, tx)) return Results.StatusCode(403);
        if (!BroadcastShowRules.CanEditPlan(show.Status)) return Results.Conflict(new { error = "Screens are locked during and after a live show." });
        var eligible = await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM broadcast_show_matches sm JOIN brkt_matches bm ON bm.id = sm.match_id
              JOIN brkt_versions v ON v.id = bm.version_id JOIN tournaments t ON t.id = v.tournament_id
              JOIN broadcast_shows s ON s.id = sm.show_id
              WHERE sm.show_id = @showId AND sm.match_id = @matchId AND t.organization_id = @orgId
                AND t.deleted_at IS NULL AND bm.winner_id IS NULL
                AND (s.tournament_id IS NULL OR s.tournament_id = v.tournament_id)
                AND v.status IN ('published', 'active') AND bm.team1_id IS NOT NULL AND bm.team2_id IS NOT NULL
                AND bm.status IN ('pending', 'scheduled', 'in_progress'))
            """, new { showId, matchId = input.MatchId, orgId = show.OrgId }, tx);
        if (!eligible) return Results.Conflict(new { error = "This match is no longer eligible. Choose an unplayed match." });
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rundown_segments WHERE show_id = @showId", new { showId }, tx);
        if (count + input.Screens.Length > 200) return Results.BadRequest(new { error = "A show supports up to 200 flow items." });
        var order = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(display_order) + 1, 0) FROM rundown_segments WHERE show_id = @showId", new { showId }, tx);
        var appearance = BroadcastShowEndpoints.ReadAppearance(await conn.ExecuteScalarAsync<string>("SELECT appearance::text FROM broadcast_shows WHERE id = @showId", new { showId }, tx));
        var rows = input.Screens.Select((screen, index) => new
        {
            showId,
            title = screen.Title.Trim(),
            type = ScreenType(screen.Overlay),
            displayOrder = order + index,
            config = JsonSerializer.Serialize(new { overlay = screen.Overlay, matchId = input.MatchId.ToString(), plan = appearance.ForNewPlan(new("manual", "manual", "manual", 5, [screen.Overlay], Transition: new("fade", "Fade", 300))) }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        }).ToArray();
        await conn.ExecuteAsync("INSERT INTO rundown_segments(show_id, title, type, display_order, config) VALUES (@showId, @title, @type, @displayOrder, CAST(@config AS jsonb))", rows, tx);
        tx.Commit();
        return Results.Ok(new { count = rows.Length });
    }

    private static string ScreenType(string overlay) => overlay switch
    {
        "ingame" => "match",
        "starting-soon" or "match-intro" => "pre_show",
        "holding" or "brb" or "break-clock" => "break",
        "map-result" or "series" or "ending" or "champion" => "post_show",
        "sponsor" => "sponsor_slot",
        _ => "custom"
    };
    internal static bool IsKnownOverlay(string overlay) => Overlays.Contains(overlay);
    private sealed record ShowScope(Guid OrgId, string Status);
}

public sealed record ImportScreensRequest(Guid MatchId, BroadcastScreenInput[]? Screens);
public sealed record BroadcastScreenInput(string Overlay, string Title);
