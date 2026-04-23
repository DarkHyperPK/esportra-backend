using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

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
            bool isSolo = ((int?)tournamentInfo.team_size ?? 1) <= 1;

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
                          AND status IN ('accepted','approved')
                          AND team_id IS NOT NULL
                        """,
                        new { tournamentId })).ToArray();

                    if (teamIds.Length == 0)
                        return Results.BadRequest(new { error = "No registered teams found. Ensure participants have status 'accepted' or 'approved'." });

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
            bool isSolo = ((int?)tournamentInfo.team_size ?? 1) <= 1;

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
                          AND status IN ('accepted','approved')
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
                       (SELECT COUNT(*) FROM br_round_results rr WHERE rr.round_id = r.id) AS result_count
                FROM br_rounds r
                WHERE r.group_id = @groupId
                ORDER BY r.round_number
                """,
                new { groupId });

            // Strip lobby_code unless user is authenticated staff/admin
            var userCtx = ctx.Items["UserContext"] as UserContext;
            var canSeeCode = false;
            if (userCtx is not null)
            {
                canSeeCode = await StaffAuthHelper.CanActOnStageAsync(
                        conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit)
                    || StaffAuthHelper.IsPlatformAdmin(userCtx);
            }

            if (!canSeeCode)
            {
                var sanitized = rounds.Select(r =>
                {
                    var dict = (IDictionary<string, object>)r;
                    dict["lobby_code"] = null!;
                    return dict;
                });
                return Results.Ok(sanitized);
            }

            return Results.Ok(rounds);
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

            var lobbyCode   = body.TryGetProperty("lobbyCode", out var lc) ? lc.GetString() : null;
            var scheduledAt = body.TryGetProperty("scheduledAt", out var sa) ? sa.GetString() : null;

            DateTimeOffset? parsedSchedule = null;
            if (scheduledAt is not null)
            {
                if (!DateTimeOffset.TryParse(scheduledAt, out var dt))
                    return Results.BadRequest(new { error = "Invalid scheduledAt format." });
                parsedSchedule = dt;
            }

            var round = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO br_rounds (group_id, round_number, lobby_code, scheduled_at)
                VALUES (
                    @groupId,
                    (SELECT COALESCE(MAX(round_number), 0) + 1 FROM br_rounds WHERE group_id = @groupId),
                    @lobbyCode,
                    @scheduledAt
                )
                RETURNING id, round_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at
                """,
                new { groupId, lobbyCode, scheduledAt = parsedSchedule });

            return Results.Ok(round);
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/br/rounds/{roundId} ──────────────────────────────────
        // Update a round (lobby code, status, schedule).
        app.MapPatch("/api/br/rounds/{roundId}", async (
            Guid                roundId,
            [FromBody] JsonElement body,
            HttpContext          ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Resolve stageId from round
            var stageId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT g.stage_id
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                WHERE r.id = @roundId
                """,
                new { roundId });
            if (stageId is null)
                return Results.NotFound(new { error = "Round not found." });

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId.Value, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            // Build dynamic SET clauses for provided fields
            var setClauses = new List<string>();
            var parameters = new DynamicParameters();
            parameters.Add("roundId", roundId);

            if (body.TryGetProperty("lobbyCode", out var lcProp))
            {
                setClauses.Add("lobby_code = @lobbyCode");
                parameters.Add("lobbyCode", lcProp.ValueKind == JsonValueKind.Null ? null : lcProp.GetString());
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

            if (body.TryGetProperty("status", out var stProp))
            {
                var newStatus = stProp.GetString();
                if (newStatus is not ("pending" or "active" or "completed"))
                    return Results.BadRequest(new { error = "status must be 'pending', 'active', or 'completed'." });

                // Enforce valid status transitions
                var currentStatus = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT status FROM br_rounds WHERE id = @roundId",
                    new { roundId });

                var validTransition = (currentStatus, newStatus) switch
                {
                    ("pending", "active")       => true,
                    ("active", "completed")      => true,
                    ("completed", "active")      => true, // allow re-opening
                    _ => false
                };
                if (!validTransition)
                    return Results.BadRequest(new { error = $"Cannot transition from '{currentStatus}' to '{newStatus}'." });

                setClauses.Add("status = @status");
                parameters.Add("status", newStatus);

                if (newStatus == "active" && currentStatus == "pending")
                    setClauses.Add("started_at = NOW()");
                else if (newStatus == "active" && currentStatus == "completed")
                    setClauses.Add("completed_at = NULL"); // clear when re-opening
                else if (newStatus == "completed")
                    setClauses.Add("completed_at = NOW()");
            }

            if (setClauses.Count == 0)
                return Results.BadRequest(new { error = "No fields to update." });

            var sql = $"""
                UPDATE br_rounds
                SET {string.Join(", ", setClauses)}
                WHERE id = @roundId
                RETURNING id, round_number, lobby_code, status, scheduled_at, started_at, completed_at, created_at
                """;

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, parameters);
            return updated is not null ? Results.Ok(updated) : Results.NotFound();
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
            bool isSolo = ((int?)roundInfo.team_size ?? 1) <= 1;

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

        // ── POST /api/stages/{stageId}/br/advance ───────────────────────────
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

            bool isSolo = ((int?)stage.team_size ?? 1) <= 1;

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
