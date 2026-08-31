namespace Esportra.Core.Match;

/// <summary>
/// Pure FSM: state derivation and transition validation.
/// No DB calls — safe for unit testing.
/// </summary>
public static class VetoEngine
{
    // ── State derivation ─────────────────────────────────────────────────────

    public static VetoState DeriveState(MatchMapVeto? veto)
    {
        if (veto is null) return VetoState.Init;
        if (veto.Status == "completed") return VetoState.Complete;
        if (veto.Status == "pending" && veto.CurrentTeamId is null) return VetoState.Init;

        return veto.CurrentAction switch
        {
            "ban" => VetoState.Ban,
            "pick" => VetoState.Pick,
            "pick_side" => VetoState.PickSide,
            _ => VetoState.Init,
        };
    }

    // ── Transition validation ─────────────────────────────────────────────────

    public static VetoTransitionResult ValidateTransition(
        VetoState state,
        VetoEvent ev,
        TurnContext context,
        string? mapId = null,
        MatchMapVeto? veto = null)
    {
        // RESET is organizer-only, always valid regardless of state
        if (ev == VetoEvent.Reset)
            return context.IsOrganizer
                ? new(true)
                : new(false, "FORBIDDEN");

        if (state == VetoState.Complete)
            return new(false, "INVALID_STATE");

        return ev switch
        {
            VetoEvent.SetBo =>
                state != VetoState.Init ? new(false, "INVALID_STATE") :
                !context.IsOrganizer ? new(false, "FORBIDDEN") :
                new(true),

            VetoEvent.BanMap or VetoEvent.PickMap or VetoEvent.PickSide =>
                ValidateMapAction(state, ev, context, mapId, veto),

            _ => new(false, "INVALID_STATE"),
        };
    }

    private static VetoTransitionResult ValidateMapAction(
        VetoState state, VetoEvent ev, TurnContext context,
        string? mapId, MatchMapVeto? veto)
    {
        var actorResult = ValidateActorPermission(context);
        if (!actorResult.Ok) return actorResult;

        var stateResult = ValidateEventMatchesState(ev, state);
        if (!stateResult.Ok) return stateResult;

        return ValidateMapAvailability(mapId, veto, ev);
    }

    private static VetoTransitionResult ValidateActorPermission(TurnContext context)
    {
        if (!context.IsCaptain && !context.IsOrganizer) return new(false, "FORBIDDEN");
        if (!context.IsOrganizer && context.UserTeamId != context.CurrentTeamId?.ToString()) return new(false, "NOT_YOUR_TURN");
        return new(true);
    }

    private static VetoTransitionResult ValidateEventMatchesState(VetoEvent ev, VetoState state)
    {
        if (ev == VetoEvent.BanMap && state != VetoState.Ban) return new(false, "INVALID_STATE");
        if (ev == VetoEvent.PickMap && state != VetoState.Pick) return new(false, "INVALID_STATE");
        if (ev == VetoEvent.PickSide && state != VetoState.PickSide) return new(false, "INVALID_STATE");
        return new(true);
    }

    private static VetoTransitionResult ValidateMapAvailability(string? mapId, MatchMapVeto? veto, VetoEvent ev)
    {
        if (mapId is null || veto is null) return new(true);

        bool isBanned = veto.Team1BannedMaps.Contains(mapId) || veto.Team2BannedMaps.Contains(mapId);
        bool isPicked = veto.Team1PickedMaps.Any(p => p.MapId == mapId)
                     || veto.Team2PickedMaps.Any(p => p.MapId == mapId);

        if (isBanned) return new(false, "MAP_ALREADY_USED");
        if (ev != VetoEvent.PickSide && isPicked) return new(false, "MAP_ALREADY_USED");
        return new(true);
    }

    // ── Next action resolution ────────────────────────────────────────────────

    /// <summary>
    /// Given the current action number, bestOf, game, and pool size, returns the next action.
    /// Returns null when the veto sequence is complete.
    /// </summary>
    public static (string? Action, string? TeamSide)? NextAction(
        int bestOf, int currentActionNumber, string game, int poolSize)
    {
        var next = VetoSequences.GetStep(bestOf, currentActionNumber + 1, game, poolSize);
        return next is null ? null : (next.Action, next.Team);
    }

    /// <summary>Backward-compatible default: Valorant pool of 7.</summary>
    public static (string? Action, string? TeamSide)? NextAction(int bestOf, int currentActionNumber)
        => NextAction(bestOf, currentActionNumber, "valorant", 7);

    /// <summary>Returns the team ID that should act next.</summary>
    public static string? ResolveCurrentTeamId(string teamSide, string? team1Id, string? team2Id)
        => teamSide == "T1" ? team1Id : team2Id;
}
