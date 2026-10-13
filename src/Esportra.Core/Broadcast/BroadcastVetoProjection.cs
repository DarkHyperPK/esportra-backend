namespace Esportra.Core.Broadcast;

public sealed record BroadcastVetoAction(int ActionNumber, string Action, string? Map, string? TeamSide, string? Side);
public sealed record BroadcastVetoStep(int Team, string Action, string Map, string? Side)
{
    public int PickedByTeam => Team;
}

public static class BroadcastVetoProjection
{
    public static BroadcastVetoStep[] Project(IEnumerable<BroadcastVetoAction> history)
    {
        var actions = history.OrderBy(action => action.ActionNumber).ToArray();
        var sides = actions.Where(action => action.Action == "side" && !string.IsNullOrWhiteSpace(action.Map))
            .GroupBy(action => action.Map!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => NormalizeSide(group.Last().Side), StringComparer.OrdinalIgnoreCase);
        return actions.Where(action => action.Action is "ban" or "pick" or "decider" && !string.IsNullOrWhiteSpace(action.Map))
            .Select(action => new BroadcastVetoStep(
                action.Action == "decider" ? -1 : TeamIndex(action.TeamSide), action.Action, action.Map!,
                sides.TryGetValue(action.Map!, out var side) ? side : NormalizeSide(action.Side))).ToArray();
    }

    private static int TeamIndex(string? side) => side?.ToLowerInvariant() switch
    {
        "team1" or "t1" => 0,
        "team2" or "t2" => 1,
        _ => -1
    };

    private static string? NormalizeSide(string? side) => side?.ToLowerInvariant() switch
    {
        "attack" or "atk" or "attacking" => "ATK",
        "defense" or "def" or "defending" => "DEF",
        _ => null
    };
}
