namespace Esportra.Core.Seasons;

using Dapper;

/// <summary>
/// Validation result for the advancement graph.
/// </summary>
public record AdvancementGraphValidationResult(
    bool IsValid,
    List<string> Errors = [],
    List<string> Warnings = []
);

/// <summary>
/// Service for validating season advancement graphs.
/// Detects cycles, validates terminal nodes, and checks capacity constraints.
/// </summary>
public class AdvancementGraphValidator
{
    private readonly IDbConnectionFactory db;

    public AdvancementGraphValidator(IDbConnectionFactory db)
    {
        this.db = db;
    }

    /// <summary>
    /// Validates the entire advancement graph for a season.
    /// </summary>
    public async Task<AdvancementGraphValidationResult> ValidateGraphAsync(
        Guid seasonId,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        using var conn = db.CreateConnection();

        // Get all nodes in the season
        var nodes = (await conn.QueryAsync<dynamic>(@"
            SELECT id, node_type FROM season_nodes WHERE season_id = @seasonId
            """, new { seasonId }, cancellationToken: ct)).ToList();

        var nodeIds = nodes.Select(n => (Guid)n.id).ToHashSet();

        if (nodeIds.Count == 0)
        {
            errors.Add("Season has no nodes defined");
            return new AdvancementGraphValidationResult(false, errors, warnings);
        }

        // Get all advancement connections
        var connections = (await conn.QueryAsync<dynamic>(@"
            SELECT id, from_node_id, to_node_id, rule_type, rule_value, seed_mode, label
            FROM season_advancement_connections
            WHERE season_id = @seasonId
            """, new { seasonId }, cancellationToken: ct)).ToList();

        // Build adjacency map for cycle detection
        var adjacency = new Dictionary<Guid, (Guid ToNodeId, string RuleType, decimal RuleValue, string SeedMode)>();
        foreach (var conn in connections)
        {
            var from = (Guid)conn.from_node_id;
            var to = (Guid)conn.to_node_id;
            if (nodeIds.Contains(from) && nodeIds.Contains(to))
            {
                adjacency[from] = (to, conn.rule_type, conn.rule_value, conn.seed_mode);
            }
        }

        // Detect cycles
        var cycleErrors = DetectCycles(nodeIds, adjacency);
        errors.AddRange(cycleErrors);

        // Validate terminal nodes
        var terminalErrors = await ValidateTerminalNodesAsync(conn, seasonId, nodeIds, adjacency, ct);
        errors.AddRange(terminalErrors);

        // Validate capacity constraints
        var capacityErrors = await ValidateCapacityAsync(conn, adjacency, ct);
        errors.AddRange(capacityErrors);

        // Validate all nodes are connected (unless explicitly standalone)
        var connectivityWarnings = ValidateConnectivity(nodeIds, adjacency, nodes);
        warnings.AddRange(connectivityWarnings);

        // Validate rule values are positive
        var ruleValueErrors = ValidateRuleValues(connections);
        errors.AddRange(ruleValueErrors);

        return new AdvancementGraphValidationResult(
            IsValid: errors.Count == 0,
            Errors: errors,
            Warnings: warnings
        );
    }

    /// <summary>
    /// Detects cycles in the advancement graph using DFS.
    /// </summary>
    private List<string> DetectCycles(
        HashSet<Guid> nodeIds,
        Dictionary<Guid, (Guid ToNodeId, string RuleType, decimal RuleValue, string SeedMode)> adjacency)
    {
        var errors = new List<string>();
        var colors = new Dictionary<Guid, int>(); // 0=unvisited, 1=visiting, 2=visited
        var cyclePath = new List<Guid>();

        bool HasCycle(Guid nodeId)
        {
            if (!colors.TryGetValue(nodeId, out var color))
                color = 0;

            if (color == 1) return true; // back edge → cycle
            if (color == 2) return false; // already fully processed

            colors[nodeId] = 1;
            cyclePath.Add(nodeId);

            if (adjacency.TryGetValue(nodeId, out var adj) && nodeIds.Contains(adj.ToNodeId))
            {
                if (HasCycle(adj.ToNodeId))
                {
                    var path = string.Join(" → ", cyclePath.Concat(new[] { adj.ToNodeId }).Select(id => id.ToString()[..8]));
                    errors.Add($"Cycle detected in advancement graph: {path}");
                    return true;
                }
            }

            colors[nodeId] = 2;
            cyclePath.RemoveAt(cyclePath.Count - 1);
            return false;
        }

        foreach (var nodeId in nodeIds)
        {
            cyclePath.Clear();
            if (HasCycle(nodeId))
                break;
        }

        return errors;
    }

    /// <summary>
    /// Validates that terminal nodes (grand_final, playoff) don't have outgoing connections
    /// and that at least one terminal championship tournament exists.
    /// </summary>
    private async Task<List<string>> ValidateTerminalNodesAsync(
        System.Data.IDbConnection conn,
        Guid seasonId,
        HashSet<Guid> nodeIds,
        Dictionary<Guid, (Guid ToNodeId, string RuleType, decimal RuleValue, string SeedMode)> adjacency,
        CancellationToken ct)
    {
        var errors = new List<string>();

        // Get terminal node types
        var terminalNodes = await conn.QueryAsync<dynamic>(@"
            SELECT id, node_type FROM season_nodes
            WHERE season_id = @seasonId AND node_type IN ('final', 'playoff')
            """, new { seasonId }, cancellationToken: ct);

        foreach (var node in terminalNodes)
        {
            var nodeId = (Guid)node.id;
            if (adjacency.ContainsKey(nodeId))
            {
                errors.Add($"Terminal tournament '{node.node_type}' (node {nodeId.ToString()[..8]}) has outgoing connections. Terminal tournaments should not advance to other tournaments.");
            }
        }

        // Ensure at least one terminal tournament exists
        var hasTerminal = await conn.ExecuteScalarAsync<bool>(@"
            SELECT 1 FROM season_nodes
            WHERE season_id = @seasonId AND node_type IN ('final', 'playoff')
            LIMIT 1
            """, new { seasonId }, cancellationToken: ct);

        if (!hasTerminal)
        {
            errors.Add("Season must have at least one terminal tournament (final or playoff).");
        }

        return errors;
    }

    /// <summary>
    /// Validates that advancement count doesn't exceed target tournament capacity.
    /// </summary>
    private async Task<List<string>> ValidateCapacityAsync(
        System.Data.IDbConnection conn,
        Dictionary<Guid, (Guid ToNodeId, string RuleType, decimal RuleValue, string SeedMode)> adjacency,
        CancellationToken ct)
    {
        var errors = new List<string>();

        foreach (var (fromNodeId, (toNodeId, ruleType, ruleValue, seedMode)) in adjacency)
        {
            // Get target tournament max teams
            var maxTeams = await conn.ExecuteScalarAsync<int?>(@"
                SELECT max_teams FROM season_nodes WHERE id = @nodeId
                """, new { nodeId = toNodeId }, cancellationToken: ct);

            if (maxTeams.HasValue && ruleValue > maxTeams.Value)
            {
                errors.Add($"Advancement from {fromNodeId.ToString()[..8]} to {toNodeId.ToString()[..8]}: rule value {ruleValue} exceeds target tournament capacity ({maxTeams.Value}).");
            }
        }

        return errors;
    }

    /// <summary>
    /// Validates that all non-root nodes are connected (not orphaned).
    /// </summary>
    private List<string> ValidateConnectivity(
        HashSet<Guid> nodeIds,
        Dictionary<Guid, (Guid ToNodeId, string RuleType, decimal RuleValue, string SeedMode)> adjacency,
        List<dynamic> nodes)
    {
        var warnings = new List<string>();

        // Find nodes with no incoming connections
        var targetNodeIds = adjacency.Values.Select(a => a.ToNodeId).ToHashSet();
        var rootNodes = nodes.Where(n => n.node_type == "root").Select(n => (Guid)n.id).ToHashSet();

        var orphanNodes = nodeIds.Except(targetNodeIds).Except(rootNodes).ToList();

        if (orphanNodes.Count > 0)
        {
            warnings.Add($"{orphanNodes.Count} node(s) have no incoming connections: {string.Join(", ", orphanNodes.Select(id => id.ToString()[..8]))}. These may be unreachable in the advancement flow.");
        }

        return warnings;
    }

    /// <summary>
    /// Validates that rule values are positive.
    /// </summary>
    private List<string> ValidateRuleValues(List<dynamic> connections)
    {
        var errors = new List<string>();

        foreach (var conn in connections)
        {
            var ruleValue = (decimal)conn.rule_value;
            if (ruleValue <= 0)
            {
                errors.Add($"Advancement connection {conn.id}: rule value must be positive (got {ruleValue}).");
            }
        }

        return errors;
    }
}
