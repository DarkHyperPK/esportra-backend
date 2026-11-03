using System.Text.Json;
using Esportra.Api.Middleware;
using Esportra.Api.Services;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Api.Services.ScrimInsights;
using Esportra.Api.Services.ScrimReports;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// The per-map scrim report: one verified Riot map in depth (rounds, opening duels, kill feed, damage, economy, abilities, spike),
/// for members of either team in the scrim. Opponent players appear by name for this one match only, as in the match history.
/// Without verified Riot data it says so and what the map does have; it never fills sections with invented values.
///
/// GET /api/scrims/sessions/{id}/games/{n}/insights?team_id=
/// </summary>
public static class ScrimGameInsightsEndpoints
{
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void MapScrimGameInsightsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/scrims/sessions/{id:guid}/games/{n:int}/insights", InsightsAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/valorant/maps", () => Results.Text(Esportra.Api.Services.ValorantMapCatalog.SlimJson, "application/json")).AllowAnonymous().WithMetadata(new RateLimitPolicyMetadata("fixed"));
        app.MapGet("/api/scrims/sessions/{id:guid}/games/{n:int}/timeline", TimelineAsync).RequireAuthorization("Authenticated");
    }

    private static async Task<IResult> InsightsAsync(Guid id, int n, HttpContext ctx, IDbConnectionFactory db, [FromQuery] Guid? team_id = null)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var game = await ScrimInsightsQueries.GameAsync(conn, id, n);
        if (game is null) return Results.NotFound();

        var admin = viewer.AdminRoles.Length > 0;
        var memberTeams = await ScrimInsightsQueries.MemberTeamsAsync(conn, viewer.UserIdGuid, game.Team1Id, game.Team2Id);
        if (!admin && memberTeams.Length == 0) return Results.Forbid();
        var myTeam = Pick(team_id, memberTeams, game);
        if (myTeam is null) return Results.BadRequest(new { error = "team_id must be one of the two teams in this scrim." });
        var mySlot = myTeam == game.Team1Id ? 1 : 2;

        var head = await ScrimInsightsQueries.HeadAsync(conn, game.GameId);
        var vetoEntry = (await ScrimVetoMaps.LoadAsync(conn, id))?.ForGame(n);
        var common = Common(id, n, game, myTeam.Value, mySlot, vetoEntry, head);

        var usable = head is { Verification: "verified" or "partially_verified", DataStatus: "retrieved", IsCompleted: true };
        if (!usable) return Json(common(null, Fallback(game, head)));

        var mySide = mySlot == 1 ? head!.Team1Side : head!.Team2Side;
        if (mySide is null) return Json(common(null, Fallback(game, head)));
        var match = await ScrimInsightsQueries.LoadMatchAsync(conn, head, mySide);
        return Json(common(ScrimInsightsBuilder.Build(match), null));
    }

    private static async Task<IResult> TimelineAsync(Guid id, int n, HttpContext ctx, IDbConnectionFactory db, [FromQuery] Guid? team_id = null)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var game = await ScrimInsightsQueries.GameAsync(conn, id, n);
        if (game is null) return Results.NotFound();
        var admin = viewer.AdminRoles.Length > 0;
        var memberTeams = await ScrimInsightsQueries.MemberTeamsAsync(conn, viewer.UserIdGuid, game.Team1Id, game.Team2Id);
        if (!admin && memberTeams.Length == 0) return Results.Forbid();
        var myTeam = Pick(team_id, memberTeams, game);
        if (myTeam is null) return Results.BadRequest(new { error = "team_id must be one of the two teams in this scrim." });
        var mySlot = myTeam == game.Team1Id ? 1 : 2;

        var head = await ScrimInsightsQueries.HeadAsync(conn, game.GameId);
        var usable = head is { Verification: "verified" or "partially_verified", DataStatus: "retrieved", IsCompleted: true };
        var mySide = head is null ? null : mySlot == 1 ? head.Team1Side : head.Team2Side;
        if (!usable || mySide is null)
            return Json(new { session_id = id, game_number = n, timeline = (object?)null, reason = Fallback(game, head).Reason });

        var match = await ScrimInsightsQueries.LoadMatchAsync(conn, head!, mySide);
        var locations = (await HeatmapQueries.LocationsAsync(conn, [head!.MatchId])).GetValueOrDefault(head.MatchId) ?? [];
        var map = ProviderMatchVerifier.DisplayMap(head.MapId) ?? ValorantMaps.Resolve(game.MapName);
        return Json(new
        {
            session_id = id,
            game_number = n,
            reason = (string?)null,
            map = string.IsNullOrEmpty(map) ? null : map,
            map_path = head.MapId,
            result = game.WinnerSlot is { } w ? (w == mySlot ? "win" : "loss") : "draw",
            score = new { mine = mySlot == 1 ? game.Team1Score : game.Team2Score, theirs = mySlot == 1 ? game.Team2Score : game.Team1Score },
            timeline = TimelineBuilder.Build(match, locations),
        });
    }

    private static Guid? Pick(Guid? requested, Guid[] memberTeams, ScrimInsightsQueries.GameRef game)
    {
        var both = new[] { game.Team1Id, game.Team2Id };
        if (requested is { } r) return both.Contains(r) && (memberTeams.Length == 0 || memberTeams.Contains(r)) ? r : null;
        return memberTeams.Length == 1 ? memberTeams[0] : game.Team1Id;
    }

    private static (string Tier, string Reason) Fallback(ScrimInsightsQueries.GameRef game, ScrimInsightsQueries.MatchHead? head)
    {
        var tier = game.Source == "ocr" ? "screenshot" : "score_only";
        var why = head is null
            ? "This map was not reported from a Riot match, so only its result is recorded."
            : head.Verification switch
            {
                "pending" => "Riot's record of this map has not arrived yet. This page fills in once it has.",
                "disputed" => "Riot's record disagrees with the reported result, so detailed stats are not shown.",
                "duplicate" => "That Riot match was already used for another map.",
                _ => "This map could not be verified against Riot's record, so detailed stats are not shown.",
            };
        return (tier, why);
    }

    private static Func<InsightsReport?, (string Tier, string Reason)?, object> Common(
        Guid sessionId, int n, ScrimInsightsQueries.GameRef game, Guid myTeam, int mySlot, VetoMapEntry? veto, ScrimInsightsQueries.MatchHead? head)
    {
        var mine = mySlot == 1 ? game.Team1Score : game.Team2Score;
        var theirs = mySlot == 1 ? game.Team2Score : game.Team1Score;
        var map = head is null ? ValorantMaps.Resolve(game.MapName) : ProviderMatchVerifier.DisplayMap(head.MapId) ?? ValorantMaps.Resolve(game.MapName);
        return (report, fallback) => new
        {
            session_id = sessionId,
            game_number = n,
            tier = report is not null ? "full" : fallback!.Value.Tier,
            reason = fallback?.Reason,
            my_team = new { id = myTeam, name = mySlot == 1 ? game.Team1Name : game.Team2Name },
            opponent = new { id = mySlot == 1 ? game.Team2Id : game.Team1Id, name = mySlot == 1 ? game.Team2Name : game.Team1Name },
            score = new { mine, theirs },
            result = game.WinnerSlot is { } w ? (w == mySlot ? "win" : "loss") : mine > theirs ? "win" : mine < theirs ? "loss" : "draw",
            map = string.IsNullOrEmpty(map) ? null : map,
            match = head is null ? null : new
            {
                verification = head.Verification,
                queue_id = head.QueueId,
                is_custom = head.IsCustom,
                started_at = head.StartedAt,
                length_ms = head.LengthMs,
            },
            veto = veto is null ? null : new
            {
                expected_map = veto.MapName,
                picked_by = veto.IsDecider ? (string?)null : veto.PickedBySlot == mySlot ? "you" : "opponent",
                is_decider = veto.IsDecider,
                order = veto.Order,
                played_on_veto_map = !string.IsNullOrEmpty(map) && string.Equals(map, veto.MapName, StringComparison.OrdinalIgnoreCase),
            },
            report,
        };
    }

    private static IResult Json(object body) => Results.Text(JsonSerializer.Serialize(body, Snake), "application/json");
}
