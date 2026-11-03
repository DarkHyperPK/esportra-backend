using System.Text.Json;
using Dapper;
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
/// </summary>
public static class ScrimReportEndpoints
{
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void MapScrimReportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/scrims/teams/{teamId:guid}/report", TeamReportAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/scrims/teams/{teamId:guid}/players/{userId:guid}/report", PlayerReportAsync).RequireAuthorization("Authenticated");
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
