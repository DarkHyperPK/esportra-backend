using System.Data;
using Dapper;
using Esportra.Api.Services;
using Esportra.Core.Broadcast;
using Esportra.Core.Match;

namespace Esportra.Api.Endpoints;

public static partial class AirBroadcastEndpoints
{
    // The match's map veto as it stands, for Air's veto graphic while the teams ban and pick: the steps (with the
    // decider once known), the order the steps follow, whose turn it is and the coin toss.
    // Read-only and scoped like the match list: same account, production device and match access.
    private static async Task<IResult> GetVeto(Guid matchId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts, VetoSettingsService settings, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Json(new { error = "An active Air account and registered production PC are required." }, statusCode: 403);
        var matches = await Matches(conn, AirAccountEndpoints.User(ctx), matchId);
        if (matches.Length == 0) return Results.NotFound();
        var match = matches[0];
        var state = await ReadVetoState(conn, match.Id, match.BestOf, settings, ct);
        var mapPool = await ReadMapPool(conn, match.TournamentId);
        return Results.Ok(new { matchId = match.Id, state.Status, bestOf = match.BestOf, state.Steps, mapPool, state.Sequence, state.Turn, state.Toss, state.Step, team1Id = match.Team1Id, team2Id = match.Team2Id });
    }

    internal sealed record AirVetoTurn(int Team, string Action, DateTime? StartedAt, int? Seconds);
    internal sealed record AirVetoOrderStep(string Action, int Team);
    /// <summary>The veto as Air reads it. <c>Step</c> is the match room's action number: Air sends it back with each step it enters, so a step made on the website in between is noticed.</summary>
    internal sealed record AirVetoState(string Status, BroadcastVetoStep[] Steps, AirVetoOrderStep[] Sequence, AirVetoTurn? Turn, object? Toss, int Step = 0);
    private sealed record VetoRow(string Status, Guid? Team1Id, Guid? CurrentTeamId, string? CurrentAction, DateTime? TurnStartedAt, int? TurnDurationSeconds,
        Guid? TossWinnerTeamId, DateTime? TossCompletedAt, string? SelectedMap, string? Game, int Step);

    internal static async Task<AirVetoState> ReadVetoState(IDbConnection conn, Guid matchId, int bestOf, VetoSettingsService settings, CancellationToken ct, IDbTransaction? tx = null)
    {
        var row = await conn.QuerySingleOrDefaultAsync<VetoRow>("""
            SELECT v.status::text AS Status, v.team1_id AS Team1Id, v.current_team_id AS CurrentTeamId, v.current_action AS CurrentAction,
                   v.turn_started_at AS TurnStartedAt, v.turn_duration_seconds AS TurnDurationSeconds,
                   v.toss_winner_team_id AS TossWinnerTeamId, v.toss_completed_at AS TossCompletedAt, gm.map_name AS SelectedMap, v.game AS Game, COALESCE(v.current_action_number,0) AS Step
            FROM match_map_vetos v LEFT JOIN game_maps gm ON gm.id::text = v.selected_map_id::text
            WHERE v.match_id=@matchId ORDER BY v.updated_at DESC NULLS LAST LIMIT 1
            """, new { matchId }, tx);
        var steps = await ReadVetoSteps(conn, matchId, tx);
        var sequence = await ReadVetoOrder(matchId, bestOf, row?.Game, settings, ct);
        if (row is null) return new("not_started", steps, sequence, null, null);
        // The decider is stored as the veto's selected map; older vetoes never recorded an action for it.
        if (row.Status == "completed" && row.SelectedMap is { } selected && !steps.Any(step => step.Action != "ban" && string.Equals(step.Map, selected, StringComparison.OrdinalIgnoreCase)))
            steps = [.. steps, new BroadcastVetoStep(-1, "decider", selected, null)];
        int TeamOf(Guid? id) => id is null ? -1 : id == row.Team1Id ? 0 : 1;
        var turn = row.Status == "in_progress" && row.CurrentTeamId is not null && row.CurrentAction is not null
            ? new AirVetoTurn(TeamOf(row.CurrentTeamId), row.CurrentAction, row.TurnStartedAt, row.TurnDurationSeconds)
            : null;
        object? toss = row.Status == "toss_choice_pending" || row.TossWinnerTeamId is not null
            ? new { pending = row.TossCompletedAt is null, winner = row.TossWinnerTeamId is null ? (int?)null : TeamOf(row.TossWinnerTeamId) }
            : null;
        return new(row.Status, steps, sequence, turn, toss, row.Step);
    }

    /// <summary>The order the match's veto follows (a custom order when set), as the steps graphics draw: bans, picks and the decider.</summary>
    private static async Task<AirVetoOrderStep[]> ReadVetoOrder(Guid matchId, int bestOf, string? game, VetoSettingsService settings, CancellationToken ct)
    {
        IReadOnlyList<VetoStep> order;
        try
        {
            order = (await settings.GetAsync(matchId, ct))?.EffectiveSequence
                ?? VetoSequences.GetSequence(bestOf, game ?? "valorant", VetoSequences.GetGameConfig(game ?? "valorant").MapPoolSize);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        return order.Where(step => step.Action != "pick_side" || step.IsDecider)
            .Select(step => step.IsDecider ? new AirVetoOrderStep("decider", -1) : new AirVetoOrderStep(step.Action, step.Team == "T2" ? 1 : 0))
            .ToArray();
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
