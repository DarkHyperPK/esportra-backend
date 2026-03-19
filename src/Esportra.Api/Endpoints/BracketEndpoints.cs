using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Core.Bracket;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: worker-bracket-advancement and compute-bracket-ui-cache Edge Functions.
/// Phase 2 adds: bracket generation, BYE advance, reset, clear, standings, Swiss next-round.
/// Phase 3 adds: SignalR broadcasts after bracket state changes.
/// </summary>
public static class BracketEndpoints
{
    private static readonly JsonSerializerOptions s_snakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public static void MapBracketEndpoints(this WebApplication app)
    {
        // ── POST /api/brackets/generate ───────────────────────────────────────
        app.MapPost("/api/brackets/generate", async (
            [FromBody] GenerateBracketRequest req,
            BracketPersistenceService         persistence,
            IHubContext<BracketHub>           bracketHub,
            CancellationToken                 ct) =>
        {
            IBracketGenerator generator = req.Format.ToLowerInvariant() switch
            {
                "double_elimination" => new DoubleEliminationGenerator(),
                "round_robin"        => new RoundRobinGenerator(),
                "swiss"              => new SwissGenerator(),
                _                    => new SingleEliminationGenerator(),
            };

            var teams = req.Teams.Select(t => (t.Id, t.Name)).ToList();
            var config = new BracketConfig(
                DailyStartTime:      req.DailyStartTime,
                TournamentStartDate: req.TournamentStartDate,
                SwissGroups:         req.SwissGroups,
                SwissRounds:         req.SwissRounds);

            var graph  = generator.Generate(teams, req.TournamentId, req.StageId,
                req.BestOf, req.BracketSize, req.AdvancementCount, config);

            var errors = GraphValidator.Validate(graph);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            var version = await persistence.SaveGraphAsync(graph, ct);

            // Notify tournament subscribers that a new bracket version was created
            if (req.TournamentId != Guid.Empty)
            {
                await bracketHub.Clients
                    .Group(BracketHub.TournamentGroup(req.TournamentId.ToString()))
                    .SendAsync(BracketHubEvents.VersionCreated,
                        new { versionId = version.Id, tournamentId = req.TournamentId, format = req.Format },
                        ct);
            }

            return Results.Ok(new
            {
                versionId  = version.Id,
                nodeCount  = graph.Nodes.Count,
                edgeCount  = graph.Edges.Count,
            });
        }).RequireAuthorization("Organizer");

        // ── POST /api/brackets/persist ────────────────────────────────────────
        // Used by MatchRepository.ts to save a client-generated bracket graph.
        // Frontend sends snake_case JSON — deserialize with SnakeCaseLower naming policy.
        app.MapPost("/api/brackets/persist", async (
            HttpContext                         ctx,
            BracketPersistenceService          persistence,
            IDbConnectionFactory               db,
            IHubContext<BracketHub>            bracketHub,
            CancellationToken                  ct) =>
        {
            var graph = await ctx.Request.ReadFromJsonAsync<BracketGraph>(s_snakeCase, ct);
            if (graph is null) return Results.BadRequest("Invalid bracket graph");

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Verify user is organizer of the tournament that owns this stage
            using var conn = db.CreateConnection();
            var stageId = graph.Version.StageId;
            if (stageId is not null)
            {
                var isOrganizer = await conn.QuerySingleOrDefaultAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM tournaments t
                        JOIN tournament_stages ts ON ts.tournament_id = t.id
                        WHERE ts.id = @stageId
                        AND (t.organizer_id = @userId OR t.organization_id IN (
                            SELECT id FROM organizations WHERE owner_id = @userId
                        ))
                    )
                    """, new { stageId, userId = userCtx.UserIdGuid });

                if (!isOrganizer) return Results.Forbid();
            }

            var errors = GraphValidator.Validate(graph);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            var version = await persistence.SaveGraphAsync(graph, ct);

            // Notify subscribers
            if (graph.Version.TournamentId != Guid.Empty)
            {
                await bracketHub.Clients
                    .Group(BracketHub.TournamentGroup(graph.Version.TournamentId.ToString()))
                    .SendAsync(BracketHubEvents.VersionCreated,
                        new { versionId = version.Id, tournamentId = graph.Version.TournamentId },
                        ct);
            }

            return Results.Ok(new { success = true, versionId = version.Id });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/brackets/{versionId}/advance-byes ───────────────────────
        app.MapPost("/api/brackets/{versionId}/advance-byes", async (
            Guid                      versionId,
            BracketPersistenceService persistence,
            IHubContext<BracketHub>   bracketHub,
            CancellationToken         ct) =>
        {
            int count = await persistence.AutoAdvanceByesAsync(versionId, ct);

            if (count > 0)
            {
                await bracketHub.Clients
                    .Group(BracketHub.BracketGroup(versionId.ToString()))
                    .SendAsync(BracketHubEvents.MatchUpdated,
                        new { versionId, byesAdvanced = count },
                        ct);
            }

            return Results.Ok(new { advanced = count });
        }).RequireAuthorization("Organizer");

        // ── POST /api/brackets/{versionId}/reset ──────────────────────────────
        app.MapPost("/api/brackets/{versionId}/reset", async (
            Guid                      versionId,
            BracketPersistenceService persistence,
            IHubContext<BracketHub>   bracketHub,
            CancellationToken         ct) =>
        {
            await persistence.ResetAsync(versionId, ct);

            await bracketHub.Clients
                .Group(BracketHub.BracketGroup(versionId.ToString()))
                .SendAsync(BracketHubEvents.BracketReset, new { versionId }, ct);

            return Results.Ok(new { message = "Bracket reset." });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/brackets/{versionId} ─────────────────────────────────
        app.MapDelete("/api/brackets/{versionId}", async (
            Guid                      versionId,
            BracketPersistenceService persistence,
            CancellationToken         ct) =>
        {
            await persistence.ClearAsync(versionId, ct);
            return Results.Ok(new { message = "Bracket deleted." });
        }).RequireAuthorization("Organizer");

        // ── PUT /api/brackets/{versionId} ─────────────────────────────────────
        // Update version status (draft → active → archived) and activated_at.
        app.MapPut("/api/brackets/{versionId}", async (
            Guid                 versionId,
            HttpContext           ctx,
            IDbConnectionFactory db,
            IHubContext<BracketHub> bracketHub,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>(ct);
            if (body is null) return Results.BadRequest("Invalid body");

            // Accept both camelCase and snake_case
            var status = body.TryGetValue("status", out var s) ? s?.ToString() : null;

            if (string.IsNullOrEmpty(status))
                return Results.BadRequest(new { error = "status is required" });

            var sql = status == "active"
                ? "UPDATE brkt_versions SET status = @status, activated_at = NOW() WHERE id = @versionId"
                : "UPDATE brkt_versions SET status = @status WHERE id = @versionId";

            await conn.ExecuteAsync(sql, new { versionId, status });

            // When publishing (status → active), notify captains of all ready matches
            if (status == "active")
            {
                var readyMatches = (await conn.QueryAsync<dynamic>(
                    """
                    SELECT id, team1_id, team2_id FROM brkt_matches
                    WHERE version_id = @versionId AND team1_id IS NOT NULL AND team2_id IS NOT NULL
                    """,
                    new { versionId })).AsList();

                foreach (var m in readyMatches)
                {
                    await BracketPersistenceService.NotifyMatchReadyCaptainsAsync(
                        conn, (Guid)m.id, (Guid)m.team1_id, (Guid)m.team2_id);
                }
            }

            // Notify subscribers
            var version = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT tournament_id FROM brkt_versions WHERE id = @versionId", new { versionId });
            if (version?.tournament_id is Guid tid)
            {
                await bracketHub.Clients
                    .Group(BracketHub.TournamentGroup(tid.ToString()))
                    .SendAsync(BracketHubEvents.VersionCreated, new { versionId, status }, ct);
            }

            return Results.Ok(new { success = true, versionId, status });
        }).RequireAuthorization("Organizer");

        // ── GET /api/brackets/{versionId}/standings ───────────────────────────
        app.MapGet("/api/brackets/{versionId}/standings", async (
            Guid             versionId,
            string?          groupId,
            StandingsService standingsSvc,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var stageId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT stage_id FROM public.brkt_versions WHERE id = @versionId",
                new { versionId });

            if (stageId is null) return Results.NotFound(new { error = "Version not found." });

            var standings = await standingsSvc.CalculateStandingsAsync(stageId.Value, groupId, ct);
            return Results.Ok(standings);
        });

        // ── GET /api/stages/{stageId}/standings ──────────────────────────────
        // Convenience route: frontend passes stageId directly (not versionId)
        app.MapGet("/api/stages/{stageId}/standings", async (
            Guid             stageId,
            string?          groupId,
            StandingsService standingsSvc,
            CancellationToken ct) =>
        {
            var standings = await standingsSvc.CalculateStandingsAsync(stageId, groupId, ct);
            return Results.Ok(standings);
        });

        // ── POST /api/swiss/next-round ────────────────────────────────────────
        app.MapPost("/api/swiss/next-round", async (
            [FromBody]      SwissNextRoundRequest req,
            SwissNextRoundService                 swissSvc,
            IHubContext<BracketHub>               bracketHub,
            CancellationToken                     ct) =>
        {
            var (ok, msg) = await swissSvc.GenerateNextRoundAsync(req.StageId, req.VersionId, req.CurrentRound, ct);
            if (!ok) return Results.BadRequest(new { error = msg });

            await bracketHub.Clients
                .Group(BracketHub.BracketGroup(req.VersionId.ToString()))
                .SendAsync(BracketHubEvents.MatchInserted,
                    new { versionId = req.VersionId, round = req.CurrentRound + 1 },
                    ct);

            return Results.Ok(new { message = $"Round {req.CurrentRound + 1} generated." });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/swiss/{stageId}/round/{roundNumber} ─────────────────
        app.MapDelete("/api/swiss/{stageId}/round/{roundNumber:int}", async (
            Guid                     stageId,
            int                      roundNumber,
            IDbConnectionFactory     db,
            IHubContext<BracketHub>  bracketHub,
            CancellationToken        ct) =>
        {
            using var conn = db.CreateConnection();

            // Find the version_id for this stage
            var versionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM public.brkt_versions WHERE stage_id = @stageId ORDER BY created_at DESC LIMIT 1",
                new { stageId });

            if (versionId is null)
                return Results.NotFound(new { error = "No bracket version found for this stage." });

            // Delete all matches for the given version and round_number
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM public.brkt_matches WHERE version_id = @versionId AND round_number = @roundNumber",
                new { versionId, roundNumber });

            if (deleted > 0)
            {
                await bracketHub.Clients
                    .Group(BracketHub.BracketGroup(versionId.Value.ToString()))
                    .SendAsync(BracketHubEvents.MatchDeleted,
                        new { versionId, stageId, roundNumber, deletedCount = deleted },
                        ct);
            }

            return Results.Ok(new { deletedCount = deleted });
        }).RequireAuthorization("Organizer");


        // ── POST /api/brackets/advance ────────────────────────────────────────
        // Replaces: worker-bracket-advancement Edge Function
        // Called by a DB webhook trigger after match completion.
        app.MapPost("/api/brackets/advance", async (
            [FromBody] AdvanceBracketRequest req,
            HttpContext                      ctx,
            IDbConnectionFactory             db,
            IHubContext<BracketHub>          bracketHub,
            ILogger<BracketHub>             logger,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var versionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT version_id FROM public.brkt_matches WHERE id = @matchId",
                new { matchId = req.MatchId });

            if (versionId is null)
                return Results.NotFound(new { error = $"Match {req.MatchId} not found." });

            // Verify caller is the tournament organizer
            var organizerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT t.organizer_id
                FROM brkt_versions bv
                JOIN tournament_stages ts ON ts.id = bv.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE bv.id = @versionId
                """,
                new { versionId });

            if (organizerId != userCtx.UserIdGuid)
                return Results.Forbid();

            var advancements = (await conn.QueryAsync("""
                SELECT ba.target_match_id, ba.target_slot, ba.type,
                       bm.winner_id, bm.loser_id
                FROM public.brkt_advancements ba
                JOIN public.brkt_matches bm ON bm.id = ba.source_match_id
                WHERE ba.source_match_id = @matchId
                """, new { matchId = req.MatchId })).ToList();

            // Resolve team IDs for each advancement
            var updates = advancements
                .Select(adv => new
                {
                    TargetMatchId = (Guid)adv.target_match_id,
                    TargetSlot    = (int)adv.target_slot,
                    TeamId        = (Guid?)((string)adv.type == "winner" ? adv.winner_id : adv.loser_id),
                })
                .Where(u => u.TeamId is not null)
                .ToList();

            int advanced = 0;
            if (updates.Count > 0)
            {
                // Batch all slot updates in a single UNNEST query
                var targetIds = updates.Select(u => u.TargetMatchId).ToArray();
                var slots     = updates.Select(u => u.TargetSlot).ToArray();
                var teamIds   = updates.Select(u => u.TeamId!.Value).ToArray();

                advanced = await conn.ExecuteAsync("""
                    UPDATE public.brkt_matches m
                    SET team1_id = CASE WHEN u.slot = 1 THEN u.team_id ELSE m.team1_id END,
                        team2_id = CASE WHEN u.slot = 2 THEN u.team_id ELSE m.team2_id END
                    FROM UNNEST(@targetIds::uuid[], @slots::int[], @teamIds::uuid[])
                         AS u(target_match_id, slot, team_id)
                    WHERE m.id = u.target_match_id
                    """,
                    new { targetIds, slots, teamIds });

                // Broadcast single update for the entire version
                await bracketHub.Clients
                    .Group(BracketHub.BracketGroup(versionId.Value.ToString()))
                    .SendAsync(BracketHubEvents.MatchUpdated,
                        new { versionId, matchesAdvanced = advanced },
                        ct);
            }

            if (req.EventId.HasValue)
            {
                await conn.ExecuteAsync(
                    "UPDATE public.match_completed_events SET status = 'processed', processed_at = NOW() WHERE id = @id",
                    new { id = req.EventId });
            }

            // Rebuild UI cache inline with error logging (not fire-and-forget)
            try
            {
                await RebuildUiCacheAsync(versionId.Value, db, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to rebuild UI cache for bracket {VersionId}", versionId);
            }

            return Results.Ok(new { success = true, matchId = req.MatchId, advanced });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/brackets/{versionId}/cache ──────────────────────────────
        app.MapPost("/api/brackets/{versionId}/cache", async (
            Guid                 versionId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var count = await RebuildUiCacheAsync(versionId, db, ct);
            return Results.Ok(new { success = true, versionId, matchCount = count });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/brackets/{versionId} ─────────────────────────────────────
        app.MapGet("/api/brackets/{versionId}", async (
            Guid                 versionId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var cached = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT cached_ui_state FROM public.brkt_versions WHERE id = @versionId",
                new { versionId });

            if (cached is null)
                return Results.NotFound(new { error = "Bracket version not found." });

            var doc = JsonDocument.Parse(cached);
            return Results.Ok(doc.RootElement);
        });

        // ── GET /api/brackets/{versionId}/graph ──────────────────────────────
        // Full graph structure (replaces MatchRepository.getGraphStructure)
        app.MapGet("/api/brackets/{versionId}/graph", async (
            Guid                 versionId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            var version = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM brkt_versions WHERE id = @versionId",
                new { versionId });
            if (version is null) return Results.NotFound(new { error = "Version not found." });

            var nodes = await conn.QueryAsync<dynamic>(
                """
                SELECT m.*,
                       l.x, l.y,
                       t1.name AS team1_name, t1.logo_url AS team1_logo,
                       t2.name AS team2_name, t2.logo_url AS team2_logo
                FROM brkt_matches m
                LEFT JOIN brkt_layout l ON l.match_id = m.id AND l.version_id = m.version_id
                LEFT JOIN teams t1 ON t1.id = m.team1_id
                LEFT JOIN teams t2 ON t2.id = m.team2_id
                WHERE m.version_id = @versionId
                """,
                new { versionId });

            var edges = await conn.QueryAsync<dynamic>(
                "SELECT * FROM brkt_advancements WHERE version_id = @versionId",
                new { versionId });

            return Results.Ok(new { version, nodes, edges });
        });

        // ── GET /api/brackets/{versionId}/bye-matches ────────────────────────
        // Pending matches with exactly one team (BYE matches)
        app.MapGet("/api/brackets/{versionId}/bye-matches", async (
            Guid                 versionId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT * FROM brkt_matches
                WHERE version_id = @versionId AND status = 'pending'
                  AND ((team1_id IS NOT NULL AND team2_id IS NULL)
                    OR (team1_id IS NULL AND team2_id IS NOT NULL))
                """,
                new { versionId });
            return Results.Ok(rows);
        });

        // ── GET /api/brackets/matches ─────────────────────────────────────────
        // Returns all matches for a bracket version
        app.MapGet("/api/brackets/matches", async (
            Guid?                versionId,
            Guid?                stageId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            if (versionId.HasValue)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT m.*,
                           t1.name AS team1_name, t1.logo_url AS team1_logo,
                           t2.name AS team2_name, t2.logo_url AS team2_logo
                    FROM brkt_matches m
                    LEFT JOIN teams t1 ON t1.id = m.team1_id
                    LEFT JOIN teams t2 ON t2.id = m.team2_id
                    WHERE m.version_id = @versionId
                    ORDER BY m.round_index, m.match_number
                    """, new { versionId });
                return Results.Ok(rows);
            }

            if (stageId.HasValue)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT m.*,
                           t1.name AS team1_name, t1.logo_url AS team1_logo,
                           t2.name AS team2_name, t2.logo_url AS team2_logo
                    FROM brkt_matches m
                    JOIN brkt_versions v ON v.id = m.version_id
                    LEFT JOIN teams t1 ON t1.id = m.team1_id
                    LEFT JOIN teams t2 ON t2.id = m.team2_id
                    WHERE v.stage_id = @stageId
                    ORDER BY v.version_number DESC, m.round_index, m.match_number
                    """, new { stageId });
                return Results.Ok(rows);
            }

            return Results.BadRequest(new { error = "version_id or stage_id required" });
        });

        // ── GET /api/brackets/matches/{id} ────────────────────────────────────
        app.MapGet("/api/brackets/matches/{id}", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT m.*,
                       t1.name AS team1_name, t1.logo_url AS team1_logo,
                       t2.name AS team2_name, t2.logo_url AS team2_logo,
                       ts.best_of AS stage_best_of
                FROM brkt_matches m
                LEFT JOIN teams t1 ON t1.id = m.team1_id
                LEFT JOIN teams t2 ON t2.id = m.team2_id
                LEFT JOIN brkt_versions bv ON bv.id = m.version_id
                LEFT JOIN tournament_stages ts ON ts.id = bv.stage_id
                WHERE m.id = @id
                """, new { id });
            return match is null ? Results.NotFound() : Results.Ok(match);
        });

        // ── GET /api/brackets/events ──────────────────────────────────────────
        // Returns bracket match events (scores, status changes, etc.)
        app.MapGet("/api/brackets/events", async (
            Guid?                matchId,
            Guid?                versionId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            if (matchId.HasValue)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    "SELECT * FROM brkt_match_events WHERE match_id = @matchId ORDER BY created_at DESC",
                    new { matchId });
                return Results.Ok(rows);
            }

            if (versionId.HasValue)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT e.*
                    FROM brkt_match_events e
                    JOIN brkt_matches m ON m.id = e.match_id
                    WHERE m.version_id = @versionId
                    ORDER BY e.created_at DESC
                    """, new { versionId });
                return Results.Ok(rows);
            }

            return Results.BadRequest(new { error = "match_id or version_id required" });
        });

        // ── GET /api/brackets/match-games ─────────────────────────────────────
        // Returns individual game results within a match (by matchId) or
        // all completed games for a tournament (by tournament_id + status)
        app.MapGet("/api/brackets/match-games", async (
            Guid?                matchId,
            Guid?                tournament_id,
            string?              status,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            if (tournament_id.HasValue)
            {
                var sql = """
                    SELECT g.*, gm.map_name
                    FROM brkt_match_games g
                    LEFT JOIN game_maps gm ON gm.id = g.map_id
                    JOIN brkt_matches m ON m.id = g.match_id
                    JOIN brkt_versions v ON v.id = m.version_id
                    WHERE v.tournament_id = @tournament_id
                    """;
                if (!string.IsNullOrEmpty(status))
                    sql += " AND g.status = @status";
                sql += " ORDER BY g.game_number ASC";
                var rows = await conn.QueryAsync<dynamic>(sql, new { tournament_id, status });
                DapperJsonbHelper.FixJsonb(rows);
                return Results.Ok(rows);
            }

            if (!matchId.HasValue)
                return Results.BadRequest(new { error = "match_id or tournament_id required" });

            var games = await conn.QueryAsync<dynamic>(
                "SELECT * FROM brkt_match_games WHERE match_id = @matchId ORDER BY game_number ASC",
                new { matchId });
            DapperJsonbHelper.FixJsonb(games);
            return Results.Ok(games);
        });

        // ── GET /api/brackets/versions/tournament/{tournamentId} ─────────────
        // List bracket versions for a tournament (replaces direct Supabase query)
        app.MapGet("/api/brackets/versions/tournament/{tournamentId}", async (
            Guid                 tournamentId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT id, tournament_id, stage_id, status
                FROM brkt_versions
                WHERE tournament_id = @tournamentId
                  AND status IN ('published', 'active')
                ORDER BY created_at ASC
                """,
                new { tournamentId });
            return Results.Ok(rows);
        });

        // ── GET /api/brackets/versions/{id} ──────────────────────────────────
        // Alias for GET /api/brackets/{versionId} — same data, different URL pattern
        app.MapGet("/api/brackets/versions/{id}", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            var version = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM brkt_versions WHERE id = @id", new { id });
            if (version is null) return Results.NotFound(new { error = "Version not found." });

            var nodes = await conn.QueryAsync<dynamic>(
                """
                SELECT m.*,
                       l.x, l.y,
                       t1.name AS team1_name, t1.logo_url AS team1_logo,
                       t2.name AS team2_name, t2.logo_url AS team2_logo
                FROM brkt_matches m
                LEFT JOIN brkt_layout l ON l.match_id = m.id AND l.version_id = m.version_id
                LEFT JOIN teams t1 ON t1.id = m.team1_id
                LEFT JOIN teams t2 ON t2.id = m.team2_id
                WHERE m.version_id = @id
                """, new { id });

            var edges = await conn.QueryAsync<dynamic>(
                "SELECT * FROM brkt_advancements WHERE version_id = @id", new { id });

            return Results.Ok(new { version, nodes, edges });
        });
    }

    // ── UI cache builder ──────────────────────────────────────────────────────

    private static async Task<int> RebuildUiCacheAsync(
        Guid versionId, IDbConnectionFactory db, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var matches = (await conn.QueryAsync("""
            SELECT m.id, m.match_number, m.round_index, m.team1_id, m.team2_id,
                   m.winner_id, m.loser_id, m.team1_score, m.team2_score,
                   m.status, m.scheduled_time, m.best_of, m.party_code,
                   m.bracket_type, m.group_id,
                   l.x AS x_pos, l.y AS y_pos,
                   bv.stage_id,
                   t1.name AS team1_name, t1.logo_url AS team1_logo,
                   t2.name AS team2_name, t2.logo_url AS team2_logo
            FROM public.brkt_matches m
            LEFT JOIN public.brkt_layout l ON l.match_id = m.id AND l.version_id = m.version_id
            LEFT JOIN public.brkt_versions bv ON bv.id = m.version_id
            LEFT JOIN public.teams t1 ON t1.id = m.team1_id
            LEFT JOIN public.teams t2 ON t2.id = m.team2_id
            WHERE m.version_id = @versionId
            ORDER BY m.round_index, m.match_number
            """, new { versionId })).AsList();

        var advancements = (await conn.QueryAsync("""
            SELECT source_match_id, target_match_id, type
            FROM public.brkt_advancements
            WHERE version_id = @versionId
            """, new { versionId })).AsList();

        var nextMatchMap = advancements
            .Where(a => (string?)a.type == "winner")
            .GroupBy(a => ((Guid)a.source_match_id).ToString())
            .ToDictionary(g => g.Key, g => ((Guid)g.First().target_match_id).ToString());

        var loserNextMap = advancements
            .Where(a => (string?)a.type == "loser")
            .GroupBy(a => ((Guid)a.source_match_id).ToString())
            .ToDictionary(g => g.Key, g => ((Guid)g.First().target_match_id).ToString());

        var uiMatches = matches.Select(m => new
        {
            id               = $"db-{m.id}",
            round            = m.round_index,
            matchNumber      = m.match_number,
            team1            = m.team1_id is null ? (object?)null : new { id = m.team1_id, name = m.team1_name, logoUrl = m.team1_logo },
            team2            = m.team2_id is null ? (object?)null : new { id = m.team2_id, name = m.team2_name, logoUrl = m.team2_logo },
            winner           = m.winner_id,
            team1_score      = m.team1_score,
            team2_score      = m.team2_score,
            status           = m.status ?? "pending",
            scheduledTime    = m.scheduled_time,
            bestOf           = m.best_of,
            partyCode        = m.party_code,
            bracketType      = m.bracket_type,
            nextMatchId      = nextMatchMap.TryGetValue(((Guid)m.id).ToString(), out string? nm) ? nm : null,
            loserNextMatchId = loserNextMap.TryGetValue(((Guid)m.id).ToString(), out string? lm) ? lm : null,
            stageId          = m.stage_id,
            groupId          = m.group_id,
            x                = m.x_pos,
            y                = m.y_pos,
        }).ToList();

        var json = JsonSerializer.Serialize(uiMatches);
        await conn.ExecuteAsync(
            "UPDATE public.brkt_versions SET cached_ui_state = @json::jsonb WHERE id = @versionId",
            new { json, versionId });

        return uiMatches.Count;
    }
}
