using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Api.Services.ScrimData;
using Esportra.Api.Services.ScrimReports;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Team and player reports for Scrim Insights: charts-ready series, splits and highlights over a team's verified scrim maps.
/// Staff (captain, owner, manager, coach) and admins see every player; other members see the team report and only their own player report.
///
/// GET /api/scrims/teams/{teamId}/report
/// GET /api/scrims/teams/{teamId}/players/{userId}/report
/// GET /api/scrims/teams/{teamId}/trends (team figures only: every member)
/// GET /api/scrims/teams/{teamId}/heatmap?map=&kind=&side= (recorded event positions: every member)
/// </summary>
public static class ScrimReportEndpoints
{
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void MapScrimReportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/scrims/teams/{teamId:guid}/report", TeamReportAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/teams/{teamId:guid}/players/{userId:guid}/report", PlayerReportAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/teams/{teamId:guid}/trends", TrendsAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/teams/{teamId:guid}/heatmap", HeatmapAsync).RequireAuthorization("Authenticated");
    }

    private static async Task<bool> IsStaffAsync(System.Data.IDbConnection conn, UserContext viewer, Guid teamId) =>
        viewer.AdminRoles.Length > 0 || await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM public.team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE AND role::text IN ('captain', 'owner', 'manager', 'coach'))",
            new { teamId, userId = viewer.UserIdGuid });

    private static async Task<IResult> TeamReportAsync(Guid teamId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await ScrimDataQueries.TeamExistsAsync(conn, teamId)) return Results.NotFound();
        if (!await ScrimDataQueries.CanViewAsync(conn, viewer, teamId)) return Results.Forbid();
        var staff = await IsStaffAsync(conn, viewer, teamId);

        var (matches, capped) = await ScrimReportLoader.LoadAsync(conn, teamId);
        var report = ScrimReportMath.Team(matches, capped);
        if (!staff) report = Scope(report, viewer.UserIdGuid);
        return Json(new
        {
            team_id = teamId,
            has_data = matches.Count > 0,
            staff,
            viewer_user_id = viewer.UserIdGuid,
            max_maps = ScrimReportQueries.MapCap,
            report,
        });
    }

    private static async Task<IResult> TrendsAsync(Guid teamId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await ScrimDataQueries.TeamExistsAsync(conn, teamId)) return Results.NotFound();
        if (!await ScrimDataQueries.CanViewAsync(conn, viewer, teamId)) return Results.Forbid();
        var (matches, capped) = await ScrimReportLoader.LoadAsync(conn, teamId);
        return Json(new { team_id = teamId, has_data = matches.Count > 0, max_maps = ScrimReportQueries.MapCap, trends = TrendsMath.Build(matches, capped, DateTime.UtcNow) });
    }

    private static async Task<IResult> HeatmapAsync(Guid teamId, HttpContext ctx, IDbConnectionFactory db, string? map = null, string kind = "kills", string? side = null)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        if (!HeatmapMath.Kinds.Contains(kind)) return Results.BadRequest(new { error = "unknown kind" });
        if (side is not (null or "attack" or "defense")) return Results.BadRequest(new { error = "side must be attack or defense" });
        using var conn = db.CreateConnection();
        if (!await ScrimDataQueries.TeamExistsAsync(conn, teamId)) return Results.NotFound();
        if (!await ScrimDataQueries.CanViewAsync(conn, viewer, teamId)) return Results.Forbid();

        var heads = await ScrimReportQueries.HeadsAsync(conn, teamId);
        var all = heads.Take(ScrimReportQueries.MapCap).Select(h => (Head: h, Map: MapName(h))).ToList();
        var available = all.Where(x => x.Map is not null).GroupBy(x => x.Map!).Select(g => new { map = g.Key, maps = g.Count() }).OrderByDescending(a => a.maps).ThenBy(a => a.map, StringComparer.Ordinal).ToList();
        var chosen = all.Where(x => map is null || string.Equals(x.Map, map, StringComparison.OrdinalIgnoreCase)).ToList();
        var (matches, _) = chosen.Count == 0 ? ([], []) : await ScrimReportQueries.LoadAsync(conn, chosen.Select(x => x.Head).ToList());
        var needsLocations = kind is "kills" or "opening_kills";
        var locations = needsLocations && chosen.Count > 0 ? await HeatmapQueries.LocationsAsync(conn, chosen.Select(x => x.Head.MatchId).ToList()) : [];
        var points = new List<HeatPoint>();
        foreach (var (head, _) in chosen)
        {
            var perMatch = locations.GetValueOrDefault(head.MatchId) ?? [];
            points.AddRange(HeatmapMath.Points(matches[head.GameId], perMatch, kind, side));
            if (points.Count >= HeatmapMath.PointCap) break;
        }
        return Json(new { team_id = teamId, map, kind, side, maps = chosen.Count, available_maps = available, truncated = points.Count >= HeatmapMath.PointCap, points = points.Take(HeatmapMath.PointCap) });
    }

    private static string? MapName(ScrimReportQueries.Head h)
    {
        var name = Services.ProviderMatches.ProviderMatchVerifier.DisplayMap(h.MapId) ?? ValorantMaps.Resolve(h.MapName);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>A member who is not staff sees the team figures and their own row only; findings about other players are dropped.</summary>
    internal static TeamReport Scope(TeamReport r, Guid viewer) => r with
    {
        Players = r.Players.Where(p => p.UserId == viewer).ToList(),
        Highlights = r.Highlights.Where(h => h.PlayerId is null || h.PlayerId == viewer).ToList(),
    };

    private static async Task<IResult> PlayerReportAsync(Guid teamId, Guid userId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await ScrimDataQueries.TeamExistsAsync(conn, teamId)) return Results.NotFound();
        if (!await ScrimDataQueries.CanViewAsync(conn, viewer, teamId)) return Results.Forbid();
        var staff = await IsStaffAsync(conn, viewer, teamId);
        if (!staff && userId != viewer.UserIdGuid) return Results.Forbid();

        var (matches, capped) = await ScrimReportLoader.LoadAsync(conn, teamId);
        var report = PlayerReportMath.Build(userId, matches, capped);
        return Json(new
        {
            team_id = teamId,
            user_id = userId,
            has_data = report is not null,
            reason = report is null ? "This player has no verified Riot maps with this team yet." : null,
            report,
        });
    }

    private static IResult Json(object body) => Results.Text(JsonSerializer.Serialize(body, Snake), "application/json");
}
