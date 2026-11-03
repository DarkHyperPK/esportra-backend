using System.Text.Json;
using Esportra.Api.Services.ScrimData;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Map picks across a team's scrims: what it and its opponents picked and banned, deciders, and results by who chose the map.
/// Members of the team only (the same rule as the rest of the scrim insights). Only scrims whose veto finished are counted.
///
/// GET /api/scrims/teams/{teamId}/map-picks
/// </summary>
public static class ScrimMapPickEndpoints
{
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void MapScrimMapPickEndpoints(this WebApplication app) =>
        app.MapGet("/api/scrims/teams/{teamId:guid}/map-picks", MapPicksAsync).RequireAuthorization("Authenticated");

    private static async Task<IResult> MapPicksAsync(Guid teamId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await ScrimDataQueries.TeamExistsAsync(conn, teamId)) return Results.NotFound();
        if (!await ScrimDataQueries.CanViewAsync(conn, viewer, teamId)) return Results.Forbid();

        var sessions = await MapPickQueries.SessionsAsync(conn, teamId);
        var actions = await MapPickQueries.ActionsAsync(conn, sessions.Select(s => s.VetoId).ToArray());
        var games = await MapPickQueries.GamesAsync(conn, sessions.Select(s => s.SessionId).ToArray());
        var picks = MapPickMath.Build(sessions, actions, games);
        return Results.Text(JsonSerializer.Serialize(new
        {
            team_id = teamId,
            has_data = picks.Vetoes > 0,
            truncated = sessions.Count >= MapPickQueries.SessionCap,
            picks,
        }, Snake), "application/json");
    }
}
