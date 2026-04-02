using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Core.Bracket;
using Esportra.Core.Match;
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
            DapperJsonbHelper.FixJsonb(reports);
            return Results.Ok(reports);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/reports ────────────────────────────────────
        app.MapPost("/api/matches/{id}/reports", async (
            Guid                          id,
            [FromBody] SubmitReportRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory          db,
            IHubContext<MatchHub>         matchHub,
            ILoggerFactory                loggerFactory,
            CancellationToken             ct) =>
        {
            var logger = loggerFactory.CreateLogger("MatchReports");
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is a captain of the reporting team
            if (!Guid.TryParse(req.ReportedByTeamId, out var reportingTeamId))
                return Results.BadRequest(new { error = "Invalid team ID" });

            var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @teamId AND user_id = @userId AND role = 'captain' AND is_active = TRUE)",
                new { teamId = reportingTeamId, userId = userCtx.UserIdGuid });
            if (!isCaptain) return Results.Forbid();

            // Verify the reporting team is actually in this match
            var isTeamInMatch = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM brkt_matches WHERE id = @matchId AND (team1_id = @teamId OR team2_id = @teamId))",
                new { matchId = id, teamId = reportingTeamId });
            if (!isTeamInMatch) return Results.Forbid();

            try
            {
            // Block submission if same game is already disputed
            var existingDisputed = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM match_result_reports
                    WHERE match_id = @matchId AND game_number = @gameNumber AND status = 'disputed'
                )
                """,
                new { matchId = id, gameNumber = req.GameNumber });
            if (existingDisputed)
                return Results.Conflict(new { error = "This game is currently disputed. Results cannot be submitted until the dispute is resolved." });

            // Derive winner from scores if not explicitly provided
            Guid? derivedWinner = Guid.TryParse(req.WinnerTeamId, out var parsedWinner) ? parsedWinner : (Guid?)null;
            if (derivedWinner is null && req.Team1Score != req.Team2Score)
            {
                var matchTeams = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT team1_id, team2_id FROM brkt_matches WHERE id = @id", new { id });
                if (matchTeams is not null)
                {
                    derivedWinner = req.Team1Score > req.Team2Score
                        ? (Guid)matchTeams.team1_id
                        : (Guid)matchTeams.team2_id;
                    logger.LogInformation("Derived winner for report on match {MatchId}: {T1}-{T2} → {Winner}",
                        id, req.Team1Score, req.Team2Score, derivedWinner);
                }
            }

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
                ON CONFLICT (match_id, game_number, reported_by_team_id)
                DO UPDATE SET
                   team1_score    = EXCLUDED.team1_score,
                   team2_score    = EXCLUDED.team2_score,
                   riot_match_id  = COALESCE(EXCLUDED.riot_match_id, match_result_reports.riot_match_id),
                   map_id         = COALESCE(EXCLUDED.map_id, match_result_reports.map_id),
                   map_name       = COALESCE(EXCLUDED.map_name, match_result_reports.map_name),
                   winner_team_id = COALESCE(EXCLUDED.winner_team_id, match_result_reports.winner_team_id),
                   match_data     = EXCLUDED.match_data,
                   screenshot_urls = EXCLUDED.screenshot_urls,
                   comment        = EXCLUDED.comment,
                   status         = 'pending',
                   updated_at     = now()
                RETURNING id, match_id, game_number, reported_by_team_id, team1_score, team2_score,
                         winner_team_id, riot_match_id, map_id, map_name, match_data,
                         screenshot_urls, comment, status, created_at, updated_at
                """,
                new
                {
                    matchId           = id,
                    gameNumber        = req.GameNumber,
                    reportedBy        = userCtx.UserIdGuid,
                    reportedByTeamId  = reportingTeamId,
                    riotMatchId       = (string?)req.RiotMatchId,
                    mapId             = Guid.TryParse(req.MapId, out var parsedMapId) ? (Guid?)parsedMapId : null,
                    mapName           = (string?)req.MapName,
                    team1Score        = req.Team1Score,
                    team2Score        = req.Team2Score,
                    winnerTeamId      = derivedWinner,
                    matchData         = req.MatchData is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.MatchData)
                        : "{}",
                    screenshotUrls    = req.ScreenshotUrls is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.ScreenshotUrls)
                        : "[]",
                    comment           = (string?)req.Comment,
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

                if (!Guid.TryParse(opposingTeamId, out var opposingTeamGuid))
                    return Results.Ok(report);

                if (opposingTeamId is not null)
                {
                    var captain = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        """
                        SELECT user_id FROM team_members
                        WHERE team_id = @tid AND role = 'captain' AND is_active = TRUE
                        LIMIT 1
                        """,
                        new { tid = opposingTeamGuid });

                    if (captain?.user_id is not null)
                    {
                        var captainId = captain.user_id is Guid g ? g : Guid.Parse(captain.user_id.ToString());
                        var reporterTeamName = await conn.QuerySingleOrDefaultAsync<string>(
                            "SELECT name FROM teams WHERE id = @id",
                            new { id = Guid.Parse(req.ReportedByTeamId) });
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO notifications
                              (user_id, type, title, message, link, data, is_read)
                            VALUES
                              (@userId, 'result_reported', @title,
                               @msg,
                               @link, @data::jsonb, FALSE)
                            """,
                            new
                            {
                                userId = captainId,
                                title  = $"⚔️ Match Result Submitted",
                                msg    = $"{reporterTeamName ?? "Your opponent"} has reported the match score. Review and confirm, or dispute if something's off.",
                                link   = matchLink,
                                data   = System.Text.Json.JsonSerializer.Serialize(new { match_id = id }),
                            });

                        // Push via NotificationHub
                        await matchHub.Clients
                            .Group(MatchHub.MatchGroup(id.ToString()))
                            .SendAsync(MatchHubEvents.ReportSubmitted,
                                new { matchId = id, reportId = report.id?.ToString() }, ct);
                    }
                }
            }

            DapperJsonbHelper.FixJsonb(report);
            return Results.Ok(report);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to submit match report for match {MatchId}", id);
                return Results.Json(new { error = "We couldn't submit your report. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/reports/{rid}/accept ───────────────────────
        app.MapPost("/api/matches/{id}/reports/{rid}/accept", async (
            Guid                           id,
            Guid                           rid,
            [FromBody] AcceptReportRequest  req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            VetoDbService                  vetoService,
            MatchFinalizationService       finalizer,
            IHubContext<MatchHub>          matchHub,
            IHubContext<BracketHub>        bracketHub,
            ILoggerFactory                 loggerFactory,
            CancellationToken              ct) =>
        {
            var logger = loggerFactory.CreateLogger("MatchReports");
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
            using var conn = db.CreateConnection();

            // Verify caller is a captain in this match (opposing team)
            var captainTeamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT tm.team_id FROM team_members tm
                JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                LIMIT 1
                """,
                new { matchId = id, userId = userCtx.UserIdGuid });
            if (captainTeamId is null) return Results.Forbid();

            // Prevent a team from accepting their own report
            var reportingTeamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT reported_by_team_id FROM match_result_reports WHERE id = @rid AND match_id = @matchId",
                new { rid, matchId = id });
            if (reportingTeamId is not null && captainTeamId == reportingTeamId)
                return Results.BadRequest(new { error = "Cannot accept your own team's report." });

            // Mark report accepted
            await conn.ExecuteAsync(
                """
                UPDATE match_result_reports
                SET status = 'accepted', responded_by = @userId, responded_at = NOW()
                WHERE id = @rid AND match_id = @matchId AND status = 'pending'
                """,
                new { rid, matchId = id, userId = userCtx.UserIdGuid });

            // Auto-process: record game result, check if series is complete
            Guid? winnerId = null;
            bool seriesComplete = false;
            try
            {
                var report = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT winner_team_id, team1_score, team2_score,
                           match_data, map_name, map_id, riot_match_id,
                           screenshot_urls, game_number, reported_by_team_id
                    FROM match_result_reports WHERE id = @rid
                    """, new { rid });

                if (report is not null)
                {
                    var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        "SELECT version, team1_id, team2_id, version_id, best_of FROM brkt_matches WHERE id = @matchId",
                        new { matchId = id });

                    if (match is not null)
                    {
                        // Use Convert.ToInt32 for safe numeric conversion (Dapper may return long/short)
                        int t1 = Convert.ToInt32(report.team1_score);
                        int t2 = Convert.ToInt32(report.team2_score);
                        int bestOf = Convert.ToInt32(match.best_of ?? 1);
                        int winsNeeded = (bestOf / 2) + 1; // BO1→1, BO3→2, BO5→3

                        logger.LogInformation(
                            "Accept report for match {MatchId}: game score {T1}-{T2}, bestOf={BestOf}, winsNeeded={WinsNeeded}",
                            id, t1, t2, bestOf, winsNeeded);

                        // Derive game winner from this report
                        Guid? gameWinnerId = null;
                        if (report.winner_team_id is not null)
                        {
                            gameWinnerId = (Guid)report.winner_team_id;
                        }
                        else if (t1 != t2)
                        {
                            gameWinnerId = t1 > t2 ? (Guid)match.team1_id : (Guid)match.team2_id;
                        }

                        if (gameWinnerId is null)
                        {
                            logger.LogWarning("Cannot auto-process match {MatchId}: tied scores {T1}-{T2} and no explicit winner", id, t1, t2);
                        }
                        else
                        {
                            var gameLoserId = gameWinnerId == (Guid)match.team1_id ? (Guid)match.team2_id : (Guid)match.team1_id;

                            // 1. Upsert brkt_match_games FIRST (before checking series)
                            try
                            {
                                var gameNumber = Convert.ToInt32(report.game_number);
                                var mapName = (string?)(report.map_name?.ToString());
                                var mapId = report.map_id is Guid mg ? (Guid?)mg : null;

                                // Enrich map info from veto data if report doesn't have it
                                if (mapId is null || mapName is null)
                                {
                                    try
                                    {
                                        var gameMapOrder = await vetoService.GetGameMapOrderAsync(id, ct);
                                        var vetoGame = gameMapOrder.FirstOrDefault(g => g.GameNumber == gameNumber);
                                        if (vetoGame != default)
                                        {
                                            mapId ??= Guid.TryParse(vetoGame.MapId, out var vid) ? vid : null;
                                            mapName ??= vetoGame.MapName;
                                        }
                                    }
                                    catch (Exception vetoEx)
                                    {
                                        logger.LogWarning(vetoEx, "Failed to enrich map from veto for match {MatchId} game {GameNumber}", id, (int)gameNumber);
                                    }
                                }
                                var riotMatchId = (string?)(report.riot_match_id?.ToString());
                                var matchDetails = report.match_data is string mdStr ? mdStr
                                    : report.match_data is not null ? System.Text.Json.JsonSerializer.Serialize(report.match_data)
                                    : null;
                                var matchDetailsJson = matchDetails ?? "{}";

                                logger.LogInformation(
                                    "Upserting brkt_match_games for match {MatchId}: game={GameNumber}, map={MapName}, mapId={MapId}, winner={Winner}",
                                    id, (int)gameNumber, mapName ?? "null", mapId?.ToString() ?? "null", (Guid?)gameWinnerId);

                                await conn.ExecuteAsync(
                                    """
                                    INSERT INTO brkt_match_games
                                        (match_id, game_number, team1_score, team2_score, map_name, map_id,
                                         riot_match_id, status, winner_id, loser_id, match_details,
                                         reported_by_team_id, completed_at)
                                    VALUES
                                        (@matchId, @gameNumber, @t1, @t2, @mapName, @mapId,
                                         @riotMatchId, 'completed', @winnerId, @loserId, @matchDetails::jsonb,
                                         @reportedByTeamId, NOW())
                                    ON CONFLICT (match_id, game_number) DO UPDATE SET
                                        team1_score    = @t1,
                                        team2_score    = @t2,
                                        map_name       = COALESCE(@mapName, brkt_match_games.map_name),
                                        map_id         = COALESCE(@mapId, brkt_match_games.map_id),
                                        riot_match_id  = COALESCE(@riotMatchId, brkt_match_games.riot_match_id),
                                        status         = 'completed',
                                        winner_id      = @winnerId,
                                        loser_id       = @loserId,
                                        match_details  = COALESCE(@matchDetails::jsonb, brkt_match_games.match_details),
                                        reported_by_team_id = @reportedByTeamId,
                                        completed_at   = NOW()
                                    """,
                                    new
                                    {
                                        matchId = id, gameNumber, t1, t2,
                                        mapName, mapId, riotMatchId,
                                        matchDetails = matchDetailsJson,
                                        winnerId = gameWinnerId, loserId = gameLoserId,
                                        reportedByTeamId = report.reported_by_team_id is Guid rg ? (Guid?)rg : null,
                                    });

                                logger.LogInformation("brkt_match_games upsert succeeded for match {MatchId} game {GameNumber}", id, (int)gameNumber);
                            }
                            catch (Exception gmEx)
                            {
                                logger.LogError(gmEx, "Failed to upsert brkt_match_games for match {MatchId}", id);
                            }

                            // 2. Count series wins from all completed games
                            // Use COUNT + FILTER instead of SUM to get integer (not bigint)
                            var seriesWins = await conn.QuerySingleAsync<dynamic>(
                                """
                                SELECT
                                    COUNT(*) FILTER (WHERE winner_id = @team1Id)::int AS team1_wins,
                                    COUNT(*) FILTER (WHERE winner_id = @team2Id)::int AS team2_wins
                                FROM brkt_match_games
                                WHERE match_id = @matchId AND status = 'completed'
                                """,
                                new { matchId = id, team1Id = (Guid)match.team1_id, team2Id = (Guid)match.team2_id });

                            int team1Wins = Convert.ToInt32(seriesWins.team1_wins);
                            int team2Wins = Convert.ToInt32(seriesWins.team2_wins);

                            logger.LogInformation(
                                "Match {MatchId} series update: {T1Wins}-{T2Wins} (need {WinsNeeded} for BO{BestOf})",
                                id, team1Wins, team2Wins, winsNeeded, bestOf);

                            // 3. Update series score on brkt_matches (visible in bracket UI)
                            await conn.ExecuteAsync(
                                """
                                UPDATE brkt_matches
                                SET team1_score = @team1Wins, team2_score = @team2Wins, updated_at = NOW()
                                WHERE id = @matchId
                                """,
                                new { matchId = id, team1Wins, team2Wins });

                            // 4. Only finalize + advance if a team has reached winsNeeded
                            if (team1Wins >= winsNeeded || team2Wins >= winsNeeded)
                            {
                                seriesComplete = true;
                                winnerId = team1Wins >= winsNeeded ? (Guid)match.team1_id : (Guid)match.team2_id;
                                var loserId = winnerId == (Guid)match.team1_id ? (Guid)match.team2_id : (Guid)match.team1_id;

                                try
                                {
                                    var finalized = await finalizer.FinalizeAsync(
                                        id, Convert.ToInt32(match.version), winnerId.Value, loserId,
                                        team1Wins, team2Wins, ct);

                                    if (finalized)
                                    {
                                        logger.LogInformation(
                                            "Match {MatchId} series complete: winner={Winner}, series={T1}-{T2} (BO{BestOf})",
                                            id, winnerId, team1Wins, team2Wins, bestOf);
                                    }
                                }
                                catch (InvalidOperationException ex)
                                {
                                    logger.LogWarning(ex, "Match {MatchId} finalization version conflict", id);
                                    // Surface the conflict so the client can retry
                                    return Results.Conflict(new
                                    {
                                        success        = false,
                                        error          = "version_conflict",
                                        message        = "This match was updated by someone else. Please try again.",
                                        matchId        = id,
                                        reportId       = rid,
                                        seriesComplete = true,
                                    });
                                }
                            }
                            else
                            {
                                logger.LogInformation(
                                    "Match {MatchId} series in progress: {T1Wins}-{T2Wins}, need {WinsNeeded} wins (BO{BestOf})",
                                    id, team1Wins, team2Wins, winsNeeded, bestOf);
                            }

                            // 5. Broadcast bracket update (even for partial series progress)
                            if (match.version_id is not null)
                            {
                                var vid = (Guid)match.version_id;
                                await bracketHub.Clients
                                    .Group(BracketHub.BracketGroup(vid.ToString()))
                                    .SendAsync(BracketHubEvents.MatchUpdated,
                                        new { versionId = vid, matchId = id }, ct);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-process after accept failed for match {MatchId} (non-fatal)", id);
            }

            // Notify via SignalR
            await matchHub.Clients
                .Group(MatchHub.MatchGroup(id.ToString()))
                .SendAsync(MatchHubEvents.ReportAccepted,
                    new { matchId = id, reportId = rid, riotMatchId = req.RiotMatchId }, ct);

            return Results.Ok(new
            {
                success       = true,
                matchId       = id,
                reportId      = rid,
                riotMatchId   = req.RiotMatchId,
                processed     = winnerId is not null,
                seriesComplete,
            });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to accept report {ReportId} for match {MatchId}", rid, id);
                return Results.Json(new { error = "We couldn't process the report. Please try again." }, statusCode: 500);
            }
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

            // Verify caller is a captain of a team in this match
            var captainTeamId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT tm.team_id FROM team_members tm
                JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                LIMIT 1
                """,
                new { matchId = id, userId = userCtx.UserIdGuid });
            if (captainTeamId is null) return Results.Forbid();

            using var tx = conn.BeginTransaction();

            try
            {
                // 1. Mark the report as disputed
                await conn.ExecuteAsync(
                    """
                    UPDATE match_result_reports
                    SET status = 'disputed', updated_at = NOW()
                    WHERE id = @rid AND match_id = @matchId
                    """,
                    new { rid, matchId = id }, tx);

                // 2. Resolve tournament info (organizer, reporter, slug)
                var info = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT bv.tournament_id, t.organizer_id, r.reported_by, t.slug
                    FROM match_result_reports r
                    JOIN brkt_matches bm ON bm.id = r.match_id
                    JOIN brkt_versions bv ON bv.id = bm.version_id
                    JOIN tournaments t ON t.id = bv.tournament_id
                    WHERE r.id = @rid
                    """,
                    new { rid }, tx);

                Guid? tournamentId = info?.tournament_id;
                Guid? organizerId  = info?.organizer_id;
                Guid? reporterId   = info?.reported_by;
                string? slug       = info?.slug;

                // 3. Insert match-level dispute (used by useMatchDispute hook)
                var teamId = Guid.TryParse(req.TeamId, out var tg) ? tg : (Guid?)null;
                await conn.ExecuteAsync(
                    """
                    INSERT INTO match_disputes
                        (match_id, disputed_by_team_id, disputed_by_user_id, reason, evidence_urls, status)
                    VALUES
                        (@matchId, @teamId, @userId, @reason, '{}', 'pending')
                    """,
                    new { matchId = id, teamId, userId = userCtx.UserIdGuid, reason = req.Reason }, tx);

                // 4. Insert tournament-level dispute (organizer disputes tab)
                var dispute = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO tournament_disputes
                        (tournament_id, match_id, raised_by_user_id, team_id,
                         title, description, dispute_reason, status, reference_number)
                    VALUES
                        (@tournamentId, @matchId, @userId, @teamId,
                         'Match Result Disputed', @reason, 'result_dispute', 'open',
                         'DSP-' || LPAD(nextval('dispute_reference_seq')::text, 4, '0'))
                    RETURNING id, tournament_id, match_id, raised_by_user_id, team_id,
                             title, description, dispute_reason, status, reference_number, created_at
                    """,
                    new
                    {
                        tournamentId, matchId = id,
                        userId = userCtx.UserIdGuid, teamId,
                        reason = req.Reason,
                    }, tx);

                // 5. Notify reporter that their result is being disputed
                if (reporterId is not null && reporterId != userCtx.UserIdGuid)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                        VALUES (@userId, 'result_disputed', '🚨 Result Disputed!',
                                'The opposing team has challenged your reported result. An organizer will step in to review.',
                                '/tournaments/captain',
                                jsonb_build_object('match_id', @matchId::text)::jsonb, false)
                        """,
                        new { userId = reporterId, matchId = id }, tx);
                }

                // 6. Notify tournament organizer
                if (organizerId is not null)
                {
                    var link = $"/organizer/tournament/{slug ?? tournamentId?.ToString()}?tab=disputes";
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                        VALUES (@userId, 'dispute_filed', '⚠️ Dispute Needs Your Attention',
                                'A team has disputed a match result in your tournament. Head to Disputes to make your ruling.',
                                @link,
                                jsonb_build_object('match_id', @matchId::text, 'tournament_id', @tournamentId::text)::jsonb, false)
                        """,
                        new { userId = organizerId, matchId = id, tournamentId, link }, tx);
                }

                tx.Commit();

                // 7. Broadcast dispute event via SignalR (after commit)
                await matchHub.Clients
                    .Group(MatchHub.MatchGroup(id.ToString()))
                    .SendAsync(MatchHubEvents.ReportDisputed,
                        new { matchId = id, reportId = rid, reason = req.Reason }, ct);

                return Results.Ok(new { success = true, matchId = id, reportId = rid, disputeId = (Guid)dispute.id });
            }
            catch (Exception)
            {
                tx.Rollback();
                return Results.Json(new { error = "We couldn't file your dispute. Please try again." }, statusCode: 500);
            }
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
                LIMIT 500
                """,
                new { id });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{id}/messages/system ────────────────────────────
        // System messages: only organizers/admins can send these.
        app.MapPost("/api/matches/{id}/messages/system", async (
            Guid                               id,
            [FromBody] SystemMessageRequest    req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            IHubContext<ChatHub>              chatHub,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is tournament organizer or admin
            var isAdmin = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM admin_user_roles WHERE user_id = @uid)",
                new { uid = userCtx.UserIdGuid });
            if (!isAdmin)
            {
                var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM brkt_matches bm
                        JOIN brkt_versions bv ON bm.version_id = bv.id
                        JOIN tournament_stages ts ON bv.stage_id = ts.id
                        JOIN tournaments t ON ts.tournament_id = t.id
                        WHERE bm.id = @matchId AND t.organizer_id = @uid
                    )
                    """,
                    new { matchId = id, uid = userCtx.UserIdGuid });
                if (!isOrganizer)
                    return Results.Json(new { error = "Only tournament organizers or admins can send system messages." }, statusCode: 403);
            }

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

            // Verify caller belongs to the team and is not a coach
            var isMember = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE AND role != 'coach')",
                new { teamId = teamIdGuid, userId = userCtx.UserIdGuid });
            if (!isMember) return Results.Forbid();

            // Verify the team is actually in this match
            var isTeamInMatch = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM brkt_matches WHERE id = @matchId AND (team1_id = @teamId OR team2_id = @teamId))",
                new { matchId = id, teamId = teamIdGuid });
            if (!isTeamInMatch) return Results.Forbid();

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

            // Verify caller has permission on this stage
            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

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

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

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
                  AND m.version_id IN (SELECT v.id FROM brkt_versions v WHERE v.stage_id = @stageId)
                """,
                new { ids, times, stageId });

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

            // Verify caller has permission on this match
            var allowed = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, matchId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

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
                return Results.Json(new { error = "We couldn't submit your time proposal. Please try again." }, statusCode: 500);
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

            // Prevent accepting own proposal
            var proposerTeamId = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT proposed_by_team_id::text FROM match_time_proposals WHERE id = @proposalId AND match_id = @matchId",
                new { proposalId, matchId });
            if (proposerTeamId is not null && captainTeam == proposerTeamId)
                return Results.BadRequest(new { error = "Cannot accept your own time proposal." });

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
                RETURNING id, match_id, disputed_by_team_id, disputed_by_user_id, reason, evidence_urls, status, created_at
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

            // Verify caller has disputes:assist permission
            // Look up match_id from the dispute, then check via match
            var disputeMatchId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT match_id FROM match_disputes WHERE id = @disputeId",
                new { disputeId });
            if (disputeMatchId is null) return Results.NotFound(new { error = "Dispute not found." });

            var allowed = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, disputeMatchId.Value, StaffAuthHelper.PermDisputesAssist);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

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
                var notifTitle   = req.Status == "resolved"
                    ? "✅ Dispute Resolved"
                    : "❌ Dispute Rejected";
                var notifMessage = req.Status == "resolved"
                    ? $"Your match dispute has been resolved in your favor. Organizer note: {req.Resolution}"
                    : $"Your match dispute was reviewed and rejected. Organizer note: {req.Resolution}";
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

        // ── GET /api/matches/{id}/riot-accounts ──────────────────────────────
        // Returns all Riot accounts for players in both teams of a match
        app.MapGet("/api/matches/{id}/riot-accounts", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT tm.team_id, teams.name AS team_name,
                       tm.user_id, COALESCE(p.full_name, p.username) AS username,
                       ra.game_name, ra.tag_line, ra.puuid
                FROM brkt_matches bm
                JOIN team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id) AND tm.is_active = true
                JOIN riot_accounts ra ON ra.user_id = tm.user_id
                LEFT JOIN teams ON teams.id = tm.team_id
                LEFT JOIN profiles p ON p.id = tm.user_id
                WHERE bm.id = @matchId
                """,
                new { matchId = id });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/matches/{id}/verify ─────────────────────────────────────
        // Organizer match verification: returns reports, riot accounts, game details
        app.MapGet("/api/matches/{id}/verify", async (
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify organizer/staff access
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT bm.id, bm.team1_id, bm.team2_id, bm.team1_score, bm.team2_score,
                       bm.match_number, bm.best_of, bm.status, bm.bracket_type, bm.round_index,
                       t1.name AS team1_name, t2.name AS team2_name,
                       bv.tournament_id
                FROM brkt_matches bm
                JOIN brkt_versions bv ON bv.id = bm.version_id
                LEFT JOIN teams t1 ON t1.id = bm.team1_id
                LEFT JOIN teams t2 ON t2.id = bm.team2_id
                WHERE bm.id = @matchId
                """, new { matchId = id });
            if (match is null) return Results.NotFound();

            Guid tournamentId = (Guid)match.tournament_id;
            var isOrgOrStaff = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM tournaments t
                    WHERE t.id = @tid AND (t.organizer_id = @uid OR EXISTS (
                        SELECT 1 FROM organization_staff os
                        JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                        WHERE sta.tournament_id = @tid AND os.user_id = @uid AND os.status = 'active'
                    ))
                )
                """, new { tid = tournamentId, uid = userCtx.UserIdGuid });
            if (!isOrgOrStaff) return Results.Forbid();

            // All submitted reports for this match
            var reports = await conn.QueryAsync<dynamic>(
                """
                SELECT mrr.*, COALESCE(p.full_name, p.username) AS reported_by_name,
                       rpt.name AS reported_by_team_name
                FROM match_result_reports mrr
                LEFT JOIN profiles p ON p.id = mrr.reported_by
                LEFT JOIN teams rpt ON rpt.id = mrr.reported_by_team_id
                WHERE mrr.match_id = @matchId
                ORDER BY mrr.game_number, mrr.created_at
                """, new { matchId = id });
            DapperJsonbHelper.FixJsonb(reports);

            // Riot accounts for both teams
            var riotAccounts = await conn.QueryAsync<dynamic>(
                """
                SELECT tm.team_id, teams.name AS team_name,
                       tm.user_id, COALESCE(p.full_name, p.username) AS username,
                       ra.game_name, ra.tag_line, ra.puuid
                FROM team_members tm
                JOIN riot_accounts ra ON ra.user_id = tm.user_id
                LEFT JOIN teams ON teams.id = tm.team_id
                LEFT JOIN profiles p ON p.id = tm.user_id
                WHERE tm.team_id IN (@team1Id, @team2Id) AND tm.is_active = true
                """,
                new { team1Id = (Guid)match.team1_id, team2Id = (Guid)match.team2_id });

            // Game details (map veto results)
            var games = await conn.QueryAsync<dynamic>(
                """
                SELECT * FROM brkt_match_games
                WHERE match_id = @matchId
                ORDER BY game_number
                """, new { matchId = id });
            DapperJsonbHelper.FixJsonb(games);

            return Results.Ok(new { match, reports, riotAccounts, games });
        }).RequireAuthorization("Authenticated");
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
    int    GameNumber,
    string? RiotMatchId = null,
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
