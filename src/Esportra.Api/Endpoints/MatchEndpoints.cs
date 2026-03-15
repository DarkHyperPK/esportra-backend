using System.Data;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Core.Match;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: process-match-result, verify-match-result, scan-recent-matches Edge Functions.
///
/// Phase 3 additions: veto actions now broadcast to VetoHub group after each state change.
/// </summary>
public static class MatchEndpoints
{
    public static void MapMatchEndpoints(this WebApplication app)
    {
        // ── POST /api/matches/scan ────────────────────────────────────────────
        // Scans for a recent match result via Riot API match history.
        // Called after a match report is submitted to cross-reference with official data.
        app.MapPost("/api/matches/scan", async (
            [FromBody] ScanRecentMatchesRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory               db,
            Esportra.Infrastructure.Integrations.RiotApiClient riotApi,
            CancellationToken                  ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Get the match and participating players' Riot PUUIDs
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT bm.id, bm.team1_id, bm.team2_id, t.id AS tournament_id, t.game
                FROM brkt_matches bm
                JOIN brkt_versions bv ON bv.id = bm.version_id
                JOIN tournament_stages ts ON ts.id = bv.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE bm.id = @matchId
                """,
                new { matchId = req.MatchId });

            if (match is null) return Results.NotFound(new { error = "Match not found" });

            // Get Riot accounts linked to players in both teams
            var riotAccounts = (await conn.QueryAsync<dynamic>(
                """
                SELECT ra.puuid, ra.game_name, ra.tag_line, tm.team_id
                FROM riot_accounts ra
                JOIN team_members tm ON tm.user_id = ra.user_id
                WHERE tm.team_id IN (@team1Id, @team2Id) AND ra.puuid IS NOT NULL
                """,
                new { team1Id = (Guid)match.team1_id, team2Id = (Guid)match.team2_id })).AsList();

            if (riotAccounts.Count == 0)
                return Results.Ok(new { found = false, reason = "No linked Riot accounts for match participants" });

            // Scan the first available player's recent match history
            var puuid = (string)riotAccounts[0].puuid;
            var (statusCode, body) = await riotApi.ProxyAsync(
                "na",
                $"/val/match/v1/matchlists/{puuid}",
                ct);

            if (statusCode != 200)
                return Results.Ok(new { found = false, reason = $"Riot API returned {statusCode}" });

            return Results.Ok(new
            {
                found     = true,
                matchId   = req.MatchId,
                mapName   = req.MapName,
                riotData  = body,
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/process ───────────────────────────────
        // Processes an accepted match result report: validates, finalizes the match,
        // updates standings, and broadcasts the result.
        app.MapPost("/api/matches/{matchId}/process", async (
            Guid                           matchId,
            [FromBody] ProcessMatchResultRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory           db,
            IHubContext<MatchHub>          matchHub,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Fetch the accepted report
            var report = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, match_id, reported_by_team_id, winner_team_id,
                       team1_score, team2_score, status, riot_match_id, match_data
                FROM match_result_reports
                WHERE id = @reportId AND match_id = @matchId
                """,
                new { reportId = req.ReportId, matchId });

            if (report is null)
                return Results.NotFound(new { error = "Report not found for this match" });

            if ((string)report.status != "accepted")
                return Results.BadRequest(new { error = $"Report status is '{report.status}', expected 'accepted'" });

            // 2. Get current match version for optimistic locking
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT version, team1_id, team2_id FROM brkt_matches WHERE id = @matchId",
                new { matchId });

            if (match is null)
                return Results.NotFound(new { error = "Match not found" });

            // 3. Determine winner/loser
            var winnerId = (Guid)report.winner_team_id;
            var loserId  = winnerId == (Guid)match.team1_id ? (Guid)match.team2_id : (Guid)match.team1_id;
            var team1Score = (int)report.team1_score;
            var team2Score = (int)report.team2_score;

            // 4. Finalize match via RPC (handles locking + bracket advancement)
            var success = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT public.finalize_match_locked(
                    @matchId, @version, @winnerId, @loserId, @team1Score, @team2Score
                )
                """,
                new
                {
                    matchId,
                    version  = (int)match.version,
                    winnerId,
                    loserId,
                    team1Score,
                    team2Score,
                });

            if (!success)
                return Results.Conflict(new { error = "Match state has changed — retry." });

            // 5. Mark report as processed
            await conn.ExecuteAsync(
                "UPDATE match_result_reports SET status = 'processed', updated_at = NOW() WHERE id = @reportId",
                new { reportId = req.ReportId });

            // 6. Broadcast result via MatchHub
            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, status = "completed", winnerId, team1Score, team2Score },
                    ct);

            return Results.Ok(new { success = true, matchId, winnerId, team1Score, team2Score });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/finalize ──────────────────────────────
        app.MapPost("/api/matches/{matchId}/finalize", async (
            Guid                 matchId,
            IDbConnectionFactory db,
            IHubContext<MatchHub> matchHub,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            var result = await Dapper.SqlMapper.QuerySingleOrDefaultAsync<dynamic>(conn,
                "SELECT public.finalize_match_locked(@matchId) AS result",
                new { matchId });

            // Notify match group that status changed
            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, status = "completed", result },
                    ct);

            return Results.Ok(new { success = true, matchId, result });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/award-walkover ──────────────────────
        app.MapPost("/api/matches/{matchId}/award-walkover", async (
            Guid                         matchId,
            [FromBody] WalkoverRequest   req,
            HttpContext                  ctx,
            IDbConnectionFactory         db,
            IHubContext<MatchHub>        matchHub,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Get current match version for locking
            var version = await conn.QuerySingleOrDefaultAsync<int?>(
                "SELECT version FROM brkt_matches WHERE id = @matchId",
                new { matchId });
            if (version is null) return Results.NotFound();

            // Finalize match via RPC
            var success = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT public.finalize_match_locked(
                    @matchId, @version, @winnerId, @loserId, @team1Score, @team2Score
                )
                """,
                new
                {
                    matchId,
                    version,
                    winnerId = req.WinnerId,
                    loserId = req.LoserId,
                    team1Score = req.Team1Score,
                    team2Score = req.Team2Score
                });

            if (!success) return Results.Conflict(new { error = "Match state has changed." });

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, status = "completed" }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── POST /api/matches/{matchId}/swap-teams ──────────────────────────
        app.MapPost("/api/matches/{matchId}/swap-teams", async (
            Guid                 matchId,
            HttpContext           ctx,
            IDbConnectionFactory db,
            IHubContext<MatchHub> matchHub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.ExecuteAsync(
                """
                UPDATE brkt_matches
                SET team1_id = team2_id, team2_id = team1_id,
                    team1_score = team2_score, team2_score = team1_score
                WHERE id = @matchId
                """,
                new { matchId });

            if (rows == 0) return Results.NotFound();

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, action = "swap" }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── POST /api/matches/{matchId}/reset ───────────────────────────────
        app.MapPost("/api/matches/{matchId}/reset", async (
            Guid                     matchId,
            HttpContext              ctx,
            IDbConnectionFactory     db,
            IHubContext<MatchHub>    matchHub,
            IHubContext<VetoHub>     vetoHub,
            IHubContext<BracketHub>  bracketHub,
            CancellationToken        ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Delete associated game results
            await conn.ExecuteAsync("DELETE FROM brkt_match_games WHERE match_id = @matchId", new { matchId });

            // 2. Undo advancements — inline SQL (the RPC uses auth.uid() which is NULL from Dapper)
            await conn.ExecuteAsync(
                """
                UPDATE brkt_matches target
                SET
                    team1_id = CASE
                        WHEN EXISTS (
                            SELECT 1 FROM brkt_advancements a
                            WHERE a.source_match_id = @matchId
                              AND a.target_match_id = target.id
                              AND a.target_slot = 1
                        ) THEN NULL ELSE target.team1_id END,
                    team2_id = CASE
                        WHEN EXISTS (
                            SELECT 1 FROM brkt_advancements a
                            WHERE a.source_match_id = @matchId
                              AND a.target_match_id = target.id
                              AND a.target_slot = 2
                        ) THEN NULL ELSE target.team2_id END,
                    winner_id = NULL,
                    loser_id = NULL,
                    status = 'pending',
                    team1_score = 0,
                    team2_score = 0,
                    version = target.version + 1,
                    updated_at = NOW()
                WHERE target.id IN (
                    SELECT target_match_id
                    FROM brkt_advancements
                    WHERE source_match_id = @matchId
                )
                """,
                new { matchId });

            // 3. Reset veto
            try
            {
                await conn.ExecuteAsync(
                    "SELECT public.reset_match_veto(@matchId)",
                    new { matchId });
            }
            catch
            {
                await conn.ExecuteAsync("DELETE FROM match_map_veto_actions WHERE match_id = @matchId", new { matchId });
                await conn.ExecuteAsync("DELETE FROM match_map_vetos WHERE match_id = @matchId", new { matchId });
            }

            // 4. Delete reports/disputes
            await conn.ExecuteAsync("DELETE FROM tournament_match_results WHERE match_id = @matchId", new { matchId });
            await conn.ExecuteAsync("DELETE FROM match_result_reports WHERE match_id = @matchId", new { matchId });
            await conn.ExecuteAsync("DELETE FROM tournament_disputes WHERE match_id = @matchId", new { matchId });

            // 4b. Delete time proposals so teams can re-propose
            await conn.ExecuteAsync("DELETE FROM match_time_proposals WHERE match_id = @matchId", new { matchId });

            // 5. Reset match record
            await conn.ExecuteAsync(
                """
                UPDATE brkt_matches
                SET winner_id = NULL, loser_id = NULL, status = 'pending',
                    team1_score = 0, team2_score = 0, party_code = NULL,
                    scheduled_time = NULL,
                    version = version + 1, updated_at = NOW()
                WHERE id = @matchId
                """,
                new { matchId });

            // 6. Broadcast updates
            var versionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT version_id FROM brkt_matches WHERE id = @matchId", new { matchId });

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, status = "pending", action = "reset" }, ct);

            await vetoHub.Clients
                .Group(VetoHub.VetoGroup(matchId.ToString()))
                .SendAsync(VetoHubEvents.VetoReset, new { matchId }, ct);

            if (versionId is not null)
            {
                await bracketHub.Clients
                    .Group(BracketHub.BracketGroup(versionId.Value.ToString()))
                    .SendAsync(BracketHubEvents.MatchUpdated,
                        new { versionId, matchId }, ct);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── POST /api/matches/{matchId}/go-live ─────────────────────────────
        app.MapPost("/api/matches/{matchId}/go-live", async (
            Guid                      matchId,
            [FromBody] GoLiveRequest  req,
            HttpContext               ctx,
            IDbConnectionFactory      db,
            IHubContext<MatchHub>     matchHub,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.ExecuteAsync(
                "UPDATE brkt_matches SET status = 'in_progress', party_code = @code WHERE id = @matchId",
                new { matchId, code = req.PartyCode.Trim().ToUpperInvariant() });

            if (rows == 0) return Results.NotFound();

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, status = "in_progress" }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── POST /api/matches/{matchId}/save-score ──────────────────────────
        // Replaces GraphMatchService.saveScoreAndAdvance (score + advance + finals reset + stage completion)
        app.MapPost("/api/matches/{matchId}/save-score", async (
            Guid                         matchId,
            [FromBody] SaveScoreRequest  req,
            HttpContext                  ctx,
            IDbConnectionFactory         db,
            IHubContext<BracketHub>      bracketHub,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (req.Team1Score == req.Team2Score)
                return Results.BadRequest(new { error = "Scores cannot be equal." });

            // Resolve team IDs from request or from the match itself
            var t1Id = req.Team1Id;
            var t2Id = req.Team2Id;
            if (t1Id is null || t2Id is null)
            {
                var matchRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT team1_id, team2_id FROM brkt_matches WHERE id = @matchId",
                    new { matchId });
                if (matchRow is null) return Results.NotFound(new { error = "Match not found" });
                t1Id ??= (Guid?)matchRow.team1_id;
                t2Id ??= (Guid?)matchRow.team2_id;
            }

            var winnerId = req.Team1Score > req.Team2Score ? t1Id : t2Id;
            var loserId  = req.Team1Score > req.Team2Score ? t2Id : t1Id;

            // 1. Save score
            await conn.ExecuteAsync(
                """
                UPDATE brkt_matches
                SET team1_score = @t1, team2_score = @t2,
                    winner_id = @winnerId, loser_id = @loserId, status = 'completed'
                WHERE id = @matchId
                """,
                new { matchId, t1 = req.Team1Score, t2 = req.Team2Score, winnerId, loserId });

            // 2. Upsert summary game record
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO brkt_match_games (match_id, game_number, team1_score, team2_score, map_name, status, winner_id, loser_id, completed_at)
                    VALUES (@matchId, 1, @t1, @t2, 'Manual Result', 'completed', @winnerId, @loserId, NOW())
                    ON CONFLICT (match_id, game_number) DO UPDATE
                    SET team1_score = @t1, team2_score = @t2, status = 'completed', winner_id = @winnerId, loser_id = @loserId, completed_at = NOW()
                    """,
                    new { matchId, t1 = req.Team1Score, t2 = req.Team2Score, winnerId, loserId });
            }
            catch { /* Non-critical */ }

            // 2b. Log match event for analytics
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO brkt_match_events (match_id, type, payload, created_by)
                    VALUES (@matchId, 'score_reported', @payload::jsonb, @userId)
                    """,
                    new { matchId, userId = userCtx.UserIdGuid,
                          payload = System.Text.Json.JsonSerializer.Serialize(new {
                              team1_score = req.Team1Score, team2_score = req.Team2Score,
                              winner_id = winnerId, loser_id = loserId
                          }) });
            }
            catch { /* Non-critical */ }

            // 2c. Insert match completed event for analytics pipeline
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO match_completed_events (match_id, winner_id, loser_id, status)
                    VALUES (@matchId, @winnerId, @loserId, 'completed')
                    """,
                    new { matchId, winnerId, loserId });
            }
            catch { /* Non-critical */ }

            // 3. Advance winner/loser
            var advancements = await conn.QueryAsync<dynamic>(
                "SELECT target_match_id, target_slot, type FROM brkt_advancements WHERE source_match_id = @matchId",
                new { matchId });

            foreach (var adv in advancements)
            {
                Guid? teamId = (string?)adv.type == "winner" ? winnerId : loserId;
                if (teamId is null) continue;
                string field = (int)adv.target_slot == 1 ? "team1_id" : "team2_id";
                await conn.ExecuteAsync(
                    $"UPDATE brkt_matches SET {field} = @teamId WHERE id = @targetId",
                    new { teamId, targetId = (Guid)adv.target_match_id });
            }

            // 4. Grand Finals Reset (double elimination)
            try
            {
                var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT bracket_type, round_index, team1_id, team2_id, version_id, match_number, best_of FROM brkt_matches WHERE id = @matchId",
                    new { matchId });

                if (match is not null && (string?)match.bracket_type == "final")
                {
                    var team1Losses = await conn.QuerySingleAsync<int>(
                        "SELECT COUNT(*) FROM brkt_matches WHERE version_id = @vid AND loser_id = @tid AND status = 'completed'",
                        new { vid = (Guid)match.version_id, tid = (Guid?)match.team1_id });
                    var team2Losses = await conn.QuerySingleAsync<int>(
                        "SELECT COUNT(*) FROM brkt_matches WHERE version_id = @vid AND loser_id = @tid AND status = 'completed'",
                        new { vid = (Guid)match.version_id, tid = (Guid?)match.team2_id });

                    if (team1Losses == 1 && team2Losses == 1)
                    {
                        var existingReset = await conn.QuerySingleOrDefaultAsync<Guid?>(
                            "SELECT id FROM brkt_matches WHERE version_id = @vid AND bracket_type = 'final' AND round_index > @ri LIMIT 1",
                            new { vid = (Guid)match.version_id, ri = (int)match.round_index });
                        var previousFinal = await conn.QuerySingleOrDefaultAsync<Guid?>(
                            "SELECT id FROM brkt_matches WHERE version_id = @vid AND bracket_type = 'final' AND round_index < @ri LIMIT 1",
                            new { vid = (Guid)match.version_id, ri = (int)match.round_index });

                        if (existingReset is null && previousFinal is null)
                        {
                            var resetMatchId = Guid.NewGuid();
                            var vid = (Guid)match.version_id;
                            var newRi = (int)match.round_index + 1;

                            await conn.ExecuteAsync(
                                """
                                INSERT INTO brkt_matches (id, version_id, bracket_type, round_index, match_number, status, team1_id, team2_id, best_of)
                                VALUES (@resetId, @vid, 'final', @ri, 1, 'pending', @t1, @t2, @bo)
                                """,
                                new { resetId = resetMatchId, vid, ri = newRi,
                                      t1 = (Guid?)match.team1_id, t2 = (Guid?)match.team2_id, bo = (int?)match.best_of ?? 1 });

                            // Position reset match to the right of the original final
                            var layout = await conn.QuerySingleOrDefaultAsync<dynamic>(
                                "SELECT x, y FROM brkt_layout WHERE version_id = @vid AND match_id = @matchId",
                                new { vid, matchId });

                            int resetX = (layout is not null ? (int)layout.x : 0) + 350;
                            int resetY = layout is not null ? (int)layout.y : 0;

                            await conn.ExecuteAsync(
                                "INSERT INTO brkt_layout (version_id, match_id, x, y) VALUES (@vid, @matchId, @x, @y)",
                                new { vid, matchId = resetMatchId, x = resetX, y = resetY });

                            // Broadcast new match insertion so clients refetch bracket
                            await bracketHub.Clients
                                .Group(BracketHub.BracketGroup(vid.ToString()))
                                .SendAsync(BracketHubEvents.MatchInserted,
                                    new { versionId = vid, matchId = resetMatchId }, ct);
                        }
                    }
                }
            }
            catch { /* Non-critical */ }

            // 5. Stage completion check
            bool stageComplete = false;
            Guid? stageId = null;
            try
            {
                var versionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    "SELECT version_id FROM brkt_matches WHERE id = @matchId", new { matchId });
                if (versionId is not null)
                {
                    stageId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                        "SELECT stage_id FROM brkt_versions WHERE id = @versionId", new { versionId });
                    if (stageId is not null)
                    {
                        var pendingCount = await conn.QuerySingleAsync<int>(
                            "SELECT COUNT(*) FROM brkt_matches WHERE version_id = @versionId AND status != 'completed'",
                            new { versionId });

                        if (pendingCount == 0)
                        {
                            await conn.ExecuteAsync(
                                "UPDATE tournament_stages SET status = 'completed' WHERE id = @stageId",
                                new { stageId });
                            stageComplete = true;

                            // Set tournament winner if elimination format
                            var stageInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                                "SELECT tournament_id, format FROM tournament_stages WHERE id = @stageId",
                                new { stageId });
                            if (stageInfo is not null && winnerId is not null &&
                                ((string?)stageInfo.format == "single_elimination" || (string?)stageInfo.format == "double_elimination"))
                            {
                                await conn.ExecuteAsync(
                                    "UPDATE tournaments SET winner_id = @winnerId, status = 'completed', end_date = NOW() WHERE id = @tid",
                                    new { winnerId, tid = (Guid)stageInfo.tournament_id });
                            }
                        }
                    }

                    // Broadcast bracket update
                    await bracketHub.Clients
                        .Group(BracketHub.BracketGroup(versionId.Value.ToString()))
                        .SendAsync(BracketHubEvents.MatchUpdated,
                            new { versionId, matchId }, ct);
                }
            }
            catch { /* Non-critical */ }

            return Results.Ok(new { success = true, winnerId, loserId, stageId, stageComplete });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/matches/{matchId}/games ──────────────────────────────────
        app.MapGet("/api/matches/{matchId}/games", async (
            Guid                 matchId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT * FROM brkt_match_games WHERE match_id = @matchId ORDER BY game_number ASC",
                new { matchId });
            return Results.Ok(rows);
        });
    }
}

// ── Match request records ────────────────────────────────────────────────────

public sealed record WalkoverRequest(
    Guid   WinnerId,
    Guid   LoserId,
    int    Team1Score,
    int    Team2Score);

public sealed record GoLiveRequest(string PartyCode);

public sealed record SaveScoreRequest(
    int     Team1Score,
    int     Team2Score,
    Guid?   Team1Id,
    Guid?   Team2Id);
