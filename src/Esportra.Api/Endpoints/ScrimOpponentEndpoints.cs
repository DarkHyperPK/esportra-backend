using System.Text.Json;
using Esportra.Api.Services.ProviderMatches.Career;
using Esportra.Api.Services.ScrimData;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Scrim partners as teams, from a team's confirmed scrim maps: the record against each, maps, vetoes, and what your team played. A post-match history for
/// the team's own members; nothing about the other team's players is shown or aggregated here.
///
/// GET /api/scrims/teams/{teamId}/opponents
/// </summary>
public static class ScrimOpponentEndpoints
{
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static void MapScrimOpponentEndpoints(this WebApplication app) =>
        app.MapGet("/api/scrims/teams/{teamId:guid}/opponents", OpponentsAsync).RequireAuthorization("Authenticated");

    private static async Task<IResult> OpponentsAsync(Guid teamId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await ScrimDataQueries.TeamExistsAsync(conn, teamId)) return Results.NotFound();
        if (!await ScrimDataQueries.CanViewAsync(conn, viewer, teamId)) return Results.Forbid();

        var rows = await ScrimDataQueries.MapRowsAsync(conn, teamId, ScrimDataQueries.MapCap);
        var riotIds = rows.Where(r => r.AssociationStatus is "verified" or "partially_verified").Select(r => r.GameId).ToArray();
        var riotGames = await ScrimDataQueries.RiotGamesAsync(conn, teamId, riotIds);
        var riotPlayers = await ScrimDataQueries.RiotPlayersAsync(conn, teamId, riotGames.Keys.ToArray());
        var set = ScrimDataBuilder.Build(rows, riotGames, riotPlayers);

        var vetoes = await MapPickQueries.SessionsAsync(conn, teamId);
        var actions = await MapPickQueries.ActionsAsync(conn, vetoes.Select(v => v.VetoId).ToArray());
        var vetoGames = await MapPickQueries.GamesAsync(conn, vetoes.Select(v => v.SessionId).ToArray());
        var opponents = OpponentMath.Build(set.Games, vetoes, actions, vetoGames);
        return Results.Text(JsonSerializer.Serialize(new { team_id = teamId, has_data = opponents.Count > 0, opponents }, Snake), "application/json");
    }
}
