using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain 6: Match System
/// GET  /api/matches/{id}/reports            — fetch all result reports
/// POST /api/matches/{id}/reports            — submit result report
/// POST /api/matches/{id}/reports/{rid}/accept  — accept report
/// POST /api/matches/{id}/reports/{rid}/dispute — dispute report
/// GET  /api/matches/{id}/checkins           — fetch check-in status
/// POST /api/matches/{id}/checkin            — submit check-in
/// </summary>
public static class MatchSystemEndpoints
{
    public static void MapMatchSystemEndpoints(this WebApplication app)
    {
        // ── GET /api/matches/{id}/reports ─────────────────────────────────────
        app.MapGet("/api/matches/{id}/reports", async (
            string               id,
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
            string                        id,
            [FromBody] SubmitReportRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory          db,
            IHubContext<MatchHub>         matchHub,
            CancellationToken             ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var report = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO match_result_reports
                  (match_id, game_number, reported_by, reported_by_team_id,
                   riot_match_id, map_id, map_name,
                   team1_score, team2_score, winner_team_id, match_data, status)
                VALUES
                  (@matchId, @gameNumber, @reportedBy, @reportedByTeamId,
                   @riotMatchId, @mapId, @mapName,
                   @team1Score, @team2Score, @winnerTeamId, @matchData::jsonb, 'pending')
                RETURNING *
                """,
                new
                {
                    matchId           = id,
                    gameNumber        = req.GameNumber,
                    reportedBy        = userCtx.UserId,
                    reportedByTeamId  = req.ReportedByTeamId,
                    riotMatchId       = req.RiotMatchId,
                    mapId             = req.MapId,
                    mapName           = req.MapName,
                    team1Score        = req.Team1Score,
                    team2Score        = req.Team2Score,
                    winnerTeamId      = req.WinnerTeamId,
                    matchData         = req.MatchData is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.MatchData)
                        : "{}",
                });

            // Notify opposing captain via notification + SignalR
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT team1_id, team2_id FROM brkt_matches WHERE id = @id", new { id });

            if (match is not null)
            {
                string? opposingTeamId = (string?)match.team1_id == req.ReportedByTeamId
                    ? (string?)match.team2_id
                    : (string?)match.team1_id;

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
                               '/tournaments/captain', @data::jsonb, FALSE)
                            """,
                            new
                            {
                                userId = captainId,
                                data   = $"{{\"match_id\":\"{id}\"}}",
                            });

                        // Push via NotificationHub
                        await matchHub.Clients
                            .Group(MatchHub.MatchGroup(id))
                            .SendAsync(MatchHubEvents.ReportSubmitted,
                                new { matchId = id, reportId = (string?)report.id }, ct);
                    }
                }
            }

            return Results.Ok(report);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/reports/{rid}/accept ───────────────────────
        app.MapPost("/api/matches/{id}/reports/{rid}/accept", async (
            string                         id,
            string                         rid,
            [FromBody] AcceptReportRequest  req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            IHubContext<MatchHub>          matchHub,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Mark report accepted
            await conn.ExecuteAsync(
                """
                UPDATE match_result_reports
                SET status = 'accepted', responded_by = @userId, responded_at = NOW()
                WHERE id = @rid AND match_id = @matchId AND status = 'pending'
                """,
                new { rid, matchId = id, userId = userCtx.UserId });

            // Delegate result processing to the .NET process endpoint (Riot API + score sync)
            // This is a fire-and-forget; the caller gets the acknowledgement immediately
            await matchHub.Clients
                .Group(MatchHub.MatchGroup(id))
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
            string                           id,
            string                           rid,
            [FromBody] DisputeReportRequest   req,
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            IHubContext<MatchHub>            matchHub,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Call existing RPC that atomically: marks report disputed, writes match_disputes,
            // tournament_disputes, and sends notifications
            await conn.ExecuteAsync(
                """
                SELECT public.notify_admins_of_dispute(
                    @matchId::uuid, @reportId::uuid,
                    @disputedByTeamId::uuid, @reason, @evidenceUrls::jsonb
                )
                """,
                new
                {
                    matchId          = id,
                    reportId         = rid,
                    disputedByTeamId = req.TeamId,
                    reason           = req.Reason,
                    evidenceUrls     = System.Text.Json.JsonSerializer.Serialize(
                        req.EvidenceUrls ?? []),
                });

            // Broadcast dispute event
            await matchHub.Clients
                .Group(MatchHub.MatchGroup(id))
                .SendAsync(MatchHubEvents.ReportDisputed,
                    new { matchId = id, reportId = rid, reason = req.Reason }, ct);

            return Results.Ok(new { success = true, matchId = id, reportId = rid });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/matches/{id}/checkins ────────────────────────────────────
        app.MapGet("/api/matches/{id}/checkins", async (
            string               id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT * FROM match_checkins WHERE match_id = @id", new { id });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/checkin ────────────────────────────────────
        app.MapPost("/api/matches/{id}/checkin", async (
            string                      id,
            [FromBody] CheckinRequest   req,
            HttpContext                 ctx,
            IDbConnectionFactory       db,
            IHubContext<MatchHub>      matchHub,
            CancellationToken          ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                """
                INSERT INTO match_checkins (match_id, team_id, user_id, checked_in_at)
                VALUES (@matchId, @teamId, @userId, NOW())
                ON CONFLICT (match_id, team_id) DO NOTHING
                """,
                new { matchId = id, teamId = req.TeamId, userId = userCtx.UserId });

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(id))
                .SendAsync(MatchHubEvents.CheckInUpdated,
                    new { matchId = id, teamId = req.TeamId }, ct);

            return Results.Ok(new { success = true, matchId = id, teamId = req.TeamId });
        }).RequireAuthorization("Authenticated");
    }
}

// ── Request records ───────────────────────────────────────────────────────────

public sealed record SubmitReportRequest(
    int     GameNumber,
    string  RiotMatchId,
    string  ReportedByTeamId,
    int     Team1Score,
    int     Team2Score,
    string  WinnerTeamId,
    string? MapId    = null,
    string? MapName  = null,
    object? MatchData = null);

public sealed record AcceptReportRequest(
    string RiotMatchId,
    int    GameNumber,
    string? MapId = null);

public sealed record DisputeReportRequest(
    string         Reason,
    string         TeamId,
    List<string>?  EvidenceUrls = null);

public sealed record CheckinRequest(string TeamId);
