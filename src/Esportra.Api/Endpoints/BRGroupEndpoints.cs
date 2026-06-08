using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Esportra.Api.Endpoints;

public static class BRGroupEndpoints
{
    private sealed record BrEntityAccess(Guid? TeamId, Guid? ParticipantId);
    private static readonly string[] SeedEligibleRegistrationStatuses = ["approved", "checked_in"];
    private const string SeedEligibleParticipantMessage = "No eligible participants found. Participants must be approved or checked in.";
    private const string SeedEligibleTeamMessage = "No eligible teams found. Teams must be approved or checked in.";
    private const string StageRoundsLockedMessage = "This stage already has rounds. Reset or recreate the stage before reseeding participants.";

    public static void MapBRGroupEndpoints(this WebApplication app)
    {
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
                    SELECT 1 FROM br_rounds r
                    JOIN br_groups g ON g.id = r.group_id
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
                        new { tournamentId, seedEligibleStatuses = SeedEligibleRegistrationStatuses })).ToArray();

                    if (participantIds.Length == 0)
                        return Results.BadRequest(new { error = SeedEligibleParticipantMessage });

                    var orderedParticipants = method == "random"
                        ? ShuffleTeams(participantIds)
                        : participantIds;

                    var assignments = method == "snake"
                        ? BuildSnakeAssignments(orderedParticipants, groupIds)
                        : BuildRoundRobinAssignments(orderedParticipants, groupIds);

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
                        new { tournamentId, seedEligibleStatuses = SeedEligibleRegistrationStatuses })).ToArray();

                    if (teamIds.Length == 0)
                        return Results.BadRequest(new { error = SeedEligibleTeamMessage });

                    var orderedTeams = method == "random"
                        ? ShuffleTeams(teamIds)
                        : teamIds;

                    var assignments = method == "snake"
                        ? BuildSnakeAssignments(orderedTeams, groupIds)
                        : BuildRoundRobinAssignments(orderedTeams, groupIds);

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
                        new { tournamentId, seedEligibleStatuses = SeedEligibleRegistrationStatuses, idsArr = teamIds.ToArray() })).ToHashSet();

                    var invalid = teamIds.Where(t => !validParticipantIds.Contains(t)).ToList();
                    if (invalid.Count > 0)
                        return Results.BadRequest(new { error = $"Participants are not eligible for seeding. They must be registered, approved or checked in: {string.Join(", ", invalid)}" });
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
                        new { tournamentId, seedEligibleStatuses = SeedEligibleRegistrationStatuses, teamIdArr = teamIds.ToArray() })).ToHashSet();

                    var invalid = teamIds.Where(t => !validTeamIds.Contains(t)).ToList();
                    if (invalid.Count > 0)
                        return Results.BadRequest(new { error = $"Teams are not eligible for seeding. They must be registered, approved or checked in: {string.Join(", ", invalid)}" });
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

        // ── GET /api/stages/{stageId}/br/groups/{groupId}/rounds ────────────
        // List rounds for a group. Public endpoint — lobby_code stripped for unauthenticated/non-staff.
        app.MapGet("/api/stages/{stageId}/br/groups/{groupId}/rounds", async (
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

            var roundsHasMapColumn = await ColumnExistsAsync(conn, "br_rounds", "map");
            var mapSelect = roundsHasMapColumn ? ", r.map" : ", NULL::text AS map";

            var rounds = await conn.QueryAsync<dynamic>(
                $"""
                SELECT r.id, r.round_number, r.lobby_code, r.status,
                       r.scheduled_at, r.started_at, r.completed_at, r.created_at,
                       r.queue_timer_minutes, r.queue_started_at{mapSelect},
                       (SELECT COUNT(*) FROM br_round_results rr WHERE rr.round_id = r.id) AS result_count,
                       (SELECT COUNT(*) FROM br_round_evidence re WHERE re.round_id = r.id) AS evidence_count,
                       (SELECT COUNT(*) FROM br_round_evidence re WHERE re.round_id = r.id AND re.reviewed = FALSE) AS pending_evidence_count
                FROM br_rounds r
                WHERE r.group_id = @groupId
                ORDER BY r.round_number
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
                    round_number = d["round_number"],
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

        // ── POST /api/stages/{stageId}/br/groups/{groupId}/rounds ───────────
        // Create a new round for a group. Auto-increments round_number.
        app.MapPost("/api/stages/{stageId}/br/groups/{groupId}/rounds", async (
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

                var nextRoundNumber = await conn.ExecuteScalarAsync<int>(
                    "SELECT COALESCE(MAX(round_number), 0) + 1 FROM br_rounds WHERE group_id = @groupId",
                    new { groupId },
                    tx);

                var stageContext = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT t.game, t.settings, ts.config AS stage_config
                    FROM tournament_stages ts
                    JOIN tournaments t ON t.id = ts.tournament_id
                    WHERE ts.id = @stageId
                    """,
                    new { stageId },
                    tx);

                var roundsHasMapColumn = await ColumnExistsAsync(conn, "br_rounds", "map", tx);
                var catalogBrConfig = await LoadCatalogBrConfigAsync(catalog, stageContext?.game as string, ct);
                string? persistedMap = null;
                if (roundsHasMapColumn && mapValue is not null)
                {
                    var mapConfig = BattleRoyaleConfigResolver.ResolveMapConfig(
                        stageContext?.settings,
                        stageContext?.stage_config,
                        catalogBrConfig);
                    if (!BattleRoyaleConfigResolver.ValidateMapInPool(mapConfig, mapValue, out string? mapError))
                    {
                        tx.Rollback();
                        return Results.BadRequest(new { error = mapError });
                    }

                    persistedMap = mapValue;
                }
                else if (roundsHasMapColumn)
                {
                    var mapConfig = BattleRoyaleConfigResolver.ResolveMapConfig(
                        stageContext?.settings,
                        stageContext?.stage_config,
                        catalogBrConfig);
                    persistedMap = BattleRoyaleConfigResolver.ResolveMapForRound(mapConfig, nextRoundNumber, null);
                }

                var mapInsertSql = roundsHasMapColumn ? ", map" : string.Empty;
                var mapValuesSql = roundsHasMapColumn ? ", @map" : string.Empty;
                var mapReturningSql = roundsHasMapColumn ? ", map" : ", NULL::text AS map";

                var round = await conn.QuerySingleAsync<dynamic>(
                    $"""
                    INSERT INTO br_rounds (group_id, round_number, lobby_code, scheduled_at, queue_timer_minutes{mapInsertSql})
                    VALUES (
                        @groupId,
                        @roundNumber,
                        @lobbyCode,
                        @scheduledAt,
                        @queueTimerMinutes{mapValuesSql}
                    )
                    RETURNING id, round_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at,
                              queue_timer_minutes, queue_started_at{mapReturningSql}
                    """,
                    new
                    {
                        groupId,
                        roundNumber = nextRoundNumber,
                        lobbyCode,
                        scheduledAt = parsedSchedule,
                        queueTimerMinutes,
                        map = persistedMap,
                    },
                    tx);

                tx.Commit();

                var roundId = (Guid)round.id;
                var roundNumber = Convert.ToInt32(round.round_number);
                var roundStatus = (string)round.status;
                var payload = BuildRoundEvent(stageId, groupId, roundId, roundNumber, roundStatus);
                await BroadcastBrAsync(brHub, BRHubEvents.RoundCreated, stageId, groupId, roundId, payload, ct);

                return Results.Ok(round);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                                               && ex.ConstraintName == "br_rounds_group_id_round_number_key")
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

        // ── PATCH /api/br/rounds/{roundId} ──────────────────────────────────
        // Update a round (lobby code, status, schedule).
        app.MapPatch("/api/br/rounds/{roundId}", async (
            Guid                roundId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db,
            GameCatalogService   catalog,
            IHubContext<NotificationHub> notifHub,
            IHubContext<BRHub>   brHub,
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
            try
            {
                var currentRound = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT g.stage_id,
                           g.id AS group_id,
                           r.status,
                           r.round_number,
                           r.map,
                           r.lobby_code,
                           r.queue_timer_minutes,
                           r.queue_started_at
                    FROM br_rounds r
                    JOIN br_groups g ON g.id = r.group_id
                    WHERE r.id = @roundId
                    FOR UPDATE OF r, g
                    """,
                    new { roundId },
                    tx);
                if (currentRound is null)
                {
                    tx.Rollback();
                    return Results.NotFound(new { error = "Round not found." });
                }

                var stageId = (Guid)currentRound.stage_id;
                var groupId = (Guid)currentRound.group_id;
                var currentStatus = (string)currentRound.status;
                var currentRoundNumber = Convert.ToInt32(currentRound.round_number);
                var currentMap = currentRound.map as string;
                var currentLobbyCode = (string?)currentRound.lobby_code;
                int? currentQueueTimerMinutes = currentRound.queue_timer_minutes is not null
                    ? Convert.ToInt32(currentRound.queue_timer_minutes)
                    : null;
                var currentQueueStartedAt = currentRound.queue_started_at as DateTimeOffset?;

                var allowed = await StaffAuthHelper.CanActOnStageAsync(
                    conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
                if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                {
                    tx.Rollback();
                    return Results.Forbid();
                }

                var setClauses = new List<string>();
                var parameters = new DynamicParameters();
                parameters.Add("roundId", roundId);
                string? finalLobbyCode = currentLobbyCode;
                var finalStatus = currentStatus;
                int? finalQueueTimerMinutes = currentQueueTimerMinutes;
                string? finalMap = currentMap;
                var roundsHasMapColumn = await ColumnExistsAsync(conn, "br_rounds", "map", tx);

                if (roundsHasMapColumn && body.TryGetProperty("map", out var mapProp))
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
                        var mapConfig = BattleRoyaleConfigResolver.ResolveMapConfig(
                            stageContext?.settings,
                            stageContext?.stage_config,
                            catalogBrConfig);

                        if (finalMap is not null)
                        {
                            if (!BattleRoyaleConfigResolver.ValidateMapInPool(mapConfig, finalMap, out string? mapError))
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

                if (body.TryGetProperty("scheduledAt", out var saProp))
                {
                    if (saProp.ValueKind == JsonValueKind.Null)
                    {
                        setClauses.Add("scheduled_at = NULL");
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
                        }
                        else
                        {
                            tx.Rollback();
                            return Results.BadRequest(new { error = "Invalid scheduledAt format." });
                        }
                    }
                }

                if (body.TryGetProperty("queueTimerMinutes", out var qtmProp))
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
                            var mapConfig = BattleRoyaleConfigResolver.ResolveMapConfig(
                                stageContext?.settings,
                                stageContext?.stage_config,
                                catalogBrConfig);

                            var effectiveMap = BattleRoyaleConfigResolver.ResolveMapForRound(
                                mapConfig,
                                currentRoundNumber,
                                finalMap);

                            if (mapConfig.Mode == BattleRoyaleConfigResolver.BrMapMode.PerRound
                                && string.IsNullOrWhiteSpace(effectiveMap))
                            {
                                tx.Rollback();
                                return Results.BadRequest(new { error = "Map is required before starting a round." });
                            }

                            if (!string.IsNullOrWhiteSpace(effectiveMap))
                            {
                                if (!BattleRoyaleConfigResolver.ValidateMapInPool(mapConfig, effectiveMap, out string? mapError))
                                {
                                    tx.Rollback();
                                    return Results.BadRequest(new { error = mapError });
                                }
                            }
                        }

                        var (tournamentStart, tournamentEnd, tournamentStatus) = await StageCompletionHelper.GetTournamentWindowForStageAsync(conn, stageId, tx);
                        var ongoingError = TournamentTimelineValidator.ValidateTournamentIsOngoing(tournamentStatus);
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
                            SELECT id, round_number
                            FROM br_rounds
                            WHERE group_id = @groupId
                              AND status = 'active'
                              AND id <> @roundId
                            ORDER BY COALESCE(queue_started_at, started_at, created_at) DESC NULLS LAST, round_number DESC
                            LIMIT 1
                            """,
                            new { groupId, roundId },
                            tx);

                        if (existingActiveRound is not null)
                        {
                            tx.Rollback();
                            return Results.BadRequest(new
                            {
                                error = $"Round {Convert.ToInt32(existingActiveRound.round_number)} is already live. Complete, re-open, or reset it before starting another round."
                            });
                        }
                    }

                    if (newStatus == "completed")
                    {
                        if (!await RoundResultsMatchCurrentRosterAsync(conn, tx, groupId, roundId))
                        {
                            tx.Rollback();
                            return Results.Conflict(new
                            {
                                error = "Save round results before completing this round."
                            });
                        }

                        var pendingEvidenceCount = await CountPendingEvidenceAsync(conn, roundId, tx);
                        if (pendingEvidenceCount > 0)
                        {
                            tx.Rollback();
                            return Results.Conflict(new
                            {
                                error = "All submitted evidence must be reviewed before the round can be completed."
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

                if (!hasLiveLobbyCode || !hasQueueTimer)
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
                    UPDATE br_rounds
                    SET {string.Join(", ", setClauses)}
                    WHERE id = @roundId
                    RETURNING id, round_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at,
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
                                               && ex.ConstraintName == "uq_br_rounds_active_group")
            {
                tx.Rollback();
                return Results.Conflict(new { error = "Another round is already live for this group." });
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            if (updated is not null && broadcastStageId != default && broadcastGroupId != default)
            {
                var roundNumber = Convert.ToInt32(updated.round_number);
                var roundPayload = BuildRoundEvent(
                    broadcastStageId,
                    broadcastGroupId,
                    roundId,
                    roundNumber,
                    broadcastNewStatus);
                await BroadcastBrAsync(
                    brHub,
                    BRHubEvents.RoundUpdated,
                    broadcastStageId,
                    broadcastGroupId,
                    roundId,
                    roundPayload,
                    ct);

                var statusChanged = !string.Equals(broadcastOldStatus, broadcastNewStatus, StringComparison.Ordinal);
                if (statusChanged && (broadcastNewStatus == "completed"
                    || (broadcastOldStatus == "completed" && broadcastNewStatus == "active")))
                {
                    await BroadcastBrAsync(
                        brHub,
                        BRHubEvents.LeaderboardUpdated,
                        broadcastStageId,
                        broadcastGroupId,
                        roundId,
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
                            SELECT r.group_id, r.round_number, r.lobby_code,
                                   g.name AS group_name,
                                   t.slug AS tournament_slug
                            FROM br_rounds r
                            JOIN br_groups g ON g.id = r.group_id
                            JOIN tournament_stages ts ON ts.id = g.stage_id
                            JOIN tournaments t ON t.id = ts.tournament_id
                            WHERE r.id = @roundId
                            """,
                            new { roundId });

                        if (roundMeta is null) return;

                        Guid   groupIdForNotif  = roundMeta.group_id;
                        int    roundNumber      = Convert.ToInt32(roundMeta.round_number);
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

                        var title   = $"Round {roundNumber} is Live!";
                        var message = $"Your group '{groupName}' has started a new round. Join the game room.";
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
                        Console.Error.WriteLine($"[BRGroupEndpoints] Notification error for round {roundId}: {ex.Message}");
                    }
                });
            }

            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/br/rounds/{roundId}/reset ─────────────────────────────
        // Clear all result/evidence state for a round and move it back to
        // pending without deleting the round itself.
        app.MapPost("/api/br/rounds/{roundId}/reset", async (
            Guid                roundId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            IHubContext<BRHub>   brHub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var roundInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.stage_id,
                       g.id AS group_id,
                       r.round_number
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                WHERE r.id = @roundId
                """,
                new { roundId });
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = (Guid)roundInfo.stage_id;
            var groupId = (Guid)roundInfo.group_id;
            var roundNumber = Convert.ToInt32(roundInfo.round_number);
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(
                    """
                    SELECT 1
                    FROM br_rounds r
                    JOIN br_groups g ON g.id = r.group_id
                    WHERE r.id = @roundId
                    FOR UPDATE OF r, g
                    """,
                    new { roundId },
                    tx);

                await conn.ExecuteAsync(
                    "DELETE FROM br_round_results WHERE round_id = @roundId",
                    new { roundId },
                    tx);

                await conn.ExecuteAsync(
                    "DELETE FROM br_round_evidence WHERE round_id = @roundId",
                    new { roundId },
                    tx);

                var round = await conn.QuerySingleAsync<dynamic>(
                    """
                    UPDATE br_rounds
                    SET status = 'pending',
                        lobby_code = NULL,
                        scheduled_at = NULL,
                        started_at = NULL,
                        completed_at = NULL,
                        queue_timer_minutes = NULL,
                        queue_started_at = NULL
                    WHERE id = @roundId
                    RETURNING id, round_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at,
                              queue_timer_minutes, queue_started_at
                    """,
                    new { roundId },
                    tx);

                tx.Commit();

                var resetPayload = BuildRoundEvent(stageId, groupId, roundId, roundNumber, "pending");
                await BroadcastBrAsync(brHub, BRHubEvents.RoundReset, stageId, groupId, roundId, resetPayload, ct);
                await BroadcastBrAsync(
                    brHub,
                    BRHubEvents.LeaderboardUpdated,
                    stageId,
                    groupId,
                    roundId,
                    BuildLeaderboardEvent(stageId, groupId),
                    ct);

                return Results.Ok(round);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/br/rounds/{roundId}/results ────────────────────────────
        // Get results for a round.
        app.MapGet("/api/br/rounds/{roundId}/results", async (
            Guid              roundId,
            HttpContext        ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var roundResultsHasParticipantId = await ColumnExistsAsync(conn, "br_round_results", "participant_id");

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
                      FROM br_round_results rr
                      LEFT JOIN teams t ON t.id = rr.team_id
                      LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                      LEFT JOIN profiles p ON p.id = tp.user_id
                      WHERE rr.round_id = @roundId
                      ORDER BY rr.placement
                      """
                    : """
                      SELECT rr.id,
                             rr.team_id AS team_id,
                             rr.placement, rr.kills,
                             rr.placement_points, rr.kill_points, rr.total_points,
                             t.name AS team_name,
                             t.logo_url AS logo_url
                      FROM br_round_results rr
                      LEFT JOIN teams t ON t.id = rr.team_id
                      WHERE rr.round_id = @roundId
                      ORDER BY rr.placement
                      """,
                new { roundId });

            return Results.Ok(results);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/br/rounds/{roundId}/evidence ───────────────────────────
        // Staff see all submissions. Players only see their own submission for
        // the active round in their assigned group.
        app.MapGet("/api/br/rounds/{roundId}/evidence", async (
            Guid                roundId,
            HttpContext          ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var roundInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.stage_id,
                       ts.tournament_id,
                       t.team_size
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                JOIN tournament_stages ts ON ts.id = g.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE r.id = @roundId
                """,
                new { roundId });
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = (Guid)roundInfo.stage_id;
            var tournamentId = (Guid)roundInfo.tournament_id;
            var isSolo = Convert.ToInt32(roundInfo.team_size ?? 1) == 1;

            var isStaff = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            isStaff = isStaff || StaffAuthHelper.IsPlatformAdmin(userCtx);

            Guid? viewerTeamId = null;
            Guid? viewerParticipantId = null;

            if (!isStaff)
            {
                var viewerAccess = await ResolveRoundEntityAccessAsync(
                    conn,
                    roundId,
                    tournamentId,
                    userCtx.UserIdGuid,
                    isSolo);
                viewerTeamId = viewerAccess.TeamId;
                viewerParticipantId = viewerAccess.ParticipantId;

                if (viewerTeamId is null && viewerParticipantId is null)
                    return Results.Forbid();
            }

            var evidence = await conn.QueryAsync<dynamic>(
                """
                SELECT COALESCE(re.team_id, re.participant_id) AS entity_id,
                       CASE
                           WHEN re.team_id IS NOT NULL THEN t.name
                           ELSE COALESCE(p.username, tp.team_name, t.name, 'Mock Player')
                       END AS entity_name,
                       CASE WHEN re.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
                       re.image_url,
                       re.submitted_at,
                       re.placement,
                       re.kills,
                       re.reviewed
                FROM br_round_evidence re
                LEFT JOIN teams t ON t.id = re.team_id
                LEFT JOIN tournament_participants tp ON tp.id = re.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE re.round_id = @roundId
                  AND (
                    @isStaff = TRUE
                    OR (@viewerTeamId IS NOT NULL AND re.team_id = @viewerTeamId)
                    OR (@viewerParticipantId IS NOT NULL AND re.participant_id = @viewerParticipantId)
                  )
                ORDER BY re.submitted_at DESC
                """,
                new { roundId, isStaff, viewerTeamId, viewerParticipantId });

            var payload = evidence.Select(row => new
            {
                teamId = ((Guid)row.entity_id).ToString(),
                teamName = (string?)row.entity_name ?? "Unknown",
                logoUrl = (string?)row.logo_url,
                imageUrl = (string)row.image_url,
                submittedAt = ((DateTimeOffset)row.submitted_at).ToString("o"),
                placement = row.placement is not null ? Convert.ToInt32(row.placement) : (int?)null,
                kills = row.kills is not null ? Convert.ToInt32(row.kills) : (int?)null,
                reviewed = (bool)row.reviewed,
            });

            return Results.Ok(payload);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/br/rounds/{roundId}/evidence ───────────────────────────
        // Stores evidence against the relational round so organizer review and
        // multi-group BR stay aligned.
        app.MapPut("/api/br/rounds/{roundId}/evidence", async (
            Guid                roundId,
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

            var roundInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.stage_id,
                       g.id AS group_id,
                       ts.tournament_id,
                       t.team_size,
                       r.status
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                JOIN tournament_stages ts ON ts.id = g.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE r.id = @roundId
                """,
                new { roundId });
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = (Guid)roundInfo.stage_id;
            var groupId = (Guid)roundInfo.group_id;
            var tournamentId = (Guid)roundInfo.tournament_id;
            var isSolo = Convert.ToInt32(roundInfo.team_size ?? 1) == 1;
            var roundStatus = (string)roundInfo.status;

            if (roundStatus != "active")
                return Results.Conflict(new { error = "Evidence can only be submitted while the round is live." });

            var entityAccess = await ResolveRoundEntityAccessAsync(
                conn,
                roundId,
                tournamentId,
                userCtx.UserIdGuid,
                isSolo);
            var teamId = entityAccess.TeamId;
            var participantId = entityAccess.ParticipantId;

            if (teamId is null && participantId is null)
                return Results.Forbid();

            // Check if evidence has already been submitted — once submitted, it is locked.
            var alreadyExists = participantId is not null
                ? await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM br_round_evidence WHERE round_id = @roundId AND participant_id = @participantId)",
                    new { roundId, participantId })
                : await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM br_round_evidence WHERE round_id = @roundId AND team_id = @teamId)",
                    new { roundId, teamId });

            if (alreadyExists)
                return Results.Conflict(new { error = "Evidence has already been submitted for this round. Submissions cannot be changed." });

            try
            {
                if (participantId is not null)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_round_evidence (
                            round_id, team_id, participant_id, image_url, submitted_by, submitted_at,
                            placement, kills, reviewed, reviewed_at, reviewed_by
                        )
                        VALUES (
                            @roundId, NULL, @participantId, @imageUrl, @submittedBy, NOW(),
                            @placement, @kills, FALSE, NULL, NULL
                        )
                        """,
                        new
                        {
                            roundId,
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
                        INSERT INTO br_round_evidence (
                            round_id, team_id, participant_id, image_url, submitted_by, submitted_at,
                            placement, kills, reviewed, reviewed_at, reviewed_by
                        )
                        VALUES (
                            @roundId, @teamId, NULL, @imageUrl, @submittedBy, NOW(),
                            @placement, @kills, FALSE, NULL, NULL
                        )
                        """,
                        new
                        {
                            roundId,
                            teamId,
                            imageUrl,
                            submittedBy = userCtx.UserIdGuid,
                            placement,
                            kills
                        });
                }
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                                               && (ex.ConstraintName == "uq_br_round_evidence_team"
                                                   || ex.ConstraintName == "uq_br_round_evidence_participant"))
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
                    $"[BRGroupEndpoints] BR evidence schema missing for round {roundId}. " +
                    $"Postgres {ex.SqlState} {ex.MessageText}");

                return Results.Json(
                    new { error = "BR evidence storage is not available yet. Try again shortly or contact support." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (PostgresException ex)
            {
                Console.Error.WriteLine(
                    $"[BRGroupEndpoints] Failed to submit evidence for round {roundId}. " +
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
            try
            {
                var pendingCount = await GetPendingEvidenceCountAsync(conn, roundId);
                var evidencePayload = new
                {
                    stageId = stageId.ToString(),
                    groupId = groupId.ToString(),
                    roundId = roundId.ToString(),
                    entityId = entityId.ToString(),
                    pendingCount,
                };
                await BroadcastBrAsync(brHub, BRHubEvents.EvidenceSubmitted, stageId, groupId, roundId, evidencePayload, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[BRGroupEndpoints] Evidence saved for round {roundId} but post-submit notify failed: {ex.Message}");
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/br/rounds/{roundId}/evidence/{entityId} ───────────────
        // Organizer/staff review state for a submission.
        app.MapPatch("/api/br/rounds/{roundId}/evidence/{entityId}", async (
            Guid                roundId,
            Guid                entityId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db,
            IHubContext<BRHub>   brHub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!body.TryGetProperty("reviewed", out var reviewedProp) ||
                (reviewedProp.ValueKind != JsonValueKind.True && reviewedProp.ValueKind != JsonValueKind.False))
                return Results.BadRequest(new { error = "reviewed must be a boolean." });

            var reviewed = reviewedProp.GetBoolean();

            using var conn = db.CreateConnection();

            var roundInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.stage_id,
                       g.id AS group_id,
                       t.team_size
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                JOIN tournament_stages ts ON ts.id = g.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE r.id = @roundId
                """,
                new { roundId });
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = (Guid)roundInfo.stage_id;
            var groupId = (Guid)roundInfo.group_id;
            var isSolo = Convert.ToInt32(roundInfo.team_size ?? 1) == 1;

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var updated = await conn.ExecuteAsync(
                isSolo
                    ? """
                      UPDATE br_round_evidence
                      SET reviewed = @reviewed,
                          reviewed_at = CASE WHEN @reviewed THEN NOW() ELSE NULL END,
                          reviewed_by = CASE WHEN @reviewed THEN @reviewedBy ELSE NULL END
                      WHERE round_id = @roundId
                        AND participant_id = @entityId
                      """
                    : """
                      UPDATE br_round_evidence
                      SET reviewed = @reviewed,
                          reviewed_at = CASE WHEN @reviewed THEN NOW() ELSE NULL END,
                          reviewed_by = CASE WHEN @reviewed THEN @reviewedBy ELSE NULL END
                      WHERE round_id = @roundId
                        AND team_id = @entityId
                      """,
                new
                {
                    roundId,
                    entityId,
                    reviewed,
                    reviewedBy = userCtx.UserIdGuid
                });

            if (updated == 0)
                return Results.NotFound(new { error = "Evidence submission not found." });

            var pendingCount = await GetPendingEvidenceCountAsync(conn, roundId);
            var reviewPayload = new
            {
                stageId = stageId.ToString(),
                groupId = groupId.ToString(),
                roundId = roundId.ToString(),
                entityId = entityId.ToString(),
                reviewed,
                pendingCount,
            };
            await BroadcastBrAsync(brHub, BRHubEvents.EvidenceReviewed, stageId, groupId, roundId, reviewPayload, ct);

            return Results.Ok(new { success = true, reviewed });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/br/rounds/{roundId}/results ────────────────────────────
        // Bulk submit/update results for a round (idempotent upsert).
        app.MapPut("/api/br/rounds/{roundId}/results", async (
            Guid                roundId,
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

            var roundInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.stage_id,
                       g.id AS group_id,
                       r.status,
                       t.team_size,
                       t.game,
                       t.settings,
                       ts.config AS stage_config
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                JOIN tournament_stages ts ON ts.id = g.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE r.id = @roundId
                """,
                new { roundId });
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var roundInfoValues = (IDictionary<string, object>)roundInfo;
            if (!TryReadGuidValue(roundInfoValues, "stage_id", out var stageIdValue) || stageIdValue is null
                || !TryReadGuidValue(roundInfoValues, "group_id", out var groupIdValue) || groupIdValue is null)
            {
                return Results.Conflict(new { error = "This BR round has inconsistent metadata. Refresh and try again." });
            }

            var stageId = stageIdValue.Value;
            var groupId = groupIdValue.Value;
            var roundStatus = roundInfoValues.TryGetValue("status", out var roundStatusValue) && roundStatusValue is not DBNull
                ? roundStatusValue?.ToString() ?? string.Empty
                : string.Empty;
            var gameName = roundInfoValues.TryGetValue("game", out var gameValue) && gameValue is not DBNull
                ? gameValue?.ToString()
                : null;
            var rawSettings = roundInfoValues.TryGetValue("settings", out var settingsValue) && settingsValue is not DBNull
                ? settingsValue
                : null;
            var stageConfig = roundInfoValues.TryGetValue("stage_config", out var stageConfigValue) && stageConfigValue is not DBNull
                ? stageConfigValue
                : null;

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

            var groupTeamsHasParticipantId = await ColumnExistsAsync(conn, "br_group_teams", "participant_id");
            var roundResultsHasParticipantId = await ColumnExistsAsync(conn, "br_round_results", "participant_id");
            var roundResultsTeamIdAllowsNull = await ColumnAllowsNullAsync(conn, "br_round_results", "team_id");
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
            var scoring = BattleRoyaleConfigResolver.ResolveScoring(rawSettings, stageConfig, catalogBrConfig);

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

            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(
                    "SELECT 1 FROM br_rounds WHERE id = @roundId FOR UPDATE",
                    new { roundId },
                    tx);

                await conn.ExecuteAsync(
                    "DELETE FROM br_round_results WHERE round_id = @roundId",
                    new { roundId },
                    tx);

                var materializedResults = parsedResults
                    .Select(result =>
                    {
                        var points = BattleRoyaleConfigResolver.CalculatePoints(result.Placement, result.Kills, scoring);
                        var rosterEntity = rosterByEntityId[result.EntityId];
                        return new
                        {
                            roundId,
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
                        result.roundId,
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
                        result.roundId,
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
                            result.roundId,
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
                        INSERT INTO br_round_results (round_id, team_id, placement, kills, placement_points, kill_points)
                        VALUES (@roundId, @teamId, @placement, @kills, @placementPoints, @killPoints)
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
                        INSERT INTO br_round_results (round_id, participant_id, placement, kills, placement_points, kill_points)
                        VALUES (@roundId, @participantId, @placement, @kills, @placementPoints, @killPoints)
                        """,
                        participantBackedResults,
                        tx);
                }

                tx.Commit();

                var resultsPayload = new
                {
                    stageId = stageId.ToString(),
                    groupId = groupId.ToString(),
                    roundId = roundId.ToString(),
                    saved = parsedResults.Count,
                };
                await BroadcastBrAsync(brHub, BRHubEvents.ResultsUpdated, stageId, groupId, roundId, resultsPayload, ct);
                await BroadcastBrAsync(
                    brHub,
                    BRHubEvents.LeaderboardUpdated,
                    stageId,
                    groupId,
                    roundId,
                    BuildLeaderboardEvent(stageId, groupId),
                    ct);

                return Results.Ok(new { saved = parsedResults.Count });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                                               && ex.ConstraintName == "uq_br_round_results_round_placement")
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
                    $"[BRGroupEndpoints] Failed to save results for round {roundId}. " +
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
                Console.Error.WriteLine($"[BRGroupEndpoints] Failed to save results for round {roundId}. {ex}");

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
            var tiebreaker = BattleRoyaleConfigResolver.ResolveTiebreaker(stageMeta?.settings);

            var roundResultsHasParticipantId = await ColumnExistsAsync(conn, "br_round_results", "participant_id");

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
                          COUNT(DISTINCT rr.round_id) AS games_played,
                          SUM(rr.placement_points) AS total_placement_points,
                          SUM(rr.kill_points) AS total_kill_points,
                          SUM(rr.total_points) AS total_points,
                          SUM(rr.kills) AS total_kills,
                          COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
                          MIN(rr.placement) AS best_placement,
                          AVG(rr.placement::numeric) AS avg_placement
                      FROM br_round_results rr
                      JOIN br_rounds r ON r.id = rr.round_id
                      LEFT JOIN teams t ON t.id = rr.team_id
                      LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                      LEFT JOIN profiles p ON p.id = tp.user_id
                      WHERE r.group_id = @groupId
                        AND r.status = 'completed'
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
                          COUNT(DISTINCT rr.round_id) AS games_played,
                          SUM(rr.placement_points) AS total_placement_points,
                          SUM(rr.kill_points) AS total_kill_points,
                          SUM(rr.total_points) AS total_points,
                          SUM(rr.kills) AS total_kills,
                          COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
                          MIN(rr.placement) AS best_placement,
                          AVG(rr.placement::numeric) AS avg_placement
                      FROM br_round_results rr
                      JOIN br_rounds r ON r.id = rr.round_id
                      LEFT JOIN teams t ON t.id = rr.team_id
                      WHERE r.group_id = @groupId
                        AND r.status = 'completed'
                      GROUP BY rr.team_id, t.name, t.logo_url
                      """,
                new { groupId })).ToList();

            leaderboardRows.Sort((a, b) =>
            {
                var aggregateA = new BattleRoyaleConfigResolver.BrLeaderboardAggregate(
                    Convert.ToInt64(a.total_points),
                    Convert.ToInt64(a.wins),
                    Convert.ToInt64(a.total_kills),
                    a.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(a.avg_placement));
                var aggregateB = new BattleRoyaleConfigResolver.BrLeaderboardAggregate(
                    Convert.ToInt64(b.total_points),
                    Convert.ToInt64(b.wins),
                    Convert.ToInt64(b.total_kills),
                    b.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(b.avg_placement));
                return BattleRoyaleConfigResolver.CompareLeaderboardEntries(aggregateA, aggregateB, tiebreaker);
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

            // ── Find the user's group ────────────────────────────────────────
            var groupRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ts.id AS stage_id, ts.name AS stage_name, ts.stage_order,
                       g.id AS group_id, g.name AS group_name
                FROM tournament_stages ts
                JOIN br_groups g ON g.stage_id = ts.id
                WHERE ts.tournament_id = @tournamentId
                  AND EXISTS (
                      SELECT 1
                      FROM br_group_teams bgt
                      JOIN tournament_participants tp ON (
                          (bgt.team_id IS NOT NULL AND tp.team_id = bgt.team_id)
                          OR (bgt.participant_id IS NOT NULL AND tp.id = bgt.participant_id)
                      )
                      LEFT JOIN team_members tm
                        ON tm.team_id = bgt.team_id
                       AND tm.user_id = @userId
                       AND tm.is_active = TRUE
                      WHERE bgt.group_id = g.id
                        AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                        AND (tp.user_id = @userId OR tm.user_id IS NOT NULL)
                  )
                ORDER BY ts.stage_order ASC
                LIMIT 1
                """,
                new { tournamentId, userId = userCtx.UserIdGuid });

            // Not in a group — return minimal response
            if (groupRow is null)
            {
                return Results.Ok(new
                {
                    stageId          = (string?)null,
                    stageName        = (string?)null,
                    groupId          = (string?)null,
                    groupName        = (string?)null,
                    totalRounds      = 0,
                    completedRounds  = 0,
                    activeRound      = (object?)null,
                });
            }

            Guid groupId = groupRow.group_id;

            // ── Fetch rounds for the group ───────────────────────────────────
            var rounds = (await conn.QueryAsync<dynamic>(
                """
                SELECT id, round_number, lobby_code, status, scheduled_at, started_at, completed_at,
                       queue_timer_minutes, queue_started_at
                FROM br_rounds
                WHERE group_id = @groupId
                ORDER BY round_number
                """,
                new { groupId })).ToList();

            int totalRounds     = rounds.Count;
            int completedRounds = rounds.Count(r => (string)r.status == "completed");

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
                var stageIdForCheck = (Guid)groupRow.stage_id;
                isStaff = await StaffAuthHelper.CanActOnStageAsync(
                    conn, userCtx.UserIdGuid, stageIdForCheck, StaffAuthHelper.PermBracketEdit);
            }

            // ── Build active round payload ───────────────────────────────────
            var activeRoundRow = rounds
                .Where(r => (string)r.status == "active")
                .OrderByDescending(r => ReadRoundTimestamp(r, "queue_started_at") ?? DateTimeOffset.MinValue)
                .ThenByDescending(r => HasRoundLobbyCode(r))
                .ThenByDescending(r => ReadRoundTimestamp(r, "started_at") ?? DateTimeOffset.MinValue)
                .ThenByDescending(r => Convert.ToInt32(r.round_number))
                .FirstOrDefault();
            object? activeRoundPayload = null;
            if (activeRoundRow is not null)
            {
                // Lobby code is returned only if the round is active OR user is staff
                var lobbyCode = isStaff || (string)activeRoundRow.status == "active"
                    ? (string?)activeRoundRow.lobby_code
                    : null;

                activeRoundPayload = new
                {
                    id           = ((Guid)activeRoundRow.id).ToString(),
                    roundNumber  = Convert.ToInt32(activeRoundRow.round_number),
                    lobbyCode,
                    status       = (string)activeRoundRow.status,
                    queueTimerMinutes = activeRoundRow.queue_timer_minutes is not null
                        ? Convert.ToInt32(activeRoundRow.queue_timer_minutes)
                        : (int?)null,
                    queueStartedAt = activeRoundRow.queue_started_at is not null
                        ? ((DateTimeOffset)activeRoundRow.queue_started_at).ToString("o")
                        : (string?)null,
                    scheduledAt  = activeRoundRow.scheduled_at is not null
                        ? ((DateTimeOffset)activeRoundRow.scheduled_at).ToString("o")
                        : (string?)null,
                };
            }

            return Results.Ok(new
            {
                stageId         = ((Guid)groupRow.stage_id).ToString(),
                stageName       = (string)groupRow.stage_name,
                groupId         = ((Guid)groupRow.group_id).ToString(),
                groupName       = (string)groupRow.group_name,
                totalRounds,
                completedRounds,
                activeRound     = activeRoundPayload,
            });
        }).RequireAuthorization("Authenticated");
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
            var tiebreaker = BattleRoyaleConfigResolver.ResolveTiebreaker(stage.settings);

            // Optional override from body: { teamsPerGroup: 4 }
            int teamsPerGroup = 0;
            if (body.TryGetProperty("teamsPerGroup", out var tpg) && tpg.TryGetInt32(out var tpgVal))
                teamsPerGroup = tpgVal;
            if (teamsPerGroup <= 0)
                teamsPerGroup = (int)(stage.advancement_count ?? 4);
            if (teamsPerGroup <= 0)
                return Results.BadRequest(new { error = "advancement_count not configured and teamsPerGroup not provided." });

            // Get all groups with their leaderboards
            var groups = (await conn.QueryAsync<dynamic>(
                "SELECT id, name, group_order FROM br_groups WHERE stage_id = @stageId ORDER BY group_order",
                new { stageId })).ToList();

            if (groups.Count == 0)
                return Results.BadRequest(new { error = "No groups exist in this stage." });

            // Check all groups have at least one completed round
            var incompleteGroups = await conn.QueryAsync<dynamic>(
                """
                SELECT g.name FROM br_groups g
                WHERE g.stage_id = @stageId
                AND NOT EXISTS (
                    SELECT 1 FROM br_rounds r
                    WHERE r.group_id = g.id AND r.status = 'completed'
                )
                """,
                new { stageId });
            var incompleteList = incompleteGroups.ToList();
            if (incompleteList.Count > 0)
            {
                var names = string.Join(", ", incompleteList.Select(g => (string)g.name));
                return Results.BadRequest(new { error = $"Groups with no completed rounds: {names}" });
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
                FROM br_round_results rr
                JOIN br_rounds r ON r.id = rr.round_id
                JOIN br_groups g ON g.id = r.group_id
                LEFT JOIN teams t ON t.id = rr.team_id
                LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE g.stage_id = @stageId
                  AND r.status = 'completed'
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
                        var aggregateA = new BattleRoyaleConfigResolver.BrLeaderboardAggregate(
                            Convert.ToInt64(a.total_points),
                            Convert.ToInt64(a.wins),
                            Convert.ToInt64(a.total_kills),
                            a.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(a.avg_placement));
                        var aggregateB = new BattleRoyaleConfigResolver.BrLeaderboardAggregate(
                            Convert.ToInt64(b.total_points),
                            Convert.ToInt64(b.wins),
                            Convert.ToInt64(b.total_kills),
                            b.avg_placement is null ? double.PositiveInfinity : Convert.ToDouble(b.avg_placement));
                        return BattleRoyaleConfigResolver.CompareLeaderboardEntries(aggregateA, aggregateB, tiebreaker);
                    });

                    return groupRows
                        .Take(teamsPerGroup)
                        .Select((row, index) => new { row, rank_in_group = index + 1 });
                })
                .OrderBy(entry => (string)entry.row.group_name)
                .ThenBy(entry => entry.rank_in_group)
                .Select(entry => entry.row)
                .ToList();

            var qualifiedTeams = qualifiedRows.Select(r => new
            {
                team_id            = (Guid)r.entity_id,
                raw_team_id        = (Guid?)r.team_id,
                raw_participant_id = (Guid?)r.participant_id,
                team_name          = (string)r.team_name,
                logo_url           = (string?)r.logo_url,
                from_group         = (string)r.group_name,
                total_points       = (long)r.total_points,
                total_kills        = (long)r.total_kills,
                wins               = (long)r.wins,
            }).ToList();

            if (preview)
            {
                return Results.Ok(new
                {
                    stage_name      = (string)stage.name,
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
                "SELECT id, name FROM tournament_stages WHERE tournament_id = @tournamentId AND stage_order = @nextOrder",
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

                // Create a single group in next stage for the finals
                var finalsGroupId = Guid.NewGuid();
                await conn.ExecuteAsync(
                    """
                    INSERT INTO br_groups (id, stage_id, name, group_order, lobby_size)
                    VALUES (@id, @stageId, @name, 1, @lobbySize)
                    """,
                    new
                    {
                        id = finalsGroupId,
                        stageId = nextStageId,
                        name = "Finals",
                        lobbySize = qualifiedTeams.Count
                    }, tx);

                // Insert qualified entities into finals group — solo vs. team
                if (isSolo)
                {
                    var participantInserts = qualifiedTeams.Select((qt, i) => new
                    {
                        id = Guid.NewGuid(),
                        group_id = finalsGroupId,
                        participant_id = qt.raw_participant_id!.Value,
                        seed_order = i + 1,
                        assigned_at = DateTime.UtcNow,
                    }).ToList();

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_group_teams (id, group_id, participant_id, seed_order, assigned_at)
                        VALUES (@id, @group_id, @participant_id, @seed_order, @assigned_at)
                        """,
                        participantInserts, tx);
                }
                else
                {
                    var teamInserts = qualifiedTeams.Select((qt, i) => new
                    {
                        id = Guid.NewGuid(),
                        group_id = finalsGroupId,
                        team_id = qt.raw_team_id!.Value,
                        seed_order = i + 1,
                        assigned_at = DateTime.UtcNow,
                    }).ToList();

                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_group_teams (id, group_id, team_id, seed_order, assigned_at)
                        VALUES (@id, @group_id, @team_id, @seed_order, @assigned_at)
                        """,
                        teamInserts, tx);
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
                    finals_group_id = finalsGroupId,
                });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<bool> IsUserAssignedToGroupAsync(
        System.Data.IDbConnection conn,
        Guid groupId,
        Guid userId,
        IDbTransaction? tx = null)
    {
        return await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM br_group_teams bgt
                JOIN tournament_participants tp ON (
                    (bgt.team_id IS NOT NULL AND tp.team_id = bgt.team_id)
                    OR (bgt.participant_id IS NOT NULL AND tp.id = bgt.participant_id)
                )
                LEFT JOIN team_members tm
                  ON tm.team_id = bgt.team_id
                 AND tm.user_id = @userId
                 AND tm.is_active = TRUE
                WHERE bgt.group_id = @groupId
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                  AND (tp.user_id = @userId OR tm.user_id IS NOT NULL)
            )
            """,
            new { groupId, userId },
            tx);
    }

    private static async Task<BrEntityAccess> ResolveRoundEntityAccessAsync(
        System.Data.IDbConnection conn,
        Guid roundId,
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
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                JOIN br_group_teams bgt ON bgt.group_id = g.id
                JOIN tournament_participants tp ON tp.id = bgt.participant_id
                WHERE r.id = @roundId
                  AND tp.tournament_id = @tournamentId
                  AND tp.user_id = @userId
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                LIMIT 1
                """,
                new { roundId, tournamentId, userId },
                tx);

            if (participantId is not null)
                return new BrEntityAccess(null, participantId);

            var soloTeamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT bgt.team_id
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                JOIN br_group_teams bgt ON bgt.group_id = g.id
                JOIN tournament_participants tp ON tp.team_id = bgt.team_id
                WHERE r.id = @roundId
                  AND tp.tournament_id = @tournamentId
                  AND tp.user_id = @userId
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                  AND bgt.team_id IS NOT NULL
                LIMIT 1
                """,
                new { roundId, tournamentId, userId },
                tx);

            return new BrEntityAccess(soloTeamId, null);
        }

        var teamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT bgt.team_id
            FROM br_rounds r
            JOIN br_groups g ON g.id = r.group_id
            JOIN br_group_teams bgt ON bgt.group_id = g.id
            JOIN tournament_participants tp ON tp.team_id = bgt.team_id
            JOIN team_members tm ON tm.team_id = bgt.team_id
            WHERE r.id = @roundId
              AND tp.tournament_id = @tournamentId
              AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
              AND tm.user_id = @userId
              AND tm.is_active = TRUE
            LIMIT 1
            """,
            new { roundId, tournamentId, userId },
            tx);

        if (teamId is not null)
            return new BrEntityAccess(teamId, null);

        var teamParticipantId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            SELECT tp.id
            FROM br_rounds r
            JOIN br_groups g ON g.id = r.group_id
            JOIN br_group_teams bgt ON bgt.group_id = g.id
            JOIN tournament_participants tp ON tp.id = bgt.participant_id
            WHERE r.id = @roundId
              AND tp.tournament_id = @tournamentId
              AND tp.user_id = @userId
              AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
            LIMIT 1
            """,
            new { roundId, tournamentId, userId },
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

    private static async Task<bool> ColumnExistsAsync(
        IDbConnection conn,
        string tableName,
        string columnName,
        IDbTransaction? tx = null)
    {
        return await conn.QuerySingleAsync<bool>(
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

    /// <summary>
    /// Shuffle a copy of team IDs using Fisher-Yates via Random.Shared.
    /// </summary>
    private static Guid[] ShuffleTeams(Guid[] teams)
    {
        var shuffled = teams.ToArray();
        Random.Shared.Shuffle(shuffled);
        return shuffled;
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

        if (tournament is null)
            return false;

        var status = ((string)tournament.status).ToLowerInvariant();

        if (!string.Equals(status, "draft", StringComparison.OrdinalIgnoreCase))
            return true;

        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null)
            return false;

        return (Guid)tournament.organizer_id == userCtx.UserIdGuid
            || StaffAuthHelper.IsPlatformAdmin(userCtx)
            || await StaffAuthHelper.CanActOnStageAsync(conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
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
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
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
        Guid roundId)
    {
        var groupTeamsHasParticipantId = await ColumnExistsAsync(conn, "br_group_teams", "participant_id", tx);
        var roundResultsHasParticipantId = await ColumnExistsAsync(conn, "br_round_results", "participant_id", tx);

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
                FROM br_round_results
                WHERE round_id = @roundId
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

        return await conn.ExecuteScalarAsync<bool>(sql, new { groupId, roundId }, tx);
    }

    /// <summary>
    /// Round-robin assignment: team 0 → group 0, team 1 → group 1, ..., wraps around.
    /// </summary>
    private static List<(Guid teamId, Guid groupId, int seedOrder)> BuildRoundRobinAssignments(
        Guid[] teams, Guid[] groupIds)
    {
        var assignments = new List<(Guid, Guid, int)>();
        var seedCounters = new int[groupIds.Length];

        for (var i = 0; i < teams.Length; i++)
        {
            var groupIndex = i % groupIds.Length;
            seedCounters[groupIndex]++;
            assignments.Add((teams[i], groupIds[groupIndex], seedCounters[groupIndex]));
        }

        return assignments;
    }

    /// <summary>
    /// Snake draft: 0,1,2 → 2,1,0 → 0,1,2 → ...
    /// For 3 groups: team 0→G0, 1→G1, 2→G2, 3→G2, 4→G1, 5→G0, 6→G0, ...
    /// </summary>
    private static List<(Guid teamId, Guid groupId, int seedOrder)> BuildSnakeAssignments(
        Guid[] teams, Guid[] groupIds)
    {
        var assignments = new List<(Guid, Guid, int)>();
        var seedCounters = new int[groupIds.Length];
        var groupCount = groupIds.Length;

        for (var i = 0; i < teams.Length; i++)
        {
            // Determine direction: even passes go forward (0,1,2), odd passes go backward (2,1,0)
            var pass = i / groupCount;
            var posInPass = i % groupCount;
            var groupIndex = pass % 2 == 0 ? posInPass : groupCount - 1 - posInPass;

            seedCounters[groupIndex]++;
            assignments.Add((teams[i], groupIds[groupIndex], seedCounters[groupIndex]));
        }

        return assignments;
    }

    private static object BuildRoundEvent(
        Guid stageId,
        Guid groupId,
        Guid roundId,
        int roundNumber,
        string? status = null) => new
    {
        stageId = stageId.ToString(),
        groupId = groupId.ToString(),
        roundId = roundId.ToString(),
        roundNumber,
        status
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
        Guid? roundId,
        object payload,
        CancellationToken ct = default)
    {
        _ = ct;
        var tasks = new List<Task>
        {
            hub.Clients.Group(BRHub.StageGroup(stageId.ToString())).SendAsync(eventName, payload, CancellationToken.None),
            hub.Clients.Group(BRHub.GroupGroup(groupId.ToString())).SendAsync(eventName, payload, CancellationToken.None),
        };

        if (roundId is not null)
        {
            tasks.Add(hub.Clients.Group(BRHub.RoundGroup(roundId.Value.ToString())).SendAsync(eventName, payload, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[BRGroupEndpoints] Failed to broadcast {eventName} for stage {stageId}, group {groupId}, round {roundId}: {ex.Message}");
        }
    }

    private static async Task<int> CountPendingEvidenceAsync(
        IDbConnection conn,
        Guid roundId,
        IDbTransaction? tx = null)
    {
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM br_round_evidence WHERE round_id = @roundId AND reviewed = FALSE",
            new { roundId },
            tx);
        return Convert.ToInt32(count);
    }

    private static async Task<int> GetPendingEvidenceCountAsync(
        IDbConnection conn,
        Guid roundId,
        IDbTransaction? tx = null) =>
        await CountPendingEvidenceAsync(conn, roundId, tx);
}
