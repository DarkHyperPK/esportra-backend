using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Br;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Esportra.Api.Endpoints;

public static partial class BRGroupEndpoints
{
    private sealed record BrEntityAccess(Guid? TeamId, Guid? ParticipantId);
    private const string StageRoundsLockedMessage = "This stage already has rounds. Reset or recreate the stage before reseeding participants.";

    static partial void MapBrConfigRoutes(WebApplication app);

    public static void MapBRGroupEndpoints(this WebApplication app)
    {
        MapBrConfigRoutes(app);

        // ── GET /api/stages/{stageId}/br/groups ─────────────────────────────
        // List all groups for a BR stage with team counts. Public endpoint.
        app.MapGet("/api/stages/{stageId}/br/groups", async (
            Guid              stageId,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewStagePublicDataAsync(conn, ctx, stageId))
                return Results.NotFound();

            var groups = await conn.QueryAsync<dynamic>(
                """
                SELECT g.id, g.name, g.group_order, g.lobby_size, g.created_at,
                       (SELECT COUNT(*) FROM br_group_teams gt WHERE gt.group_id = g.id) AS team_count
                FROM br_groups g
                WHERE g.stage_id = @stageId
                ORDER BY g.group_order
                """,
                new { stageId });

            return Results.Ok(groups);
        });

        // ── GET /api/stages/{stageId}/br/groups/detail ──────────────────────
        // Organizer endpoint: returns groups + has_rounds, and optionally all
        // group rosters when includeTeams=true.
        // Replaces N×2 parallel fetches from the organizer UI group section.
        app.MapGet("/api/stages/{stageId}/br/groups/detail", async (
            Guid              stageId,
            [FromQuery] bool? includeTeams,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Auth: must be stage staff or platform admin
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermTeamsManage);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var shouldIncludeTeams = includeTeams ?? true;

            const string groupsSql = """
                SELECT g.id, g.name, g.group_order, g.lobby_size, g.created_at,
                       (SELECT COUNT(*) FROM br_group_teams gt2 WHERE gt2.group_id = g.id) AS team_count
                FROM br_groups g
                WHERE g.stage_id = @stageId
                ORDER BY g.group_order;
                """;

            const string hasRoundsSql = """
                SELECT EXISTS(
                    SELECT 1 FROM br_lobbies r
                    JOIN br_lobby_groups lg ON lg.lobby_id = r.id
                    JOIN br_groups g ON g.id = lg.group_id
                    WHERE g.stage_id = @stageId
                ) AS has_rounds;
                """;

            const string teamsSql = """
                SELECT
                    g.id AS group_id,
                    COALESCE(gt.team_id, gt.participant_id) AS team_id,
                    gt.seed_order,
                    gt.assigned_at,
                    CASE
                        WHEN gt.team_id IS NOT NULL THEN t.name
                        ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                    END AS team_name,
                    CASE WHEN gt.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url
                FROM br_groups g
                JOIN br_group_teams gt ON gt.group_id = g.id
                LEFT JOIN teams t ON t.id = gt.team_id
                LEFT JOIN tournament_participants tp ON tp.id = gt.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE g.stage_id = @stageId
                ORDER BY g.group_order, gt.seed_order;
                """;

            var sql = shouldIncludeTeams
                ? $"{groupsSql}\n{hasRoundsSql}\n{teamsSql}"
                : $"{groupsSql}\n{hasRoundsSql}";

            using var multi = await conn.QueryMultipleAsync(sql, new { stageId });

            var groups    = (await multi.ReadAsync<dynamic>()).ToList();
            var hasRounds = await multi.ReadSingleAsync<bool>();

            var teamsByGroup = new Dictionary<string, List<object>>();
            if (shouldIncludeTeams)
            {
                var rows = (await multi.ReadAsync<dynamic>()).ToList();
                foreach (var row in rows)
                {
                    var gid = ((Guid)row.group_id).ToString();
                    if (!teamsByGroup.TryGetValue(gid, out var list))
                    {
                        list = [];
                        teamsByGroup[gid] = list;
                    }

                    list.Add(new
                    {
                        team_id    = (Guid)row.team_id,
                        team_name  = (string?)row.team_name,
                        logo_url   = (string?)row.logo_url,
                        seed_order = Convert.ToInt32(row.seed_order),
                        assigned_at= (DateTime?)row.assigned_at,
                    });
                }
            }

            return Results.Ok(new { groups, has_rounds = hasRounds, teams_by_group = teamsByGroup });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/stages/{stageId}/br/groups ────────────────────────────
        // Create groups for a stage. Deletes existing groups first (fresh setup).
        app.MapPost("/api/stages/{stageId}/br/groups", async (
            Guid                stageId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermTeamsManage);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var groupCount = body.TryGetProperty("groupCount", out var gc) && gc.TryGetInt32(out var gcv) ? gcv : 0;
            var lobbySize  = body.TryGetProperty("lobbySize", out var ls) && ls.TryGetInt32(out var lsv) ? lsv : 20;
            var force      = body.TryGetProperty("force", out var fp) && fp.ValueKind == JsonValueKind.True;

            if (groupCount <= 0 || groupCount > 128)
                return Results.BadRequest(new { error = "groupCount must be between 1 and 128." });
            if (lobbySize <= 0 || lobbySize > 150)
                return Results.BadRequest(new { error = "lobbySize must be between 1 and 150." });

            using var tx = conn.BeginTransaction();
            try
            {
                if (!await LockStageAsync(conn, tx, stageId))
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Stage not found." });
                }

                await LockStageGroupsAsync(conn, tx, stageId);

                if (await StageHasAnyRoundsAsync(conn, stageId, tx) && !force)
                {
                    tx.Rollback();
                    return Results.Conflict(new { error = StageRoundsLockedMessage });
                }

                // Delete existing groups (FK CASCADE clears teams/rounds/results)
                await conn.ExecuteAsync(
                    "DELETE FROM br_groups WHERE stage_id = @stageId",
                    new { stageId }, tx);

                var created = new List<object>();

                for (var i = 0; i < groupCount; i++)
                {
                    var name = GenerateGroupName(i);

                    var group = await conn.QuerySingleAsync<dynamic>(
                        """
                        INSERT INTO br_groups (stage_id, name, group_order, lobby_size)
                        VALUES (@stageId, @name, @groupOrder, @lobbySize)
                        RETURNING id, name, group_order, lobby_size, created_at
                        """,
                        new { stageId, name, groupOrder = i + 1, lobbySize }, tx);

                    created.Add(group);
                }

                tx.Commit();
                return Results.Ok(created);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/stages/{stageId}/br/groups/{groupId} ────────────────
        // Delete a single group. FK CASCADE handles teams/rounds/results.
        app.MapDelete("/api/stages/{stageId}/br/groups/{groupId}", async (
            Guid              stageId,
            Guid              groupId,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermTeamsManage);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            // Verify groupId belongs to this stageId
            var exists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM br_groups WHERE id = @groupId AND stage_id = @stageId)",
                new { groupId, stageId });
            if (!exists)
                return Results.NotFound(new { error = "Group not found in this stage." });

            using var tx = conn.BeginTransaction();
            try
            {
                if (!await LockStageAsync(conn, tx, stageId))
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Stage not found." });
                }

                var lockedGroupIds = await LockStageGroupsAsync(conn, tx, stageId);
                if (!lockedGroupIds.Contains(groupId))
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Group not found in this stage." });
                }

                if (await StageHasAnyRoundsAsync(conn, stageId, tx))
                {
                    tx.Rollback();
                    return Results.Conflict(new { error = StageRoundsLockedMessage });
                }

                await conn.ExecuteAsync(
                    "DELETE FROM br_groups WHERE id = @groupId",
                    new { groupId },
                    tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return Results.Ok(new { deleted = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/stages/{stageId}/br/groups/{groupId}/teams ─────────────
        // List teams/participants in a group with details.
        app.MapGet("/api/stages/{stageId}/br/groups/{groupId}/teams", async (
            Guid              stageId,
            Guid              groupId,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify groupId belongs to this stageId
            var groupExists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM br_groups WHERE id = @groupId AND stage_id = @stageId)",
                new { groupId, stageId });
            if (!groupExists)
                return Results.NotFound(new { error = "Group not found in this stage." });

            // Unified query supporting both teams and solo participants
            var teams = await conn.QueryAsync<dynamic>(
                """
                SELECT
                    COALESCE(gt.team_id, gt.participant_id) AS team_id,
                    gt.seed_order,
                    gt.assigned_at,
                    CASE
                        WHEN gt.team_id IS NOT NULL THEN t.name
                        ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                    END AS team_name,
                    CASE WHEN gt.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url
                FROM br_group_teams gt
                LEFT JOIN teams t ON t.id = gt.team_id
                LEFT JOIN tournament_participants tp ON tp.id = gt.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE gt.group_id = @groupId
                ORDER BY gt.seed_order
                """,
                new { groupId });

            return Results.Ok(teams);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/stages/{stageId}/br/groups/{groupId}/participants ───────
        // Public lightweight roster for player-facing "view your group" screens.
        app.MapGet("/api/stages/{stageId}/br/groups/{groupId}/participants", async (
            Guid              stageId,
            Guid              groupId,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewStagePublicDataAsync(conn, ctx, stageId))
                return Results.NotFound();

            var groupExists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM br_groups WHERE id = @groupId AND stage_id = @stageId)",
                new { groupId, stageId });
            if (!groupExists)
                return Results.NotFound(new { error = "Group not found in this stage." });

            var participants = await conn.QueryAsync<dynamic>(
                """
                SELECT
                    COALESCE(gt.team_id, gt.participant_id) AS team_id,
                    gt.seed_order,
                    gt.assigned_at,
                    CASE
                        WHEN gt.team_id IS NOT NULL THEN t.name
                        ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                    END AS team_name,
                    CASE WHEN gt.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url
                FROM br_group_teams gt
                LEFT JOIN teams t ON t.id = gt.team_id
                LEFT JOIN tournament_participants tp ON tp.id = gt.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE gt.group_id = @groupId
                ORDER BY gt.seed_order
                """,
                new { groupId });

            return Results.Ok(participants);
        });

        // ── POST /api/stages/{stageId}/br/groups/assign ─────────────────────
        // Auto-distribute registered teams/participants into groups.
        app.MapPost("/api/stages/{stageId}/br/groups/assign", async (
            Guid                stageId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermTeamsManage);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var method = body.TryGetProperty("method", out var m) ? m.GetString() ?? "random" : "random";
            if (method is not ("random" or "snake"))
                return Results.BadRequest(new { error = "method must be 'random' or 'snake'." });

            // Get the stage's tournament_id AND team_size to detect solo
            var tournamentInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ts.tournament_id, t.team_size
                FROM tournament_stages ts
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.id = @stageId
                """,
                new { stageId });
            if (tournamentInfo is null)
                return Results.NotFound(new { error = "Stage not found." });

            Guid tournamentId = tournamentInfo.tournament_id;
            bool isSolo = Convert.ToInt32(tournamentInfo.team_size ?? 1) == 1;
            var checkInRequired = await BRSeedEligibility.IsCheckInRequiredAsync(conn, tournamentId);
            var seedEligibleStatuses = BRSeedEligibility.ResolveStatuses(checkInRequired);

            // Get existing groups for this stage
            var groups = (await conn.QueryAsync<dynamic>(
                """
                SELECT id, group_order
                FROM br_groups
                WHERE stage_id = @stageId
                ORDER BY group_order
                """,
                new { stageId })).ToList();

            if (groups.Count == 0)
                return Results.BadRequest(new { error = "Create groups first." });

            using var tx = conn.BeginTransaction();
            try
            {
                if (!await LockStageAsync(conn, tx, stageId))
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Stage not found." });
                }

                var groupIds = (await LockStageGroupsAsync(conn, tx, stageId)).ToArray();
                if (groupIds.Length == 0)
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = "Create groups first." });
                }

                if (await StageHasAnyRoundsAsync(conn, stageId, tx))
                {
                    tx.Rollback();
                    return Results.Conflict(new { error = StageRoundsLockedMessage });
                }

                // Clear existing assignments
                await conn.ExecuteAsync(
                    "DELETE FROM br_group_teams WHERE group_id = ANY(@groupIds)",
                    new { groupIds }, tx);

                if (isSolo)
                {
                    // Solo: query participant IDs directly
                    var participantIds = (await conn.QueryAsync<Guid>(
                        """
                        SELECT DISTINCT id AS participant_id
                        FROM tournament_participants
                        WHERE tournament_id = @tournamentId
                          AND status::text = ANY(@seedEligibleStatuses)
                        """,
                        new { tournamentId, seedEligibleStatuses })).ToArray();

                    if (participantIds.Length == 0)
                        return Results.BadRequest(new { error = BRSeedEligibility.ParticipantSeedMessage(checkInRequired) });

                    var orderedParticipants = method == "random"
                        ? BrSeedingService.ShuffleTeams(participantIds)
                        : participantIds;

                    var assignments = method == "snake"
                        ? BrSeedingService.BuildSnakeAssignments(orderedParticipants, groupIds)
                        : BrSeedingService.BuildRoundRobinAssignments(orderedParticipants, groupIds);

                    // Insert with participant_id
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_group_teams (group_id, participant_id, seed_order)
                        VALUES (@groupId, @participantId, @seedOrder)
                        """,
                        assignments.Select(a => new { groupId = a.groupId, participantId = a.teamId, seedOrder = a.seedOrder }),
                        tx);

                    tx.Commit();
                    return Results.Ok(new { assigned = assignments.Count, groups = groupIds.Length });
                }
                else
                {
                    // Team-based: existing logic
                    var teamIds = (await conn.QueryAsync<Guid>(
                        """
                        SELECT DISTINCT team_id
                        FROM tournament_participants
                        WHERE tournament_id = @tournamentId
                          AND status::text = ANY(@seedEligibleStatuses)
                          AND team_id IS NOT NULL
                        """,
                        new { tournamentId, seedEligibleStatuses })).ToArray();

                    if (teamIds.Length == 0)
                        return Results.BadRequest(new { error = BRSeedEligibility.TeamSeedMessage(checkInRequired) });

                    var orderedTeams = method == "random"
                        ? BrSeedingService.ShuffleTeams(teamIds)
                        : teamIds;

                    var assignments = method == "snake"
                        ? BrSeedingService.BuildSnakeAssignments(orderedTeams, groupIds)
                        : BrSeedingService.BuildRoundRobinAssignments(orderedTeams, groupIds);

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_group_teams (group_id, team_id, seed_order)
                        VALUES (@groupId, @teamId, @seedOrder)
                        """,
                        assignments.Select(a => new { groupId = a.groupId, teamId = a.teamId, seedOrder = a.seedOrder }),
                        tx);

                    tx.Commit();
                    return Results.Ok(new { assigned = assignments.Count, groups = groupIds.Length });
                }
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/stages/{stageId}/br/bootstrap ─────────────────────────
        // Repair legacy BR stages that have no backing group. Single-lobby BR
        // is still group-backed internally so rounds/results use one path.
        app.MapPost("/api/stages/{stageId}/br/bootstrap", async (
            Guid                              stageId,
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            BattleRoyaleStageBootstrapService brBootstrap) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermTeamsManage);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            using var tx = conn.BeginTransaction();
            try
            {
                var results = await brBootstrap.EnsureStageGroupsAsync(conn, tx, stageId);
                tx.Commit();

                if (results.Count == 0)
                    return Results.NotFound(new { error = "Battle royale stage not found." });

                return Results.Ok(new
                {
                    bootstrapped = results.Any(result => result.Created),
                    stages = results,
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/stages/{stageId}/br/lobbies/generate ──────────────────
        // Create lobbies and materialize games after groups are seeded.
        app.MapPost("/api/stages/{stageId}/br/lobbies/generate", async (
            Guid                              stageId,
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            GameCatalogService               catalog,
            BattleRoyaleStageBootstrapService brBootstrap,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermTeamsManage);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var stageContext = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT t.game
                FROM tournament_stages ts
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.id = @stageId
                  AND ts.format = 'battle_royale'
                """,
                new { stageId });

            if (stageContext is null)
                return Results.NotFound(new { error = "Battle royale stage not found." });

            var catalogBrConfig = await LoadCatalogBrConfigAsync(catalog, stageContext.game as string, ct);
            var playersPerLobby = BrCatalogBrConfigHelper.ReadPlayersPerLobby(catalogBrConfig);

            using var tx = conn.BeginTransaction();
            try
            {
                if (!await LockStageAsync(conn, tx, stageId))
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Battle royale stage not found." });
                }

                var result = await brBootstrap.EnsureStageLobbiesAsync(conn, tx, stageId, playersPerLobby);
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    tx.Rollback();
                    return Results.Conflict(new { error = result.Error });
                }

                tx.Commit();
                return Results.Ok(new { generated = result.Created, stage_id = result.StageId });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/stages/{stageId}/br/groups/{groupId}/teams ─────────────
        // Manual team assignment — replace all teams in this group.
        app.MapPut("/api/stages/{stageId}/br/groups/{groupId}/teams", async (
            Guid                stageId,
            Guid                groupId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermTeamsManage);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            // Verify groupId belongs to this stageId
            var groupExists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM br_groups WHERE id = @groupId AND stage_id = @stageId)",
                new { groupId, stageId });
            if (!groupExists)
                return Results.NotFound(new { error = "Group not found in this stage." });

            // Parse teamIds from body
            if (!body.TryGetProperty("teamIds", out var teamIdsElement) ||
                teamIdsElement.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "teamIds must be an array of UUID strings." });

            var teamIds = new List<Guid>();
            foreach (var element in teamIdsElement.EnumerateArray())
            {
                var raw = element.GetString();
                if (raw is null || !Guid.TryParse(raw, out var parsed))
                    return Results.BadRequest(new { error = $"Invalid team ID: {raw}" });
                teamIds.Add(parsed);
            }

            // Detect solo and validate accordingly
            var tournamentInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ts.tournament_id, t.team_size
                FROM tournament_stages ts
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.id = @stageId
                """,
                new { stageId });
            if (tournamentInfo is null)
                return Results.NotFound(new { error = "Stage not found." });

            Guid tournamentId = tournamentInfo.tournament_id;
            bool isSolo = Convert.ToInt32(tournamentInfo.team_size ?? 1) == 1;
            var checkInRequired = await BRSeedEligibility.IsCheckInRequiredAsync(conn, tournamentId);
            var seedEligibleStatuses = BRSeedEligibility.ResolveStatuses(checkInRequired);
            var ineligibleSeedHint = checkInRequired
                ? "They must be checked in."
                : "They must be registered, approved or checked in.";

            if (teamIds.Count > 0)
            {
                if (isSolo)
                {
                    // For solo: teamIds contains participant IDs — validate against tournament_participants
                    var validParticipantIds = (await conn.QueryAsync<Guid>(
                        """
                        SELECT DISTINCT id FROM tournament_participants
                        WHERE tournament_id = @tournamentId
                          AND status::text = ANY(@seedEligibleStatuses)
                          AND id = ANY(@idsArr)
                        """,
                        new { tournamentId, seedEligibleStatuses, idsArr = teamIds.ToArray() })).ToHashSet();

                    var invalid = teamIds.Where(t => !validParticipantIds.Contains(t)).ToList();
                    if (invalid.Count > 0)
                        return Results.BadRequest(new { error = $"Participants are not eligible for seeding. {ineligibleSeedHint} Invalid: {string.Join(", ", invalid)}" });
                }
                else
                {
                    // Team-based: validate against team_id in tournament_participants
                    var validTeamIds = (await conn.QueryAsync<Guid>(
                        """
                        SELECT DISTINCT team_id FROM tournament_participants
                        WHERE tournament_id = @tournamentId
                          AND status::text = ANY(@seedEligibleStatuses)
                          AND team_id = ANY(@teamIdArr)
                        """,
                        new { tournamentId, seedEligibleStatuses, teamIdArr = teamIds.ToArray() })).ToHashSet();

                    var invalid = teamIds.Where(t => !validTeamIds.Contains(t)).ToList();
                    if (invalid.Count > 0)
                        return Results.BadRequest(new { error = $"Teams are not eligible for seeding. {ineligibleSeedHint} Invalid: {string.Join(", ", invalid)}" });
                }
            }

            using var tx = conn.BeginTransaction();
            try
            {
                if (!await LockStageAsync(conn, tx, stageId))
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Stage not found." });
                }

                var lockedGroupIds = await LockStageGroupsAsync(conn, tx, stageId);
                if (!lockedGroupIds.Contains(groupId))
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Group not found in this stage." });
                }

                if (await StageHasAnyRoundsAsync(conn, stageId, tx))
                {
                    tx.Rollback();
                    return Results.Conflict(new { error = StageRoundsLockedMessage });
                }

                // Clear existing teams in group
                await conn.ExecuteAsync(
                    "DELETE FROM br_group_teams WHERE group_id = @groupId",
                    new { groupId }, tx);

                if (isSolo)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_group_teams (group_id, participant_id, seed_order)
                        VALUES (@groupId, @participantId, @seedOrder)
                        """,
                        teamIds.Select((tid, i) => new { groupId, participantId = tid, seedOrder = i + 1 }),
                        tx);
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_group_teams (group_id, team_id, seed_order)
                        VALUES (@groupId, @teamId, @seedOrder)
                        """,
                        teamIds.Select((tid, i) => new { groupId, teamId = tid, seedOrder = i + 1 }),
                        tx);
                }

                tx.Commit();
                return Results.Ok(new { assigned = teamIds.Count });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/stages/{stageId}/br/groups/{groupId}/lobbies ────────────
        // List rounds for a group. Public endpoint — lobby_code stripped for unauthenticated/non-staff.
        app.MapGet("/api/stages/{stageId}/br/groups/{groupId}/lobbies", async (
            Guid              stageId,
            Guid              groupId,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewStagePublicDataAsync(conn, ctx, stageId))
                return Results.NotFound();

            // Verify groupId belongs to this stageId
            var groupExists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM br_groups WHERE id = @groupId AND stage_id = @stageId)",
                new { groupId, stageId });
            if (!groupExists)
                return Results.NotFound(new { error = "Group not found in this stage." });

            var roundsHasMapColumn = await BrSchemaRepository.ColumnExistsAsync(conn, "br_lobbies", "map");
            var mapSelect = roundsHasMapColumn ? ", r.map" : ", NULL::text AS map";

            var rounds = await conn.QueryAsync<dynamic>(
                """
                SELECT r.id, r.wave_number, r.lobby_index, r.lobby_code, r.status,
                       r.scheduled_at, r.started_at, r.completed_at, r.created_at,
                       r.queue_timer_minutes, r.queue_started_at
                """ + mapSelect + """
                       , (SELECT COUNT(*) FROM br_lobby_results rr JOIN br_games g ON g.id = rr.game_id WHERE g.lobby_id = r.id) AS result_count,
                       (SELECT COUNT(*) FROM br_lobby_evidence ev JOIN br_games g ON g.id = ev.game_id WHERE g.lobby_id = r.id) AS evidence_count,
                       (SELECT COUNT(*) FROM br_lobby_evidence ev JOIN br_games g ON g.id = ev.game_id WHERE g.lobby_id = r.id AND ev.reviewed = FALSE) AS pending_evidence_count,
                       (SELECT COUNT(*)::int FROM br_games g WHERE g.lobby_id = r.id) AS game_count,
                       (SELECT COUNT(*)::int FROM br_games g WHERE g.lobby_id = r.id AND g.status = 'completed') AS games_completed,
                       COALESCE((
                           SELECT array_agg(lg2.group_id ORDER BY g2.group_order)
                           FROM br_lobby_groups lg2
                           JOIN br_groups g2 ON g2.id = lg2.group_id
                           WHERE lg2.lobby_id = r.id
                       ), ARRAY[]::uuid[]) AS group_ids
                FROM br_lobbies r
                WHERE EXISTS (SELECT 1 FROM br_lobby_groups lg WHERE lg.lobby_id = r.id AND lg.group_id = @groupId)
                ORDER BY r.wave_number, r.lobby_index
                """,
                new { groupId });

            // Lobby code visibility rules:
            // - Staff/admin: see all codes (any round status)
            // - Authenticated tournament participants: see code only when round is 'active'
            // - All others (unauthenticated or non-participants): never see codes
            var userCtx = ctx.Items["UserContext"] as UserContext;

            var isStaff = userCtx is not null
                && (await StaffAuthHelper.CanActOnStageAsync(
                        conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit)
                    || StaffAuthHelper.IsPlatformAdmin(userCtx));

            if (isStaff)
                return Results.Ok(rounds);

            // For non-staff: participants see active-round codes; everyone else sees nothing.
            // Check participation ONCE outside the projection (avoids N queries).
            var isParticipant = userCtx is not null
                && await IsUserAssignedToGroupAsync(conn, groupId, userCtx.UserIdGuid);

            // Materialize into typed objects to avoid mutating live Dapper rows
            var roundsList = rounds.AsList();
            var sanitized = roundsList.Select(r =>
            {
                var d = (IDictionary<string, object>)r;
                var isActive = (d["status"] as string) == "active";
                return new
                {
                    id           = d["id"],
                    wave_number = d["wave_number"],
                    lobby_index  = d["lobby_index"],
                    group_ids    = d["group_ids"],
                    // Only participants see the code, and only for the live round
                    lobby_code   = (isParticipant && isActive) ? d["lobby_code"] : (object?)null,
                    status       = d["status"],
                    scheduled_at = d["scheduled_at"],
                    started_at   = d["started_at"],
                    completed_at = d["completed_at"],
                    created_at   = d["created_at"],
                    queue_timer_minutes = d["queue_timer_minutes"],
                    queue_started_at    = d["queue_started_at"],
                    map          = roundsHasMapColumn && d.TryGetValue("map", out var mapVal) ? mapVal : null,
                    result_count = d["result_count"],
                    evidence_count = d["evidence_count"],
                    pending_evidence_count = d["pending_evidence_count"],
                };
            });
            return Results.Ok(sanitized);
        });

        // ── POST /api/stages/{stageId}/br/groups/{groupId}/lobbies ───────────
        // Create a new round for a group. Auto-increments wave_number.
        app.MapPost("/api/stages/{stageId}/br/groups/{groupId}/lobbies", async (
            Guid                stageId,
            Guid                groupId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db,
            GameCatalogService   catalog,
            IHubContext<BRHub>   brHub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var lobbyCode   = body.TryGetProperty("lobbyCode", out var lc) ? lc.GetString()?.Trim() : null;
            var scheduledAt = body.TryGetProperty("scheduledAt", out var sa) ? sa.GetString() : null;
            string? mapValue = null;
            if (body.TryGetProperty("map", out var mapProp) && mapProp.ValueKind != JsonValueKind.Null)
            {
                mapValue = mapProp.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(mapValue))
                    mapValue = null;
            }
            int? queueTimerMinutes = null;
            if (body.TryGetProperty("queueTimerMinutes", out var qtmProp) && qtmProp.ValueKind != JsonValueKind.Null)
            {
                if (!qtmProp.TryGetInt32(out var parsedQueueTimer) || parsedQueueTimer < 0 || parsedQueueTimer > 180)
                    return Results.BadRequest(new { error = "queueTimerMinutes must be between 0 and 180." });

                queueTimerMinutes = parsedQueueTimer == 0 ? null : parsedQueueTimer;
            }

            DateTimeOffset? parsedSchedule = null;
            if (scheduledAt is not null)
            {
                if (!DateTimeOffset.TryParse(scheduledAt, out var dt))
                    return Results.BadRequest(new { error = "Invalid scheduledAt format." });
                parsedSchedule = dt;
            }

            using var tx = conn.BeginTransaction();
            try
            {
                var gamesModelReadyOnCreate = await BrSchemaRepository.BrGamesModelReadyAsync(conn, tx);
                if (BrLobbyFieldPolicy.RejectsLobbyPerGameFieldsInBody(
                        gamesModelReadyOnCreate,
                        body.TryGetProperty("scheduledAt", out _),
                        body.TryGetProperty("queueTimerMinutes", out _),
                        body.TryGetProperty("map", out _)))
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = BrLobbyFieldPolicy.PerGameFieldsError });
                }

                var (tournamentStart, tournamentEnd, _) = await StageCompletionHelper.GetTournamentWindowForStageAsync(conn, stageId, tx);
                var scheduleWindowError = TournamentTimelineValidator.ValidateTimestampWithinWindow(
                    parsedSchedule,
                    tournamentStart,
                    tournamentEnd,
                    "Round schedule");
                if (scheduleWindowError is not null)
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = scheduleWindowError });
                }

                var lockedGroup = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT id
                    FROM br_groups
                    WHERE id = @groupId
                      AND stage_id = @stageId
                    FOR UPDATE
                    """,
                    new { groupId, stageId },
                    tx);
                if (lockedGroup is null)
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Group not found in this stage." });
                }

                var stageContext = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT t.game, t.settings, ts.config AS stage_config
                    FROM tournament_stages ts
                    JOIN tournaments t ON t.id = ts.tournament_id
                    WHERE ts.id = @stageId
                    """,
                    new { stageId },
                    tx);

                var stageFormat = BrConfigService.ResolveFormat(stageContext?.stage_config);
                if (stageFormat is BrStageFormat.StaticGroups
                    or BrStageFormat.SingleLobby
                    or BrStageFormat.MultiLobbyCut)
                {
                    var existingGroupLobbies = await conn.ExecuteScalarAsync<int>(
                        """
                        SELECT COUNT(*)::int
                        FROM br_lobbies l
                        JOIN br_lobby_groups lg ON lg.lobby_id = l.id
                        WHERE lg.group_id = @groupId
                        """,
                        new { groupId },
                        tx);

                    if (existingGroupLobbies > 0)
                    {
                        tx.Rollback();
                        return Results.BadRequest(new
                        {
                            error = "This group already has a lobby. Each group plays in one lobby with multiple scored games — use the existing lobby.",
                        });
                    }
                }

                var nextRoundNumber = await conn.ExecuteScalarAsync<int>(
                    """
                    SELECT COALESCE(MAX(l.wave_number), 0) + 1
                    FROM br_lobbies l
                    JOIN br_lobby_groups lg ON lg.lobby_id = l.id
                    WHERE lg.group_id = @groupId
                    """,
                    new { groupId },
                    tx);

                var roundsHasMapColumn = await BrSchemaRepository.ColumnExistsAsync(conn, "br_lobbies", "map", tx);
                var catalogBrConfig = await LoadCatalogBrConfigAsync(catalog, stageContext?.game as string, ct);
                string? persistedMap = null;
                if (roundsHasMapColumn && mapValue is not null)
                {
                    var mapConfig = BrConfigService.ResolveMapConfig(
                        stageContext?.settings,
                        stageContext?.stage_config,
                        catalogBrConfig);
                    if (!BrConfigService.ValidateMapInPool(mapConfig, mapValue, out string? mapError))
                    {
                        tx.Rollback();
                        return Results.BadRequest(new { error = mapError });
                    }

                    persistedMap = mapValue;
                }
                else if (roundsHasMapColumn)
                {
                    var mapConfig = BrConfigService.ResolveMapConfig(
                        stageContext?.settings,
                        stageContext?.stage_config,
                        catalogBrConfig);
                    persistedMap = BrConfigService.ResolveMapForRound(mapConfig, nextRoundNumber, null);
                }

                var mapInsertSql = roundsHasMapColumn ? ", map" : string.Empty;
                var mapValuesSql = roundsHasMapColumn ? ", @map" : string.Empty;
                var mapReturningSql = roundsHasMapColumn ? ", map" : ", NULL::text AS map";

                var round = await conn.QuerySingleAsync<dynamic>(
                    $"""
                    INSERT INTO br_lobbies (stage_id, wave_number, lobby_index, lobby_code, scheduled_at, queue_timer_minutes{mapInsertSql})
                    VALUES (
                        @stageId,
                        @waveNumber,
                        0,
                        @lobbyCode,
                        @scheduledAt,
                        @queueTimerMinutes{mapValuesSql}
                    )
                    RETURNING id, wave_number, lobby_index, lobby_code, status, scheduled_at, started_at, completed_at, created_at,
                              queue_timer_minutes, queue_started_at{mapReturningSql}
                    """,
                    new
                    {
                        stageId,
                        waveNumber = nextRoundNumber,
                        lobbyCode,
                        scheduledAt = parsedSchedule,
                        queueTimerMinutes,
                        map = persistedMap,
                    },
                    tx);

                var lobbyId = (Guid)round.id;
                await conn.ExecuteAsync(
                    """
                    INSERT INTO br_lobby_groups (lobby_id, group_id)
                    VALUES (@lobbyId, @groupId)
                    ON CONFLICT DO NOTHING
                    """,
                    new { lobbyId, groupId },
                    tx);

                var gamesPerLobby = BrConfigService.ResolveGamesPerLobby(
                    stageContext?.settings, stageContext?.stage_config) ?? 6;
                await BrGameRepository.EnsureGamesForLobbyAsync(
                    conn,
                    lobbyId,
                    gamesPerLobby,
                    stageContext?.settings,
                    stageContext?.stage_config,
                    catalogBrConfig,
                    tx);

                tx.Commit();

                var waveNumber = Convert.ToInt32(round.wave_number);
                var roundStatus = (string)round.status;
                var payload = BuildRoundEvent(stageId, groupId, lobbyId, waveNumber, roundStatus);
                await BroadcastBrAsync(brHub, BRHubEvents.LobbyCreated, stageId, groupId, lobbyId, payload, ct);

                return Results.Ok(round);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                                               && ex.ConstraintName == "br_lobbies_stage_id_wave_number_lobby_index_key")
            {
                tx.Rollback();
                return Results.Conflict(new { error = "Another round was created at the same time. Please try again." });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/stages/{stageId}/br/lobbies ─────────────────────────────
        // List all lobbies for a stage; optional wave filter.
        app.MapGet("/api/stages/{stageId}/br/lobbies", async (
            Guid stageId,
            [FromQuery] int? wave,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewStagePublicDataAsync(conn, ctx, stageId))
                return Results.NotFound();

            var roundsHasMapColumn = await BrSchemaRepository.ColumnExistsAsync(conn, "br_lobbies", "map");
            var mapSelect = roundsHasMapColumn ? ", l.map" : ", NULL::text AS map";

            var lobbies = await conn.QueryAsync<dynamic>(
                $"""
                SELECT l.id,
                       l.wave_number,
                       l.lobby_index,
                       l.lobby_code,
                       l.status,
                       l.scheduled_at,
                       l.started_at,
                       l.completed_at,
                       l.created_at,
                       l.queue_timer_minutes,
                       l.queue_started_at{mapSelect},
                       (SELECT COUNT(*)::int FROM br_games g WHERE g.lobby_id = l.id) AS game_count,
                       (SELECT COUNT(*)::int FROM br_games g WHERE g.lobby_id = l.id AND g.status = 'completed') AS games_completed,
                       COALESCE(array_agg(lg.group_id) FILTER (WHERE lg.group_id IS NOT NULL), ARRAY[]::uuid[]) AS group_ids
                FROM br_lobbies l
                LEFT JOIN br_lobby_groups lg ON lg.lobby_id = l.id
                WHERE l.stage_id = @stageId
                  AND (@wave IS NULL OR l.wave_number = @wave)
                GROUP BY l.id
                ORDER BY l.wave_number, l.lobby_index
                """,
                new { stageId, wave });

            return Results.Ok(lobbies);
        });

        // ── PATCH /api/br/lobbies/{lobbyId} ──────────────────────────────────
        // Update a round (lobby code, status, schedule).
        app.MapPatch("/api/br/lobbies/{lobbyId}", async (
            Guid                lobbyId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db,
            GameCatalogService   catalog,
            IHubContext<NotificationHub> notifHub,
            IHubContext<BRHub>   brHub,
            BrScheduleNotificationService scheduleNotify,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            using var tx = conn.BeginTransaction();

            dynamic? updated;
            Guid broadcastStageId = default;
            Guid broadcastGroupId = default;
            string broadcastOldStatus = string.Empty;
            string broadcastNewStatus = string.Empty;
            var notifyLobbySchedule = false;
            DateTimeOffset? previousLobbySchedule = null;
            DateTimeOffset? nextLobbySchedule = null;
            var gamesModelReadyForSchedule = false;
            try
            {
                var currentRound = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT r.stage_id,
                           (
                               SELECT lg.group_id
                               FROM br_lobby_groups lg
                               WHERE lg.lobby_id = r.id
                               ORDER BY lg.group_id
                               LIMIT 1
                           ) AS group_id,
                           r.status,
                           r.wave_number,
                           r.map,
                           r.lobby_code,
                           r.queue_timer_minutes,
                           r.queue_started_at,
                           r.scheduled_at
                    FROM br_lobbies r
                    WHERE r.id = @lobbyId
                    FOR UPDATE OF r
                    """,
                    new { lobbyId },
                    tx);
                if (currentRound is null)
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Round not found." });
                }

                var stageId = (Guid)currentRound.stage_id;
                if (currentRound.group_id is not Guid groupId)
                {
                    tx.Rollback();
                    return Results.Conflict(new { error = "This lobby is not linked to a seed group." });
                }
                var currentStatus = (string)currentRound.status;
                var currentRoundNumber = Convert.ToInt32(currentRound.wave_number);
                var currentMap = currentRound.map as string;
                var currentLobbyCode = (string?)currentRound.lobby_code;
                int? currentQueueTimerMinutes = currentRound.queue_timer_minutes is not null
                    ? Convert.ToInt32(currentRound.queue_timer_minutes)
                    : null;
                var currentQueueStartedAt = currentRound.queue_started_at as DateTimeOffset?;
                previousLobbySchedule = currentRound.scheduled_at switch
                {
                    DateTimeOffset dto => dto,
                    DateTime dt => new DateTimeOffset(dt),
                    _ => null,
                };

                var allowed = await StaffAuthHelper.CanActOnStageAsync(
                    conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
                if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                {
                    tx.Rollback();
                    return Results.Forbid();
                }

                var setClauses = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("lobbyId", lobbyId);
                string? finalLobbyCode = currentLobbyCode;
                var finalStatus = currentStatus;
                int? finalQueueTimerMinutes = currentQueueTimerMinutes;
                string? finalMap = currentMap;
                var roundsHasMapColumn = await BrSchemaRepository.ColumnExistsAsync(conn, "br_lobbies", "map", tx);
                var gamesModelReady = await BrSchemaRepository.BrGamesModelReadyAsync(conn, tx);
                gamesModelReadyForSchedule = gamesModelReady;

                if (BrLobbyFieldPolicy.RejectsLobbyPerGameFieldsInBody(
                        gamesModelReady,
                        body.TryGetProperty("scheduledAt", out _),
                        body.TryGetProperty("queueTimerMinutes", out _),
                        roundsHasMapColumn && body.TryGetProperty("map", out _)))
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = BrLobbyFieldPolicy.PerGameFieldsError });
                }

                if (!gamesModelReady && roundsHasMapColumn && body.TryGetProperty("map", out var mapProp))
                {
                    if (mapProp.ValueKind == JsonValueKind.Null)
                    {
                        finalMap = null;
                        setClauses.Add("map = NULL");
                    }
                    else
                    {
                        finalMap = mapProp.GetString()?.Trim();
                        if (string.IsNullOrWhiteSpace(finalMap))
                            finalMap = null;

                        var stageContext = await conn.QuerySingleOrDefaultAsync<dynamic>(
                            """
                            SELECT t.game, t.settings, ts.config AS stage_config
                            FROM tournament_stages ts
                            JOIN tournaments t ON t.id = ts.tournament_id
                            WHERE ts.id = @stageId
                            """,
                            new { stageId },
                            tx);

                        var catalogBrConfig = await LoadCatalogBrConfigAsync(catalog, stageContext?.game as string, ct);
                        var mapConfig = BrConfigService.ResolveMapConfig(
                            stageContext?.settings,
                            stageContext?.stage_config,
                            catalogBrConfig);

                        if (finalMap is not null)
                        {
                            if (!BrConfigService.ValidateMapInPool(mapConfig, finalMap, out string? mapError))
                            {
                                tx.Rollback();
                                return Results.BadRequest(new { error = mapError });
                            }
                        }

                        setClauses.Add("map = @map");
                        parameters.Add("map", finalMap);
                    }
                }

                if (body.TryGetProperty("lobbyCode", out var lcProp))
                {
                    finalLobbyCode = lcProp.ValueKind == JsonValueKind.Null ? null : lcProp.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(finalLobbyCode))
                        finalLobbyCode = null;
                    setClauses.Add("lobby_code = @lobbyCode");
                    parameters.Add("lobbyCode", finalLobbyCode);
                }

                if (!gamesModelReady && body.TryGetProperty("scheduledAt", out var saProp))
                {
                    notifyLobbySchedule = true;
                    if (saProp.ValueKind == JsonValueKind.Null)
                    {
                        setClauses.Add("scheduled_at = NULL");
                        nextLobbySchedule = null;
                    }
                    else
                    {
                        var saStr = saProp.GetString();
                        if (saStr is not null && DateTimeOffset.TryParse(saStr, out var dt))
                        {
                            var (tournamentStart, tournamentEnd, _) = await StageCompletionHelper.GetTournamentWindowForStageAsync(conn, stageId, tx);
                            var scheduleWindowError = TournamentTimelineValidator.ValidateTimestampWithinWindow(
                                dt,
                                tournamentStart,
                                tournamentEnd,
                                "Round schedule");
                            if (scheduleWindowError is not null)
                            {
                                tx.Rollback();
                                return Results.BadRequest(new { error = scheduleWindowError });
                            }

                            setClauses.Add("scheduled_at = @scheduledAt");
                            parameters.Add("scheduledAt", dt);
                            nextLobbySchedule = dt;
                        }
                        else
                        {
                            tx.Rollback();
                            return Results.BadRequest(new { error = "Invalid scheduledAt format." });
                        }
                    }
                }

                if (!gamesModelReady && body.TryGetProperty("queueTimerMinutes", out var qtmProp))
                {
                    if (qtmProp.ValueKind == JsonValueKind.Null)
                    {
                        finalQueueTimerMinutes = null;
                        setClauses.Add("queue_timer_minutes = NULL");
                    }
                    else if (qtmProp.TryGetInt32(out var queueTimerMinutes) && queueTimerMinutes >= 0 && queueTimerMinutes <= 180)
                    {
                        finalQueueTimerMinutes = queueTimerMinutes == 0 ? null : queueTimerMinutes;
                        if (finalQueueTimerMinutes is null)
                        {
                            setClauses.Add("queue_timer_minutes = NULL");
                        }
                        else
                        {
                            setClauses.Add("queue_timer_minutes = @queueTimerMinutes");
                            parameters.Add("queueTimerMinutes", finalQueueTimerMinutes.Value);
                        }
                    }
                    else
                    {
                        tx.Rollback();
                        return Results.BadRequest(new { error = "queueTimerMinutes must be between 0 and 180." });
                    }
                }

                if (body.TryGetProperty("status", out var stProp))
                {
                    var newStatus = stProp.GetString();
                    if (newStatus is not ("pending" or "active" or "completed"))
                    {
                        tx.Rollback();
                        return Results.BadRequest(new { error = "status must be 'pending', 'active', or 'completed'." });
                    }

                    var validTransition = (currentStatus, newStatus) switch
                    {
                        ("pending", "active") => true,
                        ("active", "completed") => true,
                        ("completed", "active") => true,
                        _ => false
                    };
                    if (!validTransition)
                    {
                        tx.Rollback();
                        return Results.BadRequest(new { error = $"Cannot transition from '{currentStatus}' to '{newStatus}'." });
                    }

                    if (newStatus == "active" && currentStatus != "active")
                    {
                        if (string.IsNullOrWhiteSpace(finalLobbyCode))
                        {
                            tx.Rollback();
                            return Results.BadRequest(new { error = "Lobby code is required before starting a round." });
                        }

                        if (roundsHasMapColumn)
                        {
                            if (!gamesModelReady)
                            {
                                var stageContext = await conn.QuerySingleOrDefaultAsync<dynamic>(
                                    """
                                    SELECT t.game, t.settings, ts.config AS stage_config
                                    FROM tournament_stages ts
                                    JOIN tournaments t ON t.id = ts.tournament_id
                                    WHERE ts.id = @stageId
                                    """,
                                    new { stageId },
                                    tx);

                                var catalogBrConfig = await LoadCatalogBrConfigAsync(catalog, stageContext?.game as string, ct);
                                var mapConfig = BrConfigService.ResolveMapConfig(
                                    stageContext?.settings,
                                    stageContext?.stage_config,
                                    catalogBrConfig);

                                var effectiveMap = BrConfigService.ResolveMapForRound(
                                    mapConfig,
                                    currentRoundNumber,
                                    finalMap);

                                if (mapConfig.Mode == BrMapMode.PerRound
                                    && string.IsNullOrWhiteSpace(effectiveMap))
                                {
                                    tx.Rollback();
                                    return Results.BadRequest(new { error = "Map is required before starting a round." });
                                }

                                if (!string.IsNullOrWhiteSpace(effectiveMap))
                                {
                                    if (!BrConfigService.ValidateMapInPool(mapConfig, effectiveMap, out string? mapError))
                                    {
                                        tx.Rollback();
                                        return Results.BadRequest(new { error = mapError });
                                    }
                                }
                            }
                        }

                        var (tournamentStart, tournamentEnd, _) = await StageCompletionHelper.GetTournamentWindowForStageAsync(conn, stageId, tx);
                        var ongoingError = await TournamentTimelineValidator.EnsureTournamentLiveAsync(conn, stageId, tx);
                        if (ongoingError is not null)
                        {
                            tx.Rollback();
                            return Results.BadRequest(new { error = ongoingError });
                        }

                        var liveWindowError = TournamentTimelineValidator.ValidateTimestampWithinWindow(
                            DateTimeOffset.UtcNow,
                            tournamentStart,
                            tournamentEnd,
                            "Starting a round");
                        if (liveWindowError is not null)
                        {
                            tx.Rollback();
                            return Results.BadRequest(new { error = liveWindowError });
                        }

                        var existingActiveRound = await conn.QuerySingleOrDefaultAsync<dynamic>(
                            """
                            SELECT l.id, l.wave_number
                            FROM br_lobbies l
                            JOIN br_lobby_groups lg ON lg.lobby_id = l.id
                            WHERE lg.group_id = @groupId
                              AND l.status = 'active'
                              AND l.id <> @lobbyId
                            ORDER BY COALESCE(l.queue_started_at, l.started_at, l.created_at) DESC NULLS LAST, l.wave_number DESC
                            LIMIT 1
                            """,
                            new { groupId, lobbyId },
                            tx);

                        if (existingActiveRound is not null)
                        {
                            tx.Rollback();
                            return Results.BadRequest(new
                            {
                                error = $"Round {Convert.ToInt32(existingActiveRound.wave_number)} is already live. Complete, re-open, or reset it before starting another round."
                            });
                        }
                    }

                    if (newStatus == "completed")
                    {
                        if (!await RoundResultsMatchCurrentRosterAsync(conn, tx, groupId, lobbyId))
                        {
                            tx.Rollback();
                            return Results.Conflict(new
                            {
                                error = "Save round results before completing this round."
                            });
                        }

                        var pendingEvidenceCount = await BrEvidenceService.CountPendingAsync(conn, lobbyId, tx);
                        if (pendingEvidenceCount > 0)
                        {
                            tx.Rollback();
                            return Results.Conflict(new
                            {
                                error = "All submitted evidence must be approved before the round can be completed."
                            });
                        }
                    }

                    finalStatus = newStatus;
                    setClauses.Add("status = @status");
                    parameters.Add("status", newStatus);

                    if (newStatus == "active" && currentStatus == "pending")
                        setClauses.Add("started_at = NOW()");
                    else if (newStatus == "active" && currentStatus == "completed")
                        setClauses.Add("completed_at = NULL");
                    else if (newStatus == "completed")
                        setClauses.Add("completed_at = NOW()");
                }

                var hasLiveLobbyCode = finalStatus == "active" && !string.IsNullOrWhiteSpace(finalLobbyCode);
                var hasQueueTimer = finalQueueTimerMinutes is > 0;

                if (gamesModelReady)
                {
                    if (finalStatus == "active" && currentStatus != "active")
                    {
                        setClauses.Add("queue_timer_minutes = NULL");
                    }
                    setClauses.Add("queue_started_at = NULL");
                }
                else if (!hasLiveLobbyCode || !hasQueueTimer)
                {
                    setClauses.Add("queue_started_at = NULL");
                }
                else
                {
                    var shouldStartQueueNow =
                        currentQueueStartedAt is null
                        || currentStatus != "active"
                        || string.IsNullOrWhiteSpace(currentLobbyCode)
                        || currentQueueTimerMinutes is null or <= 0;

                    if (shouldStartQueueNow)
                        setClauses.Add("queue_started_at = NOW()");
                }

                if (setClauses.Count == 0)
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = "No fields to update." });
                }

                var mapReturningSql = roundsHasMapColumn ? ", map" : ", NULL::text AS map";
                var sql = $"""
                    UPDATE br_lobbies
                    SET {string.Join(", ", setClauses)}
                    WHERE id = @lobbyId
                    RETURNING id, wave_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at,
                              queue_timer_minutes, queue_started_at{mapReturningSql}
                    """;

                updated = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, parameters, tx);
                if (updated is null)
                {
                    tx.Rollback();
                    return Results.NotFound();
                }

                broadcastStageId = stageId;
                broadcastGroupId = groupId;
                broadcastOldStatus = currentStatus;
                broadcastNewStatus = finalStatus;

                tx.Commit();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                                               && ex.ConstraintName == "uq_br_lobbies_active_group")
            {
                tx.Rollback();
                return Results.Conflict(new { error = "Another round is already live for this group." });
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            if (updated is not null && broadcastStageId != default)
            {
                var lobbyGroupIds = await GetLobbyGroupIdsAsync(conn, lobbyId, broadcastGroupId);
                if (lobbyGroupIds.Count == 0 && broadcastGroupId != default)
                    lobbyGroupIds = new[] { broadcastGroupId };

                var waveNumber = Convert.ToInt32(updated.wave_number);
                var roundPayload = BuildRoundEvent(
                    broadcastStageId,
                    broadcastGroupId,
                    lobbyId,
                    waveNumber,
                    broadcastNewStatus,
                    (string?)updated.lobby_code,
                    updated.queue_timer_minutes is not null ? Convert.ToInt32(updated.queue_timer_minutes) : (int?)null,
                    updated.queue_started_at is not null
                        ? ((DateTimeOffset)updated.queue_started_at).ToString("o")
                        : null);

                if (lobbyGroupIds.Count > 0)
                {
                    await BroadcastBrToLobbyGroupsAsync(
                        brHub,
                        BRHubEvents.LobbyUpdated,
                        broadcastStageId,
                        lobbyId,
                        lobbyGroupIds,
                        roundPayload,
                        ct);
                }

                var statusChanged = !string.Equals(broadcastOldStatus, broadcastNewStatus, StringComparison.Ordinal);
                if (statusChanged && lobbyGroupIds.Count > 0 && (broadcastNewStatus == "completed"
                    || (broadcastOldStatus == "completed" && broadcastNewStatus == "active")))
                {
                    if (broadcastNewStatus == "completed")
                    {
                        await BroadcastBrToLobbyGroupsAsync(
                            brHub,
                            BRHubEvents.LobbyCompleted,
                            broadcastStageId,
                            lobbyId,
                            lobbyGroupIds,
                            roundPayload,
                            ct);
                    }

                    await BroadcastBrToLobbyGroupsAsync(
                        brHub,
                        BRHubEvents.LeaderboardUpdated,
                        broadcastStageId,
                        lobbyId,
                        lobbyGroupIds,
                        BuildLeaderboardEvent(broadcastStageId, broadcastGroupId),
                        ct);
                }
            }

            // ── Fire-and-forget notifications when a round goes active ───────
            if (body.TryGetProperty("status", out var notifStatusProp) &&
                notifStatusProp.GetString() == "active")
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var notifConn = db.CreateConnection();

                        // Get round + group + tournament info for notification content
                        var roundMeta = await notifConn.QuerySingleOrDefaultAsync<dynamic>(
                            """
                            SELECT g.id AS group_id, r.wave_number, r.lobby_code,
                                   g.name AS group_name,
                                   t.slug AS tournament_slug
                            FROM br_lobbies r
                            JOIN br_lobby_groups lg ON lg.lobby_id = r.id
                            JOIN br_groups g ON g.id = lg.group_id
                            JOIN tournament_stages ts ON ts.id = g.stage_id
                            JOIN tournaments t ON t.id = ts.tournament_id
                            WHERE r.id = @lobbyId
                            """,
                            new { lobbyId });

                        if (roundMeta is null) return;

                        Guid   groupIdForNotif  = roundMeta.group_id;
                        int    waveNumber      = Convert.ToInt32(roundMeta.wave_number);
                        string groupName        = (string)roundMeta.group_name;
                        string tournamentSlug   = (string)roundMeta.tournament_slug;

                        // Get all participant user IDs in the group
                        var userIds = (await notifConn.QueryAsync<string>(
                            """
                            SELECT DISTINCT recipients.user_id::text
                            FROM (
                                SELECT tp.user_id
                                FROM br_group_teams bgt
                                JOIN tournament_participants tp ON tp.id = bgt.participant_id
                                WHERE bgt.group_id = @groupId
                                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')

                                UNION

                                SELECT tm.user_id
                                FROM br_group_teams bgt
                                JOIN tournament_participants tp ON tp.team_id = bgt.team_id
                                JOIN team_members tm ON tm.team_id = bgt.team_id
                                WHERE bgt.group_id = @groupId
                                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                                  AND tm.is_active = TRUE
                            ) recipients
                            """,
                            new { groupId = groupIdForNotif })).ToList();

                        if (userIds.Count == 0) return;

                        var title   = $"Round {waveNumber} is live — {groupName}";
                        var lobbyCodeForNotif = roundMeta.lobby_code as string;
                        var message = !string.IsNullOrWhiteSpace(lobbyCodeForNotif)
                            ? $"Group '{groupName}' Round {waveNumber} is live. Lobby code: {lobbyCodeForNotif.Trim()}. Open Match Room to join."
                            : $"Group '{groupName}' Round {waveNumber} is live. Open Match Room — the organizer will share the lobby code shortly.";
                        var link    = $"/tournaments/{tournamentSlug}/br-game-room";
                        var type    = "br_round_active";

                        // Bulk insert notifications
                        await notifConn.ExecuteAsync(
                            """
                            INSERT INTO notifications (user_id, type, title, message, link, is_read)
                            SELECT u.id::uuid, @type, @title, @message, @link, FALSE
                            FROM unnest(@userIds::uuid[]) AS u(id)
                            ON CONFLICT DO NOTHING
                            """,
                            new { type, title, message, link, userIds = userIds.ToArray() });

                        // Push real-time SignalR notification to each participant
                        foreach (var userId in userIds)
                        {
                            await notifHub.Clients
                                .Group(NotificationHub.UserGroup(userId))
                                .SendAsync(NotificationHubEvents.NewNotification,
                                    new { type, title, message, link });
                        }
                    }
                    catch (Exception ex)
                    {
                        // Best-effort: log but don't fail the PATCH response
                        Console.Error.WriteLine($"[BRGroupEndpoints] Notification error for round {lobbyId}: {ex.Message}");
                    }
                });
            }

            if (notifyLobbySchedule && !gamesModelReadyForSchedule)
            {
                await scheduleNotify.DispatchLobbyScheduleChangedAsync(
                    lobbyId, previousLobbySchedule, nextLobbySchedule, ct);
            }

            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/br/lobbies/{lobbyId}/reset ─────────────────────────────
        // Clear all result/evidence state for a round and move it back to
        // pending without deleting the round itself.
        app.MapPost("/api/br/lobbies/{lobbyId}/reset", async (
            Guid                lobbyId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            GameCatalogService   catalog,
            IHubContext<BRHub>   brHub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var roundInfo = await BrLobbyRepository.GetContextAsync(conn, lobbyId);
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = roundInfo.StageId;
            var waveNumber = roundInfo.WaveNumber;
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var groupIds = await GetLobbyGroupIdsAsync(conn, lobbyId, roundInfo.GroupId);
            if (groupIds.Count == 0)
            {
                return Results.Conflict(new { error = "This lobby is not linked to a seed group." });
            }

            using var tx = conn.BeginTransaction();
            dynamic? round;
            try
            {
                await conn.ExecuteAsync(
                    """
                    SELECT 1
                    FROM br_lobbies r
                    WHERE r.id = @lobbyId
                    FOR UPDATE OF r
                    """,
                    new { lobbyId },
                    tx);

                await BrLobbyService.ClearScoredStateAsync(conn, lobbyId, tx);

                round = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    UPDATE br_lobbies
                    SET status = 'pending',
                        lobby_code = NULL,
                        scheduled_at = NULL,
                        started_at = NULL,
                        completed_at = NULL,
                        queue_timer_minutes = NULL,
                        queue_started_at = NULL
                    WHERE id = @lobbyId
                    RETURNING id, wave_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at,
                              queue_timer_minutes, queue_started_at
                    """,
                    new { lobbyId },
                    tx);

                if (round is null)
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Round not found." });
                }

                if (await BrSchemaRepository.TableExistsAsync(conn, "br_games", tx))
                {
                    try
                    {
                        var stageContext = await conn.QuerySingleOrDefaultAsync<dynamic>(
                            """
                            SELECT t.game, t.settings, ts.config AS stage_config
                            FROM tournament_stages ts
                            JOIN tournaments t ON t.id = ts.tournament_id
                            WHERE ts.id = @stageId
                            """,
                            new { stageId },
                            tx);

                        var catalogBrConfig = await LoadCatalogBrConfigAsync(catalog, stageContext?.game as string, ct);
                        await BrLobbyService.EnsureGamesAfterResetAsync(
                            conn,
                            lobbyId,
                            stageId,
                            stageContext?.settings,
                            stageContext?.stage_config,
                            catalogBrConfig,
                            tx);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[BRGroupEndpoints] EnsureGamesForLobby failed during reset for lobby {lobbyId}: {ex.Message}");
                    }
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            var resetPayload = BuildRoundEvent(stageId, groupIds[0], lobbyId, waveNumber, "pending");
            await BroadcastBrToLobbyGroupsAsync(
                brHub,
                BRHubEvents.LobbyReset,
                stageId,
                lobbyId,
                groupIds,
                resetPayload,
                ct);
            await BroadcastBrToLobbyGroupsAsync(
                brHub,
                BRHubEvents.LeaderboardUpdated,
                stageId,
                lobbyId,
                groupIds,
                BuildLeaderboardEvent(stageId, groupIds[0]),
                ct);

            return Results.Ok(MapLobbyRowToApiResponse(round!));
        }).RequireAuthorization("Authenticated");

        // ── GET /api/br/lobbies/{lobbyId}/results ────────────────────────────
        // Get results for a round.
        app.MapGet("/api/br/lobbies/{lobbyId}/results", async (
            Guid              lobbyId,
            [FromQuery] Guid? gameId,
            [FromQuery] int?  gameNumber,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var targetGameId = await BrGameRouteHelper.ResolveTargetGameIdAsync(
                conn, lobbyId, gameId, gameNumber);
            if (targetGameId is null)
                return Results.NotFound(new { error = "No games found for this lobby." });

            var roundResultsHasParticipantId = await BrSchemaRepository.ColumnExistsAsync(conn, "br_lobby_results", "participant_id");

            // Use LEFT JOINs with COALESCE for unified team/solo display
            var results = await conn.QueryAsync<dynamic>(
                roundResultsHasParticipantId
                    ? """
                      SELECT rr.id,
                             COALESCE(rr.team_id, rr.participant_id) AS team_id,
                             rr.placement, rr.kills,
                             rr.placement_points, rr.kill_points, rr.total_points,
                             CASE
                                 WHEN rr.team_id IS NOT NULL THEN t.name
                                 ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                             END AS team_name,
                             CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url
                      FROM br_lobby_results rr
                      LEFT JOIN teams t ON t.id = rr.team_id
                      LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                      LEFT JOIN profiles p ON p.id = tp.user_id
                      WHERE rr.game_id = @targetGameId
                      ORDER BY rr.placement
                      """
                    : """
                      SELECT rr.id,
                             rr.team_id AS team_id,
                             rr.placement, rr.kills,
                             rr.placement_points, rr.kill_points, rr.total_points,
                             t.name AS team_name,
                             t.logo_url AS logo_url
                      FROM br_lobby_results rr
                      LEFT JOIN teams t ON t.id = rr.team_id
                      WHERE rr.game_id = @targetGameId
                      ORDER BY rr.placement
                      """,
                new { targetGameId });

            return Results.Ok(results);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/br/lobbies/{lobbyId}/evidence ───────────────────────────
        // Staff see all submissions. Players only see their own submission for
        // the active round in their assigned group.
        app.MapGet("/api/br/lobbies/{lobbyId}/evidence", async (
            Guid                lobbyId,
            [FromQuery] int?    gameNumber,
            HttpContext          ctx,
            IDbConnectionFactory db,
            IHostEnvironment     env) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var roundInfo = await BrLobbyRepository.GetContextAsync(conn, lobbyId);
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = roundInfo.StageId;
            var tournamentId = roundInfo.TournamentId;
            var isSolo = roundInfo.TeamSize == 1;

            var isStaff = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            isStaff = isStaff || StaffAuthHelper.IsPlatformAdmin(userCtx);

            Guid? viewerTeamId = null;
            Guid? viewerParticipantId = null;

            if (!isStaff)
            {
                var viewerAccess = await ResolveRoundEntityAccessAsync(
                    conn,
                    lobbyId,
                    tournamentId,
                    userCtx.UserIdGuid,
                    isSolo);
                viewerTeamId = viewerAccess.TeamId;
                viewerParticipantId = viewerAccess.ParticipantId;

                if (viewerTeamId is null && viewerParticipantId is null)
                    return Results.Forbid();
            }

            try
            {
                var payload = await BrGameRouteHelper.ListLobbyEvidenceAsync(
                    conn,
                    lobbyId,
                    isStaff,
                    viewerTeamId,
                    viewerParticipantId,
                    gameNumber: gameNumber);

                return Results.Ok(payload);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[BRGroupEndpoints] Failed to list evidence for lobby {lobbyId}, gameNumber={gameNumber}: {ex.GetType().Name}: {ex.Message}");
                var detail = env.IsProduction()
                    ? "Could not load evidence submissions."
                    : $"Could not load evidence submissions: {ex.Message}";
                return Results.Problem(
                    detail: detail,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/br/lobbies/{lobbyId}/evidence ───────────────────────────
        // Stores evidence against the relational round so organizer review and
        // multi-group BR stay aligned.
        app.MapPut("/api/br/lobbies/{lobbyId}/evidence", async (
            Guid                lobbyId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db,
            IConfiguration       config,
            IHubContext<BRHub>   brHub,
            IWebHostEnvironment  env,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var supabaseUrl = config["Supabase:Url"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(supabaseUrl))
                return Results.BadRequest(new { error = "Supabase storage is not configured." });

            if (!TryNormalizeEvidenceImageUrl(body, supabaseUrl, out var imageUrl, out var imageUrlError))
                return Results.BadRequest(new { error = imageUrlError });

            int? placement = null;
            if (body.TryGetProperty("placement", out var placementProp) && placementProp.ValueKind != JsonValueKind.Null)
            {
                if (!placementProp.TryGetInt32(out var placementValue) || placementValue < 1)
                    return Results.BadRequest(new { error = "placement must be >= 1." });
                placement = placementValue;
            }

            int? kills = null;
            if (body.TryGetProperty("kills", out var killsProp) && killsProp.ValueKind != JsonValueKind.Null)
            {
                if (!killsProp.TryGetInt32(out var killsValue) || killsValue < 0)
                    return Results.BadRequest(new { error = "kills must be >= 0." });
                kills = killsValue;
            }

            using var conn = db.CreateConnection();

            int? gameNumber = null;
            if (body.TryGetProperty("gameNumber", out var gameNumberProp) && gameNumberProp.ValueKind == JsonValueKind.Number)
            {
                if (!gameNumberProp.TryGetInt32(out var gn) || gn < 1)
                    return Results.BadRequest(new { error = "gameNumber must be >= 1." });
                gameNumber = gn;
            }

            var roundInfo = await BrLobbyRepository.GetContextAsync(conn, lobbyId);
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = roundInfo.StageId;
            var groupId = roundInfo.GroupId ?? Guid.Empty;
            var tournamentId = roundInfo.TournamentId;
            var isSolo = roundInfo.TeamSize == 1;
            var roundStatus = roundInfo.Status;

            if (roundStatus != "active")
                return Results.Conflict(new { error = "Evidence can only be submitted while the lobby is live." });

            var targetGameId = await BrGameRouteHelper.ResolveTargetGameIdAsync(
                conn, lobbyId, gameNumber: gameNumber);
            if (targetGameId is null)
                return Results.NotFound(new { error = "No active game found for this lobby." });

            var gameStatus = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT status FROM br_games WHERE id = @targetGameId",
                new { targetGameId });
            if (gameStatus != "active")
                return Results.Conflict(new { error = "Evidence can only be submitted while the game is live." });

            var entityAccess = await ResolveRoundEntityAccessAsync(
                conn,
                lobbyId,
                tournamentId,
                userCtx.UserIdGuid,
                isSolo);
            var teamId = entityAccess.TeamId;
            var participantId = entityAccess.ParticipantId;

            if (teamId is null && participantId is null)
                return Results.Forbid();

            var alreadyExists = participantId is not null
                ? await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM br_lobby_evidence WHERE game_id = @targetGameId AND participant_id = @participantId)",
                    new { targetGameId, participantId })
                : await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM br_lobby_evidence WHERE game_id = @targetGameId AND team_id = @teamId)",
                    new { targetGameId, teamId });

            if (alreadyExists)
                return Results.Conflict(new { error = "Evidence has already been submitted for this game. Submissions cannot be changed." });

            try
            {
                if (participantId is not null)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_lobby_evidence (
                            lobby_id, game_id, team_id, participant_id, image_url, submitted_by, submitted_at,
                            placement, kills, reviewed, reviewed_at, reviewed_by
                        )
                        VALUES (
                            @lobbyId, @targetGameId, NULL, @participantId, @imageUrl, @submittedBy, NOW(),
                            @placement, @kills, FALSE, NULL, NULL
                        )
                        """,
                        new
                        {
                            lobbyId,
                            targetGameId,
                            participantId,
                            imageUrl,
                            submittedBy = userCtx.UserIdGuid,
                            placement,
                            kills
                        });
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_lobby_evidence (
                            lobby_id, game_id, team_id, participant_id, image_url, submitted_by, submitted_at,
                            placement, kills, reviewed, reviewed_at, reviewed_by
                        )
                        VALUES (
                            @lobbyId, @targetGameId, @teamId, NULL, @imageUrl, @submittedBy, NOW(),
                            @placement, @kills, FALSE, NULL, NULL
                        )
                        """,
                        new
                        {
                            lobbyId,
                            targetGameId,
                            teamId,
                            imageUrl,
                            submittedBy = userCtx.UserIdGuid,
                            placement,
                            kills
                        });
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                                               && (ex.ConstraintName == "uq_br_lobby_evidence_game_team"
                                                   || ex.ConstraintName == "uq_br_lobby_evidence_game_participant"
                                                   || ex.ConstraintName == "uq_br_lobby_evidence_team"
                                                   || ex.ConstraintName == "uq_br_lobby_evidence_participant"))
            {
                return Results.Conflict(new { error = "Evidence has already been submitted for this round. Submissions cannot be changed." });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                                               || ex.SqlState == PostgresErrorCodes.CheckViolation)
            {
                return Results.BadRequest(new { error = "Evidence could not be linked to your BR roster. Refresh the page and try again." });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable
                                               || ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                Console.Error.WriteLine(
                    $"[BRGroupEndpoints] BR evidence schema missing for round {lobbyId}. " +
                    $"Postgres {ex.SqlState} {ex.MessageText}");

                return Results.Json(
                    new { error = "BR evidence storage is not available yet. Try again shortly or contact support." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (PostgresException ex)
            {
                Console.Error.WriteLine(
                    $"[BRGroupEndpoints] Failed to submit evidence for round {lobbyId}. " +
                    $"Postgres {ex.SqlState} {ex.ConstraintName} {ex.TableName}.{ex.ColumnName} :: {ex.MessageText} :: {ex.Detail}");

                if (!env.IsProduction())
                {
                    return Results.Json(new
                    {
                        error = "BR evidence submit failed.",
                        detail = ex.MessageText,
                        sqlState = ex.SqlState,
                    }, statusCode: StatusCodes.Status500InternalServerError);
                }

                throw;
            }

            var entityId = teamId ?? participantId!.Value;
            var resolvedGameNumber = gameNumber ?? await conn.QuerySingleOrDefaultAsync<int?>(
                "SELECT game_number FROM br_games WHERE id = @targetGameId",
                new { targetGameId });
            try
            {
                var pendingCount = await BrEvidenceService.CountPendingAsync(conn, lobbyId);
                var lobbyGroupIds = await GetLobbyGroupIdsAsync(conn, lobbyId, groupId);
                var evidencePayload = new
                {
                    stageId = stageId.ToString(),
                    groupId = groupId.ToString(),
                    lobbyId = lobbyId.ToString(),
                    gameId = targetGameId.ToString(),
                    gameNumber = resolvedGameNumber,
                    entityId = entityId.ToString(),
                    pendingCount,
                };
                await BroadcastBrToLobbyGroupsAsync(
                    brHub,
                    BRHubEvents.EvidenceSubmitted,
                    stageId,
                    lobbyId,
                    lobbyGroupIds,
                    evidencePayload,
                    ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[BRGroupEndpoints] Evidence saved for round {lobbyId} but post-submit notify failed: {ex.Message}");
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/br/lobbies/{lobbyId}/evidence/{entityId} ───────────────
        // Approve reported stats into results, or reopen a submission.
        app.MapPatch("/api/br/lobbies/{lobbyId}/evidence/{entityId}", async (
            Guid                lobbyId,
            Guid                entityId,
            [FromQuery] int?    gameNumber,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db,
            GameCatalogService   catalog,
            IHubContext<BRHub>   brHub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var wantsApprove = body.TryGetProperty("approve", out var approveProp)
                && approveProp.ValueKind == JsonValueKind.True;
            var wantsReopen = body.TryGetProperty("reviewed", out var reviewedProp)
                && reviewedProp.ValueKind == JsonValueKind.False;

            if (wantsApprove && wantsReopen)
            {
                return Results.BadRequest(new
                {
                    error = "Request body cannot include both approve and reviewed: false.",
                });
            }

            if (!wantsApprove && !wantsReopen)
            {
                return Results.BadRequest(new
                {
                    error = "Request body must include approve: true or reviewed: false.",
                });
            }

            using var conn = db.CreateConnection();

            var roundInfo = await BrLobbyRepository.GetContextAsync(conn, lobbyId, includeStageConfig: wantsApprove);
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = roundInfo.StageId;
            var groupId = roundInfo.GroupId ?? Guid.Empty;

            if (wantsApprove)
            {
                var allowedScores = await StaffAuthHelper.CanActOnStageAsync(
                    conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermScoresUpdate);
                if (!allowedScores && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                    return Results.Forbid();

                if (roundInfo.Status == "completed")
                    return Results.Conflict(new { error = "Completed rounds are locked. Re-open the round before approving evidence." });

                var targetGameId = await BrGameRouteHelper.ResolveTargetGameIdAsync(
                    conn, lobbyId, gameNumber: gameNumber);
                if (targetGameId is null)
                    return Results.NotFound(new { error = "No game found for this lobby." });

                if (gameNumber is null)
                {
                    var gameCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*)::int FROM br_games WHERE lobby_id = @lobbyId",
                        new { lobbyId });
                    if (gameCount > 1)
                    {
                        return Results.BadRequest(new
                        {
                            error = "gameNumber is required when this lobby has multiple games.",
                        });
                    }
                }

                var gameStatus = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT status FROM br_games WHERE id = @targetGameId",
                    new { targetGameId });
                if (string.Equals(gameStatus, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Conflict(new
                    {
                        error = "This game is completed. Re-open the game before approving evidence.",
                    });
                }

                var rosterSize = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM br_group_teams WHERE group_id = @groupId",
                    new { groupId });

                if (rosterSize <= 0)
                    return Results.BadRequest(new { error = "This group has no assigned teams or participants." });

                var catalogBrConfig = await LoadCatalogBrConfigAsync(catalog, roundInfo.Game, ct);
                var scoring = BrConfigService.ResolveScoring(roundInfo.Settings, roundInfo.StageConfig, catalogBrConfig);

                using var tx = conn.BeginTransaction();
                try
                {
                    var outcome = await BrEvidenceApprovalService.ApproveAndApplyResultAsync(
                        conn,
                        lobbyId,
                        entityId,
                        targetGameId.Value,
                        groupId,
                        scoring,
                        rosterSize,
                        userCtx.UserIdGuid,
                        tx);

                    switch (outcome.Status)
                    {
                        case BrEvidenceApprovalStatus.NotFound:
                            tx.Rollback();
                            return Results.NotFound(new { error = "Evidence submission not found." });
                        case BrEvidenceApprovalStatus.MissingReportedStats:
                            tx.Rollback();
                            return Results.BadRequest(new
                            {
                                error = "This submission is missing placement or kills. Ask the player to resubmit evidence.",
                            });
                        case BrEvidenceApprovalStatus.InvalidStats:
                            tx.Rollback();
                            return Results.BadRequest(new
                            {
                                error = $"Reported placement must be between 1 and {rosterSize}, and kills must be >= 0.",
                            });
                        case BrEvidenceApprovalStatus.PlacementConflict:
                            tx.Rollback();
                            return Results.Conflict(new
                            {
                                error = $"Placement #{outcome.Placement} is already assigned to another player. Adjust results or reopen the conflicting submission.",
                            });
                        case BrEvidenceApprovalStatus.RosterMismatch:
                            tx.Rollback();
                            return Results.Conflict(new { error = "This submission does not match the current group roster." });
                        case BrEvidenceApprovalStatus.AlreadyApproved:
                            tx.Rollback();
                            return Results.Ok(new
                            {
                                success = true,
                                approved = true,
                                reviewed = true,
                                alreadyApproved = true,
                            });
                    }

                    tx.Commit();
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    tx.Rollback();
                    return Results.Conflict(new
                    {
                        error = "Could not apply this result because it conflicts with an existing placement or player row.",
                    });
                }
                catch (Exception ex)
                {
                    tx.Rollback();
                    Console.Error.WriteLine(
                        $"[BRGroupEndpoints] Failed to approve evidence for lobby {lobbyId}, entity {entityId}, gameNumber={gameNumber}: {ex}");
                    throw;
                }

                var pendingCount = await BrEvidenceService.CountPendingAsync(conn, lobbyId);
                var lobbyGroupIds = await GetLobbyGroupIdsAsync(conn, lobbyId, groupId);
                var reviewPayload = new
                {
                    stageId = stageId.ToString(),
                    groupId = groupId.ToString(),
                    lobbyId = lobbyId.ToString(),
                    entityId = entityId.ToString(),
                    reviewed = true,
                    approved = true,
                    pendingCount,
                    gameNumber,
                };
                await BroadcastBrToLobbyGroupsAsync(
                    brHub,
                    BRHubEvents.EvidenceReviewed,
                    stageId,
                    lobbyId,
                    lobbyGroupIds,
                    reviewPayload,
                    ct);

                var resultsPayload = new
                {
                    stageId = stageId.ToString(),
                    groupId = groupId.ToString(),
                    lobbyId = lobbyId.ToString(),
                    saved = 1,
                    gameNumber,
                };
                await BroadcastBrAsync(brHub, BRHubEvents.ResultsUpdated, stageId, groupId, lobbyId, resultsPayload, ct);
                await BroadcastBrAsync(
                    brHub,
                    BRHubEvents.LeaderboardUpdated,
                    stageId,
                    groupId,
                    lobbyId,
                    BuildLeaderboardEvent(stageId, groupId),
                    ct);

                return Results.Ok(new { success = true, approved = true, reviewed = true });
            }

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var reopenGameId = await BrGameRouteHelper.ResolveTargetGameIdAsync(
                conn, lobbyId, gameNumber: gameNumber);
            if (reopenGameId is null)
                return Results.NotFound(new { error = "No game found for this lobby." });

            var updated = await conn.ExecuteAsync(
                """
                UPDATE br_lobby_evidence
                SET reviewed = FALSE,
                    reviewed_at = NULL,
                    reviewed_by = NULL
                WHERE game_id = @targetGameId
                  AND (team_id = @entityId OR participant_id = @entityId)
                """,
                new
                {
                    targetGameId = reopenGameId,
                    entityId,
                });

            if (updated == 0)
                return Results.NotFound(new { error = "Evidence submission not found." });

            var pendingAfterReopen = await BrEvidenceService.CountPendingAsync(conn, lobbyId);
            var reopenLobbyGroupIds = await GetLobbyGroupIdsAsync(conn, lobbyId, groupId);
            var reopenPayload = new
            {
                stageId = stageId.ToString(),
                groupId = groupId.ToString(),
                lobbyId = lobbyId.ToString(),
                entityId = entityId.ToString(),
                reviewed = false,
                pendingCount = pendingAfterReopen,
                gameNumber,
            };
            await BroadcastBrToLobbyGroupsAsync(
                brHub,
                BRHubEvents.EvidenceReviewed,
                stageId,
                lobbyId,
                reopenLobbyGroupIds,
                reopenPayload,
                ct);

            return Results.Ok(new { success = true, reviewed = false });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/br/lobbies/{lobbyId}/readiness ──────────────────────────
        app.MapGet("/api/br/lobbies/{lobbyId}/readiness", async (
            Guid lobbyId,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var roundInfo = await BrLobbyRepository.GetContextAsync(conn, lobbyId);
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = roundInfo.StageId;
            var groupId = roundInfo.GroupId ?? Guid.Empty;
            var tournamentId = roundInfo.TournamentId;
            var isSolo = roundInfo.TeamSize == 1;

            var isStaff = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            isStaff = isStaff || StaffAuthHelper.IsPlatformAdmin(userCtx);

            if (!isStaff && (string)roundInfo.Status != "active")
                return Results.Forbid();

            var entityAccess = await ResolveRoundEntityAccessAsync(
                conn, lobbyId, tournamentId, userCtx.UserIdGuid, isSolo);
            if (!isStaff && entityAccess.TeamId is null && entityAccess.ParticipantId is null)
                return Results.Forbid();

            var entries = await BrLobbyReadinessRepository.ListAsync(conn, lobbyId);
            var assignedCount = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)::int
                FROM br_lobby_groups lg
                JOIN br_group_teams bgt ON bgt.group_id = lg.group_id
                WHERE lg.lobby_id = @lobbyId
                """,
                new { lobbyId });

            var viewerEntityId = entityAccess.TeamId ?? entityAccess.ParticipantId;
            var isReady = viewerEntityId is not null
                && entries.Any(entry =>
                    (entityAccess.TeamId is not null && entry.TeamId == entityAccess.TeamId.Value.ToString())
                    || (entityAccess.ParticipantId is not null && entry.ParticipantId == entityAccess.ParticipantId.Value.ToString()));

            return Results.Ok(new
            {
                readyCount = entries.Count,
                totalAssigned = assignedCount,
                isReady,
                entries = isStaff
                    ? entries.Select(entry => new
                    {
                        userId = entry.UserId,
                        displayName = entry.DisplayName,
                        teamId = entry.TeamId,
                        participantId = entry.ParticipantId,
                        checkedInAt = entry.CheckedInAt,
                    })
                    : null,
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/br/lobbies/{lobbyId}/readiness ─────────────────────────
        app.MapPost("/api/br/lobbies/{lobbyId}/readiness", async (
            Guid lobbyId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<BRHub> brHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var roundInfo = await BrLobbyRepository.GetContextAsync(conn, lobbyId);
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            if ((string)roundInfo.Status != "active")
                return Results.Conflict(new { error = "Readiness check-in is only available while the lobby is live." });

            var stageId = roundInfo.StageId;
            var groupId = roundInfo.GroupId ?? Guid.Empty;
            var tournamentId = roundInfo.TournamentId;
            var isSolo = roundInfo.TeamSize == 1;

            var entityAccess = await ResolveRoundEntityAccessAsync(
                conn, lobbyId, tournamentId, userCtx.UserIdGuid, isSolo);
            if (entityAccess.TeamId is null && entityAccess.ParticipantId is null)
                return Results.Forbid();

            await BrLobbyReadinessRepository.UpsertAsync(
                conn,
                lobbyId,
                userCtx.UserIdGuid,
                entityAccess.TeamId,
                entityAccess.ParticipantId);

            var entries = await BrLobbyReadinessRepository.ListAsync(conn, lobbyId);
            var assignedCount = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)::int
                FROM br_lobby_groups lg
                JOIN br_group_teams bgt ON bgt.group_id = lg.group_id
                WHERE lg.lobby_id = @lobbyId
                """,
                new { lobbyId });

            var payload = new
            {
                stageId = stageId.ToString(),
                groupId = groupId.ToString(),
                lobbyId = lobbyId.ToString(),
                readyCount = entries.Count,
                totalAssigned = assignedCount,
            };
            await BroadcastBrAsync(brHub, BRHubEvents.LobbyReadinessUpdated, stageId, groupId, lobbyId, payload, ct);

            return Results.Ok(new { success = true, readyCount = entries.Count, totalAssigned = assignedCount });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/br/lobbies/{lobbyId}/readiness ───────────────────────
        app.MapDelete("/api/br/lobbies/{lobbyId}/readiness", async (
            Guid lobbyId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<BRHub> brHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var roundInfo = await BrLobbyRepository.GetContextAsync(conn, lobbyId);
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = roundInfo.StageId;
            var groupId = roundInfo.GroupId ?? Guid.Empty;
            var tournamentId = roundInfo.TournamentId;
            var isSolo = roundInfo.TeamSize == 1;

            var entityAccess = await ResolveRoundEntityAccessAsync(
                conn, lobbyId, tournamentId, userCtx.UserIdGuid, isSolo);
            if (entityAccess.TeamId is null && entityAccess.ParticipantId is null)
                return Results.Forbid();

            await BrLobbyReadinessRepository.DeleteForEntityAsync(
                conn, lobbyId, entityAccess.TeamId, entityAccess.ParticipantId);

            var entries = await BrLobbyReadinessRepository.ListAsync(conn, lobbyId);
            var assignedCount = await conn.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)::int
                FROM br_lobby_groups lg
                JOIN br_group_teams bgt ON bgt.group_id = lg.group_id
                WHERE lg.lobby_id = @lobbyId
                """,
                new { lobbyId });

            var payload = new
            {
                stageId = stageId.ToString(),
                groupId = groupId.ToString(),
                lobbyId = lobbyId.ToString(),
                readyCount = entries.Count,
                totalAssigned = assignedCount,
            };
            await BroadcastBrAsync(brHub, BRHubEvents.LobbyReadinessUpdated, stageId, groupId, lobbyId, payload, ct);

            return Results.Ok(new { success = true, readyCount = entries.Count, totalAssigned = assignedCount });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/br/lobbies/{lobbyId}/results ────────────────────────────
        // Bulk submit/update results for a round (idempotent upsert).
        app.MapPut("/api/br/lobbies/{lobbyId}/results", async (
            Guid                lobbyId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db,
            GameCatalogService   catalog,
            IHubContext<BRHub>   brHub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var roundInfo = await BrLobbyRepository.GetContextAsync(conn, lobbyId, includeStageConfig: true);
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            if (roundInfo.GroupId is null)
            {
                return Results.Conflict(new { error = "This BR round has inconsistent metadata. Refresh and try again." });
            }

            var stageId = roundInfo.StageId;
            var groupId = roundInfo.GroupId.Value;
            var roundStatus = roundInfo.Status;
            var gameName = roundInfo.Game;
            var rawSettings = roundInfo.Settings;
            var stageConfig = roundInfo.StageConfig;

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermScoresUpdate);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            if (roundStatus == "completed")
                return Results.Conflict(new { error = "Completed rounds are locked. Re-open the round before editing results." });

            if (body.ValueKind != JsonValueKind.Object ||
                !body.TryGetProperty("results", out var resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "results must be an array." });

            var groupTeamsHasParticipantId = await BrSchemaRepository.ColumnExistsAsync(conn, "br_group_teams", "participant_id");
            var roundResultsHasParticipantId = await BrSchemaRepository.ColumnExistsAsync(conn, "br_lobby_results", "participant_id");
            var roundResultsTeamIdAllowsNull = await ColumnAllowsNullAsync(conn, "br_lobby_results", "team_id");
            var canPersistParticipantBackedResults = roundResultsHasParticipantId && roundResultsTeamIdAllowsNull;

            var rosterRows = await conn.QueryAsync<dynamic>(
                groupTeamsHasParticipantId
                    ? """
                      SELECT COALESCE(team_id, participant_id) AS entity_id,
                             team_id,
                             participant_id
                      FROM br_group_teams
                      WHERE group_id = @groupId
                      ORDER BY seed_order
                      """
                    : """
                      SELECT team_id AS entity_id,
                             team_id,
                             NULL::uuid AS participant_id
                      FROM br_group_teams
                      WHERE group_id = @groupId
                      ORDER BY seed_order
                      """,
                new { groupId });

            var rosterEntities = new List<(Guid EntityId, Guid? TeamId, Guid? ParticipantId)>();
            foreach (var row in rosterRows)
            {
                var values = (IDictionary<string, object>)row;
                if (!TryReadGuidValue(values, "entity_id", out var entityId)
                    || !TryReadGuidValue(values, "team_id", out var teamId)
                    || !TryReadGuidValue(values, "participant_id", out var participantId)
                    || entityId is null)
                {
                    return Results.Conflict(new { error = "This BR group has inconsistent roster data. Reassign the group roster and try again." });
                }

                rosterEntities.Add((entityId.Value, teamId, participantId));
            }
            if (rosterEntities.Count == 0)
                return Results.BadRequest(new { error = "This group has no assigned teams or participants." });

            if (rosterEntities.Any(entity =>
                    entity.EntityId == Guid.Empty
                    || (entity.TeamId is null && entity.ParticipantId is null)
                    || (entity.TeamId is not null && entity.ParticipantId is not null)))
            {
                return Results.Conflict(new { error = "This BR group has inconsistent roster data. Reassign the group roster and try again." });
            }

            var duplicateRosterEntityIds = rosterEntities
                .GroupBy(entity => entity.EntityId)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicateRosterEntityIds.Count > 0)
                return Results.Conflict(new { error = "This BR group contains duplicate roster entries. Reassign the group roster and try again." });

            var rosterByEntityId = rosterEntities.ToDictionary(
                entity => entity.EntityId,
                entity => (entity.TeamId, entity.ParticipantId));

            var catalogBrConfig = await LoadCatalogBrConfigAsync(catalog, gameName, ct);
            var scoring = BrConfigService.ResolveScoring(rawSettings, stageConfig, catalogBrConfig);

            var parsedResults = new List<(Guid EntityId, int Placement, int Kills)>();
            var seenEntityIds = new HashSet<Guid>();
            var seenPlacements = new HashSet<int>();
            foreach (var r in resultsElement.EnumerateArray())
            {
                var teamIdStr = r.TryGetProperty("teamId", out var tid) ? tid.GetString() : null;
                if (teamIdStr is null || !Guid.TryParse(teamIdStr, out var entityId))
                    return Results.BadRequest(new { error = $"Invalid teamId: {teamIdStr}" });

                if (!seenEntityIds.Add(entityId))
                    return Results.BadRequest(new { error = $"Duplicate result submitted for entity {teamIdStr}." });

                var placement = r.TryGetProperty("placement", out var pl) && pl.TryGetInt32(out var plv) ? plv : 0;
                var kills = r.TryGetProperty("kills", out var kl) && kl.TryGetInt32(out var klv) ? klv : 0;

                if (placement < 1)
                    return Results.BadRequest(new { error = $"placement must be >= 1 for team {teamIdStr}" });
                if (kills < 0)
                    return Results.BadRequest(new { error = $"kills must be >= 0 for team {teamIdStr}" });
                if (placement > rosterEntities.Count)
                    return Results.BadRequest(new { error = $"placement must be between 1 and {rosterEntities.Count} for team {teamIdStr}" });
                if (!seenPlacements.Add(placement))
                    return Results.BadRequest(new { error = $"Duplicate placement submitted: {placement}." });

                parsedResults.Add((entityId, placement, kills));
            }

            if (parsedResults.Count != rosterEntities.Count)
                return Results.BadRequest(new
                {
                    error = $"Results must include every team or participant in the round ({rosterEntities.Count} required, received {parsedResults.Count})."
                });

            var unexpectedEntityIds = parsedResults
                .Select(result => result.EntityId)
                .Where(entityId => !rosterByEntityId.ContainsKey(entityId))
                .Distinct()
                .ToList();
            if (unexpectedEntityIds.Count > 0)
                return Results.BadRequest(new { error = "Results contain entities that are not assigned to this BR group." });

            var requiredPlacements = Enumerable.Range(1, rosterEntities.Count).ToHashSet();
            if (!requiredPlacements.SetEquals(parsedResults.Select(result => result.Placement)))
                return Results.BadRequest(new { error = $"Placements must be a complete set from 1 to {rosterEntities.Count}." });

            int? requestGameNumber = null;
            if (body.TryGetProperty("gameNumber", out var gameNumberEl) && gameNumberEl.TryGetInt32(out var parsedGameNumber))
                requestGameNumber = parsedGameNumber;

            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(
                    "SELECT 1 FROM br_lobbies WHERE id = @lobbyId FOR UPDATE",
                    new { lobbyId },
                    tx);

                var targetGameId = await BrGameRouteHelper.ResolveTargetGameIdAsync(
                    conn, lobbyId, gameNumber: requestGameNumber, tx: tx);
                if (targetGameId is null)
                {
                    var gamesPerLobby = BrConfigService.ResolveGamesPerLobby(rawSettings, stageConfig) ?? 6;
                    await BrGameRepository.EnsureGamesForLobbyAsync(
                        conn, lobbyId, gamesPerLobby, rawSettings, stageConfig, catalogBrConfig, tx);
                    targetGameId = await BrGameRouteHelper.ResolveTargetGameIdAsync(
                        conn, lobbyId, gameNumber: requestGameNumber ?? 1, tx: tx);
                }

                if (targetGameId is null)
                {
                    tx.Rollback();
                    return Results.Conflict(new { error = "This lobby has no games configured yet." });
                }

                await conn.ExecuteAsync(
                    "DELETE FROM br_lobby_results WHERE game_id = @targetGameId",
                    new { targetGameId },
                    tx);

                var materializedResults = parsedResults
                    .Select(result =>
                    {
                        var points = BrConfigService.CalculatePoints(result.Placement, result.Kills, scoring);
                        var rosterEntity = rosterByEntityId[result.EntityId];
                        return new
                        {
                            gameId = targetGameId,
                            lobbyId,
                            teamId = rosterEntity.TeamId,
                            participantId = rosterEntity.ParticipantId,
                            placement = result.Placement,
                            kills = result.Kills,
                            placementPoints = points.Item1,
                            killPoints = points.Item2
                        };
                    })
                    .ToList();

                var teamBackedResults = materializedResults
                    .Where(result => result.teamId is not null)
                    .Select(result => new
                    {
                        gameId = result.gameId,
                        result.lobbyId,
                        result.teamId,
                        result.placement,
                        result.kills,
                        result.placementPoints,
                        result.killPoints
                    })
                    .ToList();

                var participantBackedResults = materializedResults
                    .Where(result => result.teamId is null && result.participantId is not null)
                    .Select(result => new
                    {
                        gameId = result.gameId,
                        result.lobbyId,
                        result.participantId,
                        result.placement,
                        result.kills,
                        result.placementPoints,
                        result.killPoints
                    })
                    .ToList();

                List<object>? participantFallbackTeamResults = null;
                if (!canPersistParticipantBackedResults && participantBackedResults.Count > 0)
                {
                    var participantIds = participantBackedResults
                        .Select(result => result.participantId)
                        .OfType<Guid>()
                        .Distinct()
                        .ToArray();

                    var participantTeamRows = await conn.QueryAsync<dynamic>(
                        """
                        SELECT id, team_id
                        FROM tournament_participants
                        WHERE id = ANY(@participantIds)
                        """,
                        new { participantIds },
                        tx);

                    var participantTeamMap = new Dictionary<Guid, Guid?>();
                    foreach (var row in participantTeamRows)
                    {
                        var values = (IDictionary<string, object>)row;
                        if (!TryReadGuidValue(values, "id", out var participantId)
                            || !TryReadGuidValue(values, "team_id", out var teamId)
                            || participantId is null)
                        {
                            return Results.Conflict(new { error = "The submitted BR roster could not be resolved to tournament participants." });
                        }

                        participantTeamMap[participantId.Value] = teamId;
                    }

                    if (participantIds.Length != participantTeamMap.Count
                        || participantTeamMap.Any(entry => entry.Value is null))
                    {
                        return Results.Conflict(new
                        {
                            error = "This BR environment cannot save solo round results until the solo participant schema is fully available."
                        });
                    }

                    participantFallbackTeamResults = participantBackedResults
                        .Select(result => (object)new
                        {
                            gameId = result.gameId,
                            result.lobbyId,
                            teamId = participantTeamMap[result.participantId!.Value]!.Value,
                            result.placement,
                            result.kills,
                            result.placementPoints,
                            result.killPoints
                        })
                        .ToList();
                }

                var allTeamBackedResults = teamBackedResults
                    .Cast<object>()
                    .Concat(participantFallbackTeamResults ?? Enumerable.Empty<object>())
                    .ToList();

                if (allTeamBackedResults.Count > 0)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_lobby_results (game_id, lobby_id, team_id, placement, kills, placement_points, kill_points)
                        VALUES (@gameId, @lobbyId, @teamId, @placement, @kills, @placementPoints, @killPoints)
                        """,
                        allTeamBackedResults,
                        tx);
                }

                if (teamBackedResults.Count + participantBackedResults.Count != materializedResults.Count)
                    return Results.Conflict(new { error = "This BR group has inconsistent roster data. Reassign the group roster and try again." });

                if (canPersistParticipantBackedResults && participantBackedResults.Count > 0)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_lobby_results (game_id, lobby_id, participant_id, placement, kills, placement_points, kill_points)
                        VALUES (@gameId, @lobbyId, @participantId, @placement, @kills, @placementPoints, @killPoints)
                        """,
                        participantBackedResults,
                        tx);
                }

                tx.Commit();

                var resultsPayload = new
                {
                    stageId = stageId.ToString(),
                    groupId = groupId.ToString(),
                    lobbyId = lobbyId.ToString(),
                    saved = parsedResults.Count,
                };
                await BroadcastBrAsync(brHub, BRHubEvents.ResultsUpdated, stageId, groupId, lobbyId, resultsPayload, ct);
                await BroadcastBrAsync(
                    brHub,
                    BRHubEvents.LeaderboardUpdated,
                    stageId,
                    groupId,
                    lobbyId,
                    BuildLeaderboardEvent(stageId, groupId),
                    ct);

                return Results.Ok(new { saved = parsedResults.Count });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                                               && ex.ConstraintName == "uq_br_lobby_results_round_placement")
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "Each placement can only be assigned once in a round." });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                                               || ex.SqlState == PostgresErrorCodes.CheckViolation)
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "The submitted results do not match the current BR roster. Refresh the page and try again." });
            }
            catch (PostgresException ex)
            {
                tx.Rollback();
                Console.Error.WriteLine(
                    $"[BRGroupEndpoints] Failed to save results for round {lobbyId}. " +
                    $"Postgres {ex.SqlState} {ex.ConstraintName} {ex.TableName}.{ex.ColumnName} :: {ex.MessageText} :: {ex.Detail}");

                var env = ctx.RequestServices.GetService<IWebHostEnvironment>();
                if (env is not null && !env.IsProduction())
                {
                    return Results.Json(new
                    {
                        error = "BR results save failed.",
                        exception = nameof(PostgresException),
                        sqlState = ex.SqlState,
                        constraint = ex.ConstraintName,
                        table = ex.TableName,
                        column = ex.ColumnName,
                        detail = ex.Detail,
                        message = ex.MessageText,
                        hint = ex.Hint
                    }, statusCode: StatusCodes.Status500InternalServerError);
                }

                throw;
            }
            catch (Exception ex)
            {
                tx.Rollback();
                Console.Error.WriteLine($"[BRGroupEndpoints] Failed to save results for round {lobbyId}. {ex}");

                var env = ctx.RequestServices.GetService<IWebHostEnvironment>();
                if (env is not null && !env.IsProduction())
                {
                    return Results.Json(new
                    {
                        error = "BR results save failed.",
                        exception = ex.GetType().FullName,
                        message = ex.Message
                    }, statusCode: StatusCodes.Status500InternalServerError);
                }

                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/stages/{stageId}/br/groups/{groupId}/leaderboard ───────
        // Aggregate leaderboard from all round results in a group. Public endpoint.
        app.MapGet("/api/stages/{stageId}/br/groups/{groupId}/leaderboard", async (
            Guid              stageId,
            Guid              groupId,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewStagePublicDataAsync(conn, ctx, stageId))
                return Results.NotFound();

            // Verify groupId belongs to this stageId
            var groupExists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM br_groups WHERE id = @groupId AND stage_id = @stageId)",
                new { groupId, stageId });
            if (!groupExists)
                return Results.NotFound(new { error = "Group not found in this stage." });

            var stageMeta = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT t.settings
                FROM tournament_stages ts
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.id = @stageId
                """,
                new { stageId });
            var tiebreaker = BrConfigService.ResolveTiebreaker(stageMeta?.settings);

            var roundResultsHasParticipantId = await BrSchemaRepository.ColumnExistsAsync(conn, "br_lobby_results", "participant_id");

            // Unified leaderboard with COALESCE for team/solo
            var leaderboardRows = (await conn.QueryAsync<dynamic>(
                roundResultsHasParticipantId
                    ? """
                      SELECT
                          COALESCE(rr.team_id, rr.participant_id) AS team_id,
                          CASE
                              WHEN rr.team_id IS NOT NULL THEN t.name
                              ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                          END AS team_name,
                          CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
                          COUNT(DISTINCT rr.game_id) AS games_played,
                          SUM(rr.placement_points) AS total_placement_points,
                          SUM(rr.kill_points) AS total_kill_points,
                          SUM(rr.total_points) AS total_points,
                          SUM(rr.kills) AS total_kills,
                          COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
                          MIN(rr.placement) AS best_placement,
                          AVG(rr.placement::numeric) AS avg_placement
                      FROM br_lobby_results rr
                      JOIN br_games g ON g.id = rr.game_id
                      JOIN br_lobbies r ON r.id = g.lobby_id
                      LEFT JOIN teams t ON t.id = rr.team_id
                      LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                      LEFT JOIN profiles p ON p.id = tp.user_id
                      WHERE EXISTS (SELECT 1 FROM br_lobby_groups lg WHERE lg.lobby_id = r.id AND lg.group_id = @groupId)
                        AND g.status = 'completed'
                      GROUP BY COALESCE(rr.team_id, rr.participant_id),
                               CASE
                                   WHEN rr.team_id IS NOT NULL THEN t.name
                                   ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                               END,
                               CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END
                      """
                    : """
                      SELECT
                          rr.team_id AS team_id,
                          t.name AS team_name,
                          t.logo_url AS logo_url,
                          COUNT(DISTINCT rr.game_id) AS games_played,
                          SUM(rr.placement_points) AS total_placement_points,
                          SUM(rr.kill_points) AS total_kill_points,
                          SUM(rr.total_points) AS total_points,
                          SUM(rr.kills) AS total_kills,
                          COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
                          MIN(rr.placement) AS best_placement,
                          AVG(rr.placement::numeric) AS avg_placement
                      FROM br_lobby_results rr
                      JOIN br_games g ON g.id = rr.game_id
                      JOIN br_lobbies r ON r.id = g.lobby_id
                      LEFT JOIN teams t ON t.id = rr.team_id
                      WHERE EXISTS (SELECT 1 FROM br_lobby_groups lg WHERE lg.lobby_id = r.id AND lg.group_id = @groupId)
                        AND g.status = 'completed'
                      GROUP BY rr.team_id, t.name, t.logo_url
                      """,
                new { groupId })).ToList();

            leaderboardRows.Sort((a, b) =>
            {
                var aggregateA = new BrLeaderboardAggregate(
                    Convert.ToInt64(a.total_points),
                    Convert.ToInt64(a.wins),
                    Convert.ToInt64(a.total_kills),
                    a.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(a.avg_placement));
                var aggregateB = new BrLeaderboardAggregate(
                    Convert.ToInt64(b.total_points),
                    Convert.ToInt64(b.wins),
                    Convert.ToInt64(b.total_kills),
                    b.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(b.avg_placement));
                return BrConfigService.CompareLeaderboardEntries(aggregateA, aggregateB, tiebreaker);
            });

            var leaderboard = leaderboardRows.Select(row => new
                {
                    team_id = row.team_id,
                    team_name = row.team_name,
                    logo_url = row.logo_url,
                    games_played = row.games_played,
                    total_placement_points = row.total_placement_points,
                    total_kill_points = row.total_kill_points,
                    total_points = row.total_points,
                    total_kills = row.total_kills,
                    wins = row.wins,
                    best_placement = row.best_placement,
                });

            return Results.Ok(leaderboard);
        });

        // ── GET /api/tournaments/{tournamentId}/br/player-context ────────────
        // Returns the calling user's group context within any BR stage of this
        // tournament: which stage, which group, round counts, and the active round
        // (including lobby code, but only for active rounds).
        app.MapGet("/api/tournaments/{tournamentId}/br/player-context", async (
            Guid              tournamentId,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var groupInfo = await BrPlayerContextService.FindPlayerGroupAsync(
                conn, tournamentId, userCtx.UserIdGuid);

            // Not in a group — return minimal response
            if (groupInfo is null)
            {
                var assignmentHint = await BrPlayerContextService.ResolveAssignmentHintAsync(
                    conn, tournamentId, userCtx.UserIdGuid);

                return Results.Ok(new
                {
                    stageId          = (string?)null,
                    stageName        = (string?)null,
                    groupId          = (string?)null,
                    groupName        = (string?)null,
                    gamesModelActive = false,
                    totalRounds      = 0,
                    completedRounds  = 0,
                    activeRound      = (object?)null,
                    assignmentHint,
                });
            }

            Guid groupId = groupInfo.GroupId;
            var stageId = groupInfo.StageId;

            var stageMeta = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ts.config::text AS stage_config, t.settings
                FROM tournament_stages ts
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.id = @stageId
                """,
                new { stageId });
            var gamesPerLobby = BrConfigService.ResolveGamesPerLobby(
                stageMeta?.settings, stageMeta?.stage_config) ?? 6;
            var gamesModelActive = await BrSchemaRepository.BrGamesModelReadyAsync(conn);

            // ── Fetch lobbies linked to the player's seed group ────────────────
            var rounds = (await conn.QueryAsync<dynamic>(
                """
                SELECT l.id, l.wave_number, l.lobby_index, l.lobby_code, l.status, l.scheduled_at, l.started_at, l.completed_at,
                       l.queue_timer_minutes, l.queue_started_at,
                       (
                           SELECT string_agg(g2.name, ' + ' ORDER BY g2.group_order)
                           FROM br_lobby_groups lg2
                           JOIN br_groups g2 ON g2.id = lg2.group_id
                           WHERE lg2.lobby_id = l.id
                       ) AS matchup_label
                FROM br_lobbies l
                JOIN br_lobby_groups lg ON lg.lobby_id = l.id
                WHERE lg.group_id = @groupId
                ORDER BY l.wave_number, l.lobby_index
                """,
                new { groupId })).ToList();

            var lobbyIds = rounds.Select(r => (Guid)r.id).ToList();
            var games = lobbyIds.Count == 0
                ? new List<dynamic>()
                : (await conn.QueryAsync<dynamic>(
                    """
                    SELECT g.id, g.lobby_id, g.game_number, g.map, g.status,
                           g.scheduled_at, g.started_at, g.completed_at,
                           g.queue_timer_minutes, g.queue_started_at
                    FROM br_games g
                    WHERE g.lobby_id = ANY(@lobbyIds)
                    ORDER BY g.lobby_id, g.game_number
                    """,
                    new { lobbyIds })).ToList();

            int totalRounds     = rounds.Count;
            int completedRounds = rounds.Count(r => (string)r.status == "completed");
            int totalGames      = games.Count;
            int completedGames  = games.Count(g => (string)g.status == "completed");

            static DateTimeOffset? ReadRoundTimestamp(dynamic round, string key)
            {
                var value = ((IDictionary<string, object>)round)[key];
                return value switch
                {
                    DateTimeOffset dto => dto,
                    DateTime dt => new DateTimeOffset(dt),
                    string text when DateTimeOffset.TryParse(text, out var parsed) => parsed,
                    _ => null
                };
            }

            static bool HasRoundLobbyCode(dynamic round)
            {
                var value = ((IDictionary<string, object>)round)["lobby_code"] as string;
                return !string.IsNullOrWhiteSpace(value);
            }

            // ── Check if user is staff (staff see all data regardless of status) ──
            bool isStaff = StaffAuthHelper.IsPlatformAdmin(userCtx);
            if (!isStaff)
            {
                var stageIdForCheck = groupInfo.StageId;
                isStaff = await StaffAuthHelper.CanActOnStageAsync(
                    conn, userCtx.UserIdGuid, stageIdForCheck, StaffAuthHelper.PermBracketEdit);
            }

            var activeGameRow = games
                .Where(g => (string)g.status == "active")
                .OrderBy(g => Convert.ToInt32(g.game_number))
                .FirstOrDefault();

            var activeLobbyId = activeGameRow is not null
                ? (Guid)activeGameRow.lobby_id
                : rounds
                    .Where(r => (string)r.status == "active")
                    .Select(r => (Guid)r.id)
                    .FirstOrDefault();

            // ── Build active round payload ───────────────────────────────────
            var activeRoundRow = activeLobbyId != Guid.Empty
                ? rounds.FirstOrDefault(r => (Guid)r.id == activeLobbyId)
                : rounds
                    .Where(r => (string)r.status == "active")
                    .OrderByDescending(r => ReadRoundTimestamp(r, "queue_started_at") ?? DateTimeOffset.MinValue)
                    .ThenByDescending(r => HasRoundLobbyCode(r))
                    .ThenByDescending(r => ReadRoundTimestamp(r, "started_at") ?? DateTimeOffset.MinValue)
                    .ThenByDescending(r => Convert.ToInt32(r.wave_number))
                    .FirstOrDefault();

            object? activeRoundPayload = null;
            object? activeGamePayload = null;
            if (activeRoundRow is not null)
            {
                var activeLobbyGuid = (Guid)activeRoundRow.id;
                var lobbyCode = isStaff || (string)activeRoundRow.status == "active"
                    ? (string?)activeRoundRow.lobby_code
                    : null;

                var currentGame = activeGameRow
                    ?? games.FirstOrDefault(g => (Guid)g.lobby_id == activeLobbyGuid && (string)g.status != "completed");
                var liveGame = currentGame is not null && (string)currentGame.status == "active"
                    ? currentGame
                    : null;
                var activeRoundMap = liveGame is not null ? (string?)liveGame.map : null;
                var useGameFields = gamesModelActive;

                string? activeRoundScheduledAt = null;
                if (useGameFields && currentGame is not null && currentGame.scheduled_at is not null)
                {
                    activeRoundScheduledAt = ((DateTimeOffset)currentGame.scheduled_at).ToString("o");
                }
                else if (!useGameFields && activeRoundRow.scheduled_at is not null)
                {
                    activeRoundScheduledAt = ((DateTimeOffset)activeRoundRow.scheduled_at).ToString("o");
                }

                activeRoundPayload = new
                {
                    id = activeLobbyGuid.ToString(),
                    lobbyId = activeLobbyGuid.ToString(),
                    waveNumber = Convert.ToInt32(activeRoundRow.wave_number),
                    matchupLabel = (string?)activeRoundRow.matchup_label,
                    lobbyCode,
                    status = (string)activeRoundRow.status,
                    queueTimerMinutes = useGameFields
                        ? null
                        : activeRoundRow.queue_timer_minutes is not null
                            ? Convert.ToInt32(activeRoundRow.queue_timer_minutes)
                            : (int?)null,
                    queueStartedAt = useGameFields
                        ? null
                        : activeRoundRow.queue_started_at is not null
                            ? ((DateTimeOffset)activeRoundRow.queue_started_at).ToString("o")
                            : (string?)null,
                    scheduledAt = activeRoundScheduledAt,
                    map = activeRoundMap,
                };

                if (liveGame is not null)
                {
                    activeGamePayload = new
                    {
                        id = ((Guid)liveGame.id).ToString(),
                        lobbyId = activeLobbyGuid.ToString(),
                        gameNumber = Convert.ToInt32(liveGame.game_number),
                        map = (string?)liveGame.map,
                        status = (string)liveGame.status,
                        scheduledAt = liveGame.scheduled_at is not null
                            ? ((DateTimeOffset)liveGame.scheduled_at).ToString("o")
                            : (string?)null,
                        queueTimerMinutes = liveGame.queue_timer_minutes is not null
                            ? Convert.ToInt32(liveGame.queue_timer_minutes)
                            : (int?)null,
                        queueStartedAt = liveGame.queue_started_at is not null
                            ? ((DateTimeOffset)liveGame.queue_started_at).ToString("o")
                            : (string?)null,
                    };
                }
            }

            var lobbySchedules = rounds.Select(r =>
            {
                var lobbyGuid = (Guid)r.id;
                var lobbyGames = games
                    .Where(g => (Guid)g.lobby_id == lobbyGuid)
                    .OrderBy(g => Convert.ToInt32(g.game_number))
                    .Select(g => new
                    {
                        id = ((Guid)g.id).ToString(),
                        gameNumber = Convert.ToInt32(g.game_number),
                        map = (string?)g.map,
                        status = (string)g.status,
                        scheduledAt = g.scheduled_at is not null
                            ? ((DateTimeOffset)g.scheduled_at).ToString("o")
                            : (string?)null,
                        queueTimerMinutes = g.queue_timer_minutes is not null
                            ? Convert.ToInt32(g.queue_timer_minutes)
                            : (int?)null,
                        queueStartedAt = g.queue_started_at is not null
                            ? ((DateTimeOffset)g.queue_started_at).ToString("o")
                            : (string?)null,
                    })
                    .ToList();

                return new
                {
                    lobbyId = lobbyGuid.ToString(),
                    waveNumber = Convert.ToInt32(r.wave_number),
                    matchupLabel = (string?)r.matchup_label,
                    status = (string)r.status,
                    scheduledAt = gamesModelActive
                        ? (string?)null
                        : r.scheduled_at is not null
                            ? ((DateTimeOffset)r.scheduled_at).ToString("o")
                            : (string?)null,
                    games = lobbyGames,
                };
            }).ToList();

            return Results.Ok(new
            {
                stageId = stageId.ToString(),
                stageName = groupInfo.StageName,
                groupId = groupInfo.GroupId.ToString(),
                groupName = groupInfo.GroupName,
                gamesModelActive,
                gamesPerLobby,
                totalRounds,
                completedRounds,
                totalGames,
                completedGames,
                activeRound = activeRoundPayload,
                activeGame = activeGamePayload,
                lobbies = lobbySchedules,
            });
        }).RequireAuthorization("Authenticated");

        app.MapPost("/api/stages/{stageId}/br/schedule/generate", async (
            Guid stageId,
            [FromBody] JsonElement body,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var seedGroupCount = body.TryGetProperty("seedGroupCount", out var sgc) && sgc.TryGetInt32(out var parsedSgc)
                ? parsedSgc
                : 0;
            var groupsPerLobby = body.TryGetProperty("groupsPerLobby", out var gpl) && gpl.TryGetInt32(out var parsedGpl)
                ? parsedGpl
                : 2;
            var matchesPerWave = body.TryGetProperty("matchesPerWave", out var mpw) && mpw.TryGetInt32(out var parsedMpw)
                ? parsedMpw
                : 1;

            if (seedGroupCount < 2)
                return Results.BadRequest(new { error = "seedGroupCount must be >= 2." });

            try
            {
                var manifest = BrScheduleGenerator.GenerateRotatingPairwise(seedGroupCount, groupsPerLobby, matchesPerWave);
                return Results.Ok(manifest);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/stages/{stageId}/br/schedule", async (
            Guid stageId,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var raw = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT config FROM tournament_stages WHERE id = @stageId",
                new { stageId });
            if (raw is null)
                return Results.NotFound(new { error = "Stage not found." });

            JsonElement cfg = default;
            if (TryParseJsonElement(raw.config, out cfg))
            {
                if (TryGetPropertyIgnoreCase(cfg, "br", out var br)
                    && br.ValueKind == JsonValueKind.Object
                    && TryGetPropertyIgnoreCase(br, "lobbyFormation", out var formation))
                {
                    return Results.Ok(formation);
                }
            }

            return Results.Ok(new { });
        });

        app.MapPost("/api/stages/{stageId}/br/schedule", async (
            Guid stageId,
            [FromBody] JsonElement body,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService catalog,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var stage = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT config FROM tournament_stages WHERE id = @stageId",
                new { stageId });
            if (stage is null)
                return Results.NotFound(new { error = "Stage not found." });

            await conn.ExecuteAsync(
                """
                UPDATE tournament_stages
                SET config = jsonb_set(
                    COALESCE(config, '{}'::jsonb),
                    '{br,lobbyFormation}',
                    @lobbyFormation::jsonb,
                    true
                )
                WHERE id = @stageId
                """,
                new
                {
                    stageId,
                    lobbyFormation = body.GetRawText()
                });

            var mergedConfigText = await conn.QuerySingleAsync<string>(
                "SELECT config::text FROM tournament_stages WHERE id = @stageId",
                new { stageId });

            // Optional lobby generation for group_rotation.
            var format = BrConfigService.ResolveFormat(mergedConfigText);
            if (format == BrStageFormat.GroupRotation
                && body.TryGetProperty("waves", out var wavesEl)
                && wavesEl.ValueKind == JsonValueKind.Array)
            {
                using var tx = conn.BeginTransaction();
                try
                {
                    await conn.ExecuteAsync("DELETE FROM br_lobby_groups WHERE lobby_id IN (SELECT id FROM br_lobbies WHERE stage_id = @stageId)", new { stageId }, tx);
                    await conn.ExecuteAsync("DELETE FROM br_lobbies WHERE stage_id = @stageId", new { stageId }, tx);

                    foreach (var waveEl in wavesEl.EnumerateArray())
                    {
                        if (!waveEl.TryGetProperty("wave", out var waveNumberEl) || !waveNumberEl.TryGetInt32(out var waveNumber))
                            continue;
                        if (!waveEl.TryGetProperty("lobbies", out var lobbiesEl) || lobbiesEl.ValueKind != JsonValueKind.Array)
                            continue;

                        var lobbyIndex = 0;
                        foreach (var lobbyGroupsEl in lobbiesEl.EnumerateArray())
                        {
                            var lobbyId = await conn.QuerySingleAsync<Guid>(
                                """
                                INSERT INTO br_lobbies(stage_id, wave_number, lobby_index)
                                VALUES (@stageId, @waveNumber, @lobbyIndex)
                                RETURNING id
                                """,
                                new { stageId, waveNumber, lobbyIndex },
                                tx);

                            if (lobbyGroupsEl.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var labelEl in lobbyGroupsEl.EnumerateArray())
                                {
                                    var label = labelEl.GetString();
                                    if (string.IsNullOrWhiteSpace(label))
                                        continue;
                                    var groupId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                                        "SELECT id FROM br_groups WHERE stage_id = @stageId AND lower(replace(name, 'Group ', '')) = lower(@label) LIMIT 1",
                                        new { stageId, label },
                                        tx);
                                    if (groupId is not null)
                                    {
                                        await conn.ExecuteAsync(
                                            "INSERT INTO br_lobby_groups(lobby_id, group_id) VALUES (@lobbyId, @groupId) ON CONFLICT DO NOTHING",
                                            new { lobbyId, groupId },
                                            tx);
                                    }
                                }
                            }

                            lobbyIndex++;
                        }
                    }

                    var stageContext = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        """
                        SELECT t.settings, ts.config AS stage_config, t.game
                        FROM tournament_stages ts
                        JOIN tournaments t ON t.id = ts.tournament_id
                        WHERE ts.id = @stageId
                        """,
                        new { stageId },
                        tx);
                    var catalogBrConfig = await LoadCatalogBrConfigAsync(
                        catalog, stageContext?.game as string, ct);
                    await BrGameRepository.MaterializeStageGamesAsync(
                        conn,
                        stageId,
                        tournamentSettings: stageContext?.settings,
                        stageConfig: stageContext?.stage_config,
                        catalogBrConfig: catalogBrConfig,
                        tx: tx);

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }

            return Results.Ok(new { committed = true });
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/stages/{stageId}/br/leaderboard", async (
            Guid stageId,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewStagePublicDataAsync(conn, ctx, stageId))
                return Results.NotFound();

            var stageMeta = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT t.settings
                FROM tournament_stages ts
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.id = @stageId
                """,
                new { stageId });
            var tiebreaker = BrConfigService.ResolveTiebreaker(stageMeta?.settings);

            var leaderboardRows = (await conn.QueryAsync<dynamic>(
                """
                SELECT
                    COALESCE(rr.team_id, rr.participant_id) AS team_id,
                    CASE
                        WHEN rr.team_id IS NOT NULL THEN t.name
                        ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                    END AS team_name,
                    CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
                    SUM(rr.total_points) AS total_points,
                    SUM(rr.kills) AS total_kills,
                    COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
                    AVG(rr.placement::numeric) AS avg_placement
                FROM br_lobby_results rr
                JOIN br_games g ON g.id = rr.game_id
                JOIN br_lobbies l ON l.id = g.lobby_id
                LEFT JOIN teams t ON t.id = rr.team_id
                LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE l.stage_id = @stageId
                  AND g.status = 'completed'
                GROUP BY COALESCE(rr.team_id, rr.participant_id),
                        CASE WHEN rr.team_id IS NOT NULL THEN t.name ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player') END,
                        CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END
                """,
                new { stageId })).ToList();

            leaderboardRows.Sort((a, b) =>
            {
                var aggregateA = new BrLeaderboardAggregate(
                    Convert.ToInt64(a.total_points),
                    Convert.ToInt64(a.wins),
                    Convert.ToInt64(a.total_kills),
                    a.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(a.avg_placement));
                var aggregateB = new BrLeaderboardAggregate(
                    Convert.ToInt64(b.total_points),
                    Convert.ToInt64(b.wins),
                    Convert.ToInt64(b.total_kills),
                    b.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(b.avg_placement));
                return BrConfigService.CompareLeaderboardEntries(aggregateA, aggregateB, tiebreaker);
            });

            return Results.Ok(leaderboardRows);
        });

        // Preview or execute advancement of top teams from each group to next stage.
        app.MapPost("/api/stages/{stageId}/br/advance", async (
            Guid                stageId,
            [FromQuery] bool    preview,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermTeamsManage);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            // Get stage info AND team_size for solo detection
            var stage = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ts.id, ts.tournament_id, ts.name, ts.stage_order, ts.advancement_count, ts.status,
                       ts.config,
                       t.team_size, t.settings
                FROM tournament_stages ts
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.id = @stageId
                """,
                new { stageId });
            if (stage is null)
                return Results.NotFound(new { error = "Stage not found." });

            if ((string)stage.status == "completed")
                return Results.Conflict(new { error = "This stage has already been advanced." });

            bool isSolo = Convert.ToInt32(stage.team_size ?? 1) == 1;
            var tiebreaker = BrConfigService.ResolveTiebreaker(stage.settings);
            var advancementMode = BrConfigService.ResolveAdvancement(stage.config);

            // Optional override from body: { teamsPerGroup: 4 }
            int teamsPerGroup = 0;
            if (body.TryGetProperty("teamsPerGroup", out var tpg) && tpg.TryGetInt32(out var tpgVal))
                teamsPerGroup = tpgVal;
            if (teamsPerGroup <= 0)
            {
                int? stageAdvancementCount = stage.advancement_count is null
                    ? null
                    : Convert.ToInt32(stage.advancement_count);
                var resolvedCount = BrConfigService.ResolveAdvancementCount(
                    stage.config,
                    stageAdvancementCount);
                teamsPerGroup = resolvedCount ?? 0;
            }
            if (teamsPerGroup <= 0)
                return Results.BadRequest(new { error = "advancement_count not configured and teamsPerGroup not provided." });

            // Get all groups with their leaderboards
            var groups = (await conn.QueryAsync<dynamic>(
                "SELECT id, name, group_order FROM br_groups WHERE stage_id = @stageId ORDER BY group_order",
                new { stageId })).ToList();

            if (groups.Count == 0)
                return Results.BadRequest(new { error = "No groups exist in this stage." });

            var gamesPerLobby = BrConfigService.ResolveGamesPerLobby(stage.settings, stage.config) ?? 6;

            var incompleteGroups = await conn.QueryAsync<dynamic>(
                """
                SELECT g.name FROM br_groups g
                WHERE g.stage_id = @stageId
                  AND NOT EXISTS (
                      SELECT 1
                      FROM br_lobbies l
                      JOIN br_lobby_groups lg ON lg.lobby_id = l.id
                      JOIN br_games bg ON bg.lobby_id = l.id
                      WHERE lg.group_id = g.id
                      GROUP BY l.id
                      HAVING COUNT(*) FILTER (WHERE bg.status = 'completed') >= @gamesPerLobby
                  )
                """,
                new { stageId, gamesPerLobby });
            var incompleteList = incompleteGroups.ToList();
            if (incompleteList.Count > 0)
            {
                var names = string.Join(", ", incompleteList.Select(g => (string)g.name));
                return Results.BadRequest(new { error = $"Groups missing {gamesPerLobby} completed games per lobby: {names}" });
            }

            // Build qualified teams — unified query with COALESCE for solo/team
            var aggregateRows = (await conn.QueryAsync<dynamic>(
                """
                SELECT
                    COALESCE(rr.team_id, rr.participant_id) AS entity_id,
                    rr.team_id,
                    rr.participant_id,
                    CASE
                        WHEN rr.team_id IS NOT NULL THEN t.name
                        ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                    END AS team_name,
                    CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
                    g.id AS group_id,
                    g.name AS group_name,
                    SUM(rr.total_points) AS total_points,
                    SUM(rr.kills) AS total_kills,
                    COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
                    AVG(rr.placement::numeric) AS avg_placement
                FROM br_lobby_results rr
                JOIN br_games bg ON bg.id = rr.game_id
                JOIN br_lobbies r ON r.id = bg.lobby_id
                JOIN br_lobby_groups lg ON lg.lobby_id = r.id
                JOIN br_groups g ON g.id = lg.group_id
                LEFT JOIN teams t ON t.id = rr.team_id
                LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE g.stage_id = @stageId
                  AND bg.status = 'completed'
                GROUP BY COALESCE(rr.team_id, rr.participant_id),
                         rr.team_id, rr.participant_id,
                         CASE
                             WHEN rr.team_id IS NOT NULL THEN t.name
                             ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                         END,
                         CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END,
                         g.id, g.name
                """,
                new { stageId })).ToList();

            var qualifiedRows = aggregateRows
                .GroupBy(row => (Guid)row.group_id)
                .SelectMany(group =>
                {
                    var groupRows = group.ToList();
                    groupRows.Sort((a, b) =>
                    {
                        var aggregateA = new BrLeaderboardAggregate(
                            Convert.ToInt64(a.total_points),
                            Convert.ToInt64(a.wins),
                            Convert.ToInt64(a.total_kills),
                            a.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(a.avg_placement));
                        var aggregateB = new BrLeaderboardAggregate(
                            Convert.ToInt64(b.total_points),
                            Convert.ToInt64(b.wins),
                            Convert.ToInt64(b.total_kills),
                            b.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(b.avg_placement));
                        return BrConfigService.CompareLeaderboardEntries(aggregateA, aggregateB, tiebreaker);
                    });

                    return groupRows
                        .Take(teamsPerGroup)
                        .Select((row, index) => new { row, rank_in_group = index + 1 });
                })
                .OrderBy(entry => (string)entry.row.group_name)
                .ThenBy(entry => entry.rank_in_group)
                .Select(entry => entry.row)
                .ToList();

            if (advancementMode == BrAdvancementMode.TopNOverall)
            {
                qualifiedRows = aggregateRows
                    .OrderByDescending(r => (long)r.total_points)
                    .ThenByDescending(r => (long)r.wins)
                    .ThenByDescending(r => (long)r.total_kills)
                    .ThenBy(r => r.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(r.avg_placement))
                    .Take(teamsPerGroup)
                    .ToList();
            }
            else if (advancementMode == BrAdvancementMode.TopNPerLobby)
            {
                var lobbyRows = (await conn.QueryAsync<dynamic>(
                    """
                    SELECT
                        g.lobby_id,
                        COALESCE(rr.team_id, rr.participant_id) AS entity_id,
                        rr.team_id,
                        rr.participant_id,
                        CASE
                            WHEN rr.team_id IS NOT NULL THEN t.name
                            ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                        END AS team_name,
                        CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
                        SUM(rr.total_points) AS total_points,
                        SUM(rr.kills) AS total_kills,
                        COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
                        AVG(rr.placement::numeric) AS avg_placement
                    FROM br_lobby_results rr
                    JOIN br_games g ON g.id = rr.game_id
                    JOIN br_lobbies l ON l.id = g.lobby_id
                    LEFT JOIN teams t ON t.id = rr.team_id
                    LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                    LEFT JOIN profiles p ON p.id = tp.user_id
                    WHERE l.stage_id = @stageId
                      AND g.status = 'completed'
                    GROUP BY g.lobby_id,
                             COALESCE(rr.team_id, rr.participant_id),
                             rr.team_id, rr.participant_id,
                             CASE WHEN rr.team_id IS NOT NULL THEN t.name ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player') END,
                             CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END
                    """,
                    new { stageId })).ToList();

                qualifiedRows = lobbyRows
                    .GroupBy(r => (Guid)r.lobby_id)
                    .SelectMany(group => group
                        .OrderByDescending(r => (long)r.total_points)
                        .ThenByDescending(r => (long)r.wins)
                        .ThenByDescending(r => (long)r.total_kills)
                        .ThenBy(r => r.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(r.avg_placement))
                        .Take(teamsPerGroup))
                    .ToList();
            }

            var qualifiedTeams = qualifiedRows.Select(r => new
            {
                team_id            = (Guid)r.entity_id,
                raw_team_id        = (Guid?)r.team_id,
                raw_participant_id = (Guid?)r.participant_id,
                team_name          = (string)r.team_name,
                logo_url           = (string?)r.logo_url,
                from_group         = ((IDictionary<string, object>)r).TryGetValue("group_name", out var fromGroupVal) && fromGroupVal is not null
                    ? fromGroupVal.ToString()
                    : (string?)null,
                total_points       = (long)r.total_points,
                total_kills        = (long)r.total_kills,
                wins               = (long)r.wins,
            }).ToList();

            if (preview)
            {
                return Results.Ok(new
                {
                    stage_name      = (string)stage.name,
                    advancement_mode = advancementMode.ToString(),
                    groups_count    = groups.Count,
                    teams_per_group = teamsPerGroup,
                    total_qualified = qualifiedTeams.Count,
                    qualified_teams = qualifiedTeams.Select(qt => new
                    {
                        qt.team_id,
                        qt.team_name,
                        qt.logo_url,
                        qt.from_group,
                        qt.total_points,
                        qt.total_kills,
                        qt.wins,
                    }),
                });
            }

            // ── Execute advancement ──────────────────────────────────────────
            // Find next stage
            Guid tournamentId = stage.tournament_id;
            int nextOrder = (int)stage.stage_order + 1;
            var nextStage = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, name, config, capacity, advancement_count
                FROM tournament_stages
                WHERE tournament_id = @tournamentId AND stage_order = @nextOrder
                """,
                new { tournamentId, nextOrder });

            if (nextStage is null)
                return Results.BadRequest(new { error = "No next stage exists. Create the finals stage first." });

            Guid nextStageId = nextStage.id;

            using var tx = conn.BeginTransaction();
            try
            {
                // Lock the stage row to serialize concurrent advancement attempts
                var currentStatus = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT status FROM tournament_stages WHERE id = @stageId FOR UPDATE",
                    new { stageId }, tx);
                if (currentStatus == "completed")
                {
                    tx.Rollback();
                    return Results.Conflict(new { error = "This stage has already been advanced." });
                }

                var existingNextGroups = await conn.QuerySingleOrDefaultAsync<long>(
                    "SELECT COUNT(*) FROM br_groups WHERE stage_id = @nextStageId",
                    new { nextStageId }, tx);

                if (existingNextGroups > 0)
                {
                    // Delete existing groups (cascades to teams/rounds/results)
                    await conn.ExecuteAsync(
                        "DELETE FROM br_groups WHERE stage_id = @nextStageId", new { nextStageId }, tx);
                }

                // Seed next stage: multi-group when format requires it, else single merged lobby
                var nextFormat = BrConfigService.ResolveFormat(nextStage.config);
                var nextLobbySize = nextStage.capacity is int cap && cap > 0
                    ? cap
                    : Math.Max(1, qualifiedTeams.Count);
                var targetGroupCount = nextFormat switch
                {
                    BrStageFormat.MultiLobbyCut =>
                        Math.Max(1, (int)Math.Ceiling(qualifiedTeams.Count / (double)nextLobbySize)),
                    BrStageFormat.StaticGroups =>
                        Math.Max(1, (int)Math.Ceiling(qualifiedTeams.Count / (double)nextLobbySize)),
                    BrStageFormat.GroupRotation => 4,
                    _ => 1,
                };

                var createdGroupIds = new List<Guid>();
                for (var g = 0; g < targetGroupCount; g++)
                {
                    var groupId = Guid.NewGuid();
                    var groupName = targetGroupCount == 1
                        ? "Main Lobby"
                        : $"Group {(char)('A' + g)}";
                    var perGroupLobbySize = targetGroupCount == 1
                        ? qualifiedTeams.Count
                        : nextLobbySize;

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_groups (id, stage_id, name, group_order, lobby_size)
                        VALUES (@id, @stageId, @name, @groupOrder, @lobbySize)
                        """,
                        new
                        {
                            id = groupId,
                            stageId = nextStageId,
                            name = groupName,
                            groupOrder = g,
                            lobbySize = perGroupLobbySize,
                        },
                        tx);
                    createdGroupIds.Add(groupId);
                }

                for (var i = 0; i < qualifiedTeams.Count; i++)
                {
                    var qt = qualifiedTeams[i];
                    var groupIndex = targetGroupCount == 1
                        ? 0
                        : BrSeedingService.SnakeGroupIndex(i, targetGroupCount);
                    var targetGroupId = createdGroupIds[groupIndex];

                    if (isSolo)
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO br_group_teams (id, group_id, participant_id, seed_order, assigned_at)
                            VALUES (@id, @group_id, @participant_id, @seed_order, @assigned_at)
                            """,
                            new
                            {
                                id = Guid.NewGuid(),
                                group_id = targetGroupId,
                                participant_id = qt.raw_participant_id!.Value,
                                seed_order = i + 1,
                                assigned_at = DateTime.UtcNow,
                            },
                            tx);
                    }
                    else
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO br_group_teams (id, group_id, team_id, seed_order, assigned_at)
                            VALUES (@id, @group_id, @team_id, @seed_order, @assigned_at)
                            """,
                            new
                            {
                                id = Guid.NewGuid(),
                                group_id = targetGroupId,
                                team_id = qt.raw_team_id!.Value,
                                seed_order = i + 1,
                                assigned_at = DateTime.UtcNow,
                            },
                            tx);
                    }
                }

                // Update stage statuses
                await conn.ExecuteAsync(
                    "UPDATE tournament_stages SET status = 'completed' WHERE id = @stageId",
                    new { stageId }, tx);
                await conn.ExecuteAsync(
                    "UPDATE tournament_stages SET status = 'active' WHERE id = @nextStageId",
                    new { nextStageId }, tx);

                tx.Commit();

                return Results.Ok(new
                {
                    advanced        = qualifiedTeams.Count,
                    from_stage      = (string)stage.name,
                    to_stage        = (string)nextStage.name,
                    groups_created  = createdGroupIds.Count,
                    group_ids       = createdGroupIds.Select(id => id.ToString()).ToList(),
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        app.MapBrGameEndpoints();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<bool> IsUserAssignedToGroupAsync(
        System.Data.IDbConnection conn,
        Guid groupId,
        Guid userId,
        IDbTransaction? tx = null)
    {
        var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT ts.tournament_id
            FROM br_groups g
            JOIN tournament_stages ts ON ts.id = g.stage_id
            WHERE g.id = @groupId
            """,
            new { groupId },
            tx) ?? Guid.Empty;

        if (tournamentId == Guid.Empty)
            return false;

        return await BrPlayerContextService.IsUserAssignedToGroupAsync(
            conn, tournamentId, groupId, userId, tx);
    }

    private static async Task<BrEntityAccess> ResolveRoundEntityAccessAsync(
        System.Data.IDbConnection conn,
        Guid lobbyId,
        Guid tournamentId,
        Guid userId,
        bool isSolo,
        IDbTransaction? tx = null)
    {
        if (isSolo)
        {
            var participantId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT tp.id
                FROM br_lobbies r
                JOIN br_lobby_groups lg ON lg.lobby_id = r.id
                JOIN br_groups g ON g.id = lg.group_id
                JOIN br_group_teams bgt ON bgt.group_id = g.id
                JOIN tournament_participants tp ON tp.id = bgt.participant_id
                WHERE r.id = @lobbyId
                  AND tp.tournament_id = @tournamentId
                  AND tp.user_id = @userId
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                LIMIT 1
                """,
                new { lobbyId, tournamentId, userId },
                tx);

            if (participantId is not null)
                return new BrEntityAccess(null, participantId);

            var soloTeamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT bgt.team_id
                FROM br_lobbies r
                JOIN br_lobby_groups lg ON lg.lobby_id = r.id
                JOIN br_groups g ON g.id = lg.group_id
                JOIN br_group_teams bgt ON bgt.group_id = g.id
                JOIN tournament_participants tp ON tp.team_id = bgt.team_id
                WHERE r.id = @lobbyId
                  AND tp.tournament_id = @tournamentId
                  AND tp.user_id = @userId
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                  AND bgt.team_id IS NOT NULL
                LIMIT 1
                """,
                new { lobbyId, tournamentId, userId },
                tx);

            return new BrEntityAccess(soloTeamId, null);
        }

        var teamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT bgt.team_id
            FROM br_lobbies r
            JOIN br_lobby_groups lg ON lg.lobby_id = r.id
                JOIN br_groups g ON g.id = lg.group_id
            JOIN br_group_teams bgt ON bgt.group_id = g.id
            JOIN tournament_participants tp ON tp.team_id = bgt.team_id
            JOIN team_members tm ON tm.team_id = bgt.team_id
            WHERE r.id = @lobbyId
              AND tp.tournament_id = @tournamentId
              AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
              AND tm.user_id = @userId
              AND tm.is_active = TRUE
            LIMIT 1
            """,
            new { lobbyId, tournamentId, userId },
            tx);

        if (teamId is not null)
            return new BrEntityAccess(teamId, null);

        var teamParticipantId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT tp.id
            FROM br_lobbies r
            JOIN br_lobby_groups lg ON lg.lobby_id = r.id
                JOIN br_groups g ON g.id = lg.group_id
            JOIN br_group_teams bgt ON bgt.group_id = g.id
            JOIN tournament_participants tp ON tp.id = bgt.participant_id
            WHERE r.id = @lobbyId
              AND tp.tournament_id = @tournamentId
              AND tp.user_id = @userId
              AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
            LIMIT 1
            """,
            new { lobbyId, tournamentId, userId },
            tx);

        return new BrEntityAccess(null, teamParticipantId);
    }

    private static async Task<object?> LoadCatalogBrConfigAsync(
        GameCatalogService catalog,
        string? gameName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameName))
            return null;

        var game = await catalog.GetGameAsync(gameName.Trim(), ct);
        return game?.BrConfig;
    }

    private static bool TryNormalizeEvidenceImageUrl(
        JsonElement body,
        string supabaseUrl,
        out string? normalizedImageUrl,
        out string? error)
    {
        const string bucket = "tournaments.results";
        normalizedImageUrl = null;
        error = null;

        if (TryGetPropertyIgnoreCase(body, "imagePath", out var imagePathProp)
            && imagePathProp.ValueKind == JsonValueKind.String)
        {
            var imagePath = imagePathProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "imagePath cannot be empty.";
                return false;
            }

            if (!IsSafeStoragePath(imagePath))
            {
                error = "imagePath must reference a valid storage object.";
                return false;
            }

            normalizedImageUrl = $"{supabaseUrl}/storage/v1/object/public/{bucket}/{imagePath}";
            return true;
        }

        if (!TryGetPropertyIgnoreCase(body, "imageUrl", out var imageUrlProp)
            || imageUrlProp.ValueKind != JsonValueKind.String)
        {
            error = "imageUrl is required.";
            return false;
        }

        var imageUrl = imageUrlProp.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            error = "imageUrl is required.";
            return false;
        }

        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var parsedUrl))
        {
            error = "imageUrl must be an absolute URL.";
            return false;
        }

        var expectedPrefix = $"{supabaseUrl}/storage/v1/object/public/{bucket}/";
        if (!parsedUrl.AbsoluteUri.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "Evidence must come from the approved BR results storage bucket.";
            return false;
        }

        normalizedImageUrl = parsedUrl.AbsoluteUri;
        return true;
    }

    private static bool IsSafeStoragePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return !path.StartsWith('/')
               && !path.Contains('\\')
               && !path.Contains("..", StringComparison.Ordinal)
               && !path.Contains("://", StringComparison.Ordinal);
    }

    private static bool TryParseJsonElement(object? rawValue, out JsonElement element)
    {
        switch (rawValue)
        {
            case JsonElement jsonElement:
                element = jsonElement.Clone();
                return true;
            case JsonDocument jsonDocument:
                element = jsonDocument.RootElement.Clone();
                return true;
            case string jsonText when !string.IsNullOrWhiteSpace(jsonText):
                try
                {
                    using var parsed = JsonDocument.Parse(jsonText);
                    element = parsed.RootElement.Clone();
                    return true;
                }
                catch
                {
                    element = default;
                    return false;
                }
        }

        element = default;
        return false;
    }

    private static async Task<IReadOnlyList<Guid>> GetLobbyGroupIdsAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid? fallbackGroupId = null,
        IDbTransaction? tx = null)
    {
        var groupIds = (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT group_id
            FROM br_lobby_groups
            WHERE lobby_id = @lobbyId
            ORDER BY group_id
            """,
            new { lobbyId },
            tx)).ToList();

        if (groupIds.Count > 0)
            return groupIds;

        return fallbackGroupId is Guid gid && gid != Guid.Empty
            ? new[] { gid }
            : Array.Empty<Guid>();
    }

    private static async Task BroadcastBrToLobbyGroupsAsync(
        IHubContext<BRHub> hub,
        string eventName,
        Guid stageId,
        Guid lobbyId,
        IReadOnlyList<Guid> groupIds,
        object payload,
        CancellationToken ct = default)
    {
        foreach (var groupId in groupIds)
        {
            await BroadcastBrAsync(hub, eventName, stageId, groupId, lobbyId, payload, ct);
        }
    }

    private static object MapLobbyRowToApiResponse(dynamic row)
    {
        var values = (IDictionary<string, object>)row;
        object? Read(string key) =>
            values.TryGetValue(key, out var value) && value is not DBNull ? value : null;

        return new
        {
            id = Read("id"),
            wave_number = Read("wave_number") is not null ? Convert.ToInt32(Read("wave_number")) : 0,
            lobby_code = Read("lobby_code") as string,
            status = Read("status") as string ?? "pending",
            scheduled_at = Read("scheduled_at") is DateTimeOffset scheduledAt
                ? scheduledAt.ToString("o")
                : Read("scheduled_at") as string,
            started_at = Read("started_at") is DateTimeOffset startedAt
                ? startedAt.ToString("o")
                : Read("started_at") as string,
            completed_at = Read("completed_at") is DateTimeOffset completedAt
                ? completedAt.ToString("o")
                : Read("completed_at") as string,
            created_at = Read("created_at") is DateTimeOffset createdAt
                ? createdAt.ToString("o")
                : Read("created_at") as string,
            queue_timer_minutes = Read("queue_timer_minutes") is not null
                ? Convert.ToInt32(Read("queue_timer_minutes"))
                : (int?)null,
            queue_started_at = Read("queue_started_at") is DateTimeOffset queueStartedAt
                ? queueStartedAt.ToString("o")
                : Read("queue_started_at") as string,
        };
    }

    private static async Task<bool> ColumnAllowsNullAsync(
        IDbConnection conn,
        string tableName,
        string columnName,
        IDbTransaction? tx = null)
    {
        return await conn.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT COALESCE((
                SELECT is_nullable = 'YES'
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                  AND column_name = @columnName
            ), FALSE)
            """,
            new { tableName, columnName },
            tx);
    }

    private static bool TryReadGuidValue(
        IDictionary<string, object> row,
        string key,
        out Guid? value)
    {
        value = null;

        if (!row.TryGetValue(key, out var rawValue) || rawValue is null || rawValue is DBNull)
            return true;

        switch (rawValue)
        {
            case Guid guid:
                value = guid;
                return true;
            case string text when Guid.TryParse(text, out var parsed):
                value = parsed;
                return true;
            case byte[] bytes when bytes.Length == 16:
                value = new Guid(bytes);
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Generate group name from zero-based index: 0→A, 1→B, ..., 25→Z, 26→AA, 27→AB, etc.
    /// </summary>
    private static string GenerateGroupName(int index)
    {
        var name = string.Empty;
        var n = index;
        do
        {
            name = (char)('A' + n % 26) + name;
            n = n / 26 - 1;
        } while (n >= 0);

        return $"Group {name}";
    }

    private static async Task<bool> CanViewStagePublicDataAsync(IDbConnection conn, HttpContext ctx, Guid stageId)
    {
        var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT t.id, t.organizer_id, t.is_public, t.status::text AS status
            FROM public.tournament_stages s
            JOIN public.tournaments t ON t.id = s.tournament_id
            WHERE s.id = @stageId
              AND t.deleted_at IS NULL
            """,
            new { stageId });

        // Link-accessible tournaments (draft, private, public) expose stage data to anyone with the URL.
        return tournament is not null;
    }

    private static async Task<List<Guid>> LockStageGroupsAsync(IDbConnection conn, IDbTransaction tx, Guid stageId)
    {
        return (await conn.QueryAsync<Guid>(
            """
            SELECT id
            FROM br_groups
            WHERE stage_id = @stageId
            ORDER BY group_order
            FOR UPDATE
            """,
            new { stageId },
            tx)).AsList();
    }

    private static async Task<bool> LockStageAsync(IDbConnection conn, IDbTransaction tx, Guid stageId)
    {
        var lockedStageId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT id
            FROM tournament_stages
            WHERE id = @stageId
            FOR UPDATE
            """,
            new { stageId },
            tx);

        return lockedStageId.HasValue;
    }

    private static async Task<bool> StageHasAnyRoundsAsync(IDbConnection conn, Guid stageId, IDbTransaction? tx = null)
    {
        return await conn.QuerySingleOrDefaultAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM br_lobbies r
                JOIN br_lobby_groups lg ON lg.lobby_id = r.id
                JOIN br_groups g ON g.id = lg.group_id
                WHERE g.stage_id = @stageId
            )
            """,
            new { stageId },
            tx);
    }

    private static async Task<bool> RoundResultsMatchCurrentRosterAsync(
        IDbConnection conn,
        IDbTransaction tx,
        Guid groupId,
        Guid lobbyId)
    {
        var groupTeamsHasParticipantId = await BrSchemaRepository.ColumnExistsAsync(conn, "br_group_teams", "participant_id", tx);
        var roundResultsHasParticipantId = await BrSchemaRepository.ColumnExistsAsync(conn, "br_lobby_results", "participant_id", tx);

        var rosterEntityExpression = groupTeamsHasParticipantId
            ? "COALESCE(team_id, participant_id)"
            : "team_id";
        var resultEntityExpression = roundResultsHasParticipantId
            ? "COALESCE(team_id, participant_id)"
            : "team_id";

        var sql = $"""
            WITH roster AS (
                SELECT {rosterEntityExpression} AS entity_id
                FROM br_group_teams
                WHERE group_id = @groupId
            ),
            results AS (
                SELECT {resultEntityExpression} AS entity_id
                FROM br_lobby_results
                WHERE lobby_id = @lobbyId
            ),
            roster_valid AS (
                SELECT entity_id FROM roster WHERE entity_id IS NOT NULL
            ),
            result_valid AS (
                SELECT entity_id FROM results WHERE entity_id IS NOT NULL
            )
            SELECT
                (SELECT COUNT(*) FROM roster) > 0
                AND (SELECT COUNT(*) FROM roster) = (SELECT COUNT(*) FROM roster_valid)
                AND (SELECT COUNT(*) FROM results) = (SELECT COUNT(*) FROM result_valid)
                AND (SELECT COUNT(*) FROM roster_valid) = (SELECT COUNT(DISTINCT entity_id) FROM roster_valid)
                AND (SELECT COUNT(*) FROM result_valid) = (SELECT COUNT(DISTINCT entity_id) FROM result_valid)
                AND NOT EXISTS (
                    SELECT entity_id FROM roster_valid
                    EXCEPT
                    SELECT entity_id FROM result_valid
                )
                AND NOT EXISTS (
                    SELECT entity_id FROM result_valid
                    EXCEPT
                    SELECT entity_id FROM roster_valid
                )
            """;

        return await conn.ExecuteScalarAsync<bool>(sql, new { groupId, lobbyId }, tx);
    }

    private static object BuildRoundEvent(
        Guid stageId,
        Guid groupId,
        Guid lobbyId,
        int waveNumber,
        string? status = null,
        string? lobbyCode = null,
        int? queueTimerMinutes = null,
        string? queueStartedAt = null) => new
    {
        stageId = stageId.ToString(),
        groupId = groupId.ToString(),
        lobbyId = lobbyId.ToString(),
        waveNumber,
        status,
        lobbyCode,
        queueTimerMinutes,
        queueStartedAt,
    };

    private static object BuildLeaderboardEvent(Guid stageId, Guid groupId) => new
    {
        stageId = stageId.ToString(),
        groupId = groupId.ToString(),
    };

    private static async Task BroadcastBrAsync(
        IHubContext<BRHub> hub,
        string eventName,
        Guid stageId,
        Guid groupId,
        Guid? lobbyId,
        object payload,
        CancellationToken ct = default)
    {
        _ = ct;
        var tasks = new List<Task>
        {
            hub.Clients.Group(BRHub.StageGroup(stageId.ToString())).SendAsync(eventName, payload, CancellationToken.None),
            hub.Clients.Group(BRHub.GroupGroup(groupId.ToString())).SendAsync(eventName, payload, CancellationToken.None),
        };

        if (lobbyId is not null)
        {
            tasks.Add(hub.Clients.Group(BRHub.LobbyGroup(lobbyId.Value.ToString())).SendAsync(eventName, payload, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[BRGroupEndpoints] Failed to broadcast {eventName} for stage {stageId}, group {groupId}, round {lobbyId}: {ex.Message}");
        }
    }

}
