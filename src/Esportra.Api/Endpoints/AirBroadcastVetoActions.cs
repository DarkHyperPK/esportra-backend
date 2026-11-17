using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Core.Match;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>A veto step entered in Esportra Air, for the match room.</summary>
/// <param name="ClientKey">Air's key for this step: sending it again returns the first answer.</param>
/// <param name="ExpectedStep">The match room's action number Air last saw; a different number means the website moved on.</param>
/// <param name="Action">start, toss_choice, ban, pick, pick_side or reset.</param>
/// <param name="Map">The map's name, as Air shows it.</param>
/// <param name="Side">ATK or DEF, for pick_side.</param>
/// <param name="GoFirst">For toss_choice: the toss winner goes first (true) or defers (false).</param>
public sealed record AirVetoActionRequest(string? ClientKey, int? ExpectedStep, string? Action, string? Map, string? Side, bool? GoFirst);

public static partial class AirBroadcastEndpoints
{
    private static readonly string[] VetoActions = ["start", "toss_choice", "ban", "pick", "pick_side", "reset"];
    private static readonly JsonSerializerOptions VetoJson = new(JsonSerializerDefaults.Web);

    // Air enters the veto for a match it produces: at a LAN the teams cannot open the website, so staff
    // collect the veto and the producer types each step into Air. Air acts for the team whose turn it
    // is, through the same state machine as the website, and the match room sees each step live.
    private static async Task<IResult> VetoAction(Guid matchId, AirVetoActionRequest req, HttpContext ctx, IDbConnectionFactory db,
        AirAccountService accounts, VetoSettingsService settings, VetoDbService veto, IHubContext<VetoHub> hub, EventLicenseService licenses, CancellationToken ct)
    {
        var invalid = VetoRequestError(req);
        if (invalid is not null) return Results.BadRequest(new { error = invalid });
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Json(new { error = "An active Air account and registered production PC are required." }, statusCode: 403);
        var matches = await Matches(conn, AirAccountEndpoints.User(ctx), matchId);
        if (matches.Length == 0) return Results.NotFound();
        var match = matches[0];
        var done = await conn.QuerySingleOrDefaultAsync<string>("SELECT result FROM air_veto_actions WHERE match_id=@matchId AND client_key=@ClientKey", new { matchId, req.ClientKey });
        if (done is not null) return Results.Text(done, "application/json");
        var current = await veto.GetAsync(matchId, ct);
        if (IsStale(current, req)) return await VetoConflict(conn, match, settings, ct);
        var failed = await RunVetoAction(conn, match, current, req, veto, licenses, ct);
        if (failed is not null) return failed.StatusCode == 409 ? await VetoConflict(conn, match, settings, ct) : Results.Json(new { error = failed.Error }, statusCode: failed.StatusCode);
        await AnnounceVeto(hub, veto, matchId, req.Action!, ct);
        var json = JsonSerializer.Serialize(await VetoView(conn, match, settings, ct), VetoJson);
        await conn.ExecuteAsync("INSERT INTO air_veto_actions(match_id,client_key,user_id,action,result) VALUES(@matchId,@ClientKey,@userId,@Action,@json) ON CONFLICT (match_id,client_key) DO NOTHING",
            new { matchId, req.ClientKey, userId = AirAccountEndpoints.User(ctx), req.Action, json });
        return Results.Text(json, "application/json");
    }

    private sealed record VetoFailure(int StatusCode, string Error);

    private static string? VetoRequestError(AirVetoActionRequest req)
    {
        if (req.ClientKey is null || req.ClientKey.Length is < 8 or > 80) return "Send a client key (8 to 80 characters).";
        if (req.Action is null || !VetoActions.Contains(req.Action)) return "Choose a veto action.";
        if (req.Action is "ban" or "pick" or "pick_side" && string.IsNullOrWhiteSpace(req.Map)) return "Name the map.";
        if (req.Action == "pick_side" && req.Side is not ("ATK" or "DEF")) return "Choose ATK or DEF.";
        return req.Action == "toss_choice" && req.GoFirst is null ? "Say whether the toss winner goes first." : null;
    }

    /// <summary>A step entered on the website since Air last looked: Air must not act on an old picture.</summary>
    private static bool IsStale(MatchMapVeto? current, AirVetoActionRequest req) =>
        req.ExpectedStep is { } expected && req.Action is not ("start" or "reset") && (current?.CurrentActionNumber ?? 0) != expected;

    private static async Task<object> VetoView(IDbConnection conn, AirMatch match, VetoSettingsService settings, CancellationToken ct)
    {
        var state = await ReadVetoState(conn, match.Id, match.BestOf, settings, ct);
        return new { matchId = match.Id, state.Status, bestOf = match.BestOf, state.Steps, state.Sequence, state.Turn, state.Toss, state.Step };
    }

    private static async Task<IResult> VetoConflict(IDbConnection conn, AirMatch match, VetoSettingsService settings, CancellationToken ct) =>
        Results.Json(new { error = "The match room's veto changed since Air last read it.", veto = await VetoView(conn, match, settings, ct) }, statusCode: 409);

    private static async Task<VetoFailure?> RunVetoAction(IDbConnection conn, AirMatch match, MatchMapVeto? current, AirVetoActionRequest req,
        VetoDbService veto, EventLicenseService licenses, CancellationToken ct)
    {
        try
        {
            return req.Action switch
            {
                "start" => await StartVeto(match, current, veto, licenses, ct),
                "reset" => await ResetVeto(match.Id, current, veto, ct),
                "toss_choice" => await TossChoice(match.Id, current, req.GoFirst!.Value, veto, ct),
                _ => await TeamStep(conn, match.Id, current, req, veto, ct),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new VetoFailure(409, "It is not that team's turn any more.");
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.StartsWith("CONFLICT") ? new VetoFailure(409, "The veto changed.") : new VetoFailure(400, "That veto step is not valid right now.");
        }
        catch (GameCatalogValidationException ex)
        {
            return new VetoFailure(400, ex.Message);
        }
    }

    private static async Task<VetoFailure?> StartVeto(AirMatch match, MatchMapVeto? current, VetoDbService veto, EventLicenseService licenses, CancellationToken ct)
    {
        var restart = current is { Status: "pending", CurrentTeamId: null, CurrentActionNumber: 0 };
        if (current is not null && !restart) return null; // already started: nothing to do
        if (!await licenses.IsOrganizerProAsync(match.TournamentId, ct))
            return new VetoFailure(402, "The map veto in the match room needs Organizer Pro on this tournament.");
        await veto.InitAsync(match.Id, match.TournamentId, match.Team1Id, match.Team2Id, match.BestOf, string.IsNullOrWhiteSpace(match.Game) ? "valorant" : match.Game, ct);
        return null;
    }

    private static async Task<VetoFailure?> ResetVeto(Guid matchId, MatchMapVeto? current, VetoDbService veto, CancellationToken ct)
    {
        if (current is not null) await veto.ResetAsync(matchId, ct);
        return null;
    }

    private static async Task<VetoFailure?> TossChoice(Guid matchId, MatchMapVeto? current, bool goFirst, VetoDbService veto, CancellationToken ct)
    {
        if (current?.TossWinnerTeamId is not { } winner) return new VetoFailure(400, "The match room has no coin toss to answer.");
        await veto.TossChoiceAsync(matchId, winner, goFirst, ct);
        return null;
    }

    /// <summary>A ban, pick or side choice for the team whose turn it is.</summary>
    private static async Task<VetoFailure?> TeamStep(IDbConnection conn, Guid matchId, MatchMapVeto? current, AirVetoActionRequest req, VetoDbService veto, CancellationToken ct)
    {
        if (current?.CurrentTeamId is null) return new VetoFailure(409, "No team is choosing right now.");
        var mapId = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT id::text FROM game_maps WHERE id::text = ANY(@pool) AND lower(map_name)=lower(@name) LIMIT 1",
            new { pool = current.SelectedMapPool ?? [], name = req.Map!.Trim() });
        if (mapId is null) return new VetoFailure(400, $"{req.Map} is not in this match's map pool.");
        var team = current.CurrentTeamId == current.Team1Id ? "team1" : "team2";
        _ = req.Action switch
        {
            "ban" => await veto.BanMapForTeamTokenAsync(matchId, mapId, team, ct),
            "pick" => await veto.PickMapForTeamTokenAsync(matchId, mapId, team, ct),
            _ => await veto.PickSideForTeamTokenAsync(matchId, mapId, req.Side == "ATK" ? "attack" : "defend", team, ct),
        };
        return null;
    }

    /// <summary>Tells the match room what Air did, with the same events the website's own actions send.</summary>
    private static async Task AnnounceVeto(IHubContext<VetoHub> hub, VetoDbService veto, Guid matchId, string action, CancellationToken ct)
    {
        var group = hub.Clients.Group(VetoHub.VetoGroup(matchId.ToString()));
        if (action == "reset") await group.SendAsync(VetoHubEvents.VetoReset, matchId, ct);
        else
        {
            var state = await veto.GetAsync(matchId, ct);
            await group.SendAsync(action is "start" or "toss_choice" ? VetoHubEvents.StateSync : VetoHubEvents.VetoAction, state, ct);
            if (state?.Status == "completed") await group.SendAsync(VetoHubEvents.VetoComplete, state, ct);
        }
        await VetoEndpoints.BroadcastHistoryAsync(hub, veto, matchId, ct);
    }
}
