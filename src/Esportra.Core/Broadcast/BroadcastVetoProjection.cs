namespace Esportra.Core.Broadcast;

public sealed record BroadcastVetoAction(int ActionNumber, string Action, string? Map, string? TeamSide, string? Side);
public sealed record BroadcastVetoStep(int Team, string Action, string Map, string? Side, int? SideTeam = null)
{
    public int PickedByTeam => Team;
}

/// <summary>
/// The veto as broadcast graphics show it: bans, picks and the decider, each with the starting side once chosen.
/// The veto service records a side choice as <c>pick_side</c>: on a picked map it is that map's side; on any other
/// map it is the decider (the map left over). Older history used <c>side</c> and <c>decider</c>; both still read.
/// </summary>
public static class BroadcastVetoProjection
{
    public static BroadcastVetoStep[] Project(IEnumerable<BroadcastVetoAction> history)
    {
        var steps = new List<BroadcastVetoStep>();
        foreach (var action in history.OrderBy(action => action.ActionNumber))
        {
            if (string.IsNullOrWhiteSpace(action.Map)) continue;
            switch (action.Action)
            {
                case "ban" or "pick":
                    steps.Add(new(TeamIndex(action.TeamSide), action.Action, action.Map, NormalizeSide(action.Side)));
                    break;
                case "decider":
                    AddOrUpdateDecider(steps, action);
                    break;
                case "side" or "pick_side":
                    ApplySide(steps, action);
                    break;
            }
        }
        return [.. steps];
    }

    private static void ApplySide(List<BroadcastVetoStep> steps, BroadcastVetoAction action)
    {
        var index = steps.FindIndex(step => step.Action != "ban" && SameMap(step.Map, action.Map!));
        if (index < 0 && action.Action == "pick_side") { AddOrUpdateDecider(steps, action); return; }
        if (index < 0) return;
        steps[index] = steps[index] with { Side = NormalizeSide(action.Side) ?? steps[index].Side, SideTeam = SideTeam(action) ?? steps[index].SideTeam };
    }

    private static void AddOrUpdateDecider(List<BroadcastVetoStep> steps, BroadcastVetoAction action)
    {
        var index = steps.FindIndex(step => step.Action == "decider");
        var decider = new BroadcastVetoStep(-1, "decider", action.Map!, NormalizeSide(action.Side), SideTeam(action));
        if (index < 0) steps.Add(decider);
        else steps[index] = decider;
    }

    private static int? SideTeam(BroadcastVetoAction action) => TeamIndex(action.TeamSide) is var team and >= 0 ? team : null;

    private static bool SameMap(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static int TeamIndex(string? side) => side?.ToLowerInvariant() switch
    {
        "team1" or "t1" => 0,
        "team2" or "t2" => 1,
        _ => -1
    };

    private static string? NormalizeSide(string? side) => side?.ToLowerInvariant() switch
    {
        "attack" or "atk" or "attacking" => "ATK",
        "defense" or "defend" or "def" or "defending" or "defence" => "DEF",
        _ => null
    };
}
