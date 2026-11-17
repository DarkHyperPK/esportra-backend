using System.Data;
using Dapper;
using Esportra.Api.Services;
using Esportra.Core.Broadcast;

namespace Esportra.Api.Endpoints;

public static partial class AirBroadcastEndpoints
{
    // The match's map veto as it stands, for Air's veto graphic while the teams ban and pick.
    // Read-only and scoped like the match list: same account, production device and match access.
    private static async Task<IResult> GetVeto(Guid matchId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Json(new { error = "An active Air account and registered production PC are required." }, statusCode: 403);
        var matches = await Matches(conn, AirAccountEndpoints.User(ctx), matchId);
        if (matches.Length == 0) return Results.NotFound();
        var match = matches[0];
        var steps = await ReadVetoSteps(conn, match.Id);
        var mapPool = await ReadMapPool(conn, match.TournamentId);
        var status = await conn.ExecuteScalarAsync<string?>(
            "SELECT status::text FROM match_map_vetos WHERE match_id=@matchId ORDER BY updated_at DESC NULLS LAST LIMIT 1",
            new { matchId = match.Id });
        return Results.Ok(new { matchId = match.Id, status = status ?? "not_started", bestOf = match.BestOf, steps, mapPool });
    }

    internal static async Task<BroadcastVetoStep[]> ReadVetoSteps(IDbConnection conn, Guid matchId, IDbTransaction? tx = null)
    {
        var actions = await conn.QueryAsync<BroadcastVetoAction>(
            "SELECT a.action_number AS ActionNumber,a.action_type AS Action,gm.map_name AS Map,a.team_side AS TeamSide,a.side AS Side FROM match_map_veto_actions a LEFT JOIN game_maps gm ON gm.id=a.map_id WHERE a.match_id=@matchId ORDER BY a.action_number LIMIT 100",
            new { matchId }, tx);
        return BroadcastVetoProjection.Project(actions);
    }

    internal static async Task<string[]> ReadMapPool(IDbConnection conn, Guid tournamentId, IDbTransaction? tx = null) =>
        (await conn.QueryAsync<string>(
            "SELECT gm.map_name FROM tournament_map_pools p JOIN game_maps gm ON gm.id=p.map_id WHERE p.tournament_id=@tournamentId ORDER BY gm.map_name LIMIT 100",
            new { tournamentId }, tx)).ToArray();
}
