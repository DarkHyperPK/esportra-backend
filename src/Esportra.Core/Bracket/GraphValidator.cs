namespace Esportra.Core.Bracket;

public static class GraphValidator
{
    public static List<string> Validate(BracketGraph graph)
    {
        var errors = new List<string>();
        var (_, nodes, edges) = graph;

        // 1. Cycle detection (DFS)
        if (HasCycle(nodes, edges))
            errors.Add("Graph contains a cycle.");

        // 2. Slot collision check
        var incoming = new Dictionary<string, int>();
        foreach (var e in edges)
        {
            var key = $"{e.TargetMatchId}-{e.TargetSlot}";
            incoming[key] = incoming.GetValueOrDefault(key) + 1;
        }
        foreach (var (key, count) in incoming)
        {
            if (count > 1)
                errors.Add($"Slot collision: {key} has {count} incoming edges.");
        }

        // 3. Single champion check (skip for group-stage formats with no edges)
        if (edges.Count > 0)
        {
            var nodesWithOutgoingWinner = edges
                .Where(e => e.Type == "winner")
                .Select(e => e.SourceMatchId)
                .ToHashSet();

            var potentialChampions = nodes.Where(n => !nodesWithOutgoingWinner.Contains(n.Id)).ToList();

            if (potentialChampions.Count == 0)
                errors.Add("No champion node found (infinite loop?).");
        }

        return errors;
    }

    private static bool HasCycle(List<BracketNode> nodes, List<BracketEdge> edges)
    {
        var adj = new Dictionary<string, List<string>>();
        foreach (var e in edges)
        {
            if (!adj.ContainsKey(e.SourceMatchId)) adj[e.SourceMatchId] = [];
            adj[e.SourceMatchId].Add(e.TargetMatchId);
        }

        var visited  = new HashSet<string>();
        var recStack = new HashSet<string>();

        bool Dfs(string nodeId)
        {
            if (recStack.Contains(nodeId)) return true;
            if (visited.Contains(nodeId))  return false;

            visited.Add(nodeId);
            recStack.Add(nodeId);

            if (adj.TryGetValue(nodeId, out var children))
                foreach (var child in children)
                    if (Dfs(child)) return true;

            recStack.Remove(nodeId);
            return false;
        }

        return nodes.Any(n => Dfs(n.Id));
    }
}
