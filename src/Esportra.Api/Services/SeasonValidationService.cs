using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Api.Services;

public sealed class SeasonValidationService(
    IDbConnectionFactory db,
    GameCatalogService gameCatalog)
{
    private static readonly HashSet<string> IntakeNodeTypes = new(StringComparer.OrdinalIgnoreCase) { "qualifier", "event", "custom" };
    private static readonly HashSet<string> InboundOnlyNodeTypes = new(StringComparer.OrdinalIgnoreCase) { "stage", "final" };

    public async Task<SeasonPlanValidationResult> ValidatePlanAsync(Guid seasonId, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        return await ValidatePlanAsync(conn, null, seasonId, ct);
    }

    public async Task<SeasonPlanValidationResult> ValidatePlanAsync(IDbConnection conn, IDbTransaction? tx, Guid seasonId, CancellationToken ct = default)
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        var season = await conn.QuerySingleOrDefaultAsync<SeasonCatalogRow>(
            """
            SELECT id, name, game, game_mode AS gameMode, region, organization_id AS organizationId
            FROM public.seasons
            WHERE id = @seasonId AND deleted_at IS NULL
            """,
            new { seasonId }, tx);

        if (season is null)
            return new SeasonPlanValidationResult(false, ["Season was not found."], []);

        GameModeCatalogResolution? catalog = null;
        try
        {
            catalog = await gameCatalog.ResolveGameModeAsync(season.Game, season.GameMode, null, conn, tx);
        }
        catch (GameCatalogValidationException ex)
        {
            issues.Add(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(season.Region))
            issues.Add("Season region is required.");

        var nodes = (await conn.QueryAsync<SeasonPlanNodeRow>(
            """
            SELECT id, name, node_type AS nodeType, display_order AS displayOrder,
                   linked_tournament_id AS linkedTournamentId, starts_at AS startsAt, ends_at AS endsAt,
                   registration_deadline AS registrationDeadline, metadata::text AS metadata
            FROM public.season_nodes
            WHERE season_id = @seasonId
            ORDER BY display_order ASC, created_at ASC
            """,
            new { seasonId }, tx)).AsList();

        var activeNodes = nodes.Where(n => !string.Equals(n.NodeType, "root", StringComparison.OrdinalIgnoreCase)).ToList();
        if (activeNodes.Count == 0)
            issues.Add("Add at least one qualifier or event and one final before publishing.");
        if (!activeNodes.Any(n => IntakeNodeTypes.Contains(n.NodeType)))
            issues.Add("A season must include at least one qualifier or points event that accepts registration.");
        if (!activeNodes.Any(n => string.Equals(n.NodeType, "final", StringComparison.OrdinalIgnoreCase)))
            issues.Add("A season must include one final destination.");

        var nodesById = activeNodes.ToDictionary(n => n.Id);
        var incomingCapacity = new Dictionary<Guid, int>();
        var adjacency = new Dictionary<Guid, List<Guid>>();

        foreach (var node in activeNodes)
        {
            var metadata = ParseMetadata(node.Metadata);
            var role = node.NodeType?.Trim().ToLowerInvariant() ?? "event";
            var structure = GetString(metadata, "tournamentStructure") ?? GetString(metadata, "format");
            var maxTeams = GetInt(metadata, "maxTeams") ?? 16;
            var registrationType = GetString(metadata, "registrationType") ?? (InboundOnlyNodeTypes.Contains(role) ? "closed" : "open");

            if (catalog is not null)
            {
                try
                {
                    await gameCatalog.ResolveTournamentAsync(
                        season.Game,
                        catalog.GameMode,
                        catalog.TeamSize,
                        structure,
                        null,
                        Array.Empty<string>(),
                        false,
                        null,
                        conn,
                        tx);
                }
                catch (GameCatalogValidationException ex)
                {
                    issues.Add($"{node.Name}: {ex.Message}");
                }
            }

            if (maxTeams < 2)
                issues.Add($"{node.Name}: max teams must be at least 2.");

            if (InboundOnlyNodeTypes.Contains(role) && !string.Equals(registrationType, "closed", StringComparison.OrdinalIgnoreCase))
                issues.Add($"{node.Name}: finals and stages are inbound-only and must use closed registration.");

            var connections = ReadConnections(node.Id, metadata);
            if (string.Equals(role, "final", StringComparison.OrdinalIgnoreCase) && connections.Count > 0)
                issues.Add($"{node.Name}: finals cannot advance into another node.");

            foreach (var connection in connections)
            {
                if (!nodesById.TryGetValue(connection.TargetNodeId, out var target))
                {
                    issues.Add($"{node.Name}: advancement target was not found.");
                    continue;
                }

                if (connection.AdvanceTeams < 1)
                    issues.Add($"{node.Name}: advance teams must be at least 1.");

                incomingCapacity[target.Id] = incomingCapacity.GetValueOrDefault(target.Id) + Math.Max(connection.AdvanceTeams, 0);
                if (!adjacency.TryGetValue(node.Id, out var targets))
                {
                    targets = [];
                    adjacency[node.Id] = targets;
                }
                targets.Add(target.Id);
            }

            if (node.LinkedTournamentId.HasValue)
                await ValidateLinkedTournamentAsync(conn, tx, season, catalog, node, structure, issues);
        }

        foreach (var node in activeNodes)
        {
            var metadata = ParseMetadata(node.Metadata);
            var maxTeams = GetInt(metadata, "maxTeams") ?? 16;
            if (incomingCapacity.TryGetValue(node.Id, out var inbound) && inbound > maxTeams)
                issues.Add($"{node.Name}: inbound advancement count ({inbound}) exceeds max teams ({maxTeams}).");
        }

        if (HasCycle(adjacency))
            issues.Add("Season advancement graph cannot contain cycles.");

        foreach (var inboundOnly in activeNodes.Where(n => InboundOnlyNodeTypes.Contains(n.NodeType)))
        {
            if (!incomingCapacity.ContainsKey(inboundOnly.Id))
                issues.Add($"{inboundOnly.Name}: inbound-only nodes must have at least one advancement source.");
        }

        return new SeasonPlanValidationResult(issues.Count == 0, issues, warnings);
    }

    private async Task ValidateLinkedTournamentAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        SeasonCatalogRow season,
        GameModeCatalogResolution? catalog,
        SeasonPlanNodeRow node,
        string? structure,
        List<string> issues)
    {
        var tournament = await conn.QuerySingleOrDefaultAsync<LinkedTournamentValidationRow>(
            """
            SELECT id, game, game_mode AS gameMode, format, team_size AS teamSize,
                   max_teams AS maxTeams, region, organization_id AS organizationId
            FROM public.tournaments
            WHERE id = @tournamentId AND deleted_at IS NULL
            """,
            new { tournamentId = node.LinkedTournamentId }, tx);

        if (tournament is null)
        {
            issues.Add($"{node.Name}: linked tournament was not found.");
            return;
        }

        if (catalog is not null)
        {
            try
            {
                await gameCatalog.ResolveTournamentAsync(
                    tournament.Game,
                    tournament.GameMode ?? catalog.GameMode,
                    tournament.TeamSize,
                    structure ?? tournament.Format,
                    null,
                    Array.Empty<string>(),
                    false,
                    null,
                    conn,
                    tx);
            }
            catch (GameCatalogValidationException ex)
            {
                issues.Add($"{node.Name}: linked tournament {ex.Message}");
            }
        }

        if (!string.Equals(tournament.Region, season.Region, StringComparison.OrdinalIgnoreCase))
            issues.Add($"{node.Name}: linked tournament region must match season region.");
    }

    public static bool IsIntakeNode(string? nodeType) => !string.IsNullOrWhiteSpace(nodeType) && IntakeNodeTypes.Contains(nodeType);
    public static bool IsInboundOnlyNode(string? nodeType) => !string.IsNullOrWhiteSpace(nodeType) && InboundOnlyNodeTypes.Contains(nodeType);
    public static string NormalizeSeasonTournamentRole(string? nodeType) =>
        string.Equals(nodeType, "final", StringComparison.OrdinalIgnoreCase) ? "finals" :
        string.Equals(nodeType, "qualifier", StringComparison.OrdinalIgnoreCase) ? "qualifier" :
        string.Equals(nodeType, "event", StringComparison.OrdinalIgnoreCase) ? "event" :
        "custom";

    public static JsonElement ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("{}").RootElement.Clone();
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    public static string? GetString(JsonElement metadata, string name) =>
        metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static int? GetInt(JsonElement metadata, string name) =>
        metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;

    public static IReadOnlyList<SeasonAdvancementConnection> ReadConnections(Guid sourceNodeId, JsonElement metadata)
    {
        if (!metadata.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<SeasonAdvancementConnection>();
        foreach (var item in connections.EnumerateArray())
        {
            var target = GetGuid(item, "targetNodeId") ?? GetGuid(item, "destinationNodeId");
            if (!target.HasValue) continue;
            var advanceTeams = GetInt(item, "advanceTeams")
                ?? GetInt(item, "advancementCount")
                ?? GetInt(item, "placementEnd")
                ?? 1;
            rows.Add(new SeasonAdvancementConnection(sourceNodeId, target.Value, Math.Max(1, advanceTeams)));
        }
        return rows;
    }

    private static Guid? GetGuid(JsonElement metadata, string name) =>
        metadata.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var result)
            ? result
            : null;

    private static bool HasCycle(Dictionary<Guid, List<Guid>> adjacency)
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();

        bool Visit(Guid node)
        {
            if (visited.Contains(node)) return false;
            if (!visiting.Add(node)) return true;
            foreach (var target in adjacency.GetValueOrDefault(node) ?? [])
            {
                if (Visit(target)) return true;
            }
            visiting.Remove(node);
            visited.Add(node);
            return false;
        }

        return adjacency.Keys.Any(Visit);
    }

    private sealed record SeasonCatalogRow(Guid Id, string Name, string Game, string? GameMode, string? Region, Guid? OrganizationId);
    private sealed record SeasonPlanNodeRow(Guid Id, string Name, string NodeType, int DisplayOrder, Guid? LinkedTournamentId, DateTime? StartsAt, DateTime? EndsAt, DateTime? RegistrationDeadline, string? Metadata);
    private sealed record LinkedTournamentValidationRow(Guid Id, string Game, string? GameMode, string? Format, int? TeamSize, int? MaxTeams, string? Region, Guid? OrganizationId);
}

public sealed record SeasonPlanValidationResult(bool IsValid, IReadOnlyList<string> Issues, IReadOnlyList<string> Warnings);
public sealed record SeasonAdvancementConnection(Guid SourceNodeId, Guid TargetNodeId, int AdvanceTeams);
