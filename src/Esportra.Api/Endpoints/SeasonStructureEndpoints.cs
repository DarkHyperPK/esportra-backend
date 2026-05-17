using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

public static class SeasonStructureEndpoints
{
    public static void MapSeasonStructureEndpoints(this WebApplication app)
    {
        app.MapPut("/api/seasons/{id:guid}/nodes", async (
            Guid id,
            [FromBody] JsonElement body,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var req = ReadArray<SeasonNodeDraft>(body, "nodes");
            if (req.Count == 0) return Results.BadRequest(new { error = "At least one node is required." });

            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            var nodeIds = req.ToDictionary(n => n.Id ?? Guid.NewGuid(), n => n);
            if (HasCycle(nodeIds)) return Results.BadRequest(new { error = "Season node graph cannot contain cycles." });

            using var tx = conn.BeginTransaction();
            var keepIds = new List<Guid>();
            foreach (var item in nodeIds)
            {
                var nodeId = item.Key;
                var node = item.Value;
                keepIds.Add(nodeId);
                var nodeType = SeasonEndpointHelpers.NormalizeNodeType(node.NodeType);
                var metadata = node.Metadata is null ? "{}" : JsonSerializer.Serialize(node.Metadata);

                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.season_nodes
                        (id, season_id, parent_node_id, name, slug, node_type, display_order, region, city, country,
                         linked_tournament_id, linked_stage_id, status, registration_deadline, starts_at, ends_at, metadata)
                    VALUES
                        (@nodeId, @seasonId, @parentNodeId, @name, @slug, @nodeType, @displayOrder, @region, @city, @country,
                         @linkedTournamentId, @linkedStageId, @status, @registrationDeadline, @startsAt, @endsAt, @metadata::jsonb)
                    ON CONFLICT (id) DO UPDATE SET
                        parent_node_id = EXCLUDED.parent_node_id,
                        name = EXCLUDED.name,
                        slug = EXCLUDED.slug,
                        node_type = EXCLUDED.node_type,
                        display_order = EXCLUDED.display_order,
                        region = EXCLUDED.region,
                        city = EXCLUDED.city,
                        country = EXCLUDED.country,
                        linked_tournament_id = EXCLUDED.linked_tournament_id,
                        linked_stage_id = EXCLUDED.linked_stage_id,
                        status = EXCLUDED.status,
                        registration_deadline = EXCLUDED.registration_deadline,
                        starts_at = EXCLUDED.starts_at,
                        ends_at = EXCLUDED.ends_at,
                        metadata = EXCLUDED.metadata,
                        updated_at = NOW()
                    """,
                    new
                    {
                        nodeId,
                        seasonId = id,
                        parentNodeId = node.ParentNodeId,
                        name = node.Name.Trim(),
                        slug = node.Slug,
                        nodeType,
                        displayOrder = node.DisplayOrder,
                        node.Region,
                        node.City,
                        node.Country,
                        node.LinkedTournamentId,
                        node.LinkedStageId,
                        status = SeasonEndpointHelpers.NormalizeNodeStatus(node.Status),
                        node.RegistrationDeadline,
                        node.StartsAt,
                        node.EndsAt,
                        metadata
                    }, tx);
            }

            await conn.ExecuteAsync(
                """
                DELETE FROM public.season_nodes
                WHERE season_id = @seasonId
                  AND node_type <> 'root'
                  AND id <> ALL(@keepIds)
                """,
                new { seasonId = id, keepIds = keepIds.ToArray() }, tx);

            tx.Commit();
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.nodes.sync", id, "Season", new { nodeCount = keepIds.Count }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { success = true, nodeCount = keepIds.Count });
        }).RequireAuthorization("Organizer");

        app.MapPut("/api/seasons/{id:guid}/rules", async (
            Guid id,
            [FromBody] JsonElement body,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var req = ReadArray<SeasonRuleDraft>(body, "rules");
            if (req.Count == 0) return Results.BadRequest(new { error = "At least one rule is required." });
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("DELETE FROM public.season_point_rules WHERE season_id = @id", new { id }, tx);
            foreach (var rule in req)
            {
                var placementStart = rule.PlacementFrom ?? rule.PlacementStart ?? 1;
                var placementEnd = rule.PlacementTo ?? rule.PlacementEnd ?? placementStart;
                if (placementEnd < placementStart || placementStart <= 0)
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = "Invalid placement range." });
                }

                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.season_point_rules
                        (season_id, tournament_id, source_node_id, source_stage_id, destination_node_id,
                         placement_start, placement_end, points, qualification_status,
                         destination_tournament_id, auto_create_qualification, region_key)
                    VALUES
                        (@seasonId, @tournamentId, @sourceNodeId, @sourceStageId, @destinationNodeId,
                         @placementStart, @placementEnd, @points, @qualificationStatus,
                         @destinationTournamentId, @autoCreateQualification, @regionKey)
                    """,
                    new
                    {
                        seasonId = id,
                        rule.TournamentId,
                        rule.SourceNodeId,
                        rule.SourceStageId,
                        rule.DestinationNodeId,
                        placementStart,
                        placementEnd,
                        points = rule.PointsAwarded ?? rule.Points ?? 0,
                        qualificationStatus = NormalizeQualificationStatus(rule.QualificationStatus),
                        rule.DestinationTournamentId,
                        autoCreateQualification = rule.AutoCreateQualification ?? false,
                        rule.RegionKey
                    }, tx);
            }
            tx.Commit();
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.rules.sync", id, "Season", new { ruleCount = req.Count }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { success = true, ruleCount = req.Count });
        }).RequireAuthorization("Organizer");

        app.MapPut("/api/seasons/{id:guid}/advancement-rules", async (
            Guid id,
            [FromBody] JsonElement body,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var req = ReadArray<SeasonAdvancementRuleDraft>(body, "rules");
            if (req.Count == 0) return Results.BadRequest(new { error = "At least one advancement rule is required." });
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("DELETE FROM public.season_advancement_rules WHERE season_id = @id", new { id }, tx);
            foreach (var rule in req)
            {
                var placementStart = rule.PlacementStart ?? rule.PlacementFrom ?? 1;
                var placementEnd = rule.PlacementEnd ?? rule.PlacementTo ?? placementStart;
                if (placementStart <= 0 || placementEnd < placementStart || rule.AdvancementCount <= 0)
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = "Invalid advancement rule." });
                }

                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.season_advancement_rules
                        (season_id, source_tournament_id, target_tournament_id, source_node_id, target_node_id,
                         placement_start, placement_end, advancement_count, seed_mode)
                    VALUES
                        (@seasonId, @sourceTournamentId, @targetTournamentId, @sourceNodeId, @targetNodeId,
                         @placementStart, @placementEnd, @advancementCount, @seedMode)
                    """,
                    new
                    {
                        seasonId = id,
                        rule.SourceTournamentId,
                        rule.TargetTournamentId,
                        rule.SourceNodeId,
                        rule.TargetNodeId,
                        placementStart,
                        placementEnd,
                        rule.AdvancementCount,
                        seedMode = NormalizeSeedMode(rule.SeedMode)
                    }, tx);
            }
            tx.Commit();
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.advancement_rules.sync", id, "Season", new { ruleCount = req.Count }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { success = true, ruleCount = req.Count });
        }).RequireAuthorization("Organizer");

        app.MapPut("/api/seasons/{id:guid}/staff", async (
            Guid id,
            [FromBody] JsonElement body,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var req = ReadArray<SeasonStaffDraft>(body, "staff");
            if (req.Count == 0) return Results.BadRequest(new { error = "At least one staff member is required." });
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("DELETE FROM public.season_staff WHERE season_id = @id", new { id }, tx);
            foreach (var staff in req.Where(s => s.UserId != Guid.Empty))
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.season_staff (season_id, user_id, role)
                    VALUES (@seasonId, @userId, @role)
                    ON CONFLICT (season_id, user_id) DO UPDATE SET role = EXCLUDED.role
                    """,
                    new { seasonId = id, staff.UserId, role = NormalizeStaffRole(staff.Role) }, tx);
            }
            tx.Commit();
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.staff.sync", id, "Season", new { staffCount = req.Count }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { success = true, staffCount = req.Count });
        }).RequireAuthorization("Organizer");

        app.MapGet("/api/seasons/{id:guid}/staff", async (Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var staff = await conn.QueryAsync<SeasonStaffMemberRow>(
                """
                SELECT ss.user_id, ss.role, p.username, p.full_name
                FROM public.season_staff ss
                LEFT JOIN public.profiles p ON p.id = ss.user_id
                WHERE ss.season_id = @id
                ORDER BY ss.created_at ASC
                """,
                new { id });
            return Results.Ok(staff);
        }).RequireAuthorization("Authenticated");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static List<T> ReadArray<T>(JsonElement body, string propertyName)
    {
        if (body.ValueKind == JsonValueKind.Array) return body.Deserialize<List<T>>(JsonOptions) ?? [];
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
            return property.Deserialize<List<T>>(JsonOptions) ?? [];
        return [];
    }

    private static bool HasCycle(Dictionary<Guid, SeasonNodeDraft> nodes)
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        bool Visit(Guid id)
        {
            if (visited.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            if (nodes.TryGetValue(id, out var node) && node.ParentNodeId.HasValue && nodes.ContainsKey(node.ParentNodeId.Value))
            {
                if (Visit(node.ParentNodeId.Value)) return true;
            }
            visiting.Remove(id);
            visited.Add(id);
            return false;
        }
        return nodes.Keys.Any(Visit);
    }

    private static string? NormalizeQualificationStatus(string? status)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qualified", "eliminated", "pending" };
        return !string.IsNullOrWhiteSpace(status) && allowed.Contains(status) ? status.ToLowerInvariant() : null;
    }

    private static string? NormalizeSeedMode(string? seedMode)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "random", "manual", "top_seeded" };
        return !string.IsNullOrWhiteSpace(seedMode) && allowed.Contains(seedMode) ? seedMode.ToLowerInvariant() : null;
    }

    private static string NormalizeStaffRole(string? role) => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase) ? "admin" : "co_organizer";
}

public sealed record SeasonNodeDraft(
    Guid? Id,
    Guid? SeasonId,
    Guid? ParentNodeId,
    string Name,
    string? Slug,
    string? NodeType,
    int DisplayOrder,
    string? Region,
    string? City,
    string? Country,
    Guid? LinkedTournamentId,
    Guid? LinkedStageId,
    string? Status,
    DateTime? RegistrationDeadline,
    DateTime? StartsAt,
    DateTime? EndsAt,
    object? Metadata);

public sealed record SeasonRuleDraft(
    Guid? Id,
    Guid? TournamentId,
    Guid? SourceNodeId,
    Guid? SourceStageId,
    Guid? DestinationNodeId,
    int? PlacementFrom,
    int? PlacementTo,
    int? PlacementStart,
    int? PlacementEnd,
    int? PointsAwarded,
    int? Points,
    string? QualificationStatus,
    Guid? DestinationTournamentId,
    bool? AutoCreateQualification,
    string? RegionKey);

public sealed record SeasonAdvancementRuleDraft(
    Guid? Id,
    Guid? SourceTournamentId,
    Guid? TargetTournamentId,
    Guid? SourceNodeId,
    Guid? TargetNodeId,
    int? PlacementStart,
    int? PlacementEnd,
    int? PlacementFrom,
    int? PlacementTo,
    int AdvancementCount,
    string? SeedMode);

public sealed record SeasonStaffDraft(Guid UserId, string? Role);

