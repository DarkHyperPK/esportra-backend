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

    private static readonly HashSet<string> AllowedTournamentFormats = new(StringComparer.OrdinalIgnoreCase)
        { "single_elimination", "double_elimination", "round_robin", "swiss", "groups_playoffs", "battle_royale" };

    private static readonly HashSet<string> AllowedRegistrationTypes = new(StringComparer.OrdinalIgnoreCase)
        { "open", "invite", "qualifier_feed" };

    private static readonly HashSet<string> AllowedAdvancementRuleTypes = new(StringComparer.OrdinalIgnoreCase)
        { "top_n", "top_percentage", "points_threshold", "manual_selection" };

    private static readonly HashSet<string> AllowedAdvancementSeedModes = new(StringComparer.OrdinalIgnoreCase)
        { "preserve_seed", "reseed_by_points", "randomize", "manual" };

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
            bool?                  mine,
            IDbConnectionFactory   db,
            HttpContext            ctx,
            CancellationToken      ct) =>
        {
            var mineOnly = mine ?? false;
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (mineOnly && userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (mineOnly)
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

        // POST /api/seasons/draft (as per season plan)
        app.MapPost("/api/seasons/draft", async (
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

            // Force draft status for draft endpoint
            req.Status = "draft";

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
                    start_date, end_date, visibility, created_by, settings, created_at, updated_at
                )
                VALUES (
                    @id, @name, @slug, @description, @game, @participantMode, @status,
                    @ownerUserId, @organizationId, @isPublic, @allowManualOverrides,
                    @startDate, @endDate, 'private', @actorId, COALESCE(@settings::jsonb, '{}'::jsonb), NOW(), NOW()
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
                    actorId = userCtx.UserIdGuid,
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
                       sn.tournament_format,
                       sn.team_size,
                       sn.max_teams,
                       sn.min_teams,
                       sn.best_of,
                       sn.registration_type,
                       sn.entry_fee,
                       sn.prize_pool,
                       sn.check_in_minutes_before,
                       sn.registration_opens_at,
                       sn.published_tournament_id,
                       sn.created_at,
                       sn.updated_at,
                       t.name AS linked_tournament_name,
                       ts.name AS linked_stage_name,
                       pt.name AS published_tournament_name,
                       pt.slug AS published_tournament_slug
                FROM season_nodes sn
                LEFT JOIN tournaments t ON t.id = sn.linked_tournament_id
                LEFT JOIN tournament_stages ts ON ts.id = sn.linked_stage_id
                LEFT JOIN tournaments pt ON pt.id = sn.published_tournament_id
                WHERE sn.season_id = @id
                ORDER BY sn.display_order, sn.created_at
                """,
                new { id });

            var advancementConnections = await conn.QueryAsync<dynamic>(
                """
                SELECT sac.id,
                       sac.season_id,
                       sac.from_node_id,
                       sac.to_node_id,
                       sac.rule_type,
                       sac.rule_value,
                       sac.seed_mode,
                       sac.label,
                       sac.display_order,
                       sac.metadata,
                       sac.created_at,
                       sac.updated_at
                FROM season_advancement_connections sac
                WHERE sac.season_id = @id
                ORDER BY sac.from_node_id, sac.display_order, sac.created_at
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
                advancement_connections = advancementConnections,
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

            // ── Validate inline tournament config ranges (server-side
            // defense-in-depth; DB check constraints are the ultimate source
            // of truth but returning a nice BadRequest is better than a 500) ─
            foreach (var node in nodes)
            {
                if (node.TournamentFormat is not null && !AllowedTournamentFormats.Contains(node.TournamentFormat))
                    return Results.BadRequest(new { error = $"Invalid tournament format '{node.TournamentFormat}'." });
                if (node.RegistrationType is not null && !AllowedRegistrationTypes.Contains(node.RegistrationType))
                    return Results.BadRequest(new { error = $"Invalid registration type '{node.RegistrationType}'." });
                if (node.TeamSize is not null && (node.TeamSize < 1 || node.TeamSize > 20))
                    return Results.BadRequest(new { error = "team_size must be between 1 and 20." });
                if (node.MaxTeams is not null && (node.MaxTeams < 2 || node.MaxTeams > 4096))
                    return Results.BadRequest(new { error = "max_teams must be between 2 and 4096." });
                if (node.MinTeams is not null && node.MinTeams < 2)
                    return Results.BadRequest(new { error = "min_teams must be at least 2." });
                if (node.MinTeams is not null && node.MaxTeams is not null && node.MinTeams > node.MaxTeams)
                    return Results.BadRequest(new { error = "min_teams cannot exceed max_teams." });
                if (node.BestOf is not null && (node.BestOf < 1 || node.BestOf > 9))
                    return Results.BadRequest(new { error = "best_of must be between 1 and 9." });
                if (node.EntryFee is not null && node.EntryFee < 0)
                    return Results.BadRequest(new { error = "entry_fee cannot be negative." });
                if (node.PrizePool is not null && node.PrizePool < 0)
                    return Results.BadRequest(new { error = "prize_pool cannot be negative." });
                if (node.CheckInMinutesBefore is not null && node.CheckInMinutesBefore < 0)
                    return Results.BadRequest(new { error = "check_in_minutes_before cannot be negative." });
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
                            tournament_format = @tournamentFormat,
                            team_size = @teamSize,
                            max_teams = @maxTeams,
                            min_teams = @minTeams,
                            best_of = @bestOf,
                            registration_type = @registrationType,
                            entry_fee = @entryFee,
                            prize_pool = @prizePool,
                            check_in_minutes_before = @checkInMinutesBefore,
                            registration_opens_at = @registrationOpensAt,
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
                            metadata = node.Metadata?.GetRawText(),
                            tournamentFormat = node.TournamentFormat,
                            teamSize = node.TeamSize,
                            maxTeams = node.MaxTeams,
                            minTeams = node.MinTeams,
                            bestOf = node.BestOf,
                            registrationType = node.RegistrationType,
                            entryFee = node.EntryFee,
                            prizePool = node.PrizePool,
                            checkInMinutesBefore = node.CheckInMinutesBefore,
                            registrationOpensAt = node.RegistrationOpensAt
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
                            status, registration_deadline, starts_at, ends_at, metadata,
                            tournament_format, team_size, max_teams, min_teams, best_of,
                            registration_type, entry_fee, prize_pool,
                            check_in_minutes_before, registration_opens_at
                        )
                        VALUES (
                            @id, @seasonId, @parentNodeId, @name, @slug, @nodeType, @displayOrder,
                            @region, @city, @country, @linkedTournamentId, @linkedStageId,
                            @status, @registrationDeadline, @startsAt, @endsAt,
                            COALESCE(@metadata::jsonb, '{}'::jsonb),
                            @tournamentFormat, @teamSize, @maxTeams, @minTeams, @bestOf,
                            @registrationType, @entryFee, @prizePool,
                            @checkInMinutesBefore, @registrationOpensAt
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
                            metadata = node.Metadata?.GetRawText(),
                            tournamentFormat = node.TournamentFormat,
                            teamSize = node.TeamSize,
                            maxTeams = node.MaxTeams,
                            minTeams = node.MinTeams,
                            bestOf = node.BestOf,
                            registrationType = node.RegistrationType,
                            entryFee = node.EntryFee,
                            prizePool = node.PrizePool,
                            checkInMinutesBefore = node.CheckInMinutesBefore,
                            registrationOpensAt = node.RegistrationOpensAt
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

            // ── Replace advancement connections per node ───────────────────
            // Only nodes that explicitly set OutgoingAdvancement (non-null)
            // have their outgoing edges replaced. Nodes that leave it null
            // keep their existing edges untouched — this lets the caller
            // do partial updates.
            var nodesWithOutgoing = normalizedNodes
                .Where(n => n.Node.OutgoingAdvancement is not null)
                .ToArray();

            if (nodesWithOutgoing.Length > 0)
            {
                var fromNodeIds = nodesWithOutgoing.Select(n => n.ResolvedId).ToArray();

                await conn.ExecuteAsync(
                    """
                    DELETE FROM season_advancement_connections
                    WHERE season_id = @seasonId AND from_node_id = ANY(@fromNodeIds)
                    """,
                    new { seasonId = id, fromNodeIds },
                    tx);

                var liveNodeIds = incomingIds;

                foreach (var fromNode in nodesWithOutgoing)
                {
                    var outgoing = fromNode.Node.OutgoingAdvancement!;
                    if (outgoing.Length == 0) continue;

                    for (var i = 0; i < outgoing.Length; i++)
                    {
                        var conn2 = outgoing[i];
                        if (!liveNodeIds.Contains(conn2.ToNodeId))
                            return Results.BadRequest(new { error = "An advancement connection targets a node that is not part of this season." });
                        if (conn2.ToNodeId == fromNode.ResolvedId)
                            return Results.BadRequest(new { error = "An advancement connection cannot point a node to itself." });
                        if (!AllowedAdvancementRuleTypes.Contains(conn2.RuleType))
                            return Results.BadRequest(new { error = $"Invalid advancement rule type '{conn2.RuleType}'." });
                        if (!AllowedAdvancementSeedModes.Contains(conn2.SeedMode))
                            return Results.BadRequest(new { error = $"Invalid advancement seed mode '{conn2.SeedMode}'." });
                        if (conn2.RuleValue <= 0)
                            return Results.BadRequest(new { error = "advancement rule_value must be greater than zero." });

                        await conn.ExecuteAsync(
                            """
                            INSERT INTO season_advancement_connections (
                                id, season_id, from_node_id, to_node_id,
                                rule_type, rule_value, seed_mode, label, display_order, metadata
                            )
                            VALUES (
                                @id, @seasonId, @fromNodeId, @toNodeId,
                                @ruleType, @ruleValue, @seedMode, @label, @displayOrder,
                                COALESCE(@metadata::jsonb, '{}'::jsonb)
                            )
                            """,
                            new
                            {
                                id = conn2.Id ?? Guid.NewGuid(),
                                seasonId = id,
                                fromNodeId = fromNode.ResolvedId,
                                toNodeId = conn2.ToNodeId,
                                ruleType = conn2.RuleType.Trim().ToLowerInvariant(),
                                ruleValue = conn2.RuleValue,
                                seedMode = conn2.SeedMode.Trim().ToLowerInvariant(),
                                label = string.IsNullOrWhiteSpace(conn2.Label) ? null : conn2.Label.Trim(),
                                displayOrder = conn2.DisplayOrder == 0 ? i : conn2.DisplayOrder,
                                metadata = conn2.Metadata?.GetRawText()
                            },
                            tx);
                    }
                }
            }

            tx.Commit();
            return Results.Ok(new { success = true, count = nodes.Length });
        }).RequireAuthorization("Authenticated");

        app.MapPost("/api/seasons/{id}/publish", async (
            Guid                    id,
            [FromBody] PublishSeasonRequest req,
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

            if (string.Equals(access.Status, "published", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(access.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Season has already been published. Use the management workspace to edit tournaments directly." });
            }

            // Load all non-root nodes with their inline tournament config
            var nodes = await conn.QueryAsync<dynamic>(
                """
                SELECT sn.id,
                       sn.season_id,
                       sn.name,
                       sn.node_type,
                       sn.status,
                       sn.region,
                       sn.city,
                       sn.country,
                       sn.starts_at,
                       sn.ends_at,
                       sn.registration_deadline,
                       sn.tournament_format,
                       sn.team_size,
                       sn.max_teams,
                       sn.min_teams,
                       sn.best_of,
                       sn.registration_type,
                       sn.entry_fee,
                       sn.prize_pool,
                       sn.check_in_minutes_before,
                       sn.registration_opens_at,
                       sn.published_tournament_id
                FROM season_nodes sn
                WHERE sn.season_id = @seasonId
                  AND sn.node_type <> 'root'
                ORDER BY sn.display_order, sn.created_at
                """,
                new { seasonId = id },
                tx);

            var nodeIds = nodes.Select(n => (Guid)n.id).ToHashSet();
            var warnings = new List<string>();

            // Validate tournament config completeness unless AllowIncomplete is true
            if (!req.AllowIncomplete)
            {
                var incomplete = nodes.Where(n =>
                    string.IsNullOrWhiteSpace((string?)n.tournament_format) ||
                    n.team_size is null ||
                    n.max_teams is null ||
                    string.IsNullOrWhiteSpace((string?)n.registration_type)).ToList();

                if (incomplete.Count > 0)
                {
                    var names = incomplete.Select(n => (string)n.name).Take(3);
                    var suffix = incomplete.Count > 3 ? $" and {incomplete.Count - 3} others" : "";
                    return Results.BadRequest(new
                    {
                        error = $"One or more tournaments are missing required configuration (format, team size, max teams, registration type). Incomplete: {string.Join(", ", names)}{suffix}."
                    });
                }
            }

            // Load advancement connections to detect cycles
            var connections = await conn.QueryAsync<dynamic>(
                """
                SELECT sac.id,
                       sac.from_node_id,
                       sac.to_node_id,
                       sac.rule_type,
                       sac.rule_value,
                       sac.seed_mode
                FROM season_advancement_connections sac
                WHERE sac.season_id = @seasonId
                """,
                new { seasonId = id },
                tx);

            // Build adjacency map for cycle detection
            var adjacency = connections.ToDictionary(
                c => (Guid)c.from_node_id,
                c => ((Guid)c.to_node_id, (string)c.rule_type, (decimal)c.rule_value));

            // Detect cycles in the advancement graph (DFS with explicit color map)
            var colors = new Dictionary<Guid, int>(); // 0=unvisited, 1=visiting, 2=visited
            bool HasCycle(Guid nodeId)
            {
                if (!colors.TryGetValue(nodeId, out var color))
                    color = 0;

                if (color == 1) return true; // back edge → cycle
                if (color == 2) return false; // already fully processed

                colors[nodeId] = 1;
                if (adjacency.TryGetValue(nodeId, out var adj) && nodeIds.Contains(adj.Item1))
                {
                    if (HasCycle(adj.Item1))
                        return true;
                }
                colors[nodeId] = 2;
                return false;
            }

            foreach (var nodeId in nodeIds)
            {
                colors.TryGetValue(nodeId, out var color);
                if (color == 0 && HasCycle(nodeId))
                    return Results.BadRequest(new { error = "Advancement graph contains a cycle. Tournaments cannot advance to themselves or form a circular chain." });
            }

            // Identify terminal nodes (no outgoing connections)
            var fromNodeIds = connections.Select(c => (Guid)c.from_node_id).ToHashSet();
            var terminalNodeIds = nodeIds.Except(fromNodeIds).ToHashSet();

            // Warn if there are no terminals (unless it's a trivial single-node season)
            if (terminalNodeIds.Count == 0 && nodeIds.Count > 1)
            {
                warnings.Add("No terminal tournaments found (tournaments with no outgoing advancement). All tournaments have outgoing paths, which may indicate a missing finals node.");
            }

            // Materialise tournaments
            var publishedTournaments = new List<PublishedTournamentDto>();
            var tournamentsCreated = 0;
            var tournamentsLinked = 0;

            foreach (var node in nodes)
            {
                var nodeId = (Guid)node.id;
                var nodeName = (string)node.name;

                // If already published (e.g. from a partial publish attempt), skip
                if (node.published_tournament_id is Guid existingTournamentId)
                {
                    publishedTournaments.Add(new PublishedTournamentDto(nodeId, nodeName, existingTournamentId, "", false));
                    tournamentsLinked++;
                    continue;
                }

                // Require config for materialisation
                if (string.IsNullOrWhiteSpace((string?)node.tournament_format) ||
                    node.team_size is null ||
                    node.max_teams is null ||
                    string.IsNullOrWhiteSpace((string?)node.registration_type))
                {
                    warnings.Add($"Skipping tournament '{nodeName}' due to missing configuration. It will not be materialised.");
                    continue;
                }

                // Generate slug: season-slug-node-name-hex
                var seasonSlug = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT slug FROM seasons WHERE id = @seasonId",
                    new { seasonId = id },
                    tx) ?? "season";

                var baseSlug = $"{seasonSlug}-{Slugify(nodeName)}";
                var slug = baseSlug;
                var suffix = 0;
                while (await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM tournaments WHERE slug = @slug)",
                    new { slug },
                    tx))
                {
                    suffix++;
                    slug = $"{baseSlug}-{suffix}";
                }

                // Determine registration deadline from node or season
                var registrationDeadline = node.registration_deadline is DateTimeOffset rd
                    ? rd
                    : await conn.QuerySingleOrDefaultAsync<DateTimeOffset?>(
                        "SELECT start_date FROM seasons WHERE id = @seasonId",
                        new { seasonId = id },
                        tx);

                // Create the tournament
                var tournamentId = await conn.ExecuteScalarAsync<Guid>(
                    """
                    INSERT INTO tournaments (
                        name, description, slug, game, format, max_teams, min_teams, team_size,
                        entry_fee, prize_pool, start_date, end_date, registration_deadline,
                        status, organizer_id, region, is_public
                    )
                    VALUES (
                        @name, @description, @slug, @game, @format, @maxTeams, @minTeams, @teamSize,
                        @entryFee, @prizePool, @startDate, @endDate, @registrationDeadline,
                        @status::tournament_status, @organizerId, @region, @isPublic
                    )
                    RETURNING id
                    """,
                    new
                    {
                        name = nodeName,
                        description = $"Part of season: {seasonSlug}",
                        slug,
                        game = await conn.QuerySingleOrDefaultAsync<string>(
                            "SELECT game FROM seasons WHERE id = @seasonId",
                            new { seasonId = id },
                            tx) ?? "generic",
                        format = (string)node.tournament_format,
                        maxTeams = (int)node.max_teams,
                        minTeams = node.min_teams ?? 2,
                        teamSize = (int)node.team_size,
                        entryFee = node.entry_fee ?? 0m,
                        prizePool = node.prize_pool ?? 0m,
                        startDate = node.starts_at ?? registrationDeadline?.AddDays(-1),
                        endDate = node.ends_at,
                        registrationDeadline,
                        status = "draft",
                        organizerId = access.OwnerUserId,
                        region = node.region,
                        isPublic = false // tournaments remain private until explicitly opened
                    },
                    tx);

                // Create a default stage (single-stage tournament)
                await conn.ExecuteAsync(
                    """
                    INSERT INTO tournament_stages (
                        tournament_id, name, format, stage_order, best_of, capacity, advancement_count
                    )
                    VALUES (
                        @tournamentId, @name, @format, @stageOrder, @bestOf, @capacity, @advancementCount
                    )
                    """,
                    new
                    {
                        tournamentId,
                        name = nodeName,
                        format = (string)node.tournament_format,
                        stageOrder = 0,
                        bestOf = node.best_of ?? 1,
                        capacity = (int)node.max_teams,
                        advancementCount = 0 // will be determined by incoming connections
                    },
                    tx);

                // Link the node to the published tournament
                await conn.ExecuteAsync(
                    "UPDATE season_nodes SET published_tournament_id = @tournamentId WHERE id = @nodeId",
                    new { tournamentId, nodeId },
                    tx);

                publishedTournaments.Add(new PublishedTournamentDto(nodeId, nodeName, tournamentId, slug, true));
                tournamentsCreated++;
            }

            // Update season status
            var newSeasonStatus = req.Activate ? "active" : "published";
            await conn.ExecuteAsync(
                "UPDATE seasons SET status = @status, updated_at = NOW() WHERE id = @seasonId",
                new { seasonId = id, status = newSeasonStatus },
                tx);

            tx.Commit();

            return Results.Ok(new PublishSeasonResponse(
                Success: true,
                SeasonId: id,
                SeasonStatus: newSeasonStatus,
                TournamentsCreated: tournamentsCreated,
                TournamentsLinked: tournamentsLinked,
                ConnectionsWired: connections.Count(),
                Tournaments: publishedTournaments,
                Warnings: warnings
            ));
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/seasons/{id}/validate", async (
            Guid                    id,
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

            // Load all non-root nodes with their inline tournament config
            var nodes = await conn.QueryAsync<dynamic>(
                """
                SELECT sn.id,
                       sn.season_id,
                       sn.name,
                       sn.node_type,
                       sn.status,
                       sn.region,
                       sn.city,
                       sn.country,
                       sn.starts_at,
                       sn.ends_at,
                       sn.registration_deadline,
                       sn.tournament_format,
                       sn.team_size,
                       sn.max_teams,
                       sn.min_teams,
                       sn.best_of,
                       sn.registration_type,
                       sn.entry_fee,
                       sn.prize_pool,
                       sn.check_in_minutes_before,
                       sn.registration_opens_at,
                       sn.published_tournament_id
                FROM season_nodes sn
                WHERE sn.season_id = @seasonId
                  AND sn.node_type <> 'root'
                ORDER BY sn.display_order, sn.created_at
                """,
                new { seasonId = id },
                tx);

            var nodeIds = nodes.Select(n => (Guid)n.id).ToHashSet();
            var errors = new List<string>();
            var warnings = new List<string>();

            // Validate tournament config completeness
            var incomplete = nodes.Where(n =>
                string.IsNullOrWhiteSpace((string?)n.tournament_format) ||
                n.team_size is null ||
                n.max_teams is null ||
                string.IsNullOrWhiteSpace((string?)n.registration_type)).ToList();

            if (incomplete.Count > 0)
            {
                var names = incomplete.Select(n => (string)n.name).Take(3);
                var suffix = incomplete.Count > 3 ? $" and {incomplete.Count - 3} others" : "";
                errors.Add($"One or more tournaments are missing required configuration (format, team size, max teams, registration type). Incomplete: {string.Join(", ", names)}{suffix}.");
            }

            // Validate tournament config values
            foreach (var node in nodes)
            {
                var format = (string?)node.tournament_format;
                if (format is not null && !AllowedTournamentFormats.Contains(format))
                    errors.Add($"Node '{node.name}' has invalid tournament format '{format}'. Allowed: {string.Join(", ", AllowedTournamentFormats)}.");

                var regType = (string?)node.registration_type;
                if (regType is not null && !AllowedRegistrationTypes.Contains(regType))
                    errors.Add($"Node '{node.name}' has invalid registration type '{regType}'. Allowed: {string.Join(", ", AllowedRegistrationTypes)}.");

                if (node.team_size is not null && (int)node.team_size < 1)
                    errors.Add($"Node '{node.name}' has invalid team size '{node.team_size}'. Must be at least 1.");

                if (node.max_teams is not null && (int)node.max_teams < 1)
                    errors.Add($"Node '{node.name}' has invalid max teams '{node.max_teams}'. Must be at least 1.");

                if (node.min_teams is not null && (int)node.min_teams < 1)
                    errors.Add($"Node '{node.name}' has invalid min teams '{node.min_teams}'. Must be at least 1.");

                if (node.min_teams is not null && node.max_teams is not null && (int)node.min_teams > (int)node.max_teams)
                    errors.Add($"Node '{node.name}' has min_teams greater than max_teams.");

                if (node.best_of is not null && (int)node.best_of < 1)
                    errors.Add($"Node '{node.name}' has invalid best_of '{node.best_of}'. Must be at least 1.");

                if (node.entry_fee is not null && (decimal)node.entry_fee < 0)
                    errors.Add($"Node '{node.name}' has invalid entry_fee '{node.entry_fee}'. Must be non-negative.");

                if (node.prize_pool is not null && (decimal)node.prize_pool < 0)
                    errors.Add($"Node '{node.name}' has invalid prize_pool '{node.prize_pool}'. Must be non-negative.");
            }

            // Load advancement connections to detect cycles
            var connections = await conn.QueryAsync<dynamic>(
                """
                SELECT sac.id,
                       sac.from_node_id,
                       sac.to_node_id,
                       sac.rule_type,
                       sac.rule_value,
                       sac.seed_mode
                FROM season_advancement_connections sac
                WHERE sac.season_id = @seasonId
                """,
                new { seasonId = id },
                tx);

            // Validate advancement connection values
            foreach (var connItem in connections)
            {
                var ruleType = (string?)connItem.rule_type;
                if (ruleType is not null && !AllowedAdvancementRuleTypes.Contains(ruleType))
                    errors.Add($"Advancement connection has invalid rule_type '{ruleType}'. Allowed: {string.Join(", ", AllowedAdvancementRuleTypes)}.");

                var seedMode = (string?)connItem.seed_mode;
                if (seedMode is not null && !AllowedAdvancementSeedModes.Contains(seedMode))
                    errors.Add($"Advancement connection has invalid seed_mode '{seedMode}'. Allowed: {string.Join(", ", AllowedAdvancementSeedModes)}.");

                if (connItem.rule_value is not null && (decimal)connItem.rule_value < 0)
                    errors.Add($"Advancement connection has invalid rule_value '{connItem.rule_value}'. Must be non-negative.");

                var fromNodeId = (Guid)connItem.from_node_id;
                var toNodeId = (Guid)connItem.to_node_id;

                if (!nodeIds.Contains(fromNodeId))
                    errors.Add($"Advancement connection references non-existent from_node_id '{fromNodeId}'.");

                if (!nodeIds.Contains(toNodeId))
                    errors.Add($"Advancement connection references non-existent to_node_id '{toNodeId}'.");

                if (fromNodeId == toNodeId)
                    errors.Add($"Advancement connection from_node_id and to_node_id are the same ('{fromNodeId}'). Self-advancement is not allowed.");
            }

            // Build adjacency map for cycle detection
            var adjacency = connections.ToDictionary(
                c => (Guid)c.from_node_id,
                c => ((Guid)c.to_node_id, (string)c.rule_type, (decimal)c.rule_value));

            // Detect cycles in the advancement graph (DFS with explicit color map)
            var colors = new Dictionary<Guid, int>(); // 0=unvisited, 1=visiting, 2=visited
            bool HasCycle(Guid nodeId)
            {
                if (!colors.TryGetValue(nodeId, out var color))
                    color = 0;

                if (color == 1) return true; // back edge → cycle
                if (color == 2) return false; // already fully processed

                colors[nodeId] = 1;
                if (adjacency.TryGetValue(nodeId, out var adj) && nodeIds.Contains(adj.Item1))
                {
                    if (HasCycle(adj.Item1))
                        return true;
                }
                colors[nodeId] = 2;
                return false;
            }

            foreach (var nodeId in nodeIds)
            {
                colors.TryGetValue(nodeId, out var color);
                if (color == 0 && HasCycle(nodeId))
                    errors.Add("Advancement graph contains a cycle. Tournaments cannot advance to themselves or form a circular chain.");
            }

            // Identify terminal nodes (no outgoing connections)
            var fromNodeIds = connections.Select(c => (Guid)c.from_node_id).ToHashSet();
            var terminalNodeIds = nodeIds.Except(fromNodeIds).ToHashSet();

            // Warn if there are no terminals (unless it's a trivial single-node season)
            if (terminalNodeIds.Count == 0 && nodeIds.Count > 1)
            {
                warnings.Add("No terminal tournaments found (tournaments with no outgoing advancement). All tournaments have outgoing paths, which may indicate a missing finals node.");
            }

            // Warn if there are orphaned nodes (no incoming connections and not the first node)
            var toNodeIds = connections.Select(c => (Guid)c.to_node_id).ToHashSet();
            var orphanedNodeIds = nodeIds.Except(toNodeIds).Except(terminalNodeIds).ToHashSet();
            if (orphanedNodeIds.Count > 0)
            {
                var orphanedNames = nodes.Where(n => orphanedNodeIds.Contains((Guid)n.id)).Select(n => (string)n.name).Take(3);
                var suffix = orphanedNodeIds.Count > 3 ? $" and {orphanedNodeIds.Count - 3} others" : "";
                warnings.Add($"One or more tournaments have no incoming advancement paths: {string.Join(", ", orphanedNames)}{suffix}. These may be unreachable from qualifiers.");
            }

            tx.Commit();

            var isValid = errors.Count == 0;
            return Results.Ok(new
            {
                valid = isValid,
                season_id = id,
                node_count = nodeIds.Count,
                connection_count = connections.Count(),
                errors,
                warnings
            });
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
                   s.status AS Status,
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

    // ============================================================================
    // Additional Enterprise Season Endpoints
    // ============================================================================

    // POST /api/seasons/:id/archive
    app.MapPost("/api/seasons/{id}/archive", async (
        string id,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        // Verify ownership
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id AND owner_user_id = @userId",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isOwner) return Results.Forbid();

        await conn.ExecuteAsync(
            "UPDATE seasons SET status = 'archived', archived_at = NOW(), updated_at = NOW() WHERE id = @id AND status = 'completed'",
            new { id = seasonId }, cancellationToken: ct);

        return Results.Ok(new { success = true, status = "archived" });
    }).RequireAuthorization();

    // POST /api/seasons/:id/cancel
    app.MapPost("/api/seasons/{id}/cancel", async (
        string id,
        [FromBody] CancelSeasonRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        // Verify ownership
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id AND owner_user_id = @userId",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isOwner) return Results.Forbid();

        await conn.ExecuteAsync(
            "UPDATE seasons SET status = 'cancelled', cancelled_at = NOW(), updated_at = NOW() WHERE id = @id AND status IN ('draft', 'published', 'active')",
            new { id = seasonId }, cancellationToken: ct);

        return Results.Ok(new { success = true, status = "cancelled" });
    }).RequireAuthorization();

    // POST /api/seasons/:id/duplicate
    app.MapPost("/api/seasons/{id}/duplicate", async (
        string id,
        [FromBody] DuplicateSeasonRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var sourceSeasonId = Guid.Parse(id);

        // Verify ownership
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id AND owner_user_id = @userId",
            new { id = sourceSeasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isOwner) return Results.Forbid();

        // Check slug uniqueness
        var slugExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE slug = @slug AND deleted_at IS NULL",
            new { slug = req.NewSlug }, cancellationToken: ct);

        if (slugExists) return Results.BadRequest(new { error = "Slug already exists" });

        // Duplicate season (simplified - in production use SeasonService.DuplicateAsync)
        var newSeasonId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO seasons (id, name, slug, description, game, participant_mode, status, owner_user_id, organization_id, is_public, allow_manual_overrides, start_date, end_date, created_at, updated_at)
            SELECT @newId, @newName, @newSlug, description, game, participant_mode, 'draft', owner_user_id, organization_id, is_public, allow_manual_overrides, start_date, end_date, NOW(), NOW()
            FROM seasons WHERE id = @sourceId
            ", new { newId = newSeasonId, newName = req.NewName, newSlug = req.NewSlug, sourceId }, cancellationToken: ct);

        return Results.Ok(new { success = true, seasonId = newSeasonId });
    }).RequireAuthorization();

    // POST /api/seasons/:id/tournaments (add tournament to season)
    app.MapPost("/api/seasons/{id}/tournaments", async (
        string id,
        [FromBody] AddSeasonTournamentRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        // Verify ownership
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id AND owner_user_id = @userId",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isOwner) return Results.Forbid();

        var seasonTournamentId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO season_tournaments (id, season_id, tournament_id, role, region, display_name, sort_order, status, created_at, updated_at)
            VALUES (@id, @seasonId, @tournamentId, @role, @region, @displayName, @sortOrder, 'draft', NOW(), NOW())
            ", new { id = seasonTournamentId, seasonId, tournamentId = req.TournamentId, role = req.Role, region = req.Region, displayName = req.DisplayName, sortOrder = req.SortOrder }, cancellationToken: ct);

        return Results.Ok(new { success = true, seasonTournamentId });
    }).RequireAuthorization();

    // PUT /api/seasons/:id/tournaments/:seasonTournamentId
    app.MapPut("/api/seasons/{id}/tournaments/{seasonTournamentId}", async (
        string id,
        string seasonTournamentId,
        [FromBody] UpdateSeasonTournamentRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);
        var stId = Guid.Parse(seasonTournamentId);

        // Verify ownership
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id AND owner_user_id = @userId",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isOwner) return Results.Forbid();

        await conn.ExecuteAsync(@"
            UPDATE season_tournaments
            SET role = COALESCE(@role, role),
                region = @region,
                display_name = @displayName,
                updated_at = NOW()
            WHERE id = @id AND season_id = @seasonId
            ", new { id = stId, seasonId, role = req.Role, region = req.Region, displayName = req.DisplayName }, cancellationToken: ct);

        return Results.Ok(new { success = true });
    }).RequireAuthorization();

    // DELETE /api/seasons/:id/tournaments/:seasonTournamentId
    app.MapDelete("/api/seasons/{id}/tournaments/{seasonTournamentId}", async (
        string id,
        string seasonTournamentId,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);
        var stId = Guid.Parse(seasonTournamentId);

        // Verify ownership
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id AND owner_user_id = @userId",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isOwner) return Results.Forbid();

        await conn.ExecuteAsync(
            "DELETE FROM season_tournaments WHERE id = @id AND season_id = @seasonId",
            new { id = stId, seasonId }, cancellationToken: ct);

        return Results.Ok(new { success = true });
    }).RequireAuthorization();

    // PATCH /api/seasons/:id/tournaments/reorder
    app.MapPatch("/api/seasons/{id}/tournaments/reorder", async (
        string id,
        [FromBody] ReorderSeasonTournamentsRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        // Verify ownership
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id AND owner_user_id = @userId",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isOwner) return Results.Forbid();

        foreach (var (stId, sortOrder) in req.SortOrderUpdates)
        {
            await conn.ExecuteAsync(
                "UPDATE season_tournaments SET sort_order = @sortOrder, updated_at = NOW() WHERE id = @id AND season_id = @seasonId",
                new { id = stId, sortOrder, seasonId }, cancellationToken: ct);
        }

        return Results.Ok(new { success = true });
    }).RequireAuthorization();

    // POST /api/seasons/:id/tournaments/bulk-config
    app.MapPost("/api/seasons/{id}/tournaments/bulk-config", async (
        string id,
        [FromBody] BulkConfigSeasonTournamentsRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        // Verify ownership
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id AND owner_user_id = @userId",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isOwner) return Results.Forbid();

        foreach (var stId in req.SeasonTournamentIds)
        {
            var setClauses = new List<string>();
            var parameters = new Dictionary<string, object> { { "id", stId }, { "seasonId", seasonId } };

            if (req.ConfigUpdates.ContainsKey("region"))
            {
                setClauses.Add("region = @region");
                parameters["region"] = req.ConfigUpdates["region"];
            }

            if (req.ConfigUpdates.ContainsKey("displayName"))
            {
                setClauses.Add("display_name = @displayName");
                parameters["displayName"] = req.ConfigUpdates["displayName"];
            }

            if (setClauses.Count > 0)
            {
                var sql = $"""
                    UPDATE season_tournaments
                    SET {string.Join(", ", setClauses)}, updated_at = NOW()
                    WHERE id = @id AND season_id = @seasonId
                    """;
                await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
            }
        }

        return Results.Ok(new { success = true });
    }).RequireAuthorization();

    // GET /api/seasons/:id/advancement
    app.MapGet("/api/seasons/{id}/advancement", async (
        string id,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        // Verify access
        var hasAccess = await conn.ExecuteScalarAsync<bool>(
            @"SELECT 1 FROM seasons s
             WHERE s.id = @id AND (s.owner_user_id = @userId OR s.is_public = TRUE OR EXISTS (
                 SELECT 1 FROM season_staff ss WHERE ss.season_id = s.id AND ss.user_id = @userId
             ))",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!hasAccess) return Results.Forbid();

        var connections = await conn.QueryAsync(@"
            SELECT sac.*, fn.name as from_node_name, tn.name as to_node_name
            FROM season_advancement_connections sac
            JOIN season_nodes fn ON fn.id = sac.from_node_id
            JOIN season_nodes tn ON tn.id = sac.to_node_id
            WHERE sac.season_id = @seasonId
            ORDER BY sac.display_order ASC
            ", new { seasonId }, cancellationToken: ct);

        return Results.Ok(connections);
    }).RequireAuthorization();

    // POST /api/seasons/:id/advancement/process
    app.MapPost("/api/seasons/{id}/advancement/process", async (
        string id,
        [FromBody] ProcessAdvancementRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        // Verify ownership
        var isOwner = await conn.ExecuteScalarAsync<bool>(
            "SELECT 1 FROM seasons WHERE id = @id AND owner_user_id = @userId",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isOwner) return Results.Forbid();

        // Call AdvancementProcessor.ProcessTournamentCompletionAsync for the specified tournament
        var advancementProcessor = new AdvancementProcessor(db, new SeasonAuditService(db));
        await advancementProcessor.ProcessTournamentCompletionAsync(req.TournamentId, userCtx, ct);

        return Results.Ok(new { success = true, message = "Advancement processing completed" });
    }).RequireAuthorization();

    // POST /api/seasons/:id/advancement/validate
    app.MapPost("/api/seasons/{id}/advancement/validate", async (
        string id,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        // Verify access
        var hasAccess = await conn.ExecuteScalarAsync<bool>(
            @"SELECT 1 FROM seasons s
             WHERE s.id = @id AND (s.owner_user_id = @userId OR EXISTS (
                 SELECT 1 FROM season_staff ss WHERE ss.season_id = s.id AND ss.user_id = @userId
             ))",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!hasAccess) return Results.Forbid();

        // Get all nodes in the season
        var nodes = await conn.QueryAsync<dynamic>(
            "SELECT id, node_type FROM season_nodes WHERE season_id = @seasonId",
            new { seasonId }, cancellationToken: ct);

        var nodeIds = nodes.Select(n => (Guid)n.id).ToHashSet();

        // Get all advancement connections
        var connections = await conn.QueryAsync<dynamic>(
            "SELECT id, from_node_id, to_node_id, rule_type, rule_value FROM season_advancement_connections WHERE season_id = @seasonId",
            new { seasonId }, cancellationToken: ct);

        // Build adjacency map for cycle detection
        var adjacency = new Dictionary<Guid, Guid>();
        foreach (var conn in connections)
        {
            var from = (Guid)conn.from_node_id;
            var to = (Guid)conn.to_node_id;
            if (nodeIds.Contains(from) && nodeIds.Contains(to))
            {
                adjacency[from] = to;
            }
        }

        // Detect cycles using DFS
        var errors = new List<string>();
        var warnings = new List<string>();
        var colors = new Dictionary<Guid, int>();

        bool HasCycle(Guid nodeId)
        {
            if (!colors.TryGetValue(nodeId, out var color))
                color = 0;

            if (color == 1) return true;
            if (color == 2) return false;

            colors[nodeId] = 1;
            if (adjacency.TryGetValue(nodeId, out var toNode) && nodeIds.Contains(toNode))
            {
                if (HasCycle(toNode))
                    return true;
            }
            colors[nodeId] = 2;
            return false;
        }

        foreach (var nodeId in nodeIds)
        {
            if (HasCycle(nodeId))
            {
                errors.Add("Cycle detected in advancement graph");
                break;
            }
        }

        // Check for terminal nodes
        var hasTerminal = nodes.Any(n => n.node_type == "final" || n.node_type == "playoff");
        if (!hasTerminal)
        {
            warnings.Add("No terminal tournament (final or playoff) found");
        }

        return Results.Ok(new { isValid = errors.Count == 0, errors, warnings });
    }).RequireAuthorization();

    // POST /api/seasons/:id/advancement/manual-override
    app.MapPost("/api/seasons/{id}/advancement/manual-override", async (
        string id,
        [FromBody] ManualAdvancementOverrideRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        // Verify ownership and staff role (admin required for manual override)
        var isAdmin = await conn.ExecuteScalarAsync<bool>(
            @"SELECT 1 FROM seasons s
             JOIN season_staff ss ON ss.season_id = s.id
             WHERE s.id = @id AND ss.user_id = @userId AND ss.role = 'admin'",
            new { id = seasonId, userId = userCtx.UserId }, cancellationToken: ct);

        if (!isAdmin) return Results.Forbid();

        // Call AdvancementProcessor.ManualOverrideAsync
        var advancementProcessor = new AdvancementProcessor(db, new SeasonAuditService(db));
        await advancementProcessor.ManualOverrideAsync(
            seasonId,
            req.ConnectionId,
            req.TeamId,
            req.TargetSeed,
            req.Reason,
            userCtx,
            ct);

        return Results.Ok(new { success = true, message = "Manual override applied" });
    }).RequireAuthorization();

    // GET /api/public/seasons
    app.MapGet("/api/public/seasons", async (
        int? limit,
        int? offset,
        string? game,
        IDbConnectionFactory db,
        CancellationToken ct) =>
    {
        using var conn = db.CreateConnection();
        limit ??= 50;
        offset ??= 0;

        var sql = @"
            SELECT s.*, p.username as owner_username, p.full_name as owner_full_name
            FROM seasons s
            JOIN profiles p ON p.id = s.owner_user_id
            WHERE s.is_public = TRUE AND s.status = 'published' AND s.deleted_at IS NULL
            ";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrEmpty(game))
        {
            sql += " AND s.game = @game";
            parameters.Add("game", game);
        }

        sql += " ORDER BY s.published_at DESC LIMIT @limit OFFSET @offset";
        parameters.Add("limit", limit);
        parameters.Add("offset", offset);

        var seasons = await conn.QueryAsync(sql, parameters, cancellationToken: ct);
        return Results.Ok(seasons);
    });

    // GET /api/public/seasons/:slug
    app.MapGet("/api/public/seasons/{slug}", async (
        string slug,
        IDbConnectionFactory db,
        CancellationToken ct) =>
    {
        using var conn = db.CreateConnection();

        var season = await conn.QuerySingleOrDefaultAsync<dynamic>(
            @"SELECT s.*, p.username as owner_username, p.full_name as owner_full_name
             FROM seasons s
             JOIN profiles p ON p.id = s.owner_user_id
             WHERE s.slug = @slug AND s.is_public = TRUE AND s.deleted_at IS NULL",
            new { slug }, cancellationToken: ct);

        if (season == null) return Results.NotFound();

        return Results.Ok(season);
    });

    // GET /api/public/seasons/:id/tournaments
    app.MapGet("/api/public/seasons/{id}/tournaments", async (
        string id,
        IDbConnectionFactory db,
        CancellationToken ct) =>
    {
        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        var tournaments = await conn.QueryAsync(@"
            SELECT st.*, t.name as tournament_name, t.slug as tournament_slug, t.status as tournament_status
            FROM season_tournaments st
            JOIN tournaments t ON t.id = st.tournament_id
            WHERE st.season_id = @seasonId
            ORDER BY st.sort_order ASC
            ", new { seasonId }, cancellationToken: ct);

        return Results.Ok(tournaments);
    });

    // GET /api/public/seasons/:id/standings
    app.MapGet("/api/public/seasons/{id}/standings", async (
        string id,
        int? limit,
        int? offset,
        IDbConnectionFactory db,
        CancellationToken ct) =>
    {
        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);
        limit ??= 100;
        offset ??= 0;

        var standings = await conn.QueryAsync(@"
            SELECT ss.*, t.name as team_name, t.slug as team_slug
            FROM season_standings ss
            JOIN teams t ON t.id = ss.team_id
            WHERE ss.season_id = @seasonId
            ORDER BY ss.total_points DESC, ss.best_finish ASC
            LIMIT @limit OFFSET @offset
            ", new { seasonId, limit, offset }, cancellationToken: ct);

        return Results.Ok(standings);
    });

    // GET /api/public/seasons/:id/team/:teamId/path
    app.MapGet("/api/public/seasons/{id}/team/{teamId}/path", async (
        string id,
        string teamId,
        IDbConnectionFactory db,
        CancellationToken ct) =>
    {
        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);
        var teamGuid = Guid.Parse(teamId);

        // Get all advancement records for this team in this season
        var path = await conn.QueryAsync(@"
            SELECT sar.*, t.name as tournament_name, t.slug as tournament_slug, tt.role as tournament_role
            FROM season_advancement_records sar
            JOIN tournaments t ON t.id = sar.to_tournament_id
            JOIN season_tournaments tt ON tt.tournament_id = t.id
            WHERE sar.season_id = @seasonId AND sar.team_id = @teamId
            ORDER BY sar.created_at ASC
            ", new { seasonId, teamId = teamGuid }, cancellationToken: ct);

        return Results.Ok(path);
    });

    // GET /api/admin/seasons
    app.MapGet("/api/admin/seasons", async (
        int? limit,
        int? offset,
        string? game,
        string? status,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        // Verify admin role
        var isAdmin = await db.QuerySingleOrDefaultAsync<bool>(
            "SELECT is_admin FROM profiles WHERE id = @id",
            new { id = userCtx.UserId }, cancellationToken: ct);

        if (!isAdmin) return Results.Forbid();

        using var conn = db.CreateConnection();
        limit ??= 100;
        offset ??= 0;

        var sql = @"
            SELECT s.*, p.username as owner_username, o.name as organization_name
            FROM seasons s
            LEFT JOIN profiles p ON p.id = s.owner_user_id
            LEFT JOIN organizations o ON o.id = s.organization_id
            WHERE s.deleted_at IS NULL
            ";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrEmpty(game))
        {
            sql += " AND s.game = @game";
            parameters.Add("game", game);
        }

        if (!string.IsNullOrEmpty(status))
        {
            sql += " AND s.status = @status";
            parameters.Add("status", status);
        }

        sql += " ORDER BY s.created_at DESC LIMIT @limit OFFSET @offset";
        parameters.Add("limit", limit);
        parameters.Add("offset", offset);

        var seasons = await conn.QueryAsync(sql, parameters, cancellationToken: ct);
        return Results.Ok(seasons);
    }).RequireAuthorization();

    // GET /api/admin/seasons/:id/audit
    app.MapGet("/api/admin/seasons/{id}/audit", async (
        string id,
        string? action,
        DateTime? fromDate,
        DateTime? toDate,
        int? limit,
        int? offset,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        // Verify admin role
        var isAdmin = await db.QuerySingleOrDefaultAsync<bool>(
            "SELECT is_admin FROM profiles WHERE id = @id",
            new { id = userCtx.UserId }, cancellationToken: ct);

        if (!isAdmin) return Results.Forbid();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);
        limit ??= 100;
        offset ??= 0;

        var sql = @"
            SELECT sal.*, p.username as actor_username
            FROM season_audit_logs sal
            JOIN profiles p ON p.id = sal.actor_id
            WHERE sal.season_id = @seasonId
            ";
        var parameters = new DynamicParameters { { "seasonId", seasonId } };

        if (!string.IsNullOrEmpty(action))
        {
            sql += " AND sal.action = @action";
            parameters.Add("action", action);
        }

        if (fromDate.HasValue)
        {
            sql += " AND sal.created_at >= @fromDate";
            parameters.Add("fromDate", fromDate.Value);
        }

        if (toDate.HasValue)
        {
            sql += " AND sal.created_at <= @toDate";
            parameters.Add("toDate", toDate.Value);
        }

        sql += " ORDER BY sal.created_at DESC LIMIT @limit OFFSET @offset";
        parameters.Add("limit", limit);
        parameters.Add("offset", offset);

        var logs = await conn.QueryAsync(sql, parameters, cancellationToken: ct);
        return Results.Ok(logs);
    }).RequireAuthorization();

    // GET /api/admin/seasons/:id
    app.MapGet("/api/admin/seasons/{id}", async (
        string id,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        // Verify admin role
        var isAdmin = await db.QuerySingleOrDefaultAsync<bool>(
            "SELECT is_admin FROM profiles WHERE id = @id",
            new { id = userCtx.UserId }, cancellationToken: ct);

        if (!isAdmin) return Results.Forbid();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        var season = await conn.QuerySingleOrDefaultAsync<dynamic>(
            @"SELECT s.*, p.username as owner_username, p.full_name as owner_full_name, o.name as organization_name
             FROM seasons s
             LEFT JOIN profiles p ON p.id = s.owner_user_id
             LEFT JOIN organizations o ON o.id = s.organization_id
             WHERE s.id = @id AND s.deleted_at IS NULL",
            new { id = seasonId }, cancellationToken: ct);

        if (season == null) return Results.NotFound();

        return Results.Ok(season);
    }).RequireAuthorization();

    // PATCH /api/admin/seasons/:id/status
    app.MapPatch("/api/admin/seasons/{id}/status", async (
        string id,
        [FromBody] AdminForceStatusRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        // Verify admin role
        var isAdmin = await db.QuerySingleOrDefaultAsync<bool>(
            "SELECT is_admin FROM profiles WHERE id = @id",
            new { id = userCtx.UserId }, cancellationToken: ct);

        if (!isAdmin) return Results.Forbid();

        using var conn = db.CreateConnection();
        var seasonId = Guid.Parse(id);

        await conn.ExecuteAsync(
            "UPDATE seasons SET status = @status, updated_at = NOW() WHERE id = @id",
            new { id = seasonId, status = req.Status }, cancellationToken: ct);

        return Results.Ok(new { success = true, status = req.Status });
    }).RequireAuthorization();

    // POST /api/admin/seasons/:id/force-advancement
    app.MapPost("/api/admin/seasons/{id}/force-advancement", async (
        string id,
        [FromBody] AdminForceAdvancementRequest req,
        IDbConnectionFactory db,
        HttpContext ctx,
        CancellationToken ct) =>
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        // Verify admin role
        var isAdmin = await db.QuerySingleOrDefaultAsync<bool>(
            "SELECT is_admin FROM profiles WHERE id = @id",
            new { id = userCtx.UserId }, cancellationToken: ct);

        if (!isAdmin) return Results.Forbid();

        var seasonId = Guid.Parse(id);

        // Call AdvancementProcessor.ManualOverrideAsync for admin force override
        var advancementProcessor = new AdvancementProcessor(db, new SeasonAuditService(db));
        await advancementProcessor.ManualOverrideAsync(
            seasonId,
            req.ConnectionId,
            req.TeamId,
            req.TargetSeed,
            req.Reason,
            userCtx,
            ct);

        return Results.Ok(new { success = true, message = "Force advancement applied" });
    }).RequireAuthorization();
}

// Request DTOs
public record CancelSeasonRequest(string Reason);
public record DuplicateSeasonRequest(string NewName, string NewSlug);
public record AddSeasonTournamentRequest(Guid TournamentId, string Role, string? Region, string? DisplayName, int SortOrder);
public record UpdateSeasonTournamentRequest(string? Role, string? Region, string? DisplayName);
public record ReorderSeasonTournamentsRequest(Dictionary<Guid, int> SortOrderUpdates);
public record BulkConfigSeasonTournamentsRequest(List<Guid> SeasonTournamentIds, Dictionary<string, object> ConfigUpdates);
public record ProcessAdvancementRequest(Guid TournamentId);
public record ManualAdvancementOverrideRequest(Guid ConnectionId, Guid TeamId, int TargetSeed, string Reason);
public record AdminForceStatusRequest(string Status);
public record AdminForceAdvancementRequest(Guid ConnectionId, Guid TeamId, int TargetSeed, string Reason);

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
        string Status,
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
    JsonElement? Metadata = null,
    // Inline tournament configuration persisted as first-class columns on
    // season_nodes. Every non-root node represents a real tournament that will
    // be materialised on publish. All fields are optional until the season is
    // actually published; the /publish endpoint enforces completeness.
    string? TournamentFormat = null,
    int? TeamSize = null,
    int? MaxTeams = null,
    int? MinTeams = null,
    int? BestOf = null,
    string? RegistrationType = null,
    decimal? EntryFee = null,
    decimal? PrizePool = null,
    int? CheckInMinutesBefore = null,
    DateTimeOffset? RegistrationOpensAt = null,
    Guid? PublishedTournamentId = null,
    // Outgoing advancement connections (this node → some other node). The
    // sync endpoint replaces the full set of outgoing edges for this node
    // with whatever is supplied here. Null means "leave existing edges
    // alone"; an empty array means "remove all outgoing edges".
    SeasonAdvancementConnectionDto[]? OutgoingAdvancement = null);

public sealed record SeasonAdvancementConnectionDto(
    Guid? Id,
    Guid ToNodeId,
    string RuleType,
    decimal RuleValue,
    string SeedMode = "preserve_seed",
    string? Label = null,
    int DisplayOrder = 0,
    JsonElement? Metadata = null);

// PublishSeasonRequest:
//  - Activate: when true, activate the season (status = 'active') after
//    materialising tournaments. When false (default), leave it in 'published'.
//  - AllowIncomplete: when true, skip validation that rejects missing
//    tournament configuration on non-root nodes. Useful for partial drafts
//    that intentionally leave some tournaments blank.
public sealed record PublishSeasonRequest(
    bool Activate = false,
    bool AllowIncomplete = false);

public sealed record PublishSeasonResponse(
    bool Success,
    Guid SeasonId,
    string SeasonStatus,
    int TournamentsCreated,
    int TournamentsLinked,
    int ConnectionsWired,
    List<PublishedTournamentDto> Tournaments,
    List<string> Warnings);

public sealed record PublishedTournamentDto(
    Guid NodeId,
    string NodeName,
    Guid TournamentId,
    string TournamentSlug,
    bool WasCreated);

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
