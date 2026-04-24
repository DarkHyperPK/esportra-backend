using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class BRGroupEndpoints
{
    public static void MapBRGroupEndpoints(this WebApplication app)
    {
        // ── GET /api/stages/{stageId}/br/groups ─────────────────────────────
        // List all groups for a BR stage with team counts. Public endpoint.
        app.MapGet("/api/stages/{stageId}/br/groups", async (
            Guid              stageId,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();

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
        // Batch endpoint: returns all groups' teams + has_rounds flag in 2 queries.
        // Replaces N×2 parallel fetches from the organizer UI group section.
        app.MapGet("/api/stages/{stageId}/br/groups/detail", async (
            Guid              stageId,
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

            using var multi = await conn.QueryMultipleAsync(
                """
                SELECT g.id, g.name, g.group_order, g.lobby_size, g.created_at,
                       (SELECT COUNT(*) FROM br_group_teams gt2 WHERE gt2.group_id = g.id) AS team_count
                FROM br_groups g
                WHERE g.stage_id = @stageId
                ORDER BY g.group_order;

                SELECT EXISTS(
                    SELECT 1 FROM br_rounds r
                    JOIN br_groups g ON g.id = r.group_id
                    WHERE g.stage_id = @stageId
                ) AS has_rounds;

                SELECT
                    g.id AS group_id,
                    COALESCE(gt.team_id, gt.participant_id) AS team_id,
                    gt.seed_order,
                    gt.assigned_at,
                    CASE WHEN gt.team_id IS NOT NULL THEN t.name ELSE p.username END AS team_name,
                    CASE WHEN gt.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url
                FROM br_groups g
                JOIN br_group_teams gt ON gt.group_id = g.id
                LEFT JOIN teams t ON t.id = gt.team_id
                LEFT JOIN tournament_participants tp ON tp.id = gt.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE g.stage_id = @stageId
                ORDER BY g.group_order, gt.seed_order;
                """,
                new { stageId });

            var groups    = (await multi.ReadAsync<dynamic>()).ToList();
            var hasRounds = await multi.ReadSingleAsync<bool>();
            var rows      = (await multi.ReadAsync<dynamic>()).ToList();

            var teamsByGroup = new Dictionary<string, List<object>>();
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

            // Check if existing groups have rounds (protect against accidental data loss)
            var hasRounds = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM br_rounds r
                    JOIN br_groups g ON g.id = r.group_id
                    WHERE g.stage_id = @stageId
                )
                """,
                new { stageId });
            if (hasRounds && !force)
                return Results.Conflict(new { error = "Groups already have rounds. Set force=true to delete all existing data." });

            using var tx = conn.BeginTransaction();
            try
            {
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

            await conn.ExecuteAsync(
                "DELETE FROM br_groups WHERE id = @groupId",
                new { groupId });

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
                    CASE WHEN gt.team_id IS NOT NULL THEN t.name ELSE p.username END AS team_name,
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
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();

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
                    CASE WHEN gt.team_id IS NOT NULL THEN t.name ELSE p.username END AS team_name,
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

            var groupIds = groups.Select(g => (Guid)g.id).ToArray();

            using var tx = conn.BeginTransaction();
            try
            {
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
                          AND status IN ('pending', 'approved')
                        """,
                        new { tournamentId })).ToArray();

                    if (participantIds.Length == 0)
                        return Results.BadRequest(new { error = "No registered participants found. Ensure participants have status 'pending' or 'approved'." });

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
                    return Results.Ok(new { assigned = assignments.Count, groups = groups.Count });
                }
                else
                {
                    // Team-based: existing logic
                    var teamIds = (await conn.QueryAsync<Guid>(
                        """
                        SELECT DISTINCT team_id
                        FROM tournament_participants
                        WHERE tournament_id = @tournamentId
                          AND status IN ('pending','approved')
                          AND team_id IS NOT NULL
                        """,
                        new { tournamentId })).ToArray();

                    if (teamIds.Length == 0)
                        return Results.BadRequest(new { error = "No registered teams found. Ensure participants have status 'pending' or 'approved'." });

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
                    return Results.Ok(new { assigned = assignments.Count, groups = groups.Count });
                }
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
                          AND status IN ('pending', 'approved')
                          AND id = ANY(@idsArr)
                        """,
                        new { tournamentId, idsArr = teamIds.ToArray() })).ToHashSet();

                    var invalid = teamIds.Where(t => !validParticipantIds.Contains(t)).ToList();
                    if (invalid.Count > 0)
                        return Results.BadRequest(new { error = $"Participants not registered in tournament: {string.Join(", ", invalid)}" });
                }
                else
                {
                    // Team-based: validate against team_id in tournament_participants
                    var validTeamIds = (await conn.QueryAsync<Guid>(
                        """
                        SELECT DISTINCT team_id FROM tournament_participants
                        WHERE tournament_id = @tournamentId
                          AND status IN ('pending','approved')
                          AND team_id = ANY(@teamIdArr)
                        """,
                        new { tournamentId, teamIdArr = teamIds.ToArray() })).ToHashSet();

                    var invalid = teamIds.Where(t => !validTeamIds.Contains(t)).ToList();
                    if (invalid.Count > 0)
                        return Results.BadRequest(new { error = $"Teams not registered in tournament: {string.Join(", ", invalid)}" });
                }
            }

            using var tx = conn.BeginTransaction();
            try
            {
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

            // Verify groupId belongs to this stageId
            var groupExists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM br_groups WHERE id = @groupId AND stage_id = @stageId)",
                new { groupId, stageId });
            if (!groupExists)
                return Results.NotFound(new { error = "Group not found in this stage." });

            var rounds = await conn.QueryAsync<dynamic>(
                """
                SELECT r.id, r.round_number, r.lobby_code, r.status,
                       r.scheduled_at, r.started_at, r.completed_at, r.created_at,
                       r.queue_timer_minutes, r.queue_started_at,
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
                && await conn.QuerySingleOrDefaultAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM tournament_participants tp
                        JOIN tournament_stages ts ON ts.tournament_id = tp.tournament_id
                        JOIN br_groups g          ON g.stage_id        = ts.id
                        WHERE g.id = @groupId
                          AND tp.user_id = @userId
                          AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
                    )
                    """,
                    new { groupId, userId = userCtx.UserIdGuid });

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
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            // Verify groupId belongs to this stageId
            var groupExists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM br_groups WHERE id = @groupId AND stage_id = @stageId)",
                new { groupId, stageId });
            if (!groupExists)
                return Results.NotFound(new { error = "Group not found in this stage." });

            var lobbyCode   = body.TryGetProperty("lobbyCode", out var lc) ? lc.GetString()?.Trim() : null;
            var scheduledAt = body.TryGetProperty("scheduledAt", out var sa) ? sa.GetString() : null;
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

            var round = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO br_rounds (group_id, round_number, lobby_code, scheduled_at, queue_timer_minutes)
                VALUES (
                    @groupId,
                    (SELECT COALESCE(MAX(round_number), 0) + 1 FROM br_rounds WHERE group_id = @groupId),
                    @lobbyCode,
                    @scheduledAt,
                    @queueTimerMinutes
                )
                RETURNING id, round_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at,
                          queue_timer_minutes, queue_started_at
                """,
                new { groupId, lobbyCode, scheduledAt = parsedSchedule, queueTimerMinutes });

            return Results.Ok(round);
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/br/rounds/{roundId} ──────────────────────────────────
        // Update a round (lobby code, status, schedule).
        app.MapPatch("/api/br/rounds/{roundId}", async (
            Guid                roundId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> notifHub) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Resolve stage + current round state once so update logic is consistent.
            var currentRound = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.stage_id,
                       g.id AS group_id,
                       r.status,
                       r.lobby_code,
                       r.queue_timer_minutes,
                       r.queue_started_at
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                WHERE r.id = @roundId
                """,
                new { roundId });
            if (currentRound is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = (Guid)currentRound.stage_id;
            var groupId = (Guid)currentRound.group_id;
            var currentStatus = (string)currentRound.status;
            var currentLobbyCode = (string?)currentRound.lobby_code;
            int? currentQueueTimerMinutes = currentRound.queue_timer_minutes is not null
                ? Convert.ToInt32(currentRound.queue_timer_minutes)
                : null;
            var currentQueueStartedAt = currentRound.queue_started_at as DateTimeOffset?;

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            // Build dynamic SET clauses for provided fields
            var setClauses = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("roundId", roundId);
            string? finalLobbyCode = currentLobbyCode;
            var finalStatus = currentStatus;
            int? finalQueueTimerMinutes = currentQueueTimerMinutes;

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
                        setClauses.Add("scheduled_at = @scheduledAt");
                        parameters.Add("scheduledAt", dt);
                    }
                    else
                    {
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
                    return Results.BadRequest(new { error = "queueTimerMinutes must be between 0 and 180." });
                }
            }

            if (body.TryGetProperty("status", out var stProp))
            {
                var newStatus = stProp.GetString();
                if (newStatus is not ("pending" or "active" or "completed"))
                    return Results.BadRequest(new { error = "status must be 'pending', 'active', or 'completed'." });

                // Enforce valid status transitions
                var validTransition = (currentStatus, newStatus) switch
                {
                    ("pending", "active")       => true,
                    ("active", "completed")      => true,
                    ("completed", "active")      => true, // allow re-opening
                    _ => false
                };
                if (!validTransition)
                    return Results.BadRequest(new { error = $"Cannot transition from '{currentStatus}' to '{newStatus}'." });

                if (newStatus == "active" && currentStatus != "active")
                {
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
                        new { groupId, roundId });

                    if (existingActiveRound is not null)
                    {
                        return Results.BadRequest(new
                        {
                            error = $"Round {Convert.ToInt32(existingActiveRound.round_number)} is already live. Complete, re-open, or reset it before starting another round."
                        });
                    }
                }

                finalStatus = newStatus;
                setClauses.Add("status = @status");
                parameters.Add("status", newStatus);

                if (newStatus == "active" && currentStatus == "pending")
                    setClauses.Add("started_at = NOW()");
                else if (newStatus == "active" && currentStatus == "completed")
                    setClauses.Add("completed_at = NULL"); // clear when re-opening
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
                return Results.BadRequest(new { error = "No fields to update." });

            var sql = $"""
                UPDATE br_rounds
                SET {string.Join(", ", setClauses)}
                WHERE id = @roundId
                RETURNING id, round_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at,
                          queue_timer_minutes, queue_started_at
                """;

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, parameters);
            if (updated is null) return Results.NotFound();

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
                            SELECT DISTINCT tp.user_id::text
                            FROM br_group_teams bgt
                            JOIN tournament_participants tp ON (
                                (tp.team_id IS NOT NULL AND tp.team_id = bgt.team_id)
                                OR (bgt.participant_id IS NOT NULL AND bgt.participant_id = tp.id)
                            )
                            WHERE bgt.group_id = @groupId
                              AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
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
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var roundInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.stage_id
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                WHERE r.id = @roundId
                """,
                new { roundId });
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            var stageId = (Guid)roundInfo.stage_id;
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            using var tx = conn.BeginTransaction();
            try
            {
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
                        started_at = NULL,
                        completed_at = NULL,
                        queue_started_at = NULL
                    WHERE id = @roundId
                    RETURNING id, round_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at,
                              queue_timer_minutes, queue_started_at
                    """,
                    new { roundId },
                    tx);

                tx.Commit();
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

            // Use LEFT JOINs with COALESCE for unified team/solo display
            var results = await conn.QueryAsync<dynamic>(
                """
                SELECT rr.id,
                       COALESCE(rr.team_id, rr.participant_id) AS team_id,
                       rr.placement, rr.kills,
                       rr.placement_points, rr.kill_points, rr.total_points,
                       CASE WHEN rr.team_id IS NOT NULL THEN t.name ELSE p.username END AS team_name,
                       CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url
                FROM br_round_results rr
                LEFT JOIN teams t ON t.id = rr.team_id
                LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
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
                if (isSolo)
                {
                    viewerParticipantId = await conn.QuerySingleOrDefaultAsync<Guid?>(
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
                        new { roundId, tournamentId, userId = userCtx.UserIdGuid });
                }
                else
                {
                    viewerTeamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
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
                        new { roundId, tournamentId, userId = userCtx.UserIdGuid });
                }

                if (viewerTeamId is null && viewerParticipantId is null)
                    return Results.Forbid();
            }

            var evidence = await conn.QueryAsync<dynamic>(
                """
                SELECT COALESCE(re.team_id, re.participant_id) AS entity_id,
                       CASE WHEN re.team_id IS NOT NULL THEN t.name ELSE p.username END AS entity_name,
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
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!body.TryGetProperty("imageUrl", out var imageUrlProp))
                return Results.BadRequest(new { error = "imageUrl is required." });

            var imageUrl = imageUrlProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(imageUrl))
                return Results.BadRequest(new { error = "imageUrl is required." });

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

            var tournamentId = (Guid)roundInfo.tournament_id;
            var isSolo = Convert.ToInt32(roundInfo.team_size ?? 1) == 1;

            Guid? teamId = null;
            Guid? participantId = null;

            if (isSolo)
            {
                participantId = await conn.QuerySingleOrDefaultAsync<Guid?>(
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
                    new { roundId, tournamentId, userId = userCtx.UserIdGuid });
            }
            else
            {
                teamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
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
                    new { roundId, tournamentId, userId = userCtx.UserIdGuid });
            }

            if (teamId is null && participantId is null)
                return Results.Forbid();

            if (isSolo)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO br_round_evidence (
                        round_id, participant_id, image_url, submitted_by, submitted_at,
                        placement, kills, reviewed, reviewed_at, reviewed_by
                    )
                    VALUES (
                        @roundId, @participantId, @imageUrl, @submittedBy, NOW(),
                        @placement, @kills, FALSE, NULL, NULL
                    )
                    ON CONFLICT (round_id, participant_id) WHERE participant_id IS NOT NULL DO UPDATE
                    SET image_url = EXCLUDED.image_url,
                        submitted_by = EXCLUDED.submitted_by,
                        submitted_at = NOW(),
                        placement = EXCLUDED.placement,
                        kills = EXCLUDED.kills,
                        reviewed = FALSE,
                        reviewed_at = NULL,
                        reviewed_by = NULL
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
                        round_id, team_id, image_url, submitted_by, submitted_at,
                        placement, kills, reviewed, reviewed_at, reviewed_by
                    )
                    VALUES (
                        @roundId, @teamId, @imageUrl, @submittedBy, NOW(),
                        @placement, @kills, FALSE, NULL, NULL
                    )
                    ON CONFLICT (round_id, team_id) WHERE team_id IS NOT NULL DO UPDATE
                    SET image_url = EXCLUDED.image_url,
                        submitted_by = EXCLUDED.submitted_by,
                        submitted_at = NOW(),
                        placement = EXCLUDED.placement,
                        kills = EXCLUDED.kills,
                        reviewed = FALSE,
                        reviewed_at = NULL,
                        reviewed_by = NULL
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

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/br/rounds/{roundId}/evidence/{entityId} ───────────────
        // Organizer/staff review state for a submission.
        app.MapPatch("/api/br/rounds/{roundId}/evidence/{entityId}", async (
            Guid                roundId,
            Guid                entityId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db) =>
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

            return Results.Ok(new { success = true, reviewed });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/br/rounds/{roundId}/results ────────────────────────────
        // Bulk submit/update results for a round (idempotent upsert).
        app.MapPut("/api/br/rounds/{roundId}/results", async (
            Guid                roundId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Resolve stageId AND detect solo from round → group → stage → tournament
            var roundInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.stage_id, t.team_size
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                JOIN tournament_stages ts ON ts.id = g.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE r.id = @roundId
                """,
                new { roundId });
            if (roundInfo is null)
                return Results.NotFound(new { error = "Round not found." });

            Guid stageId = roundInfo.stage_id;
            bool isSolo = Convert.ToInt32(roundInfo.team_size ?? 1) == 1;

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermScoresUpdate);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            if (!body.TryGetProperty("results", out var resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(new { error = "results must be an array." });

            // Parse and validate ALL results before opening transaction
            var parsedResults = new List<(Guid entityId, int placement, int kills, int placementPoints, int killPoints)>();
            foreach (var r in resultsElement.EnumerateArray())
            {
                var teamIdStr = r.TryGetProperty("teamId", out var tid) ? tid.GetString() : null;
                if (teamIdStr is null || !Guid.TryParse(teamIdStr, out var entityId))
                    return Results.BadRequest(new { error = $"Invalid teamId: {teamIdStr}" });

                var placement       = r.TryGetProperty("placement", out var pl) && pl.TryGetInt32(out var plv) ? plv : 0;
                var kills           = r.TryGetProperty("kills", out var kl) && kl.TryGetInt32(out var klv) ? klv : 0;
                var placementPoints = r.TryGetProperty("placementPoints", out var pp) && pp.TryGetInt32(out var ppv) ? ppv : 0;
                var killPoints      = r.TryGetProperty("killPoints", out var kp) && kp.TryGetInt32(out var kpv) ? kpv : 0;

                if (placement < 1)
                    return Results.BadRequest(new { error = $"placement must be >= 1 for team {teamIdStr}" });
                if (kills < 0)
                    return Results.BadRequest(new { error = $"kills must be >= 0 for team {teamIdStr}" });

                parsedResults.Add((entityId, placement, kills, placementPoints, killPoints));
            }

            using var tx = conn.BeginTransaction();
            try
            {
                if (isSolo)
                {
                    // Solo: teamId in request body is actually participant_id
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_round_results (round_id, participant_id, placement, kills, placement_points, kill_points)
                        VALUES (@roundId, @participantId, @placement, @kills, @placementPoints, @killPoints)
                        ON CONFLICT (round_id, participant_id) WHERE participant_id IS NOT NULL DO UPDATE
                        SET placement        = EXCLUDED.placement,
                            kills            = EXCLUDED.kills,
                            placement_points = EXCLUDED.placement_points,
                            kill_points      = EXCLUDED.kill_points
                        """,
                        parsedResults.Select(r => new
                        {
                            roundId,
                            participantId = r.entityId,
                            placement = r.placement,
                            kills = r.kills,
                            placementPoints = r.placementPoints,
                            killPoints = r.killPoints
                        }),
                        tx);
                }
                else
                {
                    // Team-based: teamId is a real team UUID
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO br_round_results (round_id, team_id, placement, kills, placement_points, kill_points)
                        VALUES (@roundId, @teamId, @placement, @kills, @placementPoints, @killPoints)
                        ON CONFLICT (round_id, team_id) WHERE team_id IS NOT NULL DO UPDATE
                        SET placement        = EXCLUDED.placement,
                            kills            = EXCLUDED.kills,
                            placement_points = EXCLUDED.placement_points,
                            kill_points      = EXCLUDED.kill_points
                        """,
                        parsedResults.Select(r => new
                        {
                            roundId,
                            teamId = r.entityId,
                            placement = r.placement,
                            kills = r.kills,
                            placementPoints = r.placementPoints,
                            killPoints = r.killPoints
                        }),
                        tx);
                }

                tx.Commit();
                return Results.Ok(new { saved = parsedResults.Count });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/stages/{stageId}/br/groups/{groupId}/leaderboard ───────
        // Aggregate leaderboard from all round results in a group. Public endpoint.
        app.MapGet("/api/stages/{stageId}/br/groups/{groupId}/leaderboard", async (
            Guid              stageId,
            Guid              groupId,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();

            // Verify groupId belongs to this stageId
            var groupExists = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM br_groups WHERE id = @groupId AND stage_id = @stageId)",
                new { groupId, stageId });
            if (!groupExists)
                return Results.NotFound(new { error = "Group not found in this stage." });

            // Unified leaderboard with COALESCE for team/solo
            var leaderboard = await conn.QueryAsync<dynamic>(
                """
                SELECT
                    COALESCE(rr.team_id, rr.participant_id) AS team_id,
                    CASE WHEN rr.team_id IS NOT NULL THEN t.name ELSE p.username END AS team_name,
                    CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
                    COUNT(DISTINCT rr.round_id) AS games_played,
                    SUM(rr.placement_points) AS total_placement_points,
                    SUM(rr.kill_points) AS total_kill_points,
                    SUM(rr.total_points) AS total_points,
                    SUM(rr.kills) AS total_kills,
                    COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
                    MIN(rr.placement) AS best_placement
                FROM br_round_results rr
                JOIN br_rounds r ON r.id = rr.round_id
                LEFT JOIN teams t ON t.id = rr.team_id
                LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                LEFT JOIN profiles p ON p.id = tp.user_id
                WHERE r.group_id = @groupId
                GROUP BY COALESCE(rr.team_id, rr.participant_id),
                         CASE WHEN rr.team_id IS NOT NULL THEN t.name ELSE p.username END,
                         CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END
                ORDER BY total_points DESC, wins DESC, total_kills DESC
                """,
                new { groupId });

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
                JOIN br_group_teams bgt ON bgt.group_id = g.id
                JOIN tournament_participants tp ON (
                    (tp.team_id IS NOT NULL AND tp.team_id = bgt.team_id)
                    OR (bgt.participant_id IS NOT NULL AND bgt.participant_id = tp.id)
                )
                WHERE ts.tournament_id = @tournamentId
                  AND tp.user_id = @userId
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
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
                       t.team_size
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
            var qualifiedRows = (await conn.QueryAsync<dynamic>(
                """
                SELECT * FROM (
                    SELECT
                        COALESCE(rr.team_id, rr.participant_id) AS entity_id,
                        rr.team_id,
                        rr.participant_id,
                        CASE WHEN rr.team_id IS NOT NULL THEN t.name ELSE p.username END AS team_name,
                        CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END AS logo_url,
                        g.name AS group_name,
                        SUM(rr.total_points) AS total_points,
                        SUM(rr.kills) AS total_kills,
                        COUNT(*) FILTER (WHERE rr.placement = 1) AS wins,
                        ROW_NUMBER() OVER (
                            PARTITION BY r.group_id
                            ORDER BY SUM(rr.total_points) DESC,
                                     COUNT(*) FILTER (WHERE rr.placement = 1) DESC,
                                     SUM(rr.kills) DESC
                        ) AS rank_in_group
                    FROM br_round_results rr
                    JOIN br_rounds r ON r.id = rr.round_id
                    JOIN br_groups g ON g.id = r.group_id
                    LEFT JOIN teams t ON t.id = rr.team_id
                    LEFT JOIN tournament_participants tp ON tp.id = rr.participant_id
                    LEFT JOIN profiles p ON p.id = tp.user_id
                    WHERE g.stage_id = @stageId
                    GROUP BY COALESCE(rr.team_id, rr.participant_id),
                             rr.team_id, rr.participant_id,
                             CASE WHEN rr.team_id IS NOT NULL THEN t.name ELSE p.username END,
                             CASE WHEN rr.team_id IS NOT NULL THEN t.logo_url ELSE p.avatar_url END,
                             g.name, r.group_id
                ) ranked
                WHERE rank_in_group <= @teamsPerGroup
                ORDER BY group_name, rank_in_group
                """,
                new { stageId, teamsPerGroup })).ToList();

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
}
