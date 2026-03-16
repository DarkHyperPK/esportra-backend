using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain 6: Match System
/// Reports, checkins, scheduling, time proposals, disputes.
/// </summary>
public static class MatchSystemEndpoints
{
    public static void MapMatchSystemEndpoints(this WebApplication app)
    {
        MapSchedulingEndpoints(app);
        MapTimeProposalEndpoints(app);
        MapDisputeEndpoints(app);
        // ── GET /api/matches/{id}/reports ─────────────────────────────────────
        app.MapGet("/api/matches/{id}/reports", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var reports = await conn.QueryAsync<dynamic>(
                """
                SELECT * FROM match_result_reports
                WHERE match_id = @id
                ORDER BY created_at DESC
                """,
                new { id });
            return Results.Ok(reports);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/reports ────────────────────────────────────
        app.MapPost("/api/matches/{id}/reports", async (
            Guid                          id,
            [FromBody] SubmitReportRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory          db,
            IHubContext<MatchHub>         matchHub,
            CancellationToken             ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is a captain of the reporting team
            var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @teamId AND user_id = @userId AND role = 'captain' AND is_active = TRUE)",
                new { teamId = req.ReportedByTeamId, userId = userCtx.UserIdGuid });
            if (!isCaptain) return Results.Forbid();

            var report = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO match_result_reports
                  (match_id, game_number, reported_by, reported_by_team_id,
                   riot_match_id, map_id, map_name,
                   team1_score, team2_score, winner_team_id, match_data,
                   screenshot_urls, comment, status)
                VALUES
                  (@matchId, @gameNumber, @reportedBy, @reportedByTeamId,
                   @riotMatchId, @mapId, @mapName,
                   @team1Score, @team2Score, @winnerTeamId, @matchData::jsonb,
                   @screenshotUrls::jsonb, @comment, 'pending')
                RETURNING *
                """,
                new
                {
                    matchId           = id,
                    gameNumber        = req.GameNumber,
                    reportedBy        = userCtx.UserIdGuid,
                    reportedByTeamId  = req.ReportedByTeamId,
                    riotMatchId       = (string?)req.RiotMatchId,
                    mapId             = req.MapId,
                    mapName           = req.MapName,
                    team1Score        = req.Team1Score,
                    team2Score        = req.Team2Score,
                    winnerTeamId      = req.WinnerTeamId,
                    matchData         = req.MatchData is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.MatchData)
                        : "{}",
                    screenshotUrls    = req.ScreenshotUrls is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.ScreenshotUrls)
                        : "[]",
                    comment           = req.Comment,
                });

            // Notify opposing captain via notification + SignalR
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT team1_id, team2_id, version_id FROM brkt_matches WHERE id = @id", new { id });

            // Resolve tournament ID for notification link
            var tournamentId = match?.version_id is not null
                ? await conn.QuerySingleOrDefaultAsync<Guid?>(
                    "SELECT tournament_id FROM brkt_versions WHERE id = @vid",
                    new { vid = (Guid)match.version_id })
                : (Guid?)null;
            var matchLink = tournamentId is not null
                ? $"/tournaments/{tournamentId}/captain-match/{id}"
                : "/tournaments";

            if (match is not null)
            {
                string? opposingTeamId = match.team1_id?.ToString() == req.ReportedByTeamId
                    ? match.team2_id?.ToString()
                    : match.team1_id?.ToString();

                if (opposingTeamId is not null)
                {
                    var captain = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        """
                        SELECT user_id FROM team_members
                        WHERE team_id = @tid AND role = 'captain' AND is_active = TRUE
                        LIMIT 1
                        """,
                        new { tid = opposingTeamId });

                    if (captain?.user_id is string captainId)
                    {
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO notifications
                              (user_id, type, title, message, link, data, is_read)
                            VALUES
                              (@userId, 'result_reported', 'Match Result Reported',
                               'Your opponent has reported the match result. Please verify or dispute.',
                               @link, @data::jsonb, FALSE)
                            """,
                            new
                            {
                                userId = captainId,
                                link   = matchLink,
                                data   = System.Text.Json.JsonSerializer.Serialize(new { match_id = id }),
                            });

                        // Push via NotificationHub
                        await matchHub.Clients
                            .Group(MatchHub.MatchGroup(id.ToString()))
                            .SendAsync(MatchHubEvents.ReportSubmitted,
                                new { matchId = id, reportId = (string?)report.id }, ct);
                    }
                }
            }

            return Results.Ok(report);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/reports/{rid}/accept ───────────────────────
        app.MapPost("/api/matches/{id}/reports/{rid}/accept", async (
            Guid                           id,
            Guid                           rid,
            [FromBody] AcceptReportRequest  req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            IHubContext<MatchHub>          matchHub,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is a captain in this match (opposing team)
            var captainTeam = await conn.QuerySingleOrDefaultAsync<string?>(
                """
                SELECT tm.team_id FROM team_members tm
                JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                LIMIT 1
                """,
                new { matchId = id, userId = userCtx.UserIdGuid });
            if (captainTeam is null) return Results.Forbid();

            // Mark report accepted
            await conn.ExecuteAsync(
                """
                UPDATE match_result_reports
                SET status = 'accepted', responded_by = @userId, responded_at = NOW()
                WHERE id = @rid AND match_id = @matchId AND status = 'pending'
                """,
                new { rid, matchId = id, userId = userCtx.UserIdGuid });

            // Delegate result processing to the .NET process endpoint (Riot API + score sync)
            // This is a fire-and-forget; the caller gets the acknowledgement immediately
            await matchHub.Clients
                .Group(MatchHub.MatchGroup(id.ToString()))
                .SendAsync(MatchHubEvents.ReportAccepted,
                    new { matchId = id, reportId = rid, riotMatchId = req.RiotMatchId }, ct);

            return Results.Ok(new
            {
                success     = true,
                matchId     = id,
                reportId    = rid,
                riotMatchId = req.RiotMatchId,
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/reports/{rid}/dispute ──────────────────────
        app.MapPost("/api/matches/{id}/reports/{rid}/dispute", async (
            Guid                             id,
            Guid                             rid,
            [FromBody] DisputeReportRequest   req,
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            IHubContext<MatchHub>            matchHub,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Mark the report as disputed
            await conn.ExecuteAsync(
                "UPDATE match_result_reports SET status = 'disputed', updated_at = NOW() WHERE id = @rid AND match_id = @matchId",
                new { rid, matchId = id });

            // 2. Find tournament_id for this match
            var tournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT bv.tournament_id FROM brkt_matches bm
                JOIN brkt_versions bv ON bv.id = bm.version_id
                WHERE bm.id = @matchId
                """,
                new { matchId = id });

            // 3. Insert dispute record
            var dispute = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO tournament_disputes
                    (tournament_id, match_id, raised_by_user_id, team_id,
                     title, description, evidence_url, dispute_reason, status)
                VALUES
                    (@tournamentId, @matchId, @userId, @teamId,
                     @title, @reason, @evidenceUrl, @reason, 'open')
                RETURNING *
                """,
                new
                {
                    tournamentId,
                    matchId      = id,
                    userId       = userCtx.UserIdGuid,
                    teamId       = Guid.TryParse(req.TeamId, out var tg) ? tg : (Guid?)null,
                    title        = $"Match result disputed",
                    reason       = req.Reason,
                    evidenceUrl  = req.EvidenceUrls is { Count: > 0 } ? req.EvidenceUrls[0] : (string?)null,
                });

            // 4. Broadcast dispute event
            await matchHub.Clients
                .Group(MatchHub.MatchGroup(id.ToString()))
                .SendAsync(MatchHubEvents.ReportDisputed,
                    new { matchId = id, reportId = rid, reason = req.Reason }, ct);

            return Results.Ok(new { success = true, matchId = id, reportId = rid, disputeId = (Guid)dispute.id });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/matches/{id}/messages ───────────────────────────────────
        app.MapGet("/api/matches/{id}/messages", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, match_id, sender_id, sender_name, team_id,
                       content, message_type, metadata, created_at
                FROM match_messages
                WHERE match_id = @id
                ORDER BY created_at ASC
                """,
                new { id });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/messages/system ────────────────────────────
        // System messages: inserted server-side and broadcast via ChatHub.
        app.MapPost("/api/matches/{id}/messages/system", async (
            Guid                               id,
            [FromBody] SystemMessageRequest    req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            IHubContext<ChatHub>              chatHub,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;

            using var conn = db.CreateConnection();
            var msg = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO match_messages
                    (match_id, sender_id, sender_name, content, message_type, metadata)
                VALUES
                    (@matchId, @senderId, 'System', @content, 'system', @metadata::jsonb)
                RETURNING id, match_id, sender_id, sender_name, team_id,
                          content, message_type, metadata, created_at
                """,
                new
                {
                    matchId  = id,
                    senderId = userCtx?.UserId ?? "00000000-0000-0000-0000-000000000000",
                    content  = req.Content,
                    metadata = req.Metadata is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.Metadata)
                        : "{}",
                });

            // Broadcast to everyone in the chat room
            await chatHub.Clients
                .Group(ChatHub.ChatGroup(id.ToString()))
                .SendAsync("MessageReceived", new
                {
                    id         = (string?)msg.id,
                    matchId    = id,
                    userId     = (string?)msg.sender_id,
                    username   = "System",
                    content    = (string?)msg.content,
                    createdAt  = (DateTime?)msg.created_at,
                }, ct);

            return Results.Ok(msg);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/matches/{id}/checkins ────────────────────────────────────
        app.MapGet("/api/matches/{id}/checkins", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT match_id::text, team_id::text, user_id::text, checked_in_at FROM match_checkins WHERE match_id = @id", new { id });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/checkin ────────────────────────────────────
        app.MapPost("/api/matches/{id}/checkin", async (
            Guid                        id,
            [FromBody] CheckinRequest   req,
            HttpContext                 ctx,
            IDbConnectionFactory       db,
            IHubContext<MatchHub>      matchHub,
            CancellationToken          ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!Guid.TryParse(req.TeamId, out var teamIdGuid))
                return Results.BadRequest(new { error = "Invalid team ID." });

            using var conn = db.CreateConnection();

            // Verify caller belongs to the team
            var isMember = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE)",
                new { teamId = teamIdGuid, userId = userCtx.UserIdGuid });
            if (!isMember) return Results.Forbid();

            await conn.ExecuteAsync(
                """
                INSERT INTO match_checkins (match_id, team_id, user_id, checked_in_at)
                VALUES (@matchId, @teamId, @userId, NOW())
                ON CONFLICT (match_id, team_id) DO NOTHING
                """,
                new { matchId = id, teamId = teamIdGuid, userId = userCtx.UserIdGuid });

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(id.ToString()))
                .SendAsync(MatchHubEvents.CheckInUpdated,
                    new { matchId = id, teamId = teamIdGuid }, ct);

            return Results.Ok(new { success = true, matchId = id, teamId = teamIdGuid });
        }).RequireAuthorization("Authenticated");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Scheduling Endpoints
    // ══════════════════════════════════════════════════════════════════════════

    private static void MapSchedulingEndpoints(WebApplication app)
    {
        // ── GET /api/stages/{stageId}/scheduling-config ─────────────────────
        app.MapGet("/api/stages/{stageId}/scheduling-config", async (
            Guid                 stageId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var config = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT scheduling_config::text FROM tournament_stages WHERE id = @stageId",
                new { stageId });
            if (config is null) return Results.NotFound(new { error = "Stage not found." });
            return Results.Ok(JsonDocument.Parse(config).RootElement);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/stages/{stageId}/scheduling-config ─────────────────────
        app.MapPut("/api/stages/{stageId}/scheduling-config", async (
            Guid                                stageId,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is organizer of the tournament that owns this stage
            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tournament_stages ts
                    JOIN tournaments t ON t.id = ts.tournament_id
                    WHERE ts.id = @stageId AND t.organizer_id = @userId
                )
                """,
                new { stageId, userId = userCtx.UserIdGuid });
            if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            // Store raw JSON body as-is to preserve frontend key casing (snake_case)
            var json = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ct);
            await conn.ExecuteAsync(
                "UPDATE tournament_stages SET scheduling_config = @json::jsonb WHERE id = @stageId",
                new { json, stageId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── GET /api/stages/{stageId}/matches ───────────────────────────────
        app.MapGet("/api/stages/{stageId}/matches", async (
            Guid                 stageId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT m.id, m.match_number, m.scheduled_time, m.team1_id, m.team2_id,
                       m.status, m.round_index, m.bracket_type,
                       t1.name AS team1_name, t2.name AS team2_name
                FROM brkt_matches m
                JOIN brkt_versions v ON v.id = m.version_id
                LEFT JOIN teams t1 ON t1.id = m.team1_id
                LEFT JOIN teams t2 ON t2.id = m.team2_id
                WHERE v.stage_id = @stageId
                ORDER BY m.round_index, m.match_number
                """,
                new { stageId });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/stages/{stageId}/schedule-bulk ────────────────────────
        app.MapPost("/api/stages/{stageId}/schedule-bulk", async (
            Guid                               stageId,
            [FromBody] BulkScheduleRequest     req,
            HttpContext                         ctx,
            IDbConnectionFactory               db,
            CancellationToken                  ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tournament_stages ts
                    JOIN tournaments t ON t.id = ts.tournament_id
                    WHERE ts.id = @stageId AND t.organizer_id = @userId
                )
                """,
                new { stageId, userId = userCtx.UserIdGuid });
            if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            if (req.Updates.Count == 0)
                return Results.BadRequest(new { error = "No updates provided." });

            // UNNEST-based bulk update — single round-trip
            var ids   = req.Updates.Select(u => u.MatchId).ToArray();
            var times = req.Updates.Select(u => u.ScheduledTime).ToArray();

            var updated = await conn.ExecuteAsync(
                """
                UPDATE brkt_matches m
                SET scheduled_time = u.scheduled_time
                FROM UNNEST(@ids::uuid[], @times::timestamptz[]) AS u(id, scheduled_time)
                WHERE m.id = u.id
                """,
                new { ids, times });

            return Results.Ok(new { success = true, updated });
        }).RequireAuthorization("Organizer");

        // ── PUT /api/matches/{matchId}/scheduled-time ───────────────────────
        app.MapPut("/api/matches/{matchId}/scheduled-time", async (
            Guid                                  matchId,
            [FromBody] UpdateMatchTimeRequest      req,
            HttpContext                             ctx,
            IDbConnectionFactory                   db,
            CancellationToken                      ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is organizer of the tournament that owns this match
            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM brkt_matches bm
                    JOIN brkt_versions v ON v.id = bm.version_id
                    JOIN tournaments t ON t.id = v.tournament_id
                    WHERE bm.id = @matchId AND t.organizer_id = @userId
                )
                """,
                new { matchId, userId = userCtx.UserIdGuid });
            if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            await conn.ExecuteAsync(
                "UPDATE brkt_matches SET scheduled_time = @scheduledTime WHERE id = @matchId",
                new { matchId, scheduledTime = string.IsNullOrEmpty(req.ScheduledTime)
                    ? (DateTime?)null
                    : DateTime.Parse(req.ScheduledTime, null, System.Globalization.DateTimeStyles.RoundtripKind) });

            return Results.Ok(new { success = true, matchId });
        }).RequireAuthorization("Organizer");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Time Proposal Endpoints
    // ══════════════════════════════════════════════════════════════════════════

    private static void MapTimeProposalEndpoints(WebApplication app)
    {
        // ── GET /api/matches/{matchId}/time-proposals ───────────────────────
        app.MapGet("/api/matches/{matchId}/time-proposals", async (
            Guid                 matchId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id::text, match_id::text, proposed_by::text, proposed_time, status,
                       created_at, responded_at
                FROM match_time_proposals
                WHERE match_id = @matchId
                ORDER BY created_at DESC
                """,
                new { matchId });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/time-proposals ──────────────────────
        app.MapPost("/api/matches/{matchId}/time-proposals", async (
            Guid                               matchId,
            [FromBody] ProposeTimeRequest       req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            ILogger<Program>                    logger,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            try
            {
                // Verify caller is a captain of one of the teams in this match
                var captainTeam = await conn.QuerySingleOrDefaultAsync<string?>(
                    """
                    SELECT tm.team_id::text FROM team_members tm
                    JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                    WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                    LIMIT 1
                    """,
                    new { matchId, userId = userCtx.UserIdGuid });
                if (captainTeam is null) return Results.Forbid();

                var proposal = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO match_time_proposals (match_id, proposed_by, proposed_time, status)
                    VALUES (@matchId, @proposedBy, @proposedTime::timestamptz, 'pending')
                    RETURNING id::text, match_id::text, proposed_by::text, proposed_time, status, created_at, responded_at
                    """,
                    new { matchId, proposedBy = userCtx.UserIdGuid, proposedTime = req.ProposedTime });

                return Results.Ok(proposal);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create time proposal for match {MatchId}", matchId);
                return Results.Json(new { error = "Failed to create time proposal.", detail = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/time-proposals/{proposalId}/accept ──
        app.MapPost("/api/matches/{matchId}/time-proposals/{proposalId}/accept", async (
            Guid                 matchId,
            Guid                 proposalId,
            HttpContext           ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var captainTeam = await conn.QuerySingleOrDefaultAsync<string?>(
                """
                SELECT tm.team_id::text FROM team_members tm
                JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                LIMIT 1
                """,
                new { matchId, userId = userCtx.UserIdGuid });
            if (captainTeam is null) return Results.Forbid();

            // Atomic: accept proposal + update match scheduled_time in a CTE
            var rows = await conn.ExecuteAsync(
                """
                WITH accepted AS (
                    UPDATE match_time_proposals
                    SET status = 'accepted', responded_at = NOW()
                    WHERE id = @proposalId AND match_id = @matchId AND status = 'pending'
                    RETURNING proposed_time
                )
                UPDATE brkt_matches
                SET scheduled_time = (SELECT proposed_time FROM accepted)
                WHERE id = @matchId AND EXISTS (SELECT 1 FROM accepted)
                """,
                new { proposalId, matchId });

            if (rows == 0) return Results.BadRequest(new { error = "Proposal not found or already handled." });
            return Results.Ok(new { success = true, matchId, proposalId });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/time-proposals/{proposalId}/reject ──
        app.MapPost("/api/matches/{matchId}/time-proposals/{proposalId}/reject", async (
            Guid                 matchId,
            Guid                 proposalId,
            HttpContext           ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var captainTeam = await conn.QuerySingleOrDefaultAsync<string?>(
                """
                SELECT tm.team_id::text FROM team_members tm
                JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                LIMIT 1
                """,
                new { matchId, userId = userCtx.UserIdGuid });
            if (captainTeam is null) return Results.Forbid();

            await conn.ExecuteAsync(
                """
                UPDATE match_time_proposals
                SET status = 'rejected', responded_at = NOW()
                WHERE id = @proposalId AND match_id = @matchId AND status = 'pending'
                """,
                new { proposalId, matchId });

            return Results.Ok(new { success = true, matchId, proposalId });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/time-proposals/{proposalId}/counter ─
        app.MapPost("/api/matches/{matchId}/time-proposals/{proposalId}/counter", async (
            Guid                                 matchId,
            Guid                                 proposalId,
            [FromBody] ProposeTimeRequest         req,
            HttpContext                            ctx,
            IDbConnectionFactory                  db,
            CancellationToken                     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var captainTeam = await conn.QuerySingleOrDefaultAsync<string?>(
                """
                SELECT tm.team_id::text FROM team_members tm
                JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                LIMIT 1
                """,
                new { matchId, userId = userCtx.UserIdGuid });
            if (captainTeam is null) return Results.Forbid();

            // Atomic: mark old as countered + insert new in a CTE
            var newProposal = await conn.QuerySingleAsync<dynamic>(
                """
                WITH countered AS (
                    UPDATE match_time_proposals
                    SET status = 'countered', responded_at = NOW()
                    WHERE id = @proposalId AND match_id = @matchId AND status = 'pending'
                )
                INSERT INTO match_time_proposals (match_id, proposed_by, proposed_time, status)
                VALUES (@matchId, @proposedBy, @proposedTime::timestamptz, 'pending')
                RETURNING id::text, match_id::text, proposed_by::text, proposed_time, status, created_at, responded_at
                """,
                new { proposalId, matchId, proposedBy = userCtx.UserIdGuid, proposedTime = req.ProposedTime });

            return Results.Ok(newProposal);
        }).RequireAuthorization("Authenticated");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Dispute Endpoints (match_disputes — separate from result report disputes)
    // ══════════════════════════════════════════════════════════════════════════

    private static void MapDisputeEndpoints(WebApplication app)
    {
        // ── GET /api/matches/{matchId}/dispute ──────────────────────────────
        app.MapGet("/api/matches/{matchId}/dispute", async (
            Guid                 matchId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var dispute = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, match_id, disputed_by_team_id, disputed_by_user_id,
                       reason, evidence_urls, status, resolution,
                       resolved_at, resolved_by, created_at
                FROM match_disputes
                WHERE match_id = @matchId
                ORDER BY created_at DESC
                LIMIT 1
                """,
                new { matchId });
            return Results.Ok(dispute);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/disputes ────────────────────────────
        app.MapPost("/api/matches/{matchId}/disputes", async (
            Guid                            matchId,
            [FromBody] FileDisputeRequest    req,
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is a member of the disputing team
            var isMember = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE)",
                new { teamId = req.TeamId, userId = userCtx.UserIdGuid });
            if (!isMember) return Results.Forbid();

            var evidenceUrls = (req.EvidenceUrls ?? []).ToArray();

            var dispute = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO match_disputes
                    (match_id, disputed_by_team_id, disputed_by_user_id, reason, evidence_urls, status)
                VALUES
                    (@matchId, @teamId, @userId, @reason, @evidenceUrls, 'pending')
                RETURNING *
                """,
                new
                {
                    matchId,
                    teamId       = req.TeamId,
                    userId       = userCtx.UserIdGuid,
                    reason       = req.Reason,
                    evidenceUrls,
                });

            return Results.Ok(dispute);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/matches/{matchId}/disputes/{disputeId}/resolve ─────────
        app.MapPut("/api/matches/{matchId}/disputes/{disputeId}/resolve", async (
            Guid                                matchId,
            Guid                                disputeId,
            [FromBody] ResolveDisputeRequest     req,
            HttpContext                           ctx,
            IDbConnectionFactory                 db,
            CancellationToken                    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is tournament organizer or admin
            var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM match_disputes md
                    JOIN brkt_matches bm ON bm.id = md.match_id
                    JOIN brkt_versions v ON v.id = bm.version_id
                    JOIN tournaments t ON t.id = v.tournament_id
                    WHERE md.id = @disputeId AND t.organizer_id = @userId
                )
                """,
                new { disputeId, userId = userCtx.UserIdGuid });
            if (!isOrganizer && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            // Update dispute
            await conn.ExecuteAsync(
                """
                UPDATE match_disputes
                SET status = @status::text, resolution = @resolution,
                    resolved_at = NOW(), resolved_by = @resolvedBy
                WHERE id = @disputeId AND match_id = @matchId
                """,
                new
                {
                    disputeId,
                    matchId,
                    status     = req.Status,
                    resolution = req.Resolution,
                    resolvedBy = userCtx.UserIdGuid,
                });

            // Notify both parties
            var dispute = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT disputed_by_user_id, match_id FROM match_disputes WHERE id = @disputeId",
                new { disputeId });

            if (dispute is not null)
            {
                var notifType    = req.Status == "resolved" ? "dispute_resolved" : "dispute_rejected";
                var notifTitle   = req.Status == "resolved" ? "Dispute Resolved" : "Dispute Rejected";
                var notifMessage = req.Status == "resolved"
                    ? $"Your match dispute has been resolved. Organizer note: {req.Resolution}"
                    : $"Your match dispute was rejected. Organizer note: {req.Resolution}";
                var notifData    = JsonSerializer.Serialize(new { match_id = matchId });

                // Resolve tournament ID for notification link
                var disputeTournamentId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    "SELECT v.tournament_id FROM brkt_matches m JOIN brkt_versions v ON v.id = m.version_id WHERE m.id = @matchId",
                    new { matchId });
                var disputeLink = disputeTournamentId is not null
                    ? $"/tournaments/{disputeTournamentId}/captain-match/{matchId}"
                    : "/tournaments";

                // Notify the disputing user
                await conn.ExecuteAsync(
                    """
                    INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                    VALUES (@userId, @type, @title, @message, @link, @data::jsonb, FALSE)
                    """,
                    new { userId = (Guid)dispute.disputed_by_user_id, type = notifType,
                          title = notifTitle, message = notifMessage, link = disputeLink, data = notifData });

                // Notify the original reporter (opposing party)
                var reporter = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    SELECT reported_by FROM match_result_reports
                    WHERE match_id = @matchId
                    ORDER BY created_at DESC LIMIT 1
                    """,
                    new { matchId });

                if (reporter is not null && reporter != (Guid)dispute.disputed_by_user_id)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                        VALUES (@userId, @type, @title, @message, @link, @data::jsonb, FALSE)
                        """,
                        new { userId = reporter, type = notifType, title = notifTitle,
                              message = notifMessage, link = disputeLink, data = notifData });
                }
            }

            return Results.Ok(new { success = true, disputeId, status = req.Status });
        }).RequireAuthorization("Organizer");
    }
}

// ── Request records ───────────────────────────────────────────────────────────

public sealed record SubmitReportRequest(
    int     GameNumber,
    string  ReportedByTeamId,
    int     Team1Score,
    int     Team2Score,
    string? WinnerTeamId  = null,
    string? RiotMatchId   = null,
    string? MapId         = null,
    string? MapName       = null,
    object? MatchData     = null,
    string[]? ScreenshotUrls = null,
    string? Comment       = null);

public sealed record AcceptReportRequest(
    string RiotMatchId,
    int    GameNumber,
    string? MapId = null);

public sealed record DisputeReportRequest(
    string         Reason,
    string         TeamId,
    List<string>?  EvidenceUrls = null);

public sealed record CheckinRequest(string TeamId);

public sealed record SystemMessageRequest(string Content, object? Metadata = null);

// ── Scheduling records ───────────────────────────────────────────────────────

public sealed record SchedulingConfigRequest(
    bool    SelfPlayEnabled,
    bool    CheckinEnabled,
    int     CheckinWindowMinutes,
    string? RoundDeadline,
    Dictionary<string, string>? RoundDeadlines,
    string? ScheduleStartTime,
    int     MatchIntervalMinutes,
    string? DailyStartTime,
    string? SchedulingMode);

public sealed record BulkScheduleRequest(
    List<BulkScheduleItem> Updates);

public sealed record BulkScheduleItem(
    string  MatchId,
    string? ScheduledTime);

public sealed record UpdateMatchTimeRequest(string? ScheduledTime);

// ── Time proposal records ────────────────────────────────────────────────────

public sealed record ProposeTimeRequest(string ProposedTime);

// ── Dispute records ──────────────────────────────────────────────────────────

public sealed record FileDisputeRequest(
    string        TeamId,
    string        Reason,
    List<string>? EvidenceUrls = null);

public sealed record ResolveDisputeRequest(
    string Status,
    string Resolution);
