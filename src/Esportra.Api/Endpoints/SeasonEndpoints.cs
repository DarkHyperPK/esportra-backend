using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Core.Bracket;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class SeasonEndpoints
{
    private static readonly HashSet<string> AllowedSeasonStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "draft", "published", "active", "completed", "archived" };

    private static readonly HashSet<string> AllowedParticipantModes = new(StringComparer.OrdinalIgnoreCase)
        { "team", "solo" };

    private static readonly HashSet<string> AllowedStaffRoles = new(StringComparer.OrdinalIgnoreCase)
        { "co_organizer", "admin" };

    private static readonly HashSet<string> AllowedNodeTypes = new(StringComparer.OrdinalIgnoreCase)
        { "root", "qualifier", "event", "stage", "final", "custom" };

    private static readonly HashSet<string> AllowedNodeStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "draft", "scheduled", "live", "completed", "archived" };

    private static readonly HashSet<string> AllowedQualificationStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "qualified", "wildcard", "reserve" };

    private static readonly HashSet<string> AllowedQualificationRecordStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "earned", "confirmed", "invited", "accepted", "declined", "revoked", "overridden" };

    private static string MapSeasonStatusToNodeStatus(string seasonStatus) => seasonStatus switch
    {
        "draft" => "draft",
        "published" => "scheduled",
        "active" => "live",
        "completed" => "completed",
        "archived" => "archived",
        _ => "draft"
    };

    public static void MapSeasonEndpoints(this WebApplication app)
    {
        app.MapGet("/api/seasons", async (
            bool                   mine,
            IDbConnectionFactory   db,
            HttpContext            ctx,
            CancellationToken      ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (mine && userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (mine)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT s.id,
                           s.name,
                           s.slug,
                           s.description,
                           s.game,
                           s.participant_mode,
                           s.status,
                           s.is_public,
                           s.owner_user_id,
                           s.organization_id,
                           s.start_date,
                           s.end_date,
                           s.created_at,
                           s.updated_at,
                           p.username AS owner_username,
                           p.full_name AS owner_full_name,
                           EXISTS (
                               SELECT 1 FROM season_staff ss
                               WHERE ss.season_id = s.id AND ss.user_id = @userId
                           ) AS is_season_staff,
                           (SELECT COUNT(*) FROM season_nodes sn WHERE sn.season_id = s.id) AS node_count
                    FROM seasons s
                    JOIN profiles p ON p.id = s.owner_user_id
                    WHERE s.owner_user_id = @userId
                       OR EXISTS (
                           SELECT 1 FROM season_staff ss
                           WHERE ss.season_id = s.id AND ss.user_id = @userId
                       )
                    ORDER BY s.created_at DESC
                    """,
                    new { userId = userCtx!.UserIdGuid });

                return Results.Ok(rows);
            }

            var publicRows = await conn.QueryAsync<dynamic>(
                """
                SELECT s.id,
                       s.name,
                       s.slug,
                       s.description,
                       s.game,
                       s.participant_mode,
                       s.status,
                       s.start_date,
                       s.end_date,
                       s.created_at,
                       s.updated_at,
                       p.username AS owner_username,
                       p.full_name AS owner_full_name,
                       (SELECT COUNT(*) FROM season_nodes sn WHERE sn.season_id = s.id) AS node_count
                FROM seasons s
                JOIN profiles p ON p.id = s.owner_user_id
                WHERE s.is_public = TRUE
                  AND s.status IN ('published', 'active', 'completed')
                ORDER BY s.created_at DESC
                """);

            return Results.Ok(publicRows);
        });

        app.MapPost("/api/seasons", async (
            [FromBody] CreateSeasonRequest req,
            HttpContext                   ctx,
            IDbConnectionFactory         db,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Season name is required." });

            if (string.IsNullOrWhiteSpace(req.Game))
                return Results.BadRequest(new { error = "Game is required." });

            if (!AllowedParticipantModes.Contains(req.ParticipantMode))
                return Results.BadRequest(new { error = "Participant mode must be 'team' or 'solo'." });

            if (!AllowedSeasonStatuses.Contains(req.Status))
                return Results.BadRequest(new { error = "Invalid season status." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var slug = await EnsureUniqueSeasonSlugAsync(
                conn,
                tx,
                req.Slug ?? Slugify(req.Name),
                excludeSeasonId: null);

            var seasonId = Guid.NewGuid();
            var rootNodeId = Guid.NewGuid();
            var normalizedSeasonStatus = req.Status.Trim().ToLowerInvariant();
            var rootNodeStatus = MapSeasonStatusToNodeStatus(normalizedSeasonStatus);

            await conn.ExecuteAsync(
                """
                INSERT INTO seasons (
                    id, name, slug, description, game, participant_mode, status,
                    owner_user_id, organization_id, is_public, allow_manual_overrides,
                    start_date, end_date, settings
                )
                VALUES (
                    @id, @name, @slug, @description, @game, @participantMode, @status,
                    @ownerUserId, @organizationId, @isPublic, @allowManualOverrides,
                    @startDate, @endDate, COALESCE(@settings::jsonb, '{}'::jsonb)
                )
                """,
                new
                {
                    id = seasonId,
                    name = req.Name.Trim(),
                    slug,
                    description = req.Description,
                    game = req.Game.Trim(),
                    participantMode = req.ParticipantMode.Trim().ToLowerInvariant(),
                    status = normalizedSeasonStatus,
                    ownerUserId = userCtx.UserIdGuid,
                    organizationId = req.OrganizationId,
                    isPublic = req.IsPublic,
                    allowManualOverrides = req.AllowManualOverrides,
                    startDate = req.StartDate,
                    endDate = req.EndDate,
                    settings = req.Settings?.GetRawText()
                },
                tx);

            await conn.ExecuteAsync(
                """
                INSERT INTO season_nodes (
                    id, season_id, parent_node_id, name, slug, node_type, display_order,
                    status, registration_deadline, starts_at, ends_at, metadata
                )
                VALUES (
                    @id, @seasonId, NULL, @name, @slug, 'root', 0,
                    @status, @registrationDeadline, @startsAt, @endsAt, '{}'::jsonb
                )
                """,
                new
                {
                    id = rootNodeId,
                    seasonId,
                    name = req.RootNodeName?.Trim() is { Length: > 0 } rootName ? rootName : req.Name.Trim(),
                    slug,
                    status = rootNodeStatus,
                    registrationDeadline = req.StartDate,
                    startsAt = req.StartDate,
                    endsAt = req.EndDate
                },
                tx);

            tx.Commit();

            return Results.Ok(new { id = seasonId, slug, root_node_id = rootNodeId });
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/seasons/{id}", async (
            Guid                  id,
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;

            using var conn = db.CreateConnection();
            var access = await GetSeasonAccessAsync(conn, id, userCtx?.UserIdGuid);
            if (access is null) return Results.NotFound();
            if (!access.IsPublic && !access.CanManage) return Results.Forbid();

            var season = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT s.id,
                       s.name,
                       s.slug,
                       s.description,
                       s.game,
                       s.participant_mode,
                       s.status,
                       s.owner_user_id,
                       s.organization_id,
                       s.is_public,
                       s.allow_manual_overrides,
                       s.start_date,
                       s.end_date,
                       s.settings,
                       s.created_at,
                       s.updated_at,
                       p.username AS owner_username,
                       p.full_name AS owner_full_name
                FROM seasons s
                JOIN profiles p ON p.id = s.owner_user_id
                WHERE s.id = @id
                """,
                new { id });

            var nodes = await conn.QueryAsync<dynamic>(
                """
                SELECT sn.id,
                       sn.season_id,
                       sn.parent_node_id,
                       sn.name,
                       sn.slug,
                       sn.node_type,
                       sn.display_order,
                       sn.region,
                       sn.city,
                       sn.country,
                       sn.linked_tournament_id,
                       sn.linked_stage_id,
                       sn.status,
                       sn.registration_deadline,
                       sn.starts_at,
                       sn.ends_at,
                       sn.metadata,
                       sn.created_at,
                       sn.updated_at,
                       t.name AS linked_tournament_name,
                       ts.name AS linked_stage_name
                FROM season_nodes sn
                LEFT JOIN tournaments t ON t.id = sn.linked_tournament_id
                LEFT JOIN tournament_stages ts ON ts.id = sn.linked_stage_id
                WHERE sn.season_id = @id
                ORDER BY sn.display_order, sn.created_at
                """,
                new { id });

            var tree = BuildSeasonTree(nodes.Select(node => new SeasonTreeNodeView(
                (Guid)node.id,
                node.parent_node_id is Guid parentNodeId ? (Guid?)parentNodeId : null,
                (Guid)node.season_id,
                (string)node.name,
                (string?)node.slug,
                (string)node.node_type,
                (int)node.display_order,
                (string?)node.region,
                (string?)node.city,
                (string?)node.country,
                node.linked_tournament_id is Guid linkedTournamentId ? (Guid?)linkedTournamentId : null,
                node.linked_stage_id is Guid linkedStageId ? (Guid?)linkedStageId : null,
                (string)node.status,
                ReadNullableDateTimeOffset(node.registration_deadline),
                ReadNullableDateTimeOffset(node.starts_at),
                ReadNullableDateTimeOffset(node.ends_at),
                node.metadata,
                node.created_at,
                node.updated_at,
                (string?)node.linked_tournament_name,
                (string?)node.linked_stage_name)).ToList());

            var rules = await conn.QueryAsync<dynamic>(
                """
                SELECT spr.id,
                       spr.season_id,
                       spr.source_node_id,
                       spr.source_stage_id,
                       spr.destination_node_id,
                       spr.placement_from,
                       spr.placement_to,
                       spr.points_awarded,
                       spr.qualification_status,
                       spr.auto_create_qualification,
                       spr.region_key,
                       spr.created_at,
                       spr.updated_at
                FROM season_points_rules spr
                WHERE spr.season_id = @id
                ORDER BY spr.source_node_id, spr.placement_from, spr.placement_to
                """,
                new { id });

            object? staff = null;
            if (access.CanManage)
            {
                staff = await conn.QueryAsync<dynamic>(
                    """
                    SELECT ss.id,
                           ss.season_id,
                           ss.user_id,
                           ss.role,
                           ss.created_at,
                           ss.updated_at,
                           p.username,
                           p.full_name,
                           p.avatar_url
                    FROM season_staff ss
                    JOIN profiles p ON p.id = ss.user_id
                    WHERE ss.season_id = @id
                    ORDER BY ss.role, p.username
                    """,
                    new { id });
            }

            return Results.Ok(new
            {
                season,
                nodes,
                tree,
                rules,
                staff,
                permissions = new
                {
                    can_manage = access.CanManage,
                    is_public = access.IsPublic
                }
            });
        });

        app.MapPut("/api/seasons/{id}", async (
            Guid                    id,
            [FromBody] UpdateSeasonRequest req,
            HttpContext             ctx,
            IDbConnectionFactory    db,
            CancellationToken       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var access = await GetSeasonAccessAsync(conn, id, userCtx.UserIdGuid, tx);
            if (access is null) return Results.NotFound();
            if (!access.CanManage) return Results.Forbid();

            var updates = new Dictionary<string, object?>();

            if (req.Name is not null) updates["name"] = req.Name.Trim();
            if (req.Description is not null) updates["description"] = req.Description;
            if (req.Game is not null) updates["game"] = req.Game.Trim();
            if (req.IsPublic.HasValue) updates["is_public"] = req.IsPublic.Value;
            if (req.AllowManualOverrides.HasValue) updates["allow_manual_overrides"] = req.AllowManualOverrides.Value;
            if (req.StartDate.HasValue) updates["start_date"] = req.StartDate.Value;
            if (req.EndDate.HasValue) updates["end_date"] = req.EndDate.Value;
            if (req.OrganizationId.HasValue) updates["organization_id"] = req.OrganizationId.Value;
            if (req.Status is not null)
            {
                if (!AllowedSeasonStatuses.Contains(req.Status))
                    return Results.BadRequest(new { error = "Invalid season status." });
                updates["status"] = req.Status.Trim().ToLowerInvariant();
            }
            if (req.Settings.HasValue) updates["settings"] = req.Settings.Value.GetRawText();
            if (req.Slug is not null)
            {
                var nextSlug = await EnsureUniqueSeasonSlugAsync(
                    conn,
                    tx,
                    Slugify(req.Slug),
                    id);
                updates["slug"] = nextSlug;
            }
            else if (req.Name is not null)
            {
                var nextSlug = await EnsureUniqueSeasonSlugAsync(
                    conn,
                    tx,
                    Slugify(req.Name),
                    id);
                updates["slug"] = nextSlug;
            }

            if (updates.Count == 0)
                return Results.BadRequest(new { error = "No valid season fields were provided." });

            var jsonbFields = new HashSet<string> { "settings" };
            var setClause = string.Join(", ", updates.Keys.Select(k =>
                jsonbFields.Contains(k) ? $"{k} = @{k}::jsonb" : $"{k} = @{k}"));
            var parameters = new DynamicParameters();
            foreach (var kv in updates)
                parameters.Add(kv.Key, kv.Value);
            parameters.Add("id", id);
            parameters.Add("updated_at", DateTime.UtcNow);

            var row = await conn.QuerySingleAsync<dynamic>(
                $"UPDATE seasons SET {setClause}, updated_at = @updated_at WHERE id = @id RETURNING id, slug, updated_at",
                parameters,
                tx);

            tx.Commit();
            return Results.Ok(row);
        }).RequireAuthorization("Authenticated");

        app.MapPut("/api/seasons/{id}/staff", async (
            Guid                     id,
            [FromBody] SyncSeasonStaffRequest req,
            HttpContext              ctx,
            IDbConnectionFactory     db,
            CancellationToken        ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var access = await GetSeasonAccessAsync(conn, id, userCtx.UserIdGuid, tx);
            if (access is null) return Results.NotFound();
            if (!access.CanManage) return Results.Forbid();

            var items = req.Staff ?? Array.Empty<SeasonStaffMemberDto>();
            foreach (var item in items)
            {
                if (!AllowedStaffRoles.Contains(item.Role))
                    return Results.BadRequest(new { error = $"Invalid staff role '{item.Role}'." });
                if (item.UserId == access.OwnerUserId)
                    return Results.BadRequest(new { error = "The season owner should not be added to season staff." });
            }

            var existingIds = (await conn.QueryAsync<Guid>(
                "SELECT user_id FROM season_staff WHERE season_id = @seasonId",
                new { seasonId = id },
                tx)).ToHashSet();

            var incomingIds = items.Select(s => s.UserId).ToHashSet();
            var toDelete = existingIds.Except(incomingIds).ToArray();

            if (toDelete.Length > 0)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM season_staff WHERE season_id = @seasonId AND user_id = ANY(@userIds)",
                    new { seasonId = id, userIds = toDelete },
                    tx);
            }

            foreach (var item in items)
            {
                if (existingIds.Contains(item.UserId))
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE season_staff
                        SET role = @role, updated_at = NOW()
                        WHERE season_id = @seasonId AND user_id = @userId
                        """,
                        new { seasonId = id, userId = item.UserId, role = item.Role.Trim().ToLowerInvariant() },
                        tx);
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO season_staff (season_id, user_id, role)
                        VALUES (@seasonId, @userId, @role)
                        """,
                        new { seasonId = id, userId = item.UserId, role = item.Role.Trim().ToLowerInvariant() },
                        tx);
                }
            }

            tx.Commit();
            return Results.Ok(new { success = true, count = items.Length });
        }).RequireAuthorization("Authenticated");

        app.MapPut("/api/seasons/{id}/nodes", async (
            Guid                    id,
            [FromBody] SyncSeasonNodesRequest req,
            HttpContext             ctx,
            IDbConnectionFactory    db,
            CancellationToken       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var access = await GetSeasonAccessAsync(conn, id, userCtx.UserIdGuid, tx);
            if (access is null) return Results.NotFound();
            if (!access.CanManage) return Results.Forbid();

            var nodes = (req.Nodes ?? Array.Empty<SeasonNodeDto>())
                .Select(node => node with
                {
                    Id = node.Id.HasValue && node.Id.Value == Guid.Empty ? null : node.Id,
                    NodeType = node.NodeType.Trim().ToLowerInvariant(),
                    Status = node.Status.Trim().ToLowerInvariant()
                })
                .ToArray();

            if (nodes.Any(node => !AllowedNodeTypes.Contains(node.NodeType)))
                return Results.BadRequest(new { error = "One or more node types are invalid." });
            if (nodes.Any(node => !AllowedNodeStatuses.Contains(node.Status)))
                return Results.BadRequest(new { error = "One or more node statuses are invalid." });

            var existingNodes = (await conn.QueryAsync<dynamic>(
                "SELECT id, node_type, parent_node_id FROM season_nodes WHERE season_id = @seasonId",
                new { seasonId = id },
                tx)).ToList();

            var existingNodeIds = existingNodes.Select(n => (Guid)n.id).ToHashSet();
            var rootNodeIds = existingNodes
                .Where(n => string.Equals((string)n.node_type, "root", StringComparison.OrdinalIgnoreCase))
                .Select(n => (Guid)n.id)
                .ToHashSet();

            var normalizedNodes = nodes
                .Select(node => new
                {
                    Node = node,
                    ResolvedId = node.Id ?? Guid.NewGuid()
                })
                .ToArray();

            var incomingIds = normalizedNodes
                .Select(node => node.ResolvedId)
                .ToHashSet();

            var incomingExistingIds = normalizedNodes
                .Where(n => n.Node.Id.HasValue)
                .Select(n => n.Node.Id!.Value)
                .ToHashSet();
            var toDelete = existingNodeIds
                .Except(incomingExistingIds)
                .Except(rootNodeIds)
                .ToArray();

            var retainedParentIds = existingNodeIds
                .Except(toDelete)
                .ToHashSet();
            retainedParentIds.UnionWith(incomingIds);

            if (nodes.Any(node => node.ParentNodeId.HasValue && !retainedParentIds.Contains(node.ParentNodeId.Value)))
                return Results.BadRequest(new { error = "One or more nodes reference a parent that is not being retained in this season tree." });
            if (normalizedNodes.Any(node => node.Node.ParentNodeId.HasValue && node.Node.ParentNodeId.Value == node.ResolvedId))
                return Results.BadRequest(new { error = "A season node cannot be its own parent." });
            if (normalizedNodes.Any(node => string.Equals(node.Node.NodeType, "root", StringComparison.OrdinalIgnoreCase) && node.Node.ParentNodeId.HasValue))
                return Results.BadRequest(new { error = "Root nodes cannot have a parent." });
            if (normalizedNodes.Any(node => !string.Equals(node.Node.NodeType, "root", StringComparison.OrdinalIgnoreCase) && !node.Node.ParentNodeId.HasValue))
                return Results.BadRequest(new { error = "Every non-root season node must have a parent." });
            if (normalizedNodes.Any(node =>
                    node.Node.Id.HasValue
                    && rootNodeIds.Contains(node.Node.Id.Value)
                    && (node.Node.ParentNodeId.HasValue || !string.Equals(node.Node.NodeType, "root", StringComparison.OrdinalIgnoreCase))))
                return Results.BadRequest(new { error = "Existing root nodes cannot be reparented or changed away from root." });
            if (normalizedNodes.Any(node =>
                    string.Equals(node.Node.NodeType, "root", StringComparison.OrdinalIgnoreCase)
                    && (!node.Node.Id.HasValue || !rootNodeIds.Contains(node.Node.Id.Value))))
                return Results.BadRequest(new { error = "A season cannot have more than one root node." });

            var stageIds = nodes
                .Where(n => n.LinkedStageId.HasValue)
                .Select(n => n.LinkedStageId!.Value)
                .Distinct()
                .ToArray();

            var allowedOrganizerIds = await GetSeasonManagerIdsAsync(conn, id, access.OwnerUserId, tx);

            var stageOwners = stageIds.Length == 0
                ? new Dictionary<Guid, Guid>()
                : (await conn.QueryAsync<(Guid id, Guid tournament_id)>(
                    """
                    SELECT id, tournament_id
                    FROM tournament_stages
                    WHERE id = ANY(@ids)
                    """,
                    new { ids = stageIds },
                    tx)).ToDictionary(x => x.id, x => x.tournament_id);

            if (stageIds.Any(stageId => !stageOwners.ContainsKey(stageId)))
                return Results.BadRequest(new { error = "One or more linked stages do not exist." });

            var tournamentIds = nodes
                .Where(n => n.LinkedTournamentId.HasValue)
                .Select(n => n.LinkedTournamentId!.Value)
                .Concat(stageOwners.Values)
                .Distinct()
                .ToArray();

            var tournamentOwners = tournamentIds.Length == 0
                ? new Dictionary<Guid, Guid>()
                : (await conn.QueryAsync<(Guid id, Guid organizer_id)>(
                    "SELECT id, organizer_id FROM tournaments WHERE id = ANY(@ids)",
                    new { ids = tournamentIds },
                    tx)).ToDictionary(x => x.id, x => x.organizer_id);

            if (tournamentIds.Any(tid => !tournamentOwners.TryGetValue(tid, out var organizerId) || !allowedOrganizerIds.Contains(organizerId)))
                return Results.BadRequest(new { error = "A linked tournament must be owned by the season owner or one of the season co-organizers/admins." });

            foreach (var node in nodes)
            {
                if (node.LinkedStageId.HasValue)
                {
                    var stageTournamentId = stageOwners[node.LinkedStageId.Value];
                    if (node.LinkedTournamentId.HasValue && node.LinkedTournamentId.Value != stageTournamentId)
                        return Results.BadRequest(new { error = "A linked stage must belong to the same linked tournament." });
                }
            }

            var parentByNode = existingNodes
                .Where(node => !toDelete.Contains((Guid)node.id))
                .ToDictionary(
                    node => (Guid)node.id,
                    node => node.parent_node_id is Guid parentNodeId ? (Guid?)parentNodeId : null);

            foreach (var normalizedNode in normalizedNodes)
                parentByNode[normalizedNode.ResolvedId] = normalizedNode.Node.ParentNodeId;

            foreach (var nodeId in parentByNode.Keys.ToArray())
            {
                var visited = new HashSet<Guid>();
                Guid? currentNodeId = nodeId;

                while (currentNodeId.HasValue)
                {
                    if (!visited.Add(currentNodeId.Value))
                        return Results.BadRequest(new { error = "Season nodes cannot create parent/child cycles." });

                    currentNodeId = parentByNode.TryGetValue(currentNodeId.Value, out var parentNodeId)
                        ? parentNodeId
                        : null;
                }
            }

            foreach (var normalizedNode in normalizedNodes)
            {
                var node = normalizedNode.Node;
                var resolvedId = normalizedNode.ResolvedId;
                var linkedTournamentId = node.LinkedTournamentId
                    ?? (node.LinkedStageId.HasValue ? stageOwners[node.LinkedStageId.Value] : null);

                if (existingNodeIds.Contains(resolvedId))
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE season_nodes
                        SET parent_node_id = @parentNodeId,
                            name = @name,
                            slug = @slug,
                            node_type = @nodeType,
                            display_order = @displayOrder,
                            region = @region,
                            city = @city,
                            country = @country,
                            linked_tournament_id = @linkedTournamentId,
                            linked_stage_id = @linkedStageId,
                            status = @status,
                            registration_deadline = @registrationDeadline,
                            starts_at = @startsAt,
                            ends_at = @endsAt,
                            metadata = COALESCE(@metadata::jsonb, '{}'::jsonb),
                            updated_at = NOW()
                        WHERE id = @id AND season_id = @seasonId
                        """,
                        new
                        {
                            id = resolvedId,
                            seasonId = id,
                            parentNodeId = node.ParentNodeId,
                            name = node.Name.Trim(),
                            slug = string.IsNullOrWhiteSpace(node.Slug) ? null : Slugify(node.Slug),
                            nodeType = node.NodeType,
                            displayOrder = node.DisplayOrder,
                            region = node.Region,
                            city = node.City,
                            country = node.Country,
                            linkedTournamentId,
                            linkedStageId = node.LinkedStageId,
                            status = node.Status,
                            registrationDeadline = node.RegistrationDeadline,
                            startsAt = node.StartsAt,
                            endsAt = node.EndsAt,
                            metadata = node.Metadata?.GetRawText()
                        },
                        tx);
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO season_nodes (
                            id, season_id, parent_node_id, name, slug, node_type, display_order,
                            region, city, country, linked_tournament_id, linked_stage_id,
                            status, registration_deadline, starts_at, ends_at, metadata
                        )
                        VALUES (
                            @id, @seasonId, @parentNodeId, @name, @slug, @nodeType, @displayOrder,
                            @region, @city, @country, @linkedTournamentId, @linkedStageId,
                            @status, @registrationDeadline, @startsAt, @endsAt,
                            COALESCE(@metadata::jsonb, '{}'::jsonb)
                        )
                        """,
                        new
                        {
                            id = resolvedId,
                            seasonId = id,
                            parentNodeId = node.ParentNodeId,
                            name = node.Name.Trim(),
                            slug = string.IsNullOrWhiteSpace(node.Slug) ? null : Slugify(node.Slug),
                            nodeType = node.NodeType,
                            displayOrder = node.DisplayOrder,
                            region = node.Region,
                            city = node.City,
                            country = node.Country,
                            linkedTournamentId,
                            linkedStageId = node.LinkedStageId,
                            status = node.Status,
                            registrationDeadline = node.RegistrationDeadline,
                            startsAt = node.StartsAt,
                            endsAt = node.EndsAt,
                            metadata = node.Metadata?.GetRawText()
                        },
                        tx);
                }
            }

            if (toDelete.Length > 0)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM season_nodes WHERE season_id = @seasonId AND id = ANY(@ids)",
                    new { seasonId = id, ids = toDelete },
                    tx);
            }

            tx.Commit();
            return Results.Ok(new { success = true, count = nodes.Length });
        }).RequireAuthorization("Authenticated");

        app.MapPut("/api/seasons/{id}/points-rules", async (
            Guid                         id,
            [FromBody] SyncSeasonPointsRulesRequest req,
            HttpContext                  ctx,
            IDbConnectionFactory         db,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var access = await GetSeasonAccessAsync(conn, id, userCtx.UserIdGuid, tx);
            if (access is null) return Results.NotFound();
            if (!access.CanManage) return Results.Forbid();

            var rules = req.Rules ?? Array.Empty<SeasonPointsRuleDto>();
            if (rules.Any(r => r.PlacementFrom < 1 || r.PlacementTo < r.PlacementFrom))
                return Results.BadRequest(new { error = "Placement ranges must start at 1 and end at or after placement_from." });
            if (rules.Any(r => r.PointsAwarded < 0))
                return Results.BadRequest(new { error = "Points awarded cannot be negative." });
            if (rules.Any(r => r.QualificationStatus is not null && !AllowedQualificationStatuses.Contains(r.QualificationStatus)))
                return Results.BadRequest(new { error = "One or more qualification statuses are invalid." });
            if (rules.Any(r => r.AutoCreateQualification && string.IsNullOrWhiteSpace(r.QualificationStatus)))
                return Results.BadRequest(new { error = "Auto-created qualifications require a qualification status type." });

            var seasonNodes = (await conn.QueryAsync<(Guid id, Guid? linked_tournament_id, Guid? linked_stage_id)>(
                """
                SELECT id, linked_tournament_id, linked_stage_id
                FROM season_nodes
                WHERE season_id = @seasonId
                """,
                new { seasonId = id },
                tx)).ToDictionary(x => x.id);

            var seasonNodeIds = seasonNodes.Keys.ToHashSet();

            if (rules.Any(r => !seasonNodeIds.Contains(r.SourceNodeId)))
                return Results.BadRequest(new { error = "A points rule references a source node outside this season." });
            if (rules.Any(r => r.DestinationNodeId.HasValue && !seasonNodeIds.Contains(r.DestinationNodeId.Value)))
                return Results.BadRequest(new { error = "A points rule references a destination node outside this season." });

            var normalizedRules = rules
                .Select(rule =>
                {
                    var sourceNode = seasonNodes[rule.SourceNodeId];
                    var effectiveSourceStageId = rule.SourceStageId ?? sourceNode.linked_stage_id;
                    return new
                    {
                        Rule = rule,
                        SourceNode = sourceNode,
                        EffectiveSourceStageId = effectiveSourceStageId
                    };
                })
                .ToArray();

            if (normalizedRules.Any(x => !x.EffectiveSourceStageId.HasValue && x.Rule.PlacementTo > 1))
                return Results.BadRequest(new
                {
                    error = "Tournament-level rules currently support winner-only placement. Use a linked stage rule for broader placement ranges."
                });

            var stageIds = normalizedRules
                .Where(x => x.EffectiveSourceStageId.HasValue)
                .Select(x => x.EffectiveSourceStageId!.Value)
                .Distinct()
                .ToArray();

            var stages = stageIds.Length == 0
                ? new Dictionary<Guid, Guid>()
                : (await conn.QueryAsync<(Guid id, Guid tournament_id)>(
                    """
                    SELECT id, tournament_id
                    FROM tournament_stages
                    WHERE id = ANY(@ids)
                    """,
                    new { ids = stageIds },
                    tx)).ToDictionary(x => x.id, x => x.tournament_id);

            if (stageIds.Any(stageId => !stages.ContainsKey(stageId)))
                return Results.BadRequest(new { error = "A points rule references a stage that does not exist." });

            foreach (var normalizedRule in normalizedRules.Where(x => x.EffectiveSourceStageId.HasValue))
            {
                var sourceNode = normalizedRule.SourceNode;
                var sourceStageId = normalizedRule.EffectiveSourceStageId!.Value;
                var stageTournamentId = stages[sourceStageId];

                if (sourceNode.linked_stage_id.HasValue && sourceNode.linked_stage_id.Value != sourceStageId)
                    return Results.BadRequest(new { error = "A points rule stage must match the source node's linked stage." });

                if (!sourceNode.linked_tournament_id.HasValue || sourceNode.linked_tournament_id.Value != stageTournamentId)
                    return Results.BadRequest(new { error = "A points rule stage must belong to the source node's linked tournament." });
            }

            var existingIds = (await conn.QueryAsync<Guid>(
                "SELECT id FROM season_points_rules WHERE season_id = @seasonId",
                new { seasonId = id },
                tx)).ToHashSet();

            var incomingExistingIds = rules.Where(r => r.Id.HasValue).Select(r => r.Id!.Value).ToHashSet();
            var toDelete = existingIds.Except(incomingExistingIds).ToArray();
            if (toDelete.Length > 0)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM season_points_rules WHERE season_id = @seasonId AND id = ANY(@ids)",
                    new { seasonId = id, ids = toDelete },
                    tx);
            }

            foreach (var normalizedRule in normalizedRules)
            {
                var rule = normalizedRule.Rule;
                var resolvedId = rule.Id ?? Guid.NewGuid();
                if (existingIds.Contains(resolvedId))
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE season_points_rules
                        SET source_node_id = @sourceNodeId,
                            source_stage_id = @sourceStageId,
                            destination_node_id = @destinationNodeId,
                            placement_from = @placementFrom,
                            placement_to = @placementTo,
                            points_awarded = @pointsAwarded,
                            qualification_status = @qualificationStatus,
                            auto_create_qualification = @autoCreateQualification,
                            region_key = @regionKey,
                            updated_at = NOW()
                        WHERE id = @id AND season_id = @seasonId
                        """,
                        new
                        {
                            id = resolvedId,
                            seasonId = id,
                            sourceNodeId = rule.SourceNodeId,
                            sourceStageId = normalizedRule.EffectiveSourceStageId,
                            destinationNodeId = rule.DestinationNodeId,
                            placementFrom = rule.PlacementFrom,
                            placementTo = rule.PlacementTo,
                            pointsAwarded = rule.PointsAwarded,
                            qualificationStatus = rule.QualificationStatus?.Trim().ToLowerInvariant(),
                            autoCreateQualification = rule.AutoCreateQualification,
                            regionKey = rule.RegionKey
                        },
                        tx);
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO season_points_rules (
                            id, season_id, source_node_id, source_stage_id, destination_node_id,
                            placement_from, placement_to, points_awarded, qualification_status,
                            auto_create_qualification, region_key
                        )
                        VALUES (
                            @id, @seasonId, @sourceNodeId, @sourceStageId, @destinationNodeId,
                            @placementFrom, @placementTo, @pointsAwarded, @qualificationStatus,
                            @autoCreateQualification, @regionKey
                        )
                        """,
                        new
                        {
                            id = resolvedId,
                            seasonId = id,
                            sourceNodeId = rule.SourceNodeId,
                            sourceStageId = normalizedRule.EffectiveSourceStageId,
                            destinationNodeId = rule.DestinationNodeId,
                            placementFrom = rule.PlacementFrom,
                            placementTo = rule.PlacementTo,
                            pointsAwarded = rule.PointsAwarded,
                            qualificationStatus = rule.QualificationStatus?.Trim().ToLowerInvariant(),
                            autoCreateQualification = rule.AutoCreateQualification,
                            regionKey = rule.RegionKey
                        },
                        tx);
                }
            }

            tx.Commit();
            return Results.Ok(new { success = true, count = rules.Length });
        }).RequireAuthorization("Authenticated");

        app.MapPost("/api/seasons/{id}/entry-locks/reassign", async (
            Guid                              id,
            [FromBody] ReassignSeasonEntryLockRequest req,
            HttpContext                       ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var access = await GetSeasonAccessAsync(conn, id, userCtx.UserIdGuid, tx);
            if (access is null) return Results.NotFound();
            if (!access.CanManage) return Results.Forbid();
            if (!access.AllowManualOverrides)
                return Results.BadRequest(new { error = "Manual overrides are disabled for this season." });

            var isTeamSeason = string.Equals(access.ParticipantMode, "team", StringComparison.OrdinalIgnoreCase);
            if (isTeamSeason)
            {
                if (!req.TeamId.HasValue || req.UserId.HasValue)
                    return Results.BadRequest(new { error = "This season requires a team override target." });
            }
            else
            {
                if (!req.UserId.HasValue || req.TeamId.HasValue)
                    return Results.BadRequest(new { error = "This season requires a solo player override target." });
            }

            var targetNode = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, parent_node_id, linked_tournament_id, name
                FROM season_nodes
                WHERE id = @nodeId AND season_id = @seasonId
                """,
                new { nodeId = req.TargetNodeId, seasonId = id },
                tx);

            if (targetNode is null)
                return Results.BadRequest(new { error = "Target season node was not found in this season." });

            if (targetNode.parent_node_id is not Guid lockGroupNodeId)
                return Results.BadRequest(new { error = "Target node must belong to a qualifier branch group." });

            if (isTeamSeason)
            {
                var teamExists = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM teams WHERE id = @teamId)",
                    new { teamId = req.TeamId!.Value },
                    tx);

                if (!teamExists)
                    return Results.BadRequest(new { error = "Target team was not found." });
            }
            else
            {
                var userExists = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM profiles WHERE id = @userId)",
                    new { userId = req.UserId!.Value },
                    tx);

                if (!userExists)
                    return Results.BadRequest(new { error = "Target player was not found." });
            }

            Guid? targetTournamentId = targetNode.linked_tournament_id is Guid linkedTournamentId
                ? linkedTournamentId
                : null;

            Guid? targetTournamentParticipantId = null;
            if (targetTournamentId.HasValue)
            {
                targetTournamentParticipantId = isTeamSeason
                    ? await conn.QuerySingleOrDefaultAsync<Guid?>(
                        """
                        SELECT id
                        FROM tournament_participants
                        WHERE tournament_id = @tournamentId
                          AND team_id = @teamId
                          AND status NOT IN ('cancelled', 'rejected', 'disqualified')
                        ORDER BY created_at DESC
                        LIMIT 1
                        """,
                        new
                        {
                            tournamentId = targetTournamentId.Value,
                            teamId = req.TeamId!.Value
                        },
                        tx)
                    : await conn.QuerySingleOrDefaultAsync<Guid?>(
                        """
                        SELECT id
                        FROM tournament_participants
                        WHERE tournament_id = @tournamentId
                          AND user_id = @userId
                          AND status NOT IN ('cancelled', 'rejected', 'disqualified')
                        ORDER BY created_at DESC
                        LIMIT 1
                        """,
                        new
                        {
                            tournamentId = targetTournamentId.Value,
                            userId = req.UserId!.Value
                        },
                        tx);

                if (!targetTournamentParticipantId.HasValue)
                {
                    return Results.BadRequest(new
                    {
                        error = "Target branch is already linked to a tournament. Create or restore that tournament registration before reassigning this season lock."
                    });
                }
            }

            var existingLock = isTeamSeason
                ? await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id, season_node_id, tournament_participant_id
                    FROM season_entry_locks
                    WHERE season_id = @seasonId
                      AND lock_group_node_id = @lockGroupNodeId
                      AND team_id = @teamId
                      AND status = 'locked'
                    LIMIT 1
                    """,
                    new
                    {
                        seasonId = id,
                        lockGroupNodeId,
                        teamId = req.TeamId!.Value
                    },
                    tx)
                : await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id, season_node_id, tournament_participant_id
                    FROM season_entry_locks
                    WHERE season_id = @seasonId
                      AND lock_group_node_id = @lockGroupNodeId
                      AND user_id = @userId
                      AND status = 'locked'
                    LIMIT 1
                    """,
                    new
                    {
                        seasonId = id,
                        lockGroupNodeId,
                        userId = req.UserId!.Value
                    },
                    tx);

            var action = "created";
            if (existingLock is not null)
            {
                var existingNodeId = (Guid)existingLock.season_node_id;
                if (existingNodeId == req.TargetNodeId)
                {
                    action = "confirmed";
                    await conn.ExecuteAsync(
                        """
                        UPDATE season_entry_locks
                        SET linked_tournament_id = @linkedTournamentId,
                            tournament_participant_id = @tournamentParticipantId,
                            locked_by = 'admin_override',
                            notes = @notes,
                            updated_at = NOW()
                        WHERE id = @id
                        """,
                        new
                        {
                            id = (Guid)existingLock.id,
                            linkedTournamentId = targetTournamentId,
                            tournamentParticipantId = targetTournamentParticipantId,
                            notes = req.Notes
                        },
                        tx);
                }
                else
                {
                    action = "reassigned";
                    if (existingLock.tournament_participant_id is Guid tournamentParticipantId)
                    {
                        await conn.ExecuteAsync(
                            """
                            UPDATE tournament_participants
                            SET status = 'cancelled'
                            WHERE id = @participantId
                              AND status NOT IN ('cancelled', 'rejected')
                            """,
                            new { participantId = tournamentParticipantId },
                            tx);
                    }

                    await conn.ExecuteAsync(
                        """
                        UPDATE season_entry_locks
                        SET status = 'reassigned',
                            tournament_participant_id = NULL,
                            notes = @notes,
                            updated_at = NOW()
                        WHERE id = @id
                        """,
                        new
                        {
                            id = (Guid)existingLock.id,
                            notes = req.Notes
                        },
                        tx);
                }
            }

            if (!string.Equals(action, "confirmed", StringComparison.Ordinal))
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO season_entry_locks (
                        season_id, season_node_id, lock_group_node_id, linked_tournament_id,
                        tournament_participant_id, participant_mode, team_id, user_id, status, locked_by, notes
                    )
                    VALUES (
                        @seasonId, @seasonNodeId, @lockGroupNodeId, @linkedTournamentId,
                        @tournamentParticipantId, @participantMode, @teamId, @userId, 'locked', 'admin_override', @notes
                    )
                    """,
                    new
                    {
                        seasonId = id,
                        seasonNodeId = req.TargetNodeId,
                        lockGroupNodeId,
                        linkedTournamentId = targetTournamentId,
                        tournamentParticipantId = targetTournamentParticipantId,
                        participantMode = access.ParticipantMode,
                        teamId = isTeamSeason ? req.TeamId : (Guid?)null,
                        userId = isTeamSeason ? (Guid?)null : req.UserId,
                        notes = req.Notes
                    },
                    tx);
            }

            tx.Commit();
            return Results.Ok(new
            {
                success = true,
                action,
                target_node_id = req.TargetNodeId,
                lock_group_node_id = lockGroupNodeId
            });
        }).RequireAuthorization("Authenticated");

        app.MapPost("/api/seasons/{id}/recalculate", async (
            Guid                  id,
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var access = await GetSeasonAccessAsync(conn, id, userCtx.UserIdGuid, tx);
            if (access is null) return Results.NotFound();
            if (!access.CanManage) return Results.Forbid();

            var nodes = (await conn.QueryAsync<SeasonSourceNode>(
                """
                SELECT id,
                       name,
                       linked_tournament_id AS LinkedTournamentId,
                       linked_stage_id AS LinkedStageId,
                       region,
                       city,
                       country
                FROM season_nodes
                WHERE season_id = @seasonId
                """,
                new { seasonId = id },
                tx)).ToDictionary(x => x.Id);

            var rules = (await conn.QueryAsync<SeasonPointsRuleRecord>(
                """
                SELECT id,
                       source_node_id AS SourceNodeId,
                       source_stage_id AS SourceStageId,
                       destination_node_id AS DestinationNodeId,
                       placement_from AS PlacementFrom,
                       placement_to AS PlacementTo,
                       points_awarded AS PointsAwarded,
                       qualification_status AS QualificationType,
                       auto_create_qualification AS AutoCreateQualification,
                       region_key AS RegionKey
                FROM season_points_rules
                WHERE season_id = @seasonId
                ORDER BY source_node_id, source_stage_id NULLS FIRST, placement_from, placement_to, created_at
                """,
                new { seasonId = id },
                tx)).ToList();

            var existingGeneratedQualificationStates = (await conn.QueryAsync<ExistingGeneratedQualificationState>(
                """
                SELECT source_node_id AS SourceNodeId,
                       source_stage_id AS SourceStageId,
                       destination_node_id AS DestinationNodeId,
                       team_id AS TeamId,
                       user_id AS UserId,
                       qualification_type AS QualificationType,
                       status AS Status,
                       notes AS Notes,
                       participant_response_note AS ParticipantResponseNote,
                       responded_at AS RespondedAt,
                       responded_by_user_id AS RespondedByUserId
                FROM season_qualification_records
                WHERE season_id = @seasonId
                  AND is_manual_override = FALSE
                """,
                new { seasonId = id },
                tx)).ToDictionary(
                    x => new GeneratedQualificationKey(
                        x.SourceNodeId,
                        x.SourceStageId,
                        x.DestinationNodeId,
                        x.TeamId,
                        x.UserId,
                        x.QualificationType),
                    x => x);

            await conn.ExecuteAsync(
                "DELETE FROM season_qualification_records WHERE season_id = @seasonId AND is_manual_override = FALSE",
                new { seasonId = id },
                tx);
            await conn.ExecuteAsync(
                "DELETE FROM season_points_ledger WHERE season_id = @seasonId AND is_manual_override = FALSE",
                new { seasonId = id },
                tx);

            if (rules.Count == 0)
            {
                tx.Commit();
                return Results.Ok(new
                {
                    success = true,
                    sources_processed = 0,
                    points_entries = 0,
                    qualification_records = 0
                });
            }

            var resultsBySource = new Dictionary<(Guid SourceNodeId, Guid? SourceStageId), List<SeasonPlacementResult>>();
            foreach (var source in rules
                         .Select(rule =>
                         {
                             var sourceNode = nodes[rule.SourceNodeId];
                             return (rule.SourceNodeId, SourceStageId: rule.SourceStageId ?? sourceNode.LinkedStageId);
                         })
                         .Distinct())
            {
                if (!nodes.TryGetValue(source.SourceNodeId, out var sourceNode))
                    continue;

                resultsBySource[source] = await GetSeasonPlacementResultsAsync(
                    db,
                    conn,
                    tx,
                    access.ParticipantMode,
                    sourceNode,
                    source.SourceStageId,
                    ct);
            }

            if (rules.Any(rule =>
                {
                    var sourceNode = nodes[rule.SourceNodeId];
                    var effectiveSourceStageId = rule.SourceStageId ?? sourceNode.LinkedStageId;
                    return !effectiveSourceStageId.HasValue && rule.PlacementTo > 1;
                }))
            {
                return Results.BadRequest(new
                {
                    error = "Tournament-level rules currently support winner-only placement. Update those rules to use a linked stage or winner-only placement before recalculating."
                });
            }

            var generatedQualifications = new List<GeneratedQualificationRecord>();
            var pointsInserted = 0;

            foreach (var rule in rules)
            {
                if (!nodes.TryGetValue(rule.SourceNodeId, out var sourceNode))
                    continue;

                var effectiveSourceStageId = rule.SourceStageId ?? sourceNode.LinkedStageId;

                if (!resultsBySource.TryGetValue((rule.SourceNodeId, effectiveSourceStageId), out var sourceResults)
                    || sourceResults.Count == 0)
                {
                    continue;
                }

                var regionKey = ResolveSeasonRegionKey(rule.RegionKey, sourceNode.Region, sourceNode.City, sourceNode.Country);

                foreach (var result in sourceResults.Where(result =>
                             result.Placement >= rule.PlacementFrom
                             && result.Placement <= rule.PlacementTo))
                {
                    if (rule.PointsAwarded > 0)
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO season_points_ledger (
                                season_id, source_node_id, source_stage_id, source_tournament_id,
                                participant_mode, team_id, user_id, region_key, points, placement, reason
                            )
                            VALUES (
                                @seasonId, @sourceNodeId, @sourceStageId, @sourceTournamentId,
                                @participantMode, @teamId, @userId, @regionKey, @points, @placement, @reason
                            )
                            """,
                            new
                            {
                                seasonId = id,
                                sourceNodeId = rule.SourceNodeId,
                                sourceStageId = effectiveSourceStageId,
                                sourceTournamentId = result.SourceTournamentId,
                                participantMode = access.ParticipantMode,
                                teamId = result.TeamId,
                                userId = result.UserId,
                                regionKey,
                                points = rule.PointsAwarded,
                                placement = result.Placement,
                                reason = $"{sourceNode.Name} placement #{result.Placement}"
                            },
                            tx);
                        pointsInserted++;
                    }

                    if (rule.AutoCreateQualification)
                    {
                        generatedQualifications.Add(new GeneratedQualificationRecord(
                            rule.SourceNodeId,
                            effectiveSourceStageId,
                            rule.DestinationNodeId,
                            result.SourceTournamentId,
                            result.TeamId,
                            result.UserId,
                            result.Placement,
                            rule.QualificationType!));
                    }
                }
            }

            var manualQualificationOverrides = (await conn.QueryAsync<ManualQualificationOverrideKey>(
                """
                SELECT source_node_id AS SourceNodeId,
                       source_stage_id AS SourceStageId,
                       team_id AS TeamId,
                       user_id AS UserId
                FROM season_qualification_records
                WHERE season_id = @seasonId
                  AND is_manual_override = TRUE
                """,
                new { seasonId = id },
                tx)).ToHashSet();

            var pointSnapshots = string.Equals(access.ParticipantMode, "team", StringComparison.OrdinalIgnoreCase)
                ? (await conn.QueryAsync<(Guid entity_id, int total_points)>(
                    """
                    SELECT team_id AS entity_id,
                           COALESCE(SUM(points), 0)::int AS total_points
                    FROM season_points_ledger
                    WHERE season_id = @seasonId
                      AND team_id IS NOT NULL
                    GROUP BY team_id
                    """,
                    new { seasonId = id },
                    tx)).ToDictionary(x => x.entity_id, x => x.total_points)
                : (await conn.QueryAsync<(Guid entity_id, int total_points)>(
                    """
                    SELECT user_id AS entity_id,
                           COALESCE(SUM(points), 0)::int AS total_points
                    FROM season_points_ledger
                    WHERE season_id = @seasonId
                      AND user_id IS NOT NULL
                    GROUP BY user_id
                    """,
                    new { seasonId = id },
                    tx)).ToDictionary(x => x.entity_id, x => x.total_points);

            var qualificationRecordsInserted = 0;
            foreach (var qualification in generatedQualifications
                         .Distinct())
            {
                var overrideKey = new ManualQualificationOverrideKey(
                    qualification.SourceNodeId,
                    qualification.SourceStageId,
                    qualification.TeamId,
                    qualification.UserId);

                if (manualQualificationOverrides.Contains(overrideKey))
                    continue;

                var generatedKey = new GeneratedQualificationKey(
                    qualification.SourceNodeId,
                    qualification.SourceStageId,
                    qualification.DestinationNodeId,
                    qualification.TeamId,
                    qualification.UserId,
                    qualification.QualificationType);

                var preservedState = existingGeneratedQualificationStates.TryGetValue(generatedKey, out var currentState)
                    ? currentState
                    : null;

                var entityId = qualification.TeamId ?? qualification.UserId;
                var pointsSnapshot = entityId.HasValue && pointSnapshots.TryGetValue(entityId.Value, out var totalPoints)
                    ? totalPoints
                    : 0;

                await conn.ExecuteAsync(
                    """
                    INSERT INTO season_qualification_records (
                        season_id, source_node_id, source_stage_id, destination_node_id, source_tournament_id,
                        participant_mode, team_id, user_id, placement, points_snapshot, qualification_type, status, notes,
                        participant_response_note, responded_at, responded_by_user_id
                    )
                    VALUES (
                        @seasonId, @sourceNodeId, @sourceStageId, @destinationNodeId, @sourceTournamentId,
                        @participantMode, @teamId, @userId, @placement, @pointsSnapshot, @qualificationType, @status, @notes,
                        @participantResponseNote, @respondedAt, @respondedByUserId
                    )
                    """,
                    new
                    {
                        seasonId = id,
                        sourceNodeId = qualification.SourceNodeId,
                        sourceStageId = qualification.SourceStageId,
                        destinationNodeId = qualification.DestinationNodeId,
                        sourceTournamentId = qualification.SourceTournamentId,
                        participantMode = access.ParticipantMode,
                        teamId = qualification.TeamId,
                        userId = qualification.UserId,
                        placement = qualification.Placement,
                        pointsSnapshot,
                        qualificationType = qualification.QualificationType,
                        status = preservedState?.Status ?? "earned",
                        notes = preservedState?.Notes,
                        participantResponseNote = preservedState?.ParticipantResponseNote,
                        respondedAt = preservedState?.RespondedAt,
                        respondedByUserId = preservedState?.RespondedByUserId
                    },
                    tx);
                qualificationRecordsInserted++;
            }

            tx.Commit();
            return Results.Ok(new
            {
                success = true,
                sources_processed = resultsBySource.Count,
                sources_with_results = resultsBySource.Count(x => x.Value.Count > 0),
                points_entries = pointsInserted,
                qualification_records = qualificationRecordsInserted
            });
        }).RequireAuthorization("Authenticated");

        app.MapPut("/api/seasons/{id}/qualifications/{recordId}", async (
            Guid                           id,
            Guid                           recordId,
            [FromBody] UpdateSeasonQualificationRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var access = await GetSeasonAccessAsync(conn, id, userCtx.UserIdGuid, tx);
            if (access is null) return Results.NotFound();
            if (!access.CanManage) return Results.Forbid();

            if (req.Status is not null && !AllowedQualificationRecordStatuses.Contains(req.Status))
                return Results.BadRequest(new { error = "Invalid qualification workflow status." });
            if (req.Status is not null)
            {
                var requestedStatus = req.Status.Trim().ToLowerInvariant();
                if (requestedStatus is "accepted" or "declined")
                    return Results.BadRequest(new { error = "Accepted and declined statuses must come from the participant response endpoint." });
            }
            if (req.QualificationType is not null && !AllowedQualificationStatuses.Contains(req.QualificationType))
                return Results.BadRequest(new { error = "Invalid qualification type." });

            if (req.DestinationNodeId.HasValue)
            {
                var destinationExists = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM season_nodes WHERE id = @nodeId AND season_id = @seasonId)",
                    new { nodeId = req.DestinationNodeId.Value, seasonId = id },
                    tx);
                if (!destinationExists)
                    return Results.BadRequest(new { error = "Destination node was not found in this season." });
            }

            var setClauses = new List<string> { "updated_at = NOW()" };
            var parameters = new DynamicParameters(new { id = recordId, seasonId = id });
            var shouldMarkManualOverride = false;

            if (req.Status is not null)
            {
                setClauses.Add("status = @status");
                var normalizedStatus = req.Status.Trim().ToLowerInvariant();
                parameters.Add("status", normalizedStatus);
                shouldMarkManualOverride = true;
            }

            if (req.QualificationType is not null)
            {
                setClauses.Add("qualification_type = @qualificationType");
                parameters.Add("qualificationType", req.QualificationType.Trim().ToLowerInvariant());
                shouldMarkManualOverride = true;
            }

            if (req.DestinationNodeId.HasValue)
            {
                setClauses.Add("destination_node_id = @destinationNodeId");
                parameters.Add("destinationNodeId", req.DestinationNodeId.Value);
                shouldMarkManualOverride = true;
            }

            if (req.Notes is not null)
            {
                setClauses.Add("notes = @notes");
                parameters.Add("notes", string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim());
            }

            if (setClauses.Count == 1)
                return Results.BadRequest(new { error = "No qualification changes were provided." });

            if (shouldMarkManualOverride && !access.AllowManualOverrides)
                return Results.BadRequest(new { error = "Manual qualification overrides are disabled for this season." });

            if (shouldMarkManualOverride)
                setClauses.Insert(0, "is_manual_override = TRUE");

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                $"""
                UPDATE season_qualification_records
                SET {string.Join(", ", setClauses)}
                WHERE id = @id AND season_id = @seasonId
                RETURNING id, destination_node_id, qualification_type, status, notes, participant_response_note, responded_at, responded_by_user_id, is_manual_override, updated_at
                """,
                parameters,
                tx);

            if (updated is null)
                return Results.NotFound();

            tx.Commit();
            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        app.MapPost("/api/seasons/{id}/qualifications/{recordId}/respond", async (
            Guid                            id,
            Guid                            recordId,
            [FromBody] RespondSeasonQualificationRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory            db,
            CancellationToken               ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var normalizedStatus = req.Status.Trim().ToLowerInvariant();
            if (normalizedStatus is not ("accepted" or "declined"))
                return Results.BadRequest(new { error = "Qualification responses must be accepted or declined." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var qualification = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, participant_mode, team_id, user_id, status
                FROM season_qualification_records
                WHERE id = @recordId AND season_id = @seasonId
                """,
                new { recordId, seasonId = id },
                tx);

            if (qualification is null)
                return Results.NotFound();

            var currentStatus = ((string)qualification.status).Trim().ToLowerInvariant();
            if (currentStatus is "revoked" or "overridden")
                return Results.BadRequest(new { error = "This qualification can no longer be responded to." });
            if (currentStatus is not ("earned" or "invited"))
                return Results.BadRequest(new { error = "This qualification is not awaiting a participant response." });

            if (qualification.team_id is Guid teamId)
            {
                var canRespond = await conn.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS (
                        SELECT 1
                        FROM teams t
                        WHERE t.id = @teamId
                          AND (
                                t.owner_id = @userId
                                OR EXISTS (
                                    SELECT 1
                                    FROM team_members tm
                                    WHERE tm.team_id = t.id
                                      AND tm.user_id = @userId
                                      AND tm.role = 'captain'
                                      AND tm.is_active = TRUE
                                )
                          )
                    )
                    """,
                    new { teamId, userId = userCtx.UserIdGuid },
                    tx);

                if (!canRespond)
                    return Results.Forbid();
            }
            else if (qualification.user_id is Guid userId)
            {
                if (userId != userCtx.UserIdGuid)
                    return Results.Forbid();
            }
            else
            {
                return Results.BadRequest(new { error = "Qualification record is missing a participant target." });
            }

            var updated = await conn.QuerySingleAsync<dynamic>(
                """
                UPDATE season_qualification_records
                SET status = @status,
                    participant_response_note = @participantResponseNote,
                    responded_at = NOW(),
                    responded_by_user_id = @respondedByUserId,
                    updated_at = NOW()
                WHERE id = @recordId AND season_id = @seasonId
                RETURNING id, qualification_type, status, participant_response_note, responded_at, responded_by_user_id, updated_at
                """,
                new
                {
                    status = normalizedStatus,
                    participantResponseNote = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
                    respondedByUserId = userCtx.UserIdGuid,
                    recordId,
                    seasonId = id
                },
                tx);

            tx.Commit();
            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/seasons/{id}/standings", async (
            Guid                  id,
            string?               region,
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            using var conn = db.CreateConnection();

            var access = await GetSeasonAccessAsync(conn, id, userCtx?.UserIdGuid);
            if (access is null) return Results.NotFound();
            if (!access.IsPublic && !access.CanManage) return Results.Forbid();

            if (string.Equals(access.ParticipantMode, "team", StringComparison.OrdinalIgnoreCase))
            {
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    WITH ranked AS (
                        SELECT spl.team_id AS entity_id,
                               t.name AS display_name,
                               t.logo_url,
                               COALESCE(SUM(spl.points), 0) AS total_points,
                               COUNT(*) AS ledger_entries
                        FROM season_points_ledger spl
                        JOIN teams t ON t.id = spl.team_id
                        WHERE spl.season_id = @seasonId
                          AND (@region IS NULL OR spl.region_key = @region)
                        GROUP BY spl.team_id, t.name, t.logo_url
                    )
                    SELECT entity_id,
                           display_name,
                           logo_url,
                           total_points,
                           ledger_entries,
                           RANK() OVER (ORDER BY total_points DESC, display_name ASC) AS rank
                    FROM ranked
                    ORDER BY rank, display_name
                    """,
                    new { seasonId = id, region });

                return Results.Ok(rows);
            }

            var soloRows = await conn.QueryAsync<dynamic>(
                """
                WITH ranked AS (
                    SELECT spl.user_id AS entity_id,
                           p.username AS display_name,
                           p.avatar_url,
                           COALESCE(SUM(spl.points), 0) AS total_points,
                           COUNT(*) AS ledger_entries
                    FROM season_points_ledger spl
                    JOIN profiles p ON p.id = spl.user_id
                    WHERE spl.season_id = @seasonId
                      AND (@region IS NULL OR spl.region_key = @region)
                    GROUP BY spl.user_id, p.username, p.avatar_url
                )
                SELECT entity_id,
                       display_name,
                       avatar_url,
                       total_points,
                       ledger_entries,
                       RANK() OVER (ORDER BY total_points DESC, display_name ASC) AS rank
                FROM ranked
                ORDER BY rank, display_name
                """,
                new { seasonId = id, region });

            return Results.Ok(soloRows);
        });

        app.MapGet("/api/seasons/{id}/qualifications", async (
            Guid                  id,
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            using var conn = db.CreateConnection();

            var access = await GetSeasonAccessAsync(conn, id, userCtx?.UserIdGuid);
            if (access is null) return Results.NotFound();
            var canViewAllQualifications = access.IsPublic || access.CanManage;
            var isPublicViewer = access.IsPublic && !access.CanManage;
            if (!canViewAllQualifications && userCtx is null) return Results.NotFound();

            if (!canViewAllQualifications)
            {
                var hasScopedQualificationAccess = string.Equals(access.ParticipantMode, "team", StringComparison.OrdinalIgnoreCase)
                    ? await conn.ExecuteScalarAsync<bool>(
                        """
                        SELECT EXISTS (
                            SELECT 1
                            FROM season_qualification_records sqr
                            JOIN teams visible_team ON visible_team.id = sqr.team_id
                            WHERE sqr.season_id = @seasonId
                              AND (
                                    visible_team.owner_id = @viewerUserId
                                    OR EXISTS (
                                        SELECT 1
                                        FROM team_members visible_member
                                        WHERE visible_member.team_id = visible_team.id
                                          AND visible_member.user_id = @viewerUserId
                                          AND visible_member.role = 'captain'
                                          AND visible_member.is_active = TRUE
                                    )
                              )
                        )
                        """,
                        new { seasonId = id, viewerUserId = userCtx!.UserIdGuid })
                    : await conn.ExecuteScalarAsync<bool>(
                        """
                        SELECT EXISTS (
                            SELECT 1
                            FROM season_qualification_records
                            WHERE season_id = @seasonId
                              AND user_id = @viewerUserId
                        )
                        """,
                        new { seasonId = id, viewerUserId = userCtx!.UserIdGuid });

                if (!hasScopedQualificationAccess)
                    return Results.NotFound();
            }

            if (string.Equals(access.ParticipantMode, "team", StringComparison.OrdinalIgnoreCase))
            {
                var notesColumn = access.CanManage
                    ? "sqr.notes"
                    : "NULL::text AS notes";
                var statusColumn = isPublicViewer
                    ? """
                      CASE
                          WHEN sqr.status IN ('accepted', 'confirmed', 'overridden') THEN 'confirmed'
                          WHEN sqr.status IN ('declined', 'revoked') THEN 'declined'
                          ELSE 'pending'
                      END AS status
                      """
                    : "sqr.status";
                var manualOverrideColumn = access.CanManage
                    ? "sqr.is_manual_override"
                    : "NULL::boolean AS is_manual_override";
                var responseColumns = (access.CanManage || !canViewAllQualifications)
                    ? """
                           sqr.participant_response_note,
                           sqr.responded_at,
                           sqr.responded_by_user_id,
                      """
                    : """
                           NULL::text AS participant_response_note,
                           NULL::timestamptz AS responded_at,
                           NULL::uuid AS responded_by_user_id,
                       """;
                var teamVisibilityFilter = canViewAllQualifications
                    ? string.Empty
                    : """
                      AND EXISTS (
                          SELECT 1
                          FROM teams visible_team
                          WHERE visible_team.id = sqr.team_id
                            AND (
                                  visible_team.owner_id = @viewerUserId
                                  OR EXISTS (
                                      SELECT 1
                                      FROM team_members visible_member
                                      WHERE visible_member.team_id = visible_team.id
                                        AND visible_member.user_id = @viewerUserId
                                        AND visible_member.role = 'captain'
                                        AND visible_member.is_active = TRUE
                                  )
                            )
                      )
                      """;

                var rows = await conn.QueryAsync<dynamic>(
                    $@"
                    SELECT sqr.id,
                           sqr.source_node_id,
                           src.name AS source_node_name,
                           sqr.destination_node_id,
                           dst.name AS destination_node_name,
                           sqr.source_tournament_id,
                           t.name AS source_tournament_name,
                           sqr.team_id AS entity_id,
                           tm.name AS display_name,
                           tm.logo_url,
                           sqr.placement,
                           sqr.points_snapshot,
                           sqr.qualification_type,
                           {statusColumn},
                           {manualOverrideColumn},
                           {notesColumn},
                           {responseColumns}
                           sqr.created_at,
                           sqr.updated_at
                    FROM season_qualification_records sqr
                    LEFT JOIN season_nodes src ON src.id = sqr.source_node_id
                    LEFT JOIN season_nodes dst ON dst.id = sqr.destination_node_id
                    LEFT JOIN tournaments t ON t.id = sqr.source_tournament_id
                    LEFT JOIN teams tm ON tm.id = sqr.team_id
                    WHERE sqr.season_id = @seasonId
                    {teamVisibilityFilter}
                    ORDER BY sqr.created_at DESC",
                    new
                    {
                        seasonId = id,
                        viewerUserId = userCtx?.UserIdGuid
                    });

                return Results.Ok(rows);
            }

            var soloNotesColumn = access.CanManage
                ? "sqr.notes"
                : "NULL::text AS notes";
            var soloStatusColumn = isPublicViewer
                ? """
                  CASE
                      WHEN sqr.status IN ('accepted', 'confirmed', 'overridden') THEN 'confirmed'
                      WHEN sqr.status IN ('declined', 'revoked') THEN 'declined'
                      ELSE 'pending'
                  END AS status
                  """
                : "sqr.status";
            var soloManualOverrideColumn = access.CanManage
                ? "sqr.is_manual_override"
                : "NULL::boolean AS is_manual_override";
            var soloResponseColumns = (access.CanManage || !canViewAllQualifications)
                ? """
                       sqr.participant_response_note,
                       sqr.responded_at,
                       sqr.responded_by_user_id,
                  """
                : """
                       NULL::text AS participant_response_note,
                       NULL::timestamptz AS responded_at,
                       NULL::uuid AS responded_by_user_id,
                  """;
            var soloVisibilityFilter = canViewAllQualifications
                ? string.Empty
                : "AND sqr.user_id = @viewerUserId";

            var soloRows = await conn.QueryAsync<dynamic>(
                $@"
                SELECT sqr.id,
                       sqr.source_node_id,
                       src.name AS source_node_name,
                       sqr.destination_node_id,
                       dst.name AS destination_node_name,
                       sqr.source_tournament_id,
                       t.name AS source_tournament_name,
                       sqr.user_id AS entity_id,
                       p.username AS display_name,
                       p.avatar_url,
                       sqr.placement,
                       sqr.points_snapshot,
                       sqr.qualification_type,
                       {soloStatusColumn},
                       {soloManualOverrideColumn},
                       {soloNotesColumn},
                       {soloResponseColumns}
                       sqr.created_at,
                       sqr.updated_at
                FROM season_qualification_records sqr
                LEFT JOIN season_nodes src ON src.id = sqr.source_node_id
                LEFT JOIN season_nodes dst ON dst.id = sqr.destination_node_id
                LEFT JOIN tournaments t ON t.id = sqr.source_tournament_id
                LEFT JOIN profiles p ON p.id = sqr.user_id
                WHERE sqr.season_id = @seasonId
                {soloVisibilityFilter}
                ORDER BY sqr.created_at DESC",
                new
                {
                    seasonId = id,
                    viewerUserId = userCtx?.UserIdGuid
                });

            return Results.Ok(soloRows);
        });
    }

    private static async Task<string> EnsureUniqueSeasonSlugAsync(
        IDbConnection conn,
        IDbTransaction tx,
        string input,
        Guid? excludeSeasonId)
    {
        var baseSlug = string.IsNullOrWhiteSpace(input) ? $"season-{Guid.NewGuid():N}"[..14] : Slugify(input);
        if (string.IsNullOrWhiteSpace(baseSlug))
            baseSlug = $"season-{Guid.NewGuid():N}"[..14];

        var slug = baseSlug;
        var suffix = 1;

        while (await conn.ExecuteScalarAsync<bool>(
                   "SELECT EXISTS(SELECT 1 FROM seasons WHERE slug = @slug AND (@excludeSeasonId IS NULL OR id != @excludeSeasonId))",
                   new { slug, excludeSeasonId },
                   tx))
        {
            suffix++;
            slug = $"{baseSlug}-{suffix}";
        }

        return slug;
    }

    private static async Task<HashSet<Guid>> GetSeasonManagerIdsAsync(
        IDbConnection conn,
        Guid seasonId,
        Guid ownerUserId,
        IDbTransaction tx)
    {
        var ids = (await conn.QueryAsync<Guid>(
            "SELECT user_id FROM season_staff WHERE season_id = @seasonId",
            new { seasonId },
            tx)).ToHashSet();
        ids.Add(ownerUserId);
        return ids;
    }

    private static async Task<SeasonAccessInfo?> GetSeasonAccessAsync(
        IDbConnection conn,
        Guid seasonId,
        Guid? userId,
        IDbTransaction? tx = null)
    {
        return await conn.QuerySingleOrDefaultAsync<SeasonAccessInfo>(
            """
            SELECT s.id,
                   s.owner_user_id AS OwnerUserId,
                   s.is_public AS IsPublic,
                   s.participant_mode AS ParticipantMode,
                   s.allow_manual_overrides AS AllowManualOverrides,
                   CASE
                     WHEN @userId IS NULL THEN FALSE
                     WHEN s.owner_user_id = @userId THEN TRUE
                     WHEN EXISTS (
                         SELECT 1 FROM season_staff ss
                         WHERE ss.season_id = s.id AND ss.user_id = @userId
                     ) THEN TRUE
                     ELSE FALSE
                   END AS CanManage
            FROM seasons s
            WHERE s.id = @seasonId
            """,
            new { seasonId, userId },
            tx);
    }

    private static string? ResolveSeasonRegionKey(
        string? explicitRegionKey,
        string? nodeRegion,
        string? nodeCity,
        string? nodeCountry)
    {
        if (!string.IsNullOrWhiteSpace(explicitRegionKey)) return explicitRegionKey.Trim();
        if (!string.IsNullOrWhiteSpace(nodeRegion)) return nodeRegion.Trim();
        if (!string.IsNullOrWhiteSpace(nodeCity)) return nodeCity.Trim();
        if (!string.IsNullOrWhiteSpace(nodeCountry)) return nodeCountry.Trim();
        return null;
    }

    private static async Task<List<SeasonPlacementResult>> GetSeasonPlacementResultsAsync(
        IDbConnectionFactory db,
        IDbConnection conn,
        IDbTransaction tx,
        string participantMode,
        SeasonSourceNode sourceNode,
        Guid? sourceStageId,
        CancellationToken ct)
    {
        if (sourceStageId.HasValue)
        {
            return await GetCompletedStagePlacementsAsync(
                db,
                conn,
                tx,
                participantMode,
                sourceStageId.Value,
                ct);
        }

        if (!sourceNode.LinkedTournamentId.HasValue)
            return [];

        return await GetCompletedTournamentPlacementsAsync(
            conn,
            tx,
            participantMode,
            sourceNode.LinkedTournamentId.Value);
    }

    private static async Task<List<SeasonPlacementResult>> GetCompletedTournamentPlacementsAsync(
        IDbConnection conn,
        IDbTransaction tx,
        string participantMode,
        Guid tournamentId)
    {
        if (string.Equals(participantMode, "team", StringComparison.OrdinalIgnoreCase))
        {
            var winnerTeamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT winner_id
                FROM tournaments
                WHERE id = @tournamentId
                  AND status = 'completed'
                  AND winner_id IS NOT NULL
                """,
                new { tournamentId },
                tx);

            return winnerTeamId.HasValue
                ? [new SeasonPlacementResult(winnerTeamId.Value, null, 1, tournamentId, null)]
                : [];
        }

        var winner = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT t.id AS source_tournament_id,
                   tp.user_id
            FROM tournaments t
            JOIN tournament_participants tp
              ON tp.tournament_id = t.id
             AND tp.team_id = t.winner_id
             AND tp.participant_type = 'solo'
             AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
            WHERE t.id = @tournamentId
              AND t.status = 'completed'
              AND t.winner_id IS NOT NULL
            LIMIT 1
            """,
            new { tournamentId },
            tx);

        if (winner is null)
            return [];

        return
        [
            new SeasonPlacementResult(
                null,
                (Guid)winner.user_id,
                1,
                (Guid)winner.source_tournament_id,
                null)
        ];
    }

    private static async Task<List<SeasonPlacementResult>> GetCompletedStagePlacementsAsync(
        IDbConnectionFactory db,
        IDbConnection conn,
        IDbTransaction tx,
        string participantMode,
        Guid stageId,
        CancellationToken ct)
    {
        var stage = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT id, tournament_id, status
            FROM tournament_stages
            WHERE id = @stageId
            """,
            new { stageId },
            tx);

        if (stage is null || !string.Equals((string)stage.status, "completed", StringComparison.OrdinalIgnoreCase))
            return [];

        var hasBrGroups = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM br_groups WHERE stage_id = @stageId)",
            new { stageId },
            tx);

        if (!hasBrGroups)
            return await GetCompletedBracketStagePlacementsAsync(db, conn, tx, participantMode, stageId, (Guid)stage.tournament_id, ct);

        if (string.Equals(participantMode, "team", StringComparison.OrdinalIgnoreCase))
        {
            var rows = await conn.QueryAsync<SeasonPlacementResult>(
                """
                SELECT rr.team_id AS TeamId,
                       NULL::uuid AS UserId,
                       ROW_NUMBER() OVER (
                           ORDER BY SUM(rr.total_points) DESC,
                                    COUNT(*) FILTER (WHERE rr.placement = 1) DESC,
                                    SUM(rr.kills) DESC,
                                    MIN(t.name) ASC
                       )::int AS Placement,
                       @sourceTournamentId AS SourceTournamentId,
                       @sourceStageId AS SourceStageId
                FROM br_round_results rr
                JOIN br_rounds r ON r.id = rr.round_id
                JOIN br_groups g ON g.id = r.group_id
                LEFT JOIN teams t ON t.id = rr.team_id
                WHERE g.stage_id = @stageId
                  AND r.status = 'completed'
                  AND rr.team_id IS NOT NULL
                GROUP BY rr.team_id
                ORDER BY Placement
                """,
                new
                {
                    stageId,
                    sourceTournamentId = (Guid)stage.tournament_id,
                    sourceStageId = stageId
                },
                tx);

            return rows.ToList();
        }

        if (!await ColumnExistsAsync(conn, "br_round_results", "participant_id", tx))
            return [];

        var soloRows = await conn.QueryAsync<SeasonPlacementResult>(
            """
            SELECT NULL::uuid AS TeamId,
                   tp.user_id AS UserId,
                   ROW_NUMBER() OVER (
                       ORDER BY SUM(rr.total_points) DESC,
                                COUNT(*) FILTER (WHERE rr.placement = 1) DESC,
                                SUM(rr.kills) DESC,
                                MIN(p.username) ASC
                   )::int AS Placement,
                   @sourceTournamentId AS SourceTournamentId,
                   @sourceStageId AS SourceStageId
            FROM br_round_results rr
            JOIN br_rounds r ON r.id = rr.round_id
            JOIN br_groups g ON g.id = r.group_id
            JOIN tournament_participants tp ON tp.id = rr.participant_id
            LEFT JOIN profiles p ON p.id = tp.user_id
            WHERE g.stage_id = @stageId
              AND r.status = 'completed'
              AND rr.participant_id IS NOT NULL
            GROUP BY tp.user_id
            ORDER BY Placement
            """,
            new
            {
                stageId,
                sourceTournamentId = (Guid)stage.tournament_id,
                sourceStageId = stageId
            },
            tx);

        return soloRows.ToList();
    }

    private static async Task<List<SeasonPlacementResult>> GetCompletedBracketStagePlacementsAsync(
        IDbConnectionFactory db,
        IDbConnection conn,
        IDbTransaction tx,
        string participantMode,
        Guid stageId,
        Guid tournamentId,
        CancellationToken ct)
    {
        var standingsService = new StandingsService(db);
        var standings = await standingsService.CalculateStandingsAsync(stageId, ct: ct);
        if (standings.Count == 0)
            return [];

        if (string.Equals(participantMode, "team", StringComparison.OrdinalIgnoreCase))
        {
            return standings
                .Select(standing => new SeasonPlacementResult(
                    standing.TeamId,
                    null,
                    standing.Rank,
                    tournamentId,
                    stageId))
                .ToList();
        }

        var teamIds = standings.Select(standing => standing.TeamId).Distinct().ToArray();
        var participantUsers = (await conn.QueryAsync<(Guid team_id, Guid user_id)>(
            """
            SELECT tp.team_id, tp.user_id
            FROM tournament_participants tp
            WHERE tp.tournament_id = @tournamentId
              AND tp.team_id = ANY(@teamIds)
              AND tp.participant_type = 'solo'
              AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
            """,
            new { tournamentId, teamIds },
            tx)).ToDictionary(x => x.team_id, x => x.user_id);

        return standings
            .Where(standing => participantUsers.ContainsKey(standing.TeamId))
            .Select(standing => new SeasonPlacementResult(
                null,
                participantUsers[standing.TeamId],
                standing.Rank,
                tournamentId,
                stageId))
            .ToList();
    }

    private static async Task<bool> ColumnExistsAsync(
        IDbConnection conn,
        string tableName,
        string columnName,
        IDbTransaction? tx = null)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                  AND column_name = @columnName
            )
            """,
            new { tableName, columnName },
            tx);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(object? value) => value switch
    {
        null => null,
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        _ => DateTimeOffset.TryParse(value.ToString(), out var parsed) ? parsed : null
    };

    private static List<object> BuildSeasonTree(List<SeasonTreeNodeView> nodes)
    {
        static string ParentKey(Guid? parentNodeId) => parentNodeId?.ToString() ?? "__root__";

        var childrenByParent = nodes
            .GroupBy(node => node.ParentNodeId)
            .ToDictionary(group => ParentKey(group.Key), group => group.OrderBy(node => node.DisplayOrder).ThenBy(node => node.Name).ToList());

        List<object> Build(Guid? parentNodeId)
        {
            if (!childrenByParent.TryGetValue(ParentKey(parentNodeId), out var children))
                return [];

            return children
                .Select(node => (object)new
                {
                    id = node.Id,
                    season_id = node.SeasonId,
                    parent_node_id = node.ParentNodeId,
                    name = node.Name,
                    slug = node.Slug,
                    node_type = node.NodeType,
                    display_order = node.DisplayOrder,
                    region = node.Region,
                    city = node.City,
                    country = node.Country,
                    linked_tournament_id = node.LinkedTournamentId,
                    linked_stage_id = node.LinkedStageId,
                    status = node.Status,
                    registration_deadline = node.RegistrationDeadline,
                    starts_at = node.StartsAt,
                    ends_at = node.EndsAt,
                    metadata = node.Metadata,
                    created_at = node.CreatedAt,
                    updated_at = node.UpdatedAt,
                    linked_tournament_name = node.LinkedTournamentName,
                    linked_stage_name = node.LinkedStageName,
                    children = Build(node.Id)
                })
                .ToList();
        }

        return Build(parentNodeId: null);
    }

    private static string Slugify(string input) =>
        System.Text.RegularExpressions.Regex.Replace(
            input.ToLowerInvariant().Trim(),
            @"[^a-z0-9]+",
            "-").Trim('-');

    private sealed record SeasonTreeNodeView(
        Guid Id,
        Guid? ParentNodeId,
        Guid SeasonId,
        string Name,
        string? Slug,
        string NodeType,
        int DisplayOrder,
        string? Region,
        string? City,
        string? Country,
        Guid? LinkedTournamentId,
        Guid? LinkedStageId,
        string Status,
        DateTimeOffset? RegistrationDeadline,
        DateTimeOffset? StartsAt,
        DateTimeOffset? EndsAt,
        object? Metadata,
        object? CreatedAt,
        object? UpdatedAt,
        string? LinkedTournamentName,
        string? LinkedStageName);

    private sealed record SeasonSourceNode(
        Guid Id,
        string Name,
        Guid? LinkedTournamentId,
        Guid? LinkedStageId,
        string? Region,
        string? City,
        string? Country);

    private sealed record SeasonPointsRuleRecord(
        Guid Id,
        Guid SourceNodeId,
        Guid? SourceStageId,
        Guid? DestinationNodeId,
        int PlacementFrom,
        int PlacementTo,
        int PointsAwarded,
        string? QualificationType,
        bool AutoCreateQualification,
        string? RegionKey);

    private sealed record SeasonPlacementResult(
        Guid? TeamId,
        Guid? UserId,
        int Placement,
        Guid? SourceTournamentId,
        Guid? SourceStageId);

    private sealed record GeneratedQualificationRecord(
        Guid SourceNodeId,
        Guid? SourceStageId,
        Guid? DestinationNodeId,
        Guid? SourceTournamentId,
        Guid? TeamId,
        Guid? UserId,
        int Placement,
        string QualificationType);

    private sealed record GeneratedQualificationKey(
        Guid SourceNodeId,
        Guid? SourceStageId,
        Guid? DestinationNodeId,
        Guid? TeamId,
        Guid? UserId,
        string QualificationType);

    private sealed record ExistingGeneratedQualificationState(
        Guid SourceNodeId,
        Guid? SourceStageId,
        Guid? DestinationNodeId,
        Guid? TeamId,
        Guid? UserId,
        string QualificationType,
        string Status,
        string? Notes,
        string? ParticipantResponseNote,
        DateTimeOffset? RespondedAt,
        Guid? RespondedByUserId);

    private sealed record ManualQualificationOverrideKey(
        Guid SourceNodeId,
        Guid? SourceStageId,
        Guid? TeamId,
        Guid? UserId);

    private sealed record SeasonAccessInfo(
        Guid Id,
        Guid OwnerUserId,
        bool IsPublic,
        string ParticipantMode,
        bool AllowManualOverrides,
        bool CanManage);
}

public sealed record CreateSeasonRequest(
    string Name,
    string Game,
    string ParticipantMode,
    string Status = "draft",
    string? Slug = null,
    string? Description = null,
    Guid? OrganizationId = null,
    bool IsPublic = false,
    bool AllowManualOverrides = true,
    DateTimeOffset? StartDate = null,
    DateTimeOffset? EndDate = null,
    string? RootNodeName = null,
    JsonElement? Settings = null);

public sealed record UpdateSeasonRequest(
    string? Name = null,
    string? Game = null,
    string? ParticipantMode = null,
    string? Status = null,
    string? Slug = null,
    string? Description = null,
    Guid? OrganizationId = null,
    bool? IsPublic = null,
    bool? AllowManualOverrides = null,
    DateTimeOffset? StartDate = null,
    DateTimeOffset? EndDate = null,
    JsonElement? Settings = null);

public sealed record SyncSeasonStaffRequest(SeasonStaffMemberDto[]? Staff);

public sealed record SeasonStaffMemberDto(Guid UserId, string Role);

public sealed record SyncSeasonNodesRequest(SeasonNodeDto[]? Nodes);

public sealed record SeasonNodeDto(
    Guid? Id,
    Guid? ParentNodeId,
    string Name,
    string NodeType = "custom",
    int DisplayOrder = 0,
    string Status = "draft",
    string? Slug = null,
    string? Region = null,
    string? City = null,
    string? Country = null,
    Guid? LinkedTournamentId = null,
    Guid? LinkedStageId = null,
    DateTimeOffset? RegistrationDeadline = null,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null,
    JsonElement? Metadata = null);

public sealed record SyncSeasonPointsRulesRequest(SeasonPointsRuleDto[]? Rules);

public sealed record SeasonPointsRuleDto(
    Guid? Id,
    Guid SourceNodeId,
    int PlacementFrom,
    int PlacementTo,
    int PointsAwarded,
    Guid? SourceStageId = null,
    Guid? DestinationNodeId = null,
    string? QualificationStatus = null,
    bool AutoCreateQualification = false,
    string? RegionKey = null);

public sealed record ReassignSeasonEntryLockRequest(
    Guid TargetNodeId,
    Guid? TeamId = null,
    Guid? UserId = null,
    string? Notes = null);

public sealed record UpdateSeasonQualificationRequest(
    string? Status = null,
    string? QualificationType = null,
    Guid? DestinationNodeId = null,
    string? Notes = null);

public sealed record RespondSeasonQualificationRequest(
    string Status,
    string? Notes = null);
