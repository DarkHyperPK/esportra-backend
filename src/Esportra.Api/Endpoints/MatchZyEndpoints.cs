using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Integrations;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class MatchZyEndpoints
{
    /// <summary>
    /// Maps human-readable CS2 map names to engine map names expected by MatchZy.
    /// Static copy — same values as GameServerEndpoints.Cs2EngineMapNames.
    /// </summary>
    private static readonly Dictionary<string, string> Cs2EngineMapNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Anubis"] = "de_anubis",
        ["Dust II"] = "de_dust2",
        ["Mirage"] = "de_mirage",
        ["Inferno"] = "de_inferno",
        ["Nuke"] = "de_nuke",
        ["Overpass"] = "de_overpass",
        ["Vertigo"] = "de_vertigo",
        ["Ancient"] = "de_ancient",
        ["Cache"] = "de_cache",
        ["Train"] = "de_train",
    };

    /// <summary>Snake-case JSON options matching the PostgreSQL JSONB convention for picked maps.</summary>
    private static readonly JsonSerializerOptions SnakeCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Preserve exact property names when serializing the MatchZy config response.</summary>
    private static readonly JsonSerializerOptions ExactCaseJson = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static void MapMatchZyEndpoints(this WebApplication app)
    {
        // ═══════════════════════════════════════════════════════════════════════
        // GET /api/matchzy/config/{matchId} — MatchZy configuration endpoint
        //
        // Called by the MatchZy CS2 server plugin via URL. Authenticated via
        // ?secret= query parameter matched against game_servers.matchzy_secret.
        // NO RequireAuthorization — this is a machine-to-machine endpoint.
        // ═══════════════════════════════════════════════════════════════════════
        app.MapGet("/api/matchzy/config/{matchId}", async (
            Guid matchId,
            string? secret,
            HttpContext ctx,
            IDbConnectionFactory db,
            ILogger<Program> logger) =>
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                logger.LogWarning("MatchZy config request for match {MatchId}: missing secret parameter", matchId);
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            }

            try
            {
                using var conn = db.CreateConnection();

                // ── 1. Verify secret ──────────────────────────────────────────
                var serverSecret = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT matchzy_secret FROM game_servers WHERE match_id = @matchId AND status != 'deleted' LIMIT 1",
                    new { matchId });

                if (serverSecret is null || !string.Equals(serverSecret, secret, StringComparison.Ordinal))
                {
                    logger.LogWarning("MatchZy config: secret mismatch for match {MatchId}", matchId);
                    return Results.Json(new { error = "Forbidden" }, statusCode: 403);
                }

                // ── 2. Get match data ─────────────────────────────────────────
                var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT id, team1_id, team2_id, best_of FROM brkt_matches WHERE id = @matchId",
                    new { matchId });

                if (match is null)
                {
                    logger.LogWarning("MatchZy config: match {MatchId} not found", matchId);
                    return Results.NotFound(new { error = "Match not found" });
                }

                if (match.team1_id is null || match.team2_id is null)
                    return Results.BadRequest(new { error = "Match teams have not been assigned yet." });

                Guid team1Id = (Guid)match.team1_id;
                Guid team2Id = (Guid)match.team2_id;
                int bestOf = Convert.ToInt32(match.best_of ?? 1);

                // ── 3. Get team names ─────────────────────────────────────────
                var teams = (await conn.QueryAsync<dynamic>(
                    "SELECT id, name FROM teams WHERE id = ANY(@teamIds)",
                    new { teamIds = new[] { team1Id, team2Id } }))
                    .ToDictionary(t => (Guid)t.id, t => (string)t.name);

                var team1Name = teams.GetValueOrDefault(team1Id, "Team 1");
                var team2Name = teams.GetValueOrDefault(team2Id, "Team 2");

                // ── 4. Get veto data ──────────────────────────────────────────
                var veto = await conn.QuerySingleOrDefaultAsync<dynamic>(@"
                    SELECT team1_picked_maps, team2_picked_maps,
                           selected_map_id::text AS selected_map_id,
                           team1_banned_maps, team2_banned_maps,
                           selected_map_pool, best_of
                    FROM match_map_vetos
                    WHERE match_id = @matchId AND status = 'completed'
                    LIMIT 1",
                    new { matchId });

                // ── 5. Build map list + map sides ─────────────────────────────
                var mapList = new List<string>();
                var mapSides = new List<string>();

                if (veto is not null)
                {
                    PickedMapEntry[] t1Picked = ParsePickedMaps(veto.team1_picked_maps);
                    PickedMapEntry[] t2Picked = ParsePickedMaps(veto.team2_picked_maps);
                    string? selectedMapId = (string?)veto.selected_map_id;

                    // Compute decider map if not explicitly stored (BO3/BO5)
                    if (selectedMapId is null && bestOf > 1)
                    {
                        string[] pool = ParseStringArray(veto.selected_map_pool);
                        string[] bans1 = ParseStringArray(veto.team1_banned_maps);
                        string[] bans2 = ParseStringArray(veto.team2_banned_maps);
                        var picks = new HashSet<string>(
                            t1Picked.Select(p => p.MapId).Concat(t2Picked.Select(p => p.MapId)));
                        var allExcluded = new HashSet<string>(bans1.Concat(bans2));
                        allExcluded.UnionWith(picks);
                        selectedMapId = pool.FirstOrDefault(m => !allExcluded.Contains(m));
                    }

                    // Collect ordered maps: (mapId, pickerTeam, side)
                    var orderedMaps = new List<(string MapId, string Picker, string? Side)>();

                    if (bestOf == 1)
                    {
                        // BO1: single map from team1 pick or selected_map_id
                        if (t1Picked.Length > 0)
                            orderedMaps.Add((t1Picked[0].MapId, "team1", t1Picked[0].Side));
                        else if (selectedMapId is not null)
                            orderedMaps.Add((selectedMapId, "decider", null));
                    }
                    else if (bestOf == 3)
                    {
                        // BO3: T1 pick → T2 pick → decider
                        if (t1Picked.Length > 0) orderedMaps.Add((t1Picked[0].MapId, "team1", t1Picked[0].Side));
                        if (t2Picked.Length > 0) orderedMaps.Add((t2Picked[0].MapId, "team2", t2Picked[0].Side));
                        if (selectedMapId is not null) orderedMaps.Add((selectedMapId, "decider", null));
                    }
                    else if (bestOf == 5)
                    {
                        // BO5: T1 pick → T2 pick → T1 pick → T2 pick → decider
                        if (t1Picked.Length > 0) orderedMaps.Add((t1Picked[0].MapId, "team1", t1Picked[0].Side));
                        if (t2Picked.Length > 0) orderedMaps.Add((t2Picked[0].MapId, "team2", t2Picked[0].Side));
                        if (t1Picked.Length > 1) orderedMaps.Add((t1Picked[1].MapId, "team1", t1Picked[1].Side));
                        if (t2Picked.Length > 1) orderedMaps.Add((t2Picked[1].MapId, "team2", t2Picked[1].Side));
                        if (selectedMapId is not null) orderedMaps.Add((selectedMapId, "decider", null));
                    }

                    // Batch-resolve map UUIDs → display names
                    var allMapIds = orderedMaps.Select(m => m.MapId).Distinct().ToArray();
                    var mapNameLookup = new Dictionary<string, string>();
                    if (allMapIds.Length > 0)
                    {
                        var dbMaps = await conn.QueryAsync<dynamic>(
                            "SELECT id::text AS id, map_name FROM game_maps WHERE id::text = ANY(@ids)",
                            new { ids = allMapIds });
                        foreach (var m in dbMaps)
                            mapNameLookup[(string)m.id] = (string)m.map_name;
                    }

                    // Translate to engine names + side preferences
                    for (int i = 0; i < orderedMaps.Count; i++)
                    {
                        var (mapId, picker, side) = orderedMaps[i];
                        bool isLastMap = i == orderedMaps.Count - 1 && bestOf > 1;

                        // Resolve UUID → display name → engine name
                        string engineName = "de_dust2"; // fallback
                        if (mapNameLookup.TryGetValue(mapId, out var displayName) &&
                            Cs2EngineMapNames.TryGetValue(displayName, out var resolved))
                        {
                            engineName = resolved;
                        }
                        mapList.Add(engineName);

                        // Decider map always uses knife round
                        mapSides.Add(isLastMap ? "knife" : TranslateSide(picker, side));
                    }
                }

                // Fallback if no maps resolved (veto missing or incomplete)
                if (mapList.Count == 0)
                {
                    logger.LogWarning("MatchZy config: no maps resolved for match {MatchId}, falling back to de_dust2", matchId);
                    mapList.Add("de_dust2");
                    mapSides.Add("knife");
                }

                // ── 6. Get team rosters (Steam IDs) ──────────────────────────
                var team1Players = await GetTeamRosterAsync(conn, team1Id);
                var team2Players = await GetTeamRosterAsync(conn, team2Id);

                // ── 7. Build MatchZy config response ─────────────────────────
                var config = new Dictionary<string, object>
                {
                    ["matchid"] = matchId.ToString(),
                    ["team1"] = new Dictionary<string, object>
                    {
                        ["id"] = team1Id.ToString(),
                        ["name"] = team1Name,
                        ["players"] = team1Players,
                    },
                    ["team2"] = new Dictionary<string, object>
                    {
                        ["id"] = team2Id.ToString(),
                        ["name"] = team2Name,
                        ["players"] = team2Players,
                    },
                    ["num_maps"] = bestOf,
                    ["maplist"] = mapList,
                    ["map_sides"] = mapSides,
                    ["clinch_series"] = true,
                };

                logger.LogInformation(
                    "MatchZy config served for match {MatchId}: BO{BestOf}, maps=[{Maps}], sides=[{Sides}], team1={T1Count}p, team2={T2Count}p",
                    matchId, bestOf, string.Join(", ", mapList), string.Join(", ", mapSides),
                    team1Players.Count, team2Players.Count);

                return Results.Json(config, ExactCaseJson);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MatchZy config: unhandled error for match {MatchId}", matchId);
                return Results.Problem("Internal server error", statusCode: 500);
            }
        });

        // ═══════════════════════════════════════════════════════════════════════
        // POST /api/matchzy/events — MatchZy webhook receiver
        //
        // Receives ALL MatchZy webhook events. Authenticated via
        // Authorization: Bearer {secret} header matched against
        // game_servers.matchzy_secret.
        // NO RequireAuthorization — this is a machine-to-machine endpoint.
        //
        // IMPORTANT: Always return 200 to MatchZy (except auth failures).
        // MatchZy does not retry on non-200 responses.
        // ═══════════════════════════════════════════════════════════════════════
        app.MapPost("/api/matchzy/events", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<MatchHub> matchHub,
            IHubContext<BracketHub> bracketHub,
            IServiceScopeFactory scopeFactory,
            ILogger<Program> logger) =>
        {
            // ── Read raw body ─────────────────────────────────────────────
            string body;
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                body = await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MatchZy webhook: failed to read request body");
                return Results.Ok();
            }

            // ── Extract bearer token ──────────────────────────────────────
            var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
            string? bearerToken = null;
            if (authHeader is not null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                bearerToken = authHeader["Bearer ".Length..].Trim();

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // ── Parse event + matchid ─────────────────────────────────
                var eventType = root.TryGetProperty("event", out var eventEl) ? eventEl.GetString() : null;
                var matchIdStr = root.TryGetProperty("matchid", out var matchIdEl) ? matchIdEl.GetString() : null;

                if (string.IsNullOrEmpty(eventType))
                {
                    logger.LogWarning("MatchZy webhook: missing 'event' field in body");
                    return Results.Ok();
                }

                if (!Guid.TryParse(matchIdStr, out var matchId))
                {
                    logger.LogWarning("MatchZy webhook: invalid or missing matchid '{MatchId}' for event {Event}", matchIdStr, eventType);
                    return Results.Ok();
                }

                logger.LogInformation("MatchZy webhook received: event={Event}, matchId={MatchId}", eventType, matchId);

                // ── Verify secret ─────────────────────────────────────────
                using var conn = db.CreateConnection();

                var server = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT id, match_id, matchzy_secret, external_id FROM game_servers WHERE match_id = @matchId AND status != 'deleted' LIMIT 1",
                    new { matchId });

                if (server is null)
                {
                    logger.LogWarning("MatchZy webhook: no active server found for match {MatchId}", matchId);
                    return Results.Json(new { error = "Forbidden" }, statusCode: 403);
                }

                var expectedSecret = (string?)server.matchzy_secret;
                if (string.IsNullOrEmpty(bearerToken) || !string.Equals(expectedSecret, bearerToken, StringComparison.Ordinal))
                {
                    logger.LogWarning("MatchZy webhook: secret mismatch for match {MatchId}", matchId);
                    return Results.Json(new { error = "Forbidden" }, statusCode: 403);
                }

                // ── Route by event type ───────────────────────────────────
                switch (eventType)
                {
                    case "series_start":
                        await HandleSeriesStart(conn, matchId, logger);
                        break;

                    case "going_live":
                        await HandleGoingLive(conn, matchId, root, matchHub, logger);
                        break;

                    case "round_end":
                        await HandleRoundEnd(conn, matchId, root, matchHub, logger);
                        break;

                    case "map_result":
                        await HandleMapResult(conn, matchId, root, matchHub, logger);
                        break;

                    case "series_end":
                        await HandleSeriesEnd(conn, matchId, root, server, matchHub, bracketHub, scopeFactory, logger);
                        break;

                    case "demo_upload_ended":
                        await HandleDemoUploadEnded(conn, matchId, root, logger);
                        break;

                    default:
                        logger.LogInformation("MatchZy webhook: unrecognized event '{Event}' for match {MatchId}", eventType, matchId);
                        break;
                }

                return Results.Ok();
            }
            catch (JsonException jsonEx)
            {
                logger.LogError(jsonEx, "MatchZy webhook: malformed JSON body");
                return Results.Ok();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MatchZy webhook: unhandled error processing event");
                return Results.Ok();
            }
            finally
            {
                doc?.Dispose();
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Event Handlers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>series_start — Mark the game server as live.</summary>
    private static async Task HandleSeriesStart(
        System.Data.IDbConnection conn, Guid matchId, ILogger logger)
    {
        try
        {
            await conn.ExecuteAsync(
                "UPDATE game_servers SET status = 'live', updated_at = NOW() WHERE match_id = @matchId AND status != 'deleted'",
                new { matchId });

            logger.LogInformation("MatchZy series_start: server status set to 'live' for match {MatchId}", matchId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MatchZy series_start: failed to update server status for match {MatchId}", matchId);
        }
    }

    /// <summary>going_live — A map is about to begin. Update current map number and broadcast.</summary>
    private static async Task HandleGoingLive(
        System.Data.IDbConnection conn, Guid matchId, JsonElement root,
        IHubContext<MatchHub> matchHub, ILogger logger)
    {
        try
        {
            int mapNumber = 0;
            if (root.TryGetProperty("params", out var p) && p.TryGetProperty("map_number", out var mn))
                mapNumber = mn.GetInt32();

            int gameNumber = mapNumber + 1; // MatchZy is 0-indexed, we are 1-indexed

            await conn.ExecuteAsync(
                "UPDATE game_servers SET current_map_number = @gameNumber, updated_at = NOW() WHERE match_id = @matchId AND status != 'deleted'",
                new { matchId, gameNumber });

            await matchHub.Clients.Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync("GoingLive", new { matchId, mapNumber, gameNumber });

            logger.LogInformation("MatchZy going_live: match {MatchId} map {MapNumber} (game {GameNumber})",
                matchId, mapNumber, gameNumber);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MatchZy going_live: failed for match {MatchId}", matchId);
        }
    }

    /// <summary>round_end — Update live scores and broadcast to clients.</summary>
    private static async Task HandleRoundEnd(
        System.Data.IDbConnection conn, Guid matchId, JsonElement root,
        IHubContext<MatchHub> matchHub, ILogger logger)
    {
        try
        {
            if (!root.TryGetProperty("params", out var p))
            {
                logger.LogWarning("MatchZy round_end: missing 'params' for match {MatchId}", matchId);
                return;
            }

            int mapNumber = p.TryGetProperty("map_number", out var mn) ? mn.GetInt32() : 0;
            int team1Score = p.TryGetProperty("team1_score", out var t1s) ? t1s.GetInt32() : 0;
            int team2Score = p.TryGetProperty("team2_score", out var t2s) ? t2s.GetInt32() : 0;
            int team1SeriesScore = p.TryGetProperty("team1_series_score", out var t1ss) ? t1ss.GetInt32() : 0;
            int team2SeriesScore = p.TryGetProperty("team2_series_score", out var t2ss) ? t2ss.GetInt32() : 0;

            int gameNumber = mapNumber + 1;

            // Upsert live scores into brkt_match_games
            await conn.ExecuteAsync(@"
                INSERT INTO brkt_match_games (match_id, game_number, team1_score, team2_score)
                VALUES (@matchId, @gameNumber, @team1Score, @team2Score)
                ON CONFLICT (match_id, game_number) DO UPDATE SET
                    team1_score = @team1Score,
                    team2_score = @team2Score",
                new { matchId, gameNumber, team1Score, team2Score });

            // Broadcast live scores
            await matchHub.Clients.Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync("MatchScoreUpdated", new
                {
                    matchId,
                    gameNumber,
                    mapNumber,
                    team1Score,
                    team2Score,
                    team1SeriesScore,
                    team2SeriesScore,
                });

            logger.LogDebug(
                "MatchZy round_end: match {MatchId} game {GameNumber}: {T1}-{T2} (series {S1}-{S2})",
                matchId, gameNumber, team1Score, team2Score, team1SeriesScore, team2SeriesScore);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MatchZy round_end: failed for match {MatchId}", matchId);
        }
    }

    /// <summary>map_result — Finalize map score and persist per-player stats.</summary>
    private static async Task HandleMapResult(
        System.Data.IDbConnection conn, Guid matchId, JsonElement root,
        IHubContext<MatchHub> matchHub, ILogger logger)
    {
        try
        {
            if (!root.TryGetProperty("params", out var p))
            {
                logger.LogWarning("MatchZy map_result: missing 'params' for match {MatchId}", matchId);
                return;
            }

            int mapNumber = p.TryGetProperty("map_number", out var mn) ? mn.GetInt32() : 0;
            int gameNumber = mapNumber + 1;

            // Extract final scores
            int team1Score = 0, team2Score = 0;
            if (p.TryGetProperty("team1", out var t1Obj))
                team1Score = t1Obj.TryGetProperty("score", out var s1) ? s1.GetInt32() : 0;
            if (p.TryGetProperty("team2", out var t2Obj))
                team2Score = t2Obj.TryGetProperty("score", out var s2) ? s2.GetInt32() : 0;

            // Determine winner
            string? winnerLabel = p.TryGetProperty("winner", out var w) ? w.GetString() : null;

            // Get team IDs to determine winner_id
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT team1_id, team2_id FROM brkt_matches WHERE id = @matchId",
                new { matchId });

            Guid? winnerId = null;
            Guid? loserId = null;
            if (match is not null && !string.IsNullOrEmpty(winnerLabel)
                && match.team1_id is not null && match.team2_id is not null)
            {
                Guid team1Id = (Guid)match.team1_id;
                Guid team2Id = (Guid)match.team2_id;
                (winnerId, loserId) = winnerLabel switch
                {
                    "team1" => ((Guid?)team1Id, (Guid?)team2Id),
                    "team2" => ((Guid?)team2Id, (Guid?)team1Id),
                    _ => (null, null),
                };
            }

            // Finalize map score in brkt_match_games
            await conn.ExecuteAsync(@"
                INSERT INTO brkt_match_games (match_id, game_number, team1_score, team2_score, status, winner_id, loser_id, completed_at)
                VALUES (@matchId, @gameNumber, @team1Score, @team2Score, 'completed', @winnerId, @loserId, NOW())
                ON CONFLICT (match_id, game_number) DO UPDATE SET
                    team1_score  = @team1Score,
                    team2_score  = @team2Score,
                    status       = 'completed',
                    winner_id    = @winnerId,
                    loser_id     = @loserId,
                    completed_at = NOW()",
                new { matchId, gameNumber, team1Score, team2Score, winnerId, loserId });

            logger.LogInformation(
                "MatchZy map_result: match {MatchId} game {GameNumber} finalized: {T1}-{T2}, winner={Winner}",
                matchId, gameNumber, team1Score, team2Score, winnerLabel ?? "unknown");

            // ── Persist per-player stats ──────────────────────────────────
            await UpsertPlayerStats(conn, matchId, mapNumber, gameNumber, "team1", p, logger);
            await UpsertPlayerStats(conn, matchId, mapNumber, gameNumber, "team2", p, logger);

            // Broadcast map result
            await matchHub.Clients.Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync("MapResultFinalized", new
                {
                    matchId,
                    gameNumber,
                    mapNumber,
                    team1Score,
                    team2Score,
                    winner = winnerLabel,
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MatchZy map_result: failed for match {MatchId}", matchId);
        }
    }

    /// <summary>series_end — Match is complete. Update records, broadcast, fire-and-forget server deletion.</summary>
    private static async Task HandleSeriesEnd(
        System.Data.IDbConnection conn, Guid matchId, JsonElement root,
        dynamic server,
        IHubContext<MatchHub> matchHub, IHubContext<BracketHub> bracketHub,
        IServiceScopeFactory scopeFactory, ILogger logger)
    {
        try
        {
            // Extract winner
            string? winnerLabel = root.TryGetProperty("params", out var p) && p.TryGetProperty("winner", out var w)
                ? w.GetString()
                : null;

            // Get match team IDs + bracket version (with idempotency guard)
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT team1_id, team2_id, version_id, status FROM brkt_matches WHERE id = @matchId",
                new { matchId });

            if (match is null)
            {
                logger.LogWarning("MatchZy series_end: match {MatchId} not found in brkt_matches", matchId);
                return;
            }

            // Idempotency: if already completed (duplicate webhook), skip processing
            if ((string?)match.status == "completed")
            {
                logger.LogInformation("MatchZy series_end: match {MatchId} already completed, skipping duplicate event", matchId);
                return;
            }

            if (match.team1_id is null || match.team2_id is null)
            {
                logger.LogWarning("MatchZy series_end: match {MatchId} has unassigned team slots, skipping", matchId);
                return;
            }

            Guid team1Id = (Guid)match.team1_id;
            Guid team2Id = (Guid)match.team2_id;

            // Determine winner team ID
            Guid? winnerTeamId = winnerLabel switch
            {
                "team1" => team1Id,
                "team2" => team2Id,
                _ => null,
            };

            if (winnerTeamId is null)
            {
                logger.LogWarning("MatchZy series_end: unrecognized winner '{Winner}' for match {MatchId}", winnerLabel, matchId);
                return;
            }

            Guid loserId = winnerTeamId == team1Id ? team2Id : team1Id;

            // Update match to completed
            await conn.ExecuteAsync(@"
                UPDATE brkt_matches
                SET status = 'completed', winner_id = @winnerTeamId, loser_id = @loserId, ended_at = NOW()
                WHERE id = @matchId",
                new { matchId, winnerTeamId, loserId });

            // Update game server to completed
            await conn.ExecuteAsync(
                "UPDATE game_servers SET status = 'completed', updated_at = NOW() WHERE match_id = @matchId AND status != 'deleted'",
                new { matchId });

            logger.LogInformation(
                "MatchZy series_end: match {MatchId} completed, winner={WinnerTeamId}",
                matchId, winnerTeamId);

            // ── Broadcast via MatchHub ─────────────────────────────────────
            await matchHub.Clients.Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.StatusChanged, new
                {
                    matchId,
                    status = "completed",
                    winnerId = winnerTeamId,
                });

            // ── Broadcast via BracketHub ──────────────────────────────────
            if (match.version_id is not null)
            {
                var versionId = (Guid)match.version_id;
                await bracketHub.Clients
                    .Group(BracketHub.BracketGroup(versionId.ToString()))
                    .SendAsync(BracketHubEvents.MatchUpdated, new { versionId, matchId });
            }

            // ── Fire-and-forget: delete DatHost server ────────────────────
            // Resolve external_id BEFORE entering the background task
            string? externalId = (string?)server.external_id;

            if (!string.IsNullOrEmpty(externalId))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var dathost = scope.ServiceProvider.GetRequiredService<IDatHostService>();
                        var bgDb = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
                        var bgLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

                        try
                        {
                            await dathost.DeleteServerAsync(externalId, CancellationToken.None);
                            bgLogger.LogInformation("MatchZy series_end: DatHost server {ExternalId} deleted for match {MatchId}", externalId, matchId);
                        }
                        catch (Exception delEx)
                        {
                            bgLogger.LogWarning(delEx, "MatchZy series_end: failed to delete DatHost server {ExternalId} for match {MatchId}", externalId, matchId);
                        }

                        using var bgConn = bgDb.CreateConnection();
                        await bgConn.ExecuteAsync(
                            "UPDATE game_servers SET status = 'deleted', deleted_at = NOW(), stopped_at = NOW(), updated_at = NOW() WHERE match_id = @matchId AND status != 'deleted'",
                            new { matchId });
                    }
                    catch (Exception ex)
                    {
                        // Last-resort catch — fire-and-forget must not crash the process
                        try
                        {
                            using var scope = scopeFactory.CreateScope();
                            scope.ServiceProvider.GetRequiredService<ILogger<Program>>()
                                .LogError(ex, "MatchZy series_end: fire-and-forget server cleanup failed for match {MatchId}", matchId);
                        }
                        catch { /* Truly nothing we can do */ }
                    }
                }, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MatchZy series_end: failed for match {MatchId}", matchId);
        }
    }

    /// <summary>demo_upload_ended — Log the demo filename. Full demo storage TBD.</summary>
    private static async Task HandleDemoUploadEnded(
        System.Data.IDbConnection conn, Guid matchId, JsonElement root, ILogger logger)
    {
        try
        {
            string? filename = null;
            if (root.TryGetProperty("params", out var p) && p.TryGetProperty("filename", out var fn))
                filename = fn.GetString();

            await conn.ExecuteAsync(
                "UPDATE game_servers SET updated_at = NOW() WHERE match_id = @matchId AND status != 'deleted'",
                new { matchId });

            logger.LogInformation("MatchZy demo_upload_ended: match {MatchId}, filename={Filename}", matchId, filename ?? "(none)");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MatchZy demo_upload_ended: failed for match {MatchId}", matchId);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Private Helpers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Query team roster Steam IDs for the MatchZy config.
    /// Returns a dict of steam64_id → display_name.
    /// </summary>
    private static async Task<Dictionary<string, string>> GetTeamRosterAsync(
        System.Data.IDbConnection conn, Guid teamId)
    {
        var players = await conn.QueryAsync<dynamic>(@"
            SELECT psa.steam64_id,
                   COALESCE(psa.steam_name, p.username, 'Unknown') AS display_name
            FROM team_members tm
            JOIN player_steam_accounts psa ON psa.user_id = tm.user_id
            LEFT JOIN profiles p ON p.id = tm.user_id
            WHERE tm.team_id = @teamId AND tm.is_active = TRUE",
            new { teamId });

        var roster = new Dictionary<string, string>();
        foreach (var p in players)
        {
            string steamId = (string)p.steam64_id;
            string name = (string)p.display_name;
            roster[steamId] = name;
        }
        return roster;
    }

    /// <summary>
    /// Translate a veto side choice into a MatchZy map_sides value.
    /// "ct" → picker starts CT, "t" → picker starts T (so other team is CT), null → "knife".
    /// </summary>
    private static string TranslateSide(string picker, string? side)
    {
        if (side is null) return "knife";

        return (picker, side.ToLowerInvariant()) switch
        {
            ("team1", "ct") => "team1_ct",
            ("team1", "t") => "team2_ct", // Team1 chose T side → Team2 starts CT
            ("team2", "ct") => "team2_ct",
            ("team2", "t") => "team1_ct", // Team2 chose T side → Team1 starts CT
            _ => "knife",
        };
    }

    /// <summary>
    /// Upsert per-player stats from a MatchZy map_result event into match_player_stats.
    /// </summary>
    private static async Task UpsertPlayerStats(
        System.Data.IDbConnection conn, Guid matchId, int mapNumber, int gameNumber,
        string teamLabel, JsonElement paramsEl, ILogger logger)
    {
        try
        {
            var statsKey = $"stats_{teamLabel}";
            if (!paramsEl.TryGetProperty(statsKey, out var statsObj))
            {
                logger.LogDebug("MatchZy map_result: no {StatsKey} in params for match {MatchId}", statsKey, matchId);
                return;
            }

            if (!statsObj.TryGetProperty("players", out var playersObj) || playersObj.ValueKind != JsonValueKind.Object)
            {
                logger.LogDebug("MatchZy map_result: no players in {StatsKey} for match {MatchId}", statsKey, matchId);
                return;
            }

            // Collect all steam IDs for batch user_id lookup
            var steamIds = new List<string>();
            foreach (var prop in playersObj.EnumerateObject())
                steamIds.Add(prop.Name);

            if (steamIds.Count == 0) return;

            // Batch-resolve steam64_id → user_id
            var steamUserMap = (await conn.QueryAsync<dynamic>(
                "SELECT steam64_id, user_id FROM player_steam_accounts WHERE steam64_id = ANY(@steamIds)",
                new { steamIds = steamIds.ToArray() }))
                .ToDictionary(
                    r => (string)r.steam64_id,
                    r => (Guid?)r.user_id);

            // The map_number stored in match_player_stats uses the same convention as game_number (1-indexed)
            int storedMapNumber = gameNumber;

            foreach (var prop in playersObj.EnumerateObject())
            {
                var steam64Id = prop.Name;
                var stats = prop.Value;

                steamUserMap.TryGetValue(steam64Id, out var userId);

                var playerName = stats.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

                await conn.ExecuteAsync(@"
                    INSERT INTO match_player_stats (
                        match_id, map_number, user_id, steam64_id, team, player_name,
                        kills, deaths, assists, flash_assists, team_kills, suicides,
                        damage, utility_damage, enemies_flashed, friendlies_flashed,
                        knife_kills, headshot_kills, rounds_played,
                        bomb_defuses, bomb_plants,
                        ""1k"", ""2k"", ""3k"", ""4k"", ""5k"",
                        ""1v1"", ""1v2"", ""1v3"", ""1v4"", ""1v5"",
                        first_kills_t, first_kills_ct, first_deaths_t, first_deaths_ct,
                        trade_kills, kast, score, mvp
                    ) VALUES (
                        @matchId, @mapNumber, @userId, @steam64Id, @team, @playerName,
                        @kills, @deaths, @assists, @flashAssists, @teamKills, @suicides,
                        @damage, @utilityDamage, @enemiesFlashed, @friendliesFlashed,
                        @knifeKills, @headshotKills, @roundsPlayed,
                        @bombDefuses, @bombPlants,
                        @k1, @k2, @k3, @k4, @k5,
                        @v1v1, @v1v2, @v1v3, @v1v4, @v1v5,
                        @firstKillsT, @firstKillsCt, @firstDeathsT, @firstDeathsCt,
                        @tradeKills, @kast, @score, @mvp
                    )
                    ON CONFLICT (match_id, map_number, steam64_id) DO UPDATE SET
                        user_id            = COALESCE(@userId, match_player_stats.user_id),
                        team               = @team,
                        player_name        = @playerName,
                        kills              = @kills,
                        deaths             = @deaths,
                        assists            = @assists,
                        flash_assists      = @flashAssists,
                        team_kills         = @teamKills,
                        suicides           = @suicides,
                        damage             = @damage,
                        utility_damage     = @utilityDamage,
                        enemies_flashed    = @enemiesFlashed,
                        friendlies_flashed = @friendliesFlashed,
                        knife_kills        = @knifeKills,
                        headshot_kills     = @headshotKills,
                        rounds_played      = @roundsPlayed,
                        bomb_defuses       = @bombDefuses,
                        bomb_plants        = @bombPlants,
                        ""1k""             = @k1,
                        ""2k""             = @k2,
                        ""3k""             = @k3,
                        ""4k""             = @k4,
                        ""5k""             = @k5,
                        ""1v1""            = @v1v1,
                        ""1v2""            = @v1v2,
                        ""1v3""            = @v1v3,
                        ""1v4""            = @v1v4,
                        ""1v5""            = @v1v5,
                        first_kills_t      = @firstKillsT,
                        first_kills_ct     = @firstKillsCt,
                        first_deaths_t     = @firstDeathsT,
                        first_deaths_ct    = @firstDeathsCt,
                        trade_kills        = @tradeKills,
                        kast               = @kast,
                        score              = @score,
                        mvp                = @mvp",
                    new
                    {
                        matchId,
                        mapNumber = storedMapNumber,
                        userId,
                        steam64Id,
                        team = teamLabel,
                        playerName,
                        kills = GetIntStat(stats, "kills"),
                        deaths = GetIntStat(stats, "deaths"),
                        assists = GetIntStat(stats, "assists"),
                        flashAssists = GetIntStat(stats, "flash_assists"),
                        teamKills = GetIntStat(stats, "team_kills"),
                        suicides = GetIntStat(stats, "suicides"),
                        damage = GetIntStat(stats, "damage"),
                        utilityDamage = GetIntStat(stats, "utility_damage"),
                        enemiesFlashed = GetIntStat(stats, "enemies_flashed"),
                        friendliesFlashed = GetIntStat(stats, "friendlies_flashed"),
                        knifeKills = GetIntStat(stats, "knife_kills"),
                        headshotKills = GetIntStat(stats, "headshot_kills"),
                        roundsPlayed = GetIntStat(stats, "rounds_played"),
                        bombDefuses = GetIntStat(stats, "bomb_defuses"),
                        bombPlants = GetIntStat(stats, "bomb_plants"),
                        k1 = GetIntStat(stats, "1k"),
                        k2 = GetIntStat(stats, "2k"),
                        k3 = GetIntStat(stats, "3k"),
                        k4 = GetIntStat(stats, "4k"),
                        k5 = GetIntStat(stats, "5k"),
                        v1v1 = GetIntStat(stats, "1v1"),
                        v1v2 = GetIntStat(stats, "1v2"),
                        v1v3 = GetIntStat(stats, "1v3"),
                        v1v4 = GetIntStat(stats, "1v4"),
                        v1v5 = GetIntStat(stats, "1v5"),
                        firstKillsT = GetIntStat(stats, "first_kills_t"),
                        firstKillsCt = GetIntStat(stats, "first_kills_ct"),
                        firstDeathsT = GetIntStat(stats, "first_deaths_t"),
                        firstDeathsCt = GetIntStat(stats, "first_deaths_ct"),
                        tradeKills = GetIntStat(stats, "trade_kills"),
                        kast = GetIntStat(stats, "kast"),
                        score = GetIntStat(stats, "score"),
                        mvp = GetIntStat(stats, "mvp"),
                    });
            }

            logger.LogInformation(
                "MatchZy map_result: upserted {Count} player stats for {Team} on match {MatchId} game {GameNumber}",
                steamIds.Count, teamLabel, matchId, gameNumber);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MatchZy map_result: failed to upsert {Team} player stats for match {MatchId}", teamLabel, matchId);
        }
    }

    /// <summary>Safely extract an integer stat from a MatchZy player stats JSON element.</summary>
    private static int GetIntStat(JsonElement stats, string propertyName)
    {
        if (stats.TryGetProperty(propertyName, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number)
                return val.TryGetInt32(out var i) ? i : 0;
            if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out var parsed))
                return parsed;
        }
        return 0;
    }

    /// <summary>
    /// Parse the JSONB team picked maps array from the database.
    /// Format: [{"map_id": "uuid-string", "side": "ct"|"t"|null}, ...]
    /// </summary>
    private static PickedMapEntry[] ParsePickedMaps(object? json)
    {
        if (json is null) return [];
        var str = json.ToString() ?? "[]";
        try
        {
            return JsonSerializer.Deserialize<PickedMapEntry[]>(str, SnakeCaseJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Parse a PostgreSQL text[] or string array from Dapper's dynamic result.
    /// </summary>
    private static string[] ParseStringArray(object? arr) => arr switch
    {
        string[] s => s,
        string s => s.Trim('{', '}').Split(',', StringSplitOptions.RemoveEmptyEntries),
        _ => [],
    };

    /// <summary>Represents a picked map entry from the match_map_vetos JSONB column.</summary>
    private sealed record PickedMapEntry(string MapId, string? Side = null);
}
