using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Core.Bracket;
using Esportra.Core.Match;
using Esportra.Core.Tournaments;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Hybrid;

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
        // Scans Riot API match history and returns parsed MatchCandidate objects.
        app.MapPost("/api/matches/scan", async (
            [FromBody] ScanRecentMatchesRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory               db,
            VetoDbService                      vetoService,
            Esportra.Infrastructure.Integrations.RiotApiClient riotApi,
            HybridCache                        cache,
            ILoggerFactory                     loggerFactory,
            CancellationToken                  ct) =>
        {
            var log = loggerFactory.CreateLogger("MatchEndpoints.Scan");
            try
            {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Get match + tournament info
            if (!Guid.TryParse(userCtx.UserId, out var userGuid))
                return Results.BadRequest(new { error = "Invalid user ID" });

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

            // 2. Get scanning user's Riot account (puuid + region)
            var scanner = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ra.puuid, ra.game_name, ra.tag_line, ra.region, tm.team_id
                FROM riot_accounts ra
                JOIN team_members tm ON tm.user_id = ra.user_id
                WHERE ra.user_id = @userId AND ra.puuid IS NOT NULL
                  AND tm.team_id IN (@team1Id, @team2Id)
                """,
                new { userId = userGuid, team1Id = (Guid)match.team1_id, team2Id = (Guid)match.team2_id });

            if (scanner is null)
                return Results.Ok(new { matches = Array.Empty<object>(), reason = "Your Riot account is not linked or you are not in this match" });

            var scannerPuuid = (string)scanner.puuid;

            // Detect shard (cached 30 min — shard rarely changes)
            var shard = await cache.GetOrCreateAsync(
                $"riot:shard:{scannerPuuid}",
                async (_) =>
                {
                    var (s, b) = await riotApi.ProxyAsync(
                        "americas", $"/riot/account/v1/active-shards/by-game/val/by-puuid/{scannerPuuid}", ct);
                    if (s == 200)
                    {
                        using var doc = JsonDocument.Parse(b);
                        var val = doc.RootElement.GetProperty("activeShard").GetString()?.ToLowerInvariant();
                        if (!string.IsNullOrEmpty(val)) return val;
                    }
                    // Fallback to DB region
                    var r = ((string?)scanner.region)?.ToLowerInvariant() ?? "eu";
                    return r switch
                    {
                        "na" or "br" or "latam" or "kr" or "ap" or "eu" => r,
                        "americas" => "na", "europe" => "eu", "asia" => "ap",
                        _ => "eu"
                    };
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30) },
                cancellationToken: ct) ?? "eu";

            log.LogInformation("Scanning PUUID {Puuid} on shard {Shard}, map filter: {Map}",
                scannerPuuid, shard, req.MapName);

            // 3. Get veto-derived maps for this match (only these maps are scannable)
            var gameMapOrder = await vetoService.GetGameMapOrderAsync(req.MatchId, ct);

            // Get completed game count to determine which maps are still pending
            var completedMaps = (await conn.QueryAsync<string>(
                "SELECT map_name FROM brkt_match_games WHERE match_id = @matchId AND status = 'completed'",
                new { matchId = req.MatchId })).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var vetoMaps = gameMapOrder
                .Where(g => !completedMaps.Contains(g.MapName))
                .Select(g => g.MapName)
                .ToList();

            // If specific game's map is provided, use that; otherwise use all veto maps
            var allowedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(req.MapName))
                allowedMaps.Add(req.MapName);
            foreach (var m in vetoMaps)
                if (!string.IsNullOrEmpty(m)) allowedMaps.Add(m);

            log.LogInformation("Allowed maps for scan: [{Maps}]", string.Join(", ", allowedMaps));

            // 4. Collect all team members' PUUIDs for player identification
            var allAccounts = (await conn.QueryAsync<dynamic>(
                """
                SELECT ra.puuid, tm.team_id
                FROM riot_accounts ra
                JOIN team_members tm ON tm.user_id = ra.user_id
                WHERE tm.team_id IN (@team1Id, @team2Id) AND ra.puuid IS NOT NULL
                """,
                new { team1Id = (Guid)match.team1_id, team2Id = (Guid)match.team2_id })).AsList();

            var team1Puuids = allAccounts
                .Where(a => (Guid)a.team_id == (Guid)match.team1_id)
                .Select(a => (string)a.puuid).ToHashSet();
            var team2Puuids = allAccounts
                .Where(a => (Guid)a.team_id == (Guid)match.team2_id)
                .Select(a => (string)a.puuid).ToHashSet();

            // 5. Fetch matchlist from Riot API (cached 5 min per PUUID)
            var matchlistJson = await cache.GetOrCreateAsync(
                $"riot:matchlist:{scannerPuuid}",
                async (_) =>
                {
                    var (s, b) = await riotApi.ProxyAsync(
                        shard, $"/val/match/v1/matchlists/by-puuid/{scannerPuuid}", ct);
                    return s == 200 ? b : null;
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) },
                cancellationToken: ct);

            if (matchlistJson is null)
            {
                log.LogWarning("Riot matchlist unavailable for {Puuid} on shard {Shard}", scannerPuuid, shard);
                return Results.Ok(new { matches = Array.Empty<object>(), reason = "Could not fetch match history from Riot" });
            }

            // 6. Parse matchlist — take last 10 entries
            using var listDoc = JsonDocument.Parse(matchlistJson);
            var history = listDoc.RootElement.GetProperty("history");
            var recentIds = history.EnumerateArray()
                .Take(10)
                .Select(e => e.GetProperty("matchId").GetString()!)
                .ToList();

            if (recentIds.Count == 0)
                return Results.Ok(new { matches = Array.Empty<object>(), reason = "No recent matches in Riot history" });

            // 7. Fetch each match detail and build candidates
            var candidates = new List<object>();

            foreach (var riotMatchId in recentIds)
            {
                var (detStatus, detBody) = await riotApi.ProxyAsync(
                    shard, $"/val/match/v1/matches/{riotMatchId}", ct);
                if (detStatus != 200) continue;

                try
                {
                    using var doc = JsonDocument.Parse(detBody);
                    var info = doc.RootElement.GetProperty("matchInfo");
                    var riotMapId = info.GetProperty("mapId").GetString() ?? "";
                    var mapDisplayName = ResolveValorantMapName(riotMapId);
                    log.LogInformation("Match {RiotId}: mapId={MapId}, resolved={MapName}",
                        riotMatchId, riotMapId, mapDisplayName);

                    // Skip matches not on a veto-finalized map
                    if (allowedMaps.Count > 0 && !allowedMaps.Contains(mapDisplayName))
                    {
                        log.LogDebug("Skipping match {RiotId}: map {Map} not in allowed set", riotMatchId, mapDisplayName);
                        continue;
                    }

                    // Track if this match is on the expected map (for UI highlighting)
                    var isExpectedMap = !string.IsNullOrEmpty(req.MapName) &&
                        string.Equals(mapDisplayName, req.MapName, StringComparison.OrdinalIgnoreCase);

                    var queueId = info.GetProperty("queueId").GetString() ?? "";
                    var gameLengthMillis = info.GetProperty("gameLengthMillis").GetInt64();
                    var gameStartMillis = info.GetProperty("gameStartMillis").GetInt64();

                    // Parse teams
                    int blueRounds = 0, redRounds = 0;
                    bool blueWon = false, redWon = false;
                    foreach (var team in doc.RootElement.GetProperty("teams").EnumerateArray())
                    {
                        var tid = team.GetProperty("teamId").GetString();
                        var won = team.GetProperty("won").GetBoolean();
                        var rw = team.GetProperty("roundsWon").GetInt32();
                        if (tid == "Blue") { blueRounds = rw; blueWon = won; }
                        else if (tid == "Red") { redRounds = rw; redWon = won; }
                    }

                    // Parse players, find scanner
                    string? scannerSide = null, scannerAgent = null;
                    int kills = 0, deaths = 0, assists = 0;
                    var playerList = new List<object>();
                    var roundAggregates = ValorantMatchStatsHelper.BuildRoundAggregates(doc.RootElement);

                    foreach (var p in doc.RootElement.GetProperty("players").EnumerateArray())
                    {
                        var pPuuid = p.GetProperty("puuid").GetString() ?? "";
                        playerList.Add(ValorantMatchStatsHelper.BuildPlayerPayload(
                            p,
                            roundAggregates,
                            team1Puuids,
                            team2Puuids));

                        if (pPuuid == scannerPuuid)
                        {
                            scannerSide = p.GetProperty("teamId").GetString() ?? "";
                            scannerAgent = p.GetProperty("characterId").GetString() ?? "";
                            var stats = p.GetProperty("stats");
                            kills = stats.GetProperty("kills").GetInt32();
                            deaths = stats.GetProperty("deaths").GetInt32();
                            assists = stats.GetProperty("assists").GetInt32();
                        }
                    }

                    if (scannerSide is null) continue; // scanner not in this match

                    var isBlue = scannerSide == "Blue";
                    var myRounds = isBlue ? blueRounds : redRounds;
                    var enemyRounds = isBlue ? redRounds : blueRounds;
                    var didWin = isBlue ? blueWon : redWon;

                    var derivedDetails = ValorantMatchStatsHelper.BuildDerivedMatchDetails(doc.RootElement);
                    var serializedDerived = ValorantMatchStatsHelper.SerializeDerivedDetails(derivedDetails);

                    candidates.Add(new
                    {
                        id = riotMatchId,
                        map = mapDisplayName,
                        mapId = riotMapId,
                        queueId,
                        startTime = gameStartMillis,
                        gameLengthMillis,
                        myTeamScore = myRounds,
                        enemyTeamScore = enemyRounds,
                        reporterSide = scannerSide,
                        score = $"{myRounds}-{enemyRounds}",
                        result = didWin ? "Victory" : "Defeat",
                        kda = $"{kills}/{deaths}/{assists}",
                        agent = scannerAgent,
                        isExpectedMap,
                        blueTeam = new { roundsWon = blueRounds, won = blueWon },
                        redTeam = new { roundsWon = redRounds, won = redWon },
                        players = playerList,
                        matchInfo = ValorantMatchStatsHelper.BuildMatchInfoPayload(doc.RootElement),
                        roundTimeline = serializedDerived.RoundTimeline,
                        economyTimeline = serializedDerived.EconomyTimeline,
                        weaponSummaries = serializedDerived.WeaponSummaries,
                    });
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Failed to parse Riot match {MatchId}", riotMatchId);
                }
            }

            log.LogInformation("Found {Count} candidates for map '{Map}'", candidates.Count, req.MapName);
            return Results.Ok(new { matches = candidates });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Scan failed for match {MatchId}", req.MatchId);
                return Results.Json(new { error = "We couldn't scan the match. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/matches/{matchId}/games/{gameNumber}/riot-details ───────
        // Re-fetch and parse full Riot match payload for stored game rows.
        app.MapGet("/api/matches/{matchId:guid}/games/{gameNumber:int}/riot-details", async (
            Guid                           matchId,
            int                            gameNumber,
            HttpContext                    ctx,
            IDbConnectionFactory           db,
            Esportra.Infrastructure.Integrations.RiotApiClient riotApi,
            HybridCache                    cache,
            ILoggerFactory                 loggerFactory,
            CancellationToken              ct) =>
        {
            var log = loggerFactory.CreateLogger("MatchEndpoints.RiotDetails");
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!Guid.TryParse(userCtx.UserId, out var userGuid))
                return Results.BadRequest(new { error = "Invalid user ID" });

            using var conn = db.CreateConnection();

            var gameRow = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT g.riot_match_id, g.match_details::text AS match_details
                FROM brkt_match_games g
                WHERE g.match_id = @matchId AND g.game_number = @gameNumber
                """,
                new { matchId, gameNumber });

            if (gameRow is null)
                return Results.NotFound(new { error = "Game not found" });

            var riotMatchId = (string?)gameRow.riot_match_id;
            if (string.IsNullOrWhiteSpace(riotMatchId))
                return Results.NotFound(new { error = "No Riot match ID stored for this game" });

            var membership = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT bm.team1_id, bm.team2_id
                FROM brkt_matches bm
                JOIN team_members tm ON tm.team_id IN (bm.team1_id, bm.team2_id)
                WHERE bm.id = @matchId AND tm.user_id = @userId
                LIMIT 1
                """,
                new { matchId, userId = userGuid });

            if (membership is null)
                return Results.Forbid();

            var shardAccount = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ra.puuid, ra.region
                FROM riot_accounts ra
                JOIN team_members tm ON tm.user_id = ra.user_id
                WHERE tm.team_id IN (@team1Id, @team2Id) AND ra.puuid IS NOT NULL
                LIMIT 1
                """,
                new { team1Id = (Guid)membership.team1_id, team2Id = (Guid)membership.team2_id });

            if (shardAccount is null)
                return Results.BadRequest(new { error = "No linked Riot account found for this match" });

            var shardPuuid = (string)shardAccount.puuid;
            var shard = await cache.GetOrCreateAsync(
                $"riot:shard:{shardPuuid}",
                async (_) =>
                {
                    var (s, b) = await riotApi.ProxyAsync(
                        "americas", $"/riot/account/v1/active-shards/by-game/val/by-puuid/{shardPuuid}", ct);
                    if (s == 200)
                    {
                        using var doc = JsonDocument.Parse(b);
                        var val = doc.RootElement.GetProperty("activeShard").GetString()?.ToLowerInvariant();
                        if (!string.IsNullOrEmpty(val)) return val;
                    }

                    var r = ((string?)shardAccount.region)?.ToLowerInvariant() ?? "eu";
                    return r switch
                    {
                        "na" or "br" or "latam" or "kr" or "ap" or "eu" => r,
                        "americas" => "na", "europe" => "eu", "asia" => "ap",
                        _ => "eu"
                    };
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30) },
                cancellationToken: ct) ?? "eu";

            var (detStatus, detBody) = await riotApi.ProxyAsync(
                shard, $"/val/match/v1/matches/{riotMatchId}", ct);
            if (detStatus != 200)
            {
                log.LogWarning("Riot match fetch failed for {RiotMatchId} status {Status}", riotMatchId, detStatus);
                return Results.BadRequest(new { error = "Could not fetch match details from Riot" });
            }

            using var riotDoc = JsonDocument.Parse(detBody);
            var allAccounts = (await conn.QueryAsync<dynamic>(
                """
                SELECT ra.puuid, tm.team_id
                FROM riot_accounts ra
                JOIN team_members tm ON tm.user_id = ra.user_id
                WHERE tm.team_id IN (@team1Id, @team2Id) AND ra.puuid IS NOT NULL
                """,
                new { team1Id = (Guid)membership.team1_id, team2Id = (Guid)membership.team2_id })).AsList();

            var team1Puuids = allAccounts
                .Where(a => (Guid)a.team_id == (Guid)membership.team1_id)
                .Select(a => (string)a.puuid).ToHashSet();
            var team2Puuids = allAccounts
                .Where(a => (Guid)a.team_id == (Guid)membership.team2_id)
                .Select(a => (string)a.puuid).ToHashSet();

            var parsed = RiotMatchDetailsParser.Parse(riotDoc.RootElement, team1Puuids, team2Puuids);
            if (parsed is null)
                return Results.BadRequest(new { error = "Failed to parse Riot match payload" });

            var serializedDerived = ValorantMatchStatsHelper.SerializeDerivedDetails(parsed.Derived);
            var payload = new
            {
                players = parsed.Players,
                blueTeam = parsed.BlueTeam,
                redTeam = parsed.RedTeam,
                gameLengthMillis = parsed.GameLengthMillis,
                startTime = parsed.StartTime,
                matchInfo = parsed.MatchInfo,
                roundTimeline = serializedDerived.RoundTimeline,
                economyTimeline = serializedDerived.EconomyTimeline,
                weaponSummaries = serializedDerived.WeaponSummaries,
            };

            var payloadJson = JsonSerializer.Serialize(payload);
            await conn.ExecuteAsync(
                """
                UPDATE brkt_match_games
                SET match_details = COALESCE(match_details, '{}'::jsonb) || @payload::jsonb
                WHERE match_id = @matchId AND game_number = @gameNumber
                """,
                new { matchId, gameNumber, payload = payloadJson });

            return Results.Content(payloadJson, "application/json");
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/process ───────────────────────────────
        // Processes an accepted match result report: validates, finalizes the match,
        // updates standings, and broadcasts the result.
        app.MapPost("/api/matches/{matchId}/process", async (
            Guid                           matchId,
            [FromBody] ProcessMatchResultRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory           db,
            MatchFinalizationService       finalizer,
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

            // 4. Finalize match via .NET service (handles locking + bracket advancement)
            try
            {
                var success = await finalizer.FinalizeAsync(
                    matchId, (int)match.version, winnerId, loserId, team1Score, team2Score, ct);

                if (!success)
                    return Results.Conflict(new { error = "This match was updated by someone else. Please refresh and try again." });
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = "This match was updated by someone else. Please refresh and try again." });
            }

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
            Guid                         matchId,
            [FromBody] FinalizeRequest?  req,
            HttpContext                  ctx,
            MatchFinalizationService     finalizer,
            IDbConnectionFactory         db,
            IHubContext<MatchHub>        matchHub,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller has permission (organizer staff OR match captain for self-play)
            var allowed = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, matchId, StaffAuthHelper.PermScoresUpdate);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
            {
                var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM team_members tm
                        JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                        WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                    )
                    """,
                    new { matchId, userId = userCtx.UserIdGuid });
                if (!isCaptain) return Results.Forbid();
            }

            var winnerId = req?.WinnerId ?? Guid.Empty;
            var loserId  = req?.LoserId  ?? Guid.Empty;

            // Auto-detect winner/loser from existing scores if not provided
            if (winnerId == Guid.Empty || loserId == Guid.Empty)
            {
                var m = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT team1_id, team2_id, team1_score, team2_score FROM brkt_matches WHERE id = @matchId",
                    new { matchId });
                if (m is not null && m.team1_score is not null && m.team2_score is not null)
                {
                    if ((int)m.team1_score == (int)m.team2_score)
                        return Results.BadRequest(new { error = "Scores are tied — a winner can't be determined automatically." });

                    winnerId = (int)m.team1_score > (int)m.team2_score ? (Guid)m.team1_id : (Guid)m.team2_id;
                    loserId  = winnerId == (Guid)m.team1_id ? (Guid)m.team2_id : (Guid)m.team1_id;
                }
            }

            var success = await finalizer.FinalizeAsync(matchId, winnerId, loserId, ct: ct);

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, status = "completed" },
                    ct);

            return Results.Ok(new { success, matchId });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/award-walkover ──────────────────────
        app.MapPost("/api/matches/{matchId}/award-walkover", async (
            Guid                         matchId,
            [FromBody] WalkoverRequest   req,
            HttpContext                  ctx,
            MatchFinalizationService     finalizer,
            IDbConnectionFactory         db,
            IHubContext<MatchHub>        matchHub,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller has permission (organizer staff OR match captain for self-play)
            var allowed = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, matchId, StaffAuthHelper.PermScoresUpdate);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
            {
                var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM team_members tm
                        JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                        WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                    )
                    """,
                    new { matchId, userId = userCtx.UserIdGuid });
                if (!isCaptain) return Results.Forbid();
            }

            try
            {
                var success = await finalizer.FinalizeAsync(
                    matchId, req.WinnerId, req.LoserId, req.Team1Score, req.Team2Score, ct);

                if (!success) return Results.Conflict(new { error = "This match was updated by someone else. Please refresh and try again." });
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = "This match was updated by someone else. Please refresh and try again." });
            }

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, status = "completed" }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

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

            // Verify caller has permission
            var allowed = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, matchId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

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
        }).RequireAuthorization("Authenticated");

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

            // Verify caller has permission
            var allowed = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, matchId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

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
                    team1_score = NULL,
                    team2_score = NULL,
                    version = target.version + 1,
                    updated_at = NOW()
                WHERE target.id IN (
                    SELECT target_match_id
                    FROM brkt_advancements
                    WHERE source_match_id = @matchId
                )
                """,
                new { matchId });

            // 3. Reset veto — delete entirely for a clean reinit
            try
            {
                await conn.ExecuteAsync("DELETE FROM match_map_veto_actions WHERE match_id = @matchId", new { matchId });
                await conn.ExecuteAsync("DELETE FROM match_map_vetos WHERE match_id = @matchId", new { matchId });
            }
            catch { /* veto tables may not exist yet */ }

            // 4. Soft-close open disputes, delete reports
            await conn.ExecuteAsync(
                """
                UPDATE tournament_disputes
                SET status = 'resolved',
                    resolution_notes = 'Match was reset by organizer.',
                    updated_at = NOW()
                WHERE match_id = @matchId AND status NOT IN ('resolved', 'closed')
                """,
                new { matchId });
            await conn.ExecuteAsync("DELETE FROM tournament_match_results WHERE match_id = @matchId", new { matchId });
            await conn.ExecuteAsync("DELETE FROM match_result_reports WHERE match_id = @matchId", new { matchId });

            // 4b. Delete time proposals and check-ins so teams can re-propose and re-checkin
            await conn.ExecuteAsync("DELETE FROM match_time_proposals WHERE match_id = @matchId", new { matchId });
            await conn.ExecuteAsync("DELETE FROM match_checkins WHERE match_id = @matchId", new { matchId });

            // 5. Reset match record
            await conn.ExecuteAsync(
                """
                UPDATE brkt_matches
                SET winner_id = NULL, loser_id = NULL, status = 'pending',
                    team1_score = NULL, team2_score = NULL, party_code = NULL,
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
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/go-live ─────────────────────────────
        app.MapPost("/api/matches/{matchId}/go-live", async (
            Guid                      matchId,
            [FromBody] GoLiveRequest  req,
            HttpContext               ctx,
            IDbConnectionFactory      db,
            SelfPlayMatchRoomService  roomService,
            GameCatalogService        gameCatalog,
            IHubContext<MatchHub>     matchHub,
            CancellationToken         ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller has permission (organizer staff OR match captain for self-play)
            var allowed = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, matchId, StaffAuthHelper.PermScoresUpdate);
            var isPlatformAdminForGoLive = StaffAuthHelper.IsPlatformAdmin(userCtx);
            var canForceGoLive = allowed || isPlatformAdminForGoLive;
            var callerCompetitorId = await BracketCompetitorResolver.GetUserCompetitorIdInMatchAsync(
                conn, userCtx.UserIdGuid, matchId);
            if (!allowed && !isPlatformAdminForGoLive)
            {
                if (callerCompetitorId is null) return Results.Forbid();
            }

            var code = req.PartyCode?.Trim().ToUpperInvariant() ?? "";

            var gameRow = await conn.QuerySingleOrDefaultAsync<GoLiveGameRow>(
                """
                SELECT t.game AS Game, t.game_mode AS GameMode
                FROM brkt_matches m
                JOIN brkt_versions v ON v.id = m.version_id
                JOIN tournaments t ON t.id = v.tournament_id
                WHERE m.id = @matchId
                """,
                new { matchId });

            if (gameRow?.Game is not null)
            {
                var supportsMapVeto = await gameCatalog.SupportsMapVetoAsync(
                    gameRow.Game, gameRow.GameMode, existingConnection: conn);
                var context = await roomService.LoadContextAsync(matchId, supportsMapVeto, ct);
                if (context is not null && SelfPlayMatchRoomService.IsSelfPlayActive(context))
                {
                    if (canForceGoLive)
                    {
                        var staffGuard = roomService.CanStaffForceGoLive(
                            context, code, req.Force, DateTime.UtcNow);
                        if (!staffGuard.Allowed)
                            return Results.Json(new
                            {
                                error = staffGuard.Message,
                                code = staffGuard.Code,
                                phase = staffGuard.Phase,
                                nextAction = staffGuard.NextAction,
                            }, statusCode: 400);
                    }
                    else
                    {
                        var captainGuard = roomService.CanCaptainGoLive(
                            context, callerCompetitorId, code, DateTime.UtcNow);
                        if (!captainGuard.Allowed)
                            return Results.Json(new
                            {
                                error = captainGuard.Message,
                                code = captainGuard.Code,
                                phase = captainGuard.Phase,
                                nextAction = captainGuard.NextAction,
                            }, statusCode: 400);
                    }
                }
                else if (!canForceGoLive)
                {
                    var (effectiveTime, _) = context is not null
                        ? SelfPlayMatchRoomService.ResolveEffectiveSchedule(context)
                        : (await conn.QuerySingleOrDefaultAsync<DateTime?>(
                            "SELECT scheduled_time FROM brkt_matches WHERE id = @matchId",
                            new { matchId }), (string?)null);
                    var timingGuard = SelfPlayMatchRoomService.ValidateCaptainGoLiveTiming(
                        effectiveTime, DateTime.UtcNow);
                    if (!timingGuard.Allowed)
                        return Results.Json(new { error = timingGuard.Message, code = timingGuard.Code }, statusCode: 400);
                }
            }
            else if (!canForceGoLive)
            {
                var scheduledTime = await conn.QuerySingleOrDefaultAsync<DateTime?>(
                    "SELECT scheduled_time FROM brkt_matches WHERE id = @matchId",
                    new { matchId });
                var timingGuard = SelfPlayMatchRoomService.ValidateCaptainGoLiveTiming(
                    scheduledTime, DateTime.UtcNow);
                if (!timingGuard.Allowed)
                    return Results.Json(new { error = timingGuard.Message, code = timingGuard.Code }, statusCode: 400);
            }

            var rows = await conn.ExecuteAsync(
                "UPDATE brkt_matches SET status = 'in_progress', party_code = @code WHERE id = @matchId",
                new { matchId, code });

            if (rows == 0) return Results.NotFound();

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new { matchId, status = "in_progress" }, ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/save-score ──────────────────────────
        // Replaces GraphMatchService.saveScoreAndAdvance (score + advance + finals reset + stage completion)
        app.MapPost("/api/matches/{matchId}/save-score", async (
            Guid                         matchId,
            [FromBody] SaveScoreRequest  req,
            HttpContext                  ctx,
            IDbConnectionFactory         db,
            IHubContext<BracketHub>      bracketHub,
            IHubContext<MatchHub>        matchHub,
            TournamentWinnerService      winnerService,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller has permission (organizer staff OR match captain for self-play)
            var isOrganizerOrStaff = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, matchId, StaffAuthHelper.PermScoresUpdate)
                || StaffAuthHelper.IsPlatformAdmin(userCtx);
            if (!isOrganizerOrStaff)
            {
                var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM team_members tm
                        JOIN brkt_matches bm ON (bm.team1_id = tm.team_id OR bm.team2_id = tm.team_id)
                        WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                    )
                    """,
                    new { matchId, userId = userCtx.UserIdGuid });
                if (!isCaptain) return Results.Forbid();
            }

            if (req.Team1Score == req.Team2Score)
                return Results.BadRequest(new { error = "Scores can't be tied. One team must win." });

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

                            // Set tournament winner if elimination format — use grand final winner
                            var stageInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                                "SELECT tournament_id, format FROM tournament_stages WHERE id = @stageId",
                                new { stageId });
                            if (stageInfo is not null &&
                                ((string?)stageInfo.format == "single_elimination" || (string?)stageInfo.format == "double_elimination"))
                            {
                                // Grand final = highest round_index in the bracket (bracket_type is 'winners', not 'final')
                                var gfWinnerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                                    """
                                    SELECT winner_id FROM brkt_matches
                                    WHERE version_id = @versionId
                                      AND status = 'completed' AND winner_id IS NOT NULL
                                    ORDER BY round_index DESC, match_number DESC
                                    LIMIT 1
                                    """,
                                    new { versionId });
                                if (gfWinnerId is not null)
                                {
                                    await winnerService.SetWinnerAsync(
                                        conn,
                                        tx: null,
                                        (Guid)stageInfo.tournament_id,
                                        gfWinnerId.Value,
                                        reason: "match score completed final bracket",
                                        ct);

                                    // Send tournament won notification to winning team captains
                                    try
                                    {
                                        var tournamentName = await conn.QuerySingleOrDefaultAsync<string>(
                                            "SELECT name FROM tournaments WHERE id = @tid",
                                            new { tid = (Guid)stageInfo.tournament_id });
                                        var winningCaptains = await conn.QueryAsync<Guid>(
                                            """
                                            SELECT tm.user_id FROM team_members tm
                                            WHERE tm.team_id = @teamId AND tm.role = 'captain' AND tm.is_active = true
                                            """,
                                            new { teamId = gfWinnerId });
                                        var winningTeamName = await conn.QuerySingleOrDefaultAsync<string>(
                                            "SELECT name FROM teams WHERE id = @id",
                                            new { id = gfWinnerId });
                                        foreach (var captainId in winningCaptains)
                                        {
                                            await conn.ExecuteAsync(
                                                """
                                                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                                                VALUES (@userId, 'tournament_announcement', @title,
                                                        @msg, @link,
                                                        jsonb_build_object('tournament_id', @tid::text, 'team_id', @teamId::text)::jsonb, false)
                                                """,
                                                new {
                                                    userId = captainId,
                                                    title = $"🏆 Champions! {winningTeamName ?? "Your Team"} Wins!",
                                                    msg = $"WHAT A RUN! {winningTeamName ?? "Your team"} just conquered {tournamentName ?? "the tournament"}! The trophy is yours — celebrate with your squad!",
                                                    link = $"/tournaments/{stageInfo.tournament_id}",
                                                    tid = ((Guid)stageInfo.tournament_id).ToString(),
                                                    teamId = ((Guid)gfWinnerId).ToString()
                                                });
                                        }
                                    }
                                    catch { /* Notification is non-critical */ }
                                }
                            }
                        }
                    }
                }
            }
            catch { /* Non-critical */ }

            // Organizer/staff manual score is authoritative — clear open dispute artifacts
            if (isOrganizerOrStaff)
            {
                var actorId = userCtx.UserIdGuid;
                await conn.ExecuteAsync(
                    """
                    UPDATE match_result_reports
                    SET status = 'rejected', responded_at = NOW(), responded_by = @userId
                    WHERE match_id = @matchId AND status IN ('disputed', 'pending')
                    """,
                    new { matchId, userId = actorId });

                await conn.ExecuteAsync(
                    """
                    UPDATE match_disputes
                    SET status = 'resolved',
                        resolution = 'Match score manually settled by organizer',
                        resolved_at = NOW(),
                        resolved_by = @userId
                    WHERE match_id = @matchId AND status = 'pending'
                    """,
                    new { matchId, userId = actorId });

                await conn.ExecuteAsync(
                    """
                    UPDATE tournament_disputes
                    SET status = 'resolved',
                        resolution_notes = COALESCE(
                            resolution_notes,
                            'Match score manually settled by organizer'
                        ),
                        assigned_to_user_id = COALESCE(assigned_to_user_id, @userId),
                        updated_at = NOW()
                    WHERE match_id = @matchId AND status = 'open'
                    """,
                    new { matchId, userId = actorId });

                await matchHub.Clients
                    .Group(MatchHub.MatchGroup(matchId.ToString()))
                    .SendAsync(MatchHubEvents.DisputeResolved,
                        new { match_id = matchId, status = "resolved", source = "manual_score" }, ct);
            }

            // Always notify match room subscribers — manual score completes the match
            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged,
                    new
                    {
                        matchId,
                        status = "completed",
                        winnerId,
                        loserId,
                        team1Score = req.Team1Score,
                        team2Score = req.Team2Score,
                        source = isOrganizerOrStaff ? "manual_score" : "captain_score",
                    }, ct);

            // Guaranteed bracket broadcast (stage-completion try block may swallow errors)
            var broadcastVersionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT version_id FROM brkt_matches WHERE id = @matchId", new { matchId });
            if (broadcastVersionId is not null)
            {
                await bracketHub.Clients
                    .Group(BracketHub.BracketGroup(broadcastVersionId.Value.ToString()))
                    .SendAsync(BracketHubEvents.MatchUpdated,
                        new { versionId = broadcastVersionId, matchId }, ct);
            }

            return Results.Ok(new { success = true, winnerId, loserId, stageId, stageComplete });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/matches/{matchId}/games ──────────────────────────────────
        app.MapGet("/api/matches/{matchId}/games", async (
            Guid                 matchId,
            IDbConnectionFactory db,
            VetoDbService        vetoService,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            // Get completed game rows from DB
            var completedRows = (await conn.QueryAsync<dynamic>(
                "SELECT * FROM brkt_match_games WHERE match_id = @matchId ORDER BY game_number ASC",
                new { matchId })).AsList();

            // Derive full game map order from veto data
            var gameMapOrder = await vetoService.GetGameMapOrderAsync(matchId, ct);

            if (gameMapOrder.Count == 0)
                return Results.Ok(completedRows);

            // Merge: completed rows take precedence, pending games filled from veto
            var completedGameNumbers = completedRows
                .Select(r => (int)Convert.ToInt32(r.game_number))
                .ToHashSet();

            var result = new List<dynamic>(completedRows);
            foreach (var (gameNumber, mapId, mapName) in gameMapOrder)
            {
                if (!completedGameNumbers.Contains(gameNumber))
                {
                    result.Add(new Dictionary<string, object?>
                    {
                        ["match_id"] = matchId,
                        ["game_number"] = gameNumber,
                        ["map_id"] = Guid.Parse(mapId),
                        ["map_name"] = mapName,
                        ["status"] = "pending",
                        ["team1_score"] = null,
                        ["team2_score"] = null,
                        ["winner_id"] = null,
                        ["loser_id"] = null,
                    });
                }
            }

            return Results.Ok(result.OrderBy(r =>
            {
                if (r is IDictionary<string, object?> dict) return (int)dict["game_number"]!;
                return Convert.ToInt32(((dynamic)r).game_number);
            }));
        });
    }

    /// <summary>Resolve Riot Valorant map path to display name.</summary>
    private static string ResolveValorantMapName(string riotMapId)
    {
        // Riot uses internal paths like /Game/Maps/Triad/Triad
        var map = ValorantMaps.GetValueOrDefault(riotMapId);
        if (map is not null) return map;

        // Fallback: extract last path segment
        var lastSlash = riotMapId.LastIndexOf('/');
        return lastSlash >= 0 ? riotMapId[(lastSlash + 1)..] : riotMapId;
    }

    private static readonly Dictionary<string, string> ValorantMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/Game/Maps/Ascent/Ascent"]       = "Ascent",
        ["/Game/Maps/Duality/Duality"]     = "Bind",
        ["/Game/Maps/Triad/Triad"]         = "Haven",
        ["/Game/Maps/Bonsai/Bonsai"]       = "Split",
        ["/Game/Maps/Port/Port"]           = "Icebox",
        ["/Game/Maps/Foxtrot/Foxtrot"]     = "Breeze",
        ["/Game/Maps/Canyon/Canyon"]       = "Fracture",
        ["/Game/Maps/Pitt/Pitt"]           = "Pearl",
        ["/Game/Maps/Jam/Jam"]             = "Lotus",
        ["/Game/Maps/Juliett/Juliett"]     = "Sunset",
        ["/Game/Maps/Infinity/Infinity"]   = "Abyss",
        ["/Game/Maps/Rook/Rook"]           = "Corrode",
        ["/Game/Maps/HURM/HURM_Alley/HURM_Alley"]     = "District",
        ["/Game/Maps/HURM/HURM_Bowl/HURM_Bowl"]       = "Kasbah",
        ["/Game/Maps/HURM/HURM_Yard/HURM_Yard"]       = "Piazza",
    };
}

// ── Match request records ────────────────────────────────────────────────────

public sealed record WalkoverRequest(
    Guid   WinnerId,
    Guid?  LoserId,
    int    Team1Score,
    int    Team2Score);

public sealed record FinalizeRequest(
    Guid? WinnerId = null,
    Guid? LoserId  = null);

internal sealed record GoLiveGameRow(string Game, string? GameMode);

public sealed record GoLiveRequest(string? PartyCode, bool Force = false);

public sealed record SaveScoreRequest(
    int     Team1Score,
    int     Team2Score,
    Guid?   Team1Id,
    Guid?   Team2Id);
