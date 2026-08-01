using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Requests;
using Esportra.Core.Bracket;
using Esportra.Core.Tournaments;
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
            HttpContext ctx,
            [FromBody] GenerateBracketRequest req,
            BracketPersistenceService persistence,
            TournamentAuthorizationService tournamentAuth,
            IHubContext<BracketHub> bracketHub,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.StageId is Guid stageId)
            {
                if (!await tournamentAuth.CanEditBracketByStageAsync(userCtx, stageId, ct))
                    return Results.Forbid();
            }
            else if (req.TournamentId != Guid.Empty)
            {
                if (!await tournamentAuth.CanManageTournamentAsync(
                        userCtx, req.TournamentId, StaffAuthHelper.PermBracketEdit, ct))
                    return Results.Forbid();
            }
            else
            {
                return Results.BadRequest(new { error = "TournamentId or StageId is required." });
            }

            IBracketGenerator generator = req.Format.ToLowerInvariant() switch
            {
                "double_elimination" => new DoubleEliminationGenerator(),
                "round_robin" => new RoundRobinGenerator(),
                "swiss" => new SwissGenerator(),
                _ => new SingleEliminationGenerator(),
            };

            var teams = req.Teams.Select(t => (t.Id, t.Name)).ToList();
            if (teams.Count < 2)
                return Results.BadRequest(new { error = "At least 2 teams are required to generate a bracket." });

            // Validate teams have at least one eligible participant row
            using var conn = db.CreateConnection();
            var teamIds = req.Teams.Select(t => t.Id).ToArray();
            var ineligibleTeams = await conn.QueryAsync<Guid>(
                """
                SELECT unnested_id AS slot_id
                FROM UNNEST(@teamIds) AS unnested_id
                WHERE NOT EXISTS (
                    SELECT 1 FROM tournament_participants tp
                    WHERE tp.tournament_id = @tournamentId
                      AND (tp.team_id = unnested_id OR tp.id = unnested_id)
                      AND tp.status::text IN ('approved', 'checked_in', 'pending')
                )
                AND EXISTS (
                    SELECT 1 FROM tournament_participants tp
                    WHERE tp.tournament_id = @tournamentId
                      AND (tp.team_id = unnested_id OR tp.id = unnested_id)
                )
                """,
                new { tournamentId = req.TournamentId, teamIds });

            var ineligibleList = ineligibleTeams.ToList();
            if (ineligibleList.Count > 0)
            {
                return Results.BadRequest(new
                {
                    error = "Some teams are not eligible for seeding (banned, cancelled, or rejected).",
                    ineligibleTeamIds = ineligibleList
                });
            }

            var config = new BracketConfig(
                DailyStartTime: req.DailyStartTime,
                TournamentStartDate: req.TournamentStartDate,
                SwissGroups: req.SwissGroups,
                SwissRounds: req.SwissRounds);

            // Validate per-round BO configuration
            var effectiveBoMode = req.BoMode ?? "per_stage";

            if (effectiveBoMode == "per_round")
            {
                if (req.RoundBoOverrides is null || req.RoundBoOverrides.Count == 0)
                {
                    return Results.BadRequest(new
                    {
                        error = "Per-round BO mode requires roundBoOverrides to be specified. " +
                                "Configure BO values for each round or use 'per_stage' mode."
                    });
                }

                // Warn about missing keys (but allow generation)
                var expectedRounds = StageRoundConfiguration.GetRoundStructure(
                    req.Format, req.BracketSize ?? teams.Count);
                var missingKeys = expectedRounds
                    .Select(r => r.Key)
                    .Where(k => !req.RoundBoOverrides.ContainsKey(k))
                    .ToList();

                if (missingKeys.Count > 0)
                {
                    Console.WriteLine($"[BracketGenerate] Warning: Missing round overrides for keys: {string.Join(", ", missingKeys)}. " +
                                      $"Using default BO={req.BestOf} for these rounds.");
                }
            }

            var roundConfig = new StageRoundConfiguration(
                req.Format,
                req.BestOf,
                effectiveBoMode,
                req.RoundBoOverrides);

            var graph = generator.Generate(
                teams,
                req.TournamentId,
                req.StageId,
                roundConfig,
                req.BracketSize,
                req.AdvancementCount,
                config);

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
                versionId = version.Id,
                nodeCount = graph.Nodes.Count,
                edgeCount = graph.Edges.Count,
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/brackets/persist ────────────────────────────────────────
        // Used by MatchRepository.ts to save a client-generated bracket graph.
        // Frontend sends snake_case JSON — deserialize with SnakeCaseLower naming policy.
        app.MapPost("/api/brackets/persist", async (
            HttpContext ctx,
            BracketPersistenceService persistence,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            IHubContext<BracketHub> bracketHub,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var graph = await ctx.Request.ReadFromJsonAsync<BracketGraph>(s_snakeCase, ct);
            if (graph is null) return Results.BadRequest("Invalid bracket graph");

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var logger = loggerFactory.CreateLogger("BracketEndpoints.Persist");

            using var conn = db.CreateConnection();
            var stageId = graph.Version.StageId;
            var tournamentId = graph.Version.TournamentId;

            if (stageId is Guid resolvedStageId)
            {
                if (!await tournamentAuth.CanEditBracketByStageAsync(userCtx, resolvedStageId, ct))
                    return Results.Forbid();
            }
            else if (tournamentId != Guid.Empty)
            {
                if (!await tournamentAuth.CanManageTournamentAsync(
                        userCtx, tournamentId, StaffAuthHelper.PermBracketEdit, ct))
                    return Results.Forbid();
            }
            else
            {
                return Results.BadRequest(new { error = "Bracket graph must include stageId or tournamentId." });
            }

            var errors = GraphValidator.Validate(graph);
            if (errors.Count > 0)
                return Results.BadRequest(new { error = "Bracket validation failed.", errors });

            Guid versionId;
            try
            {
                var version = await persistence.SaveGraphAsync(graph, ct);
                versionId = version.Id;
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                var traceId = ctx.TraceIdentifier;
                logger.LogError(ex, "Failed to persist bracket graph (trace {TraceId})", traceId);
                return Results.Json(
                    new { error = "Could not save bracket. Please try again.", traceId },
                    statusCode: 500);
            }

            // Notify subscribers
            if (graph.Version.TournamentId != Guid.Empty)
            {
                await bracketHub.Clients
                    .Group(BracketHub.TournamentGroup(graph.Version.TournamentId.ToString()))
                    .SendAsync(BracketHubEvents.VersionCreated,
                        new { versionId, tournamentId = graph.Version.TournamentId },
                        ct);
            }

            return Results.Ok(new { success = true, versionId });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/brackets/{versionId}/advance-byes ───────────────────────
        app.MapPost("/api/brackets/{versionId}/advance-byes", async (
            Guid versionId,
            HttpContext ctx,
            BracketPersistenceService persistence,
            IDbConnectionFactory db,
            IHubContext<BracketHub> bracketHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var allowed = await StaffAuthHelper.CanActOnBracketVersionAsync(
                conn, userCtx.UserIdGuid, versionId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

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
        }).RequireAuthorization("Authenticated");

        // ── POST /api/brackets/{versionId}/reset ──────────────────────────────
        app.MapPost("/api/brackets/{versionId}/reset", async (
            Guid versionId,
            HttpContext ctx,
            BracketPersistenceService persistence,
            IDbConnectionFactory db,
            IHubContext<BracketHub> bracketHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var allowed = await StaffAuthHelper.CanActOnBracketVersionAsync(
                conn, userCtx.UserIdGuid, versionId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

            await persistence.ResetAsync(versionId, ct);

            await bracketHub.Clients
                .Group(BracketHub.BracketGroup(versionId.ToString()))
                .SendAsync(BracketHubEvents.BracketReset, new { versionId }, ct);

            return Results.Ok(new { message = "Bracket reset." });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/brackets/{versionId} ─────────────────────────────────
        app.MapDelete("/api/brackets/{versionId}", async (
            Guid versionId,
            HttpContext ctx,
            BracketPersistenceService persistence,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var allowed = await StaffAuthHelper.CanActOnBracketVersionAsync(
                conn, userCtx.UserIdGuid, versionId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

            try
            {
                await persistence.ClearAsync(versionId, ct);
                return Results.Ok(new { message = "Bracket deleted." });
            }
            catch (Exception)
            {
                return Results.Json(new { error = "We couldn't delete the bracket. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/brackets/{versionId} ─────────────────────────────────────
        // Update version status (draft → active → archived) and activated_at.
        app.MapPut("/api/brackets/{versionId}", async (
            Guid versionId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<BracketHub> bracketHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnBracketVersionAsync(
                conn, userCtx.UserIdGuid, versionId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

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
        }).RequireAuthorization("Authenticated");

        // ── GET /api/brackets/{versionId}/standings ───────────────────────────
        app.MapGet("/api/brackets/{versionId}/standings", async (
            Guid versionId,
            string? groupId,
            StandingsService standingsSvc,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var stageId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT stage_id FROM public.brkt_versions WHERE id = @versionId",
                new { versionId });

            if (stageId is null) return Results.NotFound(new { error = "Bracket not found." });

            var standings = await standingsSvc.CalculateStandingsAsync(stageId.Value, groupId, ct);
            return Results.Ok(standings);
        });

        // ── GET /api/stages/{stageId}/standings ──────────────────────────────
        // Convenience route: frontend passes stageId directly (not versionId)
        app.MapGet("/api/stages/{stageId}/standings", async (
            Guid stageId,
            string? groupId,
            StandingsService standingsSvc,
            CancellationToken ct) =>
        {
            var standings = await standingsSvc.CalculateStandingsAsync(stageId, groupId, ct);
            return Results.Ok(standings);
        });

        // ── POST /api/swiss/next-round ────────────────────────────────────────
        app.MapPost("/api/swiss/next-round", async (
            [FromBody] SwissNextRoundRequest req,
            HttpContext ctx,
            SwissNextRoundService swissSvc,
            IDbConnectionFactory db,
            IHubContext<BracketHub> bracketHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var allowed = await StaffAuthHelper.CanActOnBracketVersionAsync(
                conn, userCtx.UserIdGuid, req.VersionId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx)) return Results.Forbid();

            var (ok, msg) = await swissSvc.GenerateNextRoundAsync(req.StageId, req.VersionId, req.CurrentRound, ct);
            if (!ok) return Results.BadRequest(new { error = msg });

            await bracketHub.Clients
                .Group(BracketHub.BracketGroup(req.VersionId.ToString()))
                .SendAsync(BracketHubEvents.MatchInserted,
                    new { versionId = req.VersionId, round = req.CurrentRound + 1 },
                    ct);

            return Results.Ok(new { message = $"Round {req.CurrentRound + 1} generated." });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/swiss/{stageId}/round/{roundNumber} ─────────────────
        app.MapDelete("/api/swiss/{stageId}/round/{roundNumber:int}", async (
            Guid stageId,
            int roundNumber,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentWinnerService winnerService,
            IHubContext<BracketHub> bracketHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var allowed = await StaffAuthHelper.CanActOnStageAsync(
                conn, userCtx.UserIdGuid, stageId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            using var tx = conn.BeginTransaction();

            // Find the version_id for this stage
            var version = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, tournament_id FROM public.brkt_versions WHERE stage_id = @stageId ORDER BY created_at DESC LIMIT 1",
                new { stageId }, tx);

            if (version is null)
            {
                tx.Rollback();
                return Results.NotFound(new { error = "No bracket found for this stage." });
            }

            var versionId = (Guid)version.id;
            var tournamentId = (Guid)version.tournament_id;
            var matchIds = (await conn.QueryAsync<Guid>(
                "SELECT id FROM public.brkt_matches WHERE version_id = @versionId AND round_number = @roundNumber",
                new { versionId, roundNumber }, tx)).ToArray();

            await ClearTournamentWinnerIfMatchesContainWinnerAsync(conn, tx, winnerService, tournamentId, matchIds, ct);
            await DeleteMatchDerivedRowsAsync(conn, tx, matchIds);

            // Delete all matches for the given version and round_number
            var deleted = await conn.ExecuteAsync(
                "DELETE FROM public.brkt_matches WHERE version_id = @versionId AND round_number = @roundNumber",
                new { versionId, roundNumber }, tx);

            tx.Commit();

            if (deleted > 0)
            {
                await bracketHub.Clients
                    .Group(BracketHub.BracketGroup(versionId.ToString()))
                    .SendAsync(BracketHubEvents.MatchDeleted,
                        new { versionId, stageId, roundNumber, deletedCount = deleted },
                        ct);
            }

            return Results.Ok(new { deletedCount = deleted });
        }).RequireAuthorization("Authenticated");


        // ── POST /api/brackets/advance ────────────────────────────────────────
        // Replaces: worker-bracket-advancement Edge Function
        // Called by a DB webhook trigger after match completion.
        app.MapPost("/api/brackets/advance", async (
            [FromBody] AdvanceBracketRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<BracketHub> bracketHub,
            ILogger<BracketHub> logger,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var versionId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT version_id FROM public.brkt_matches WHERE id = @matchId",
                new { matchId = req.MatchId });

            if (versionId is null)
                return Results.NotFound(new { error = $"Match {req.MatchId} not found." });

            // Verify caller has bracket:edit access
            var allowed = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, req.MatchId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            var advancements = (await conn.QueryAsync("""
                SELECT ba.target_match_id, ba.target_slot, ba.type,
                       bm.winner_id, bm.loser_id, bm.team1_id, bm.team2_id,
                       bm.team1_seed, bm.team2_seed
                FROM public.brkt_advancements ba
                JOIN public.brkt_matches bm ON bm.id = ba.source_match_id
                WHERE ba.source_match_id = @matchId
                """, new { matchId = req.MatchId })).ToList();

            // Resolve team IDs and seeds for each advancement
            var updates = advancements
                .Select(adv =>
                {
                    bool isWinner = (string)adv.type == "winner";
                    Guid? teamId = isWinner ? (Guid?)adv.winner_id : (Guid?)adv.loser_id;
                    int? teamSeed = null;
                    if (teamId is not null)
                    {
                        // Determine which slot the advancing team came from
                        if ((Guid?)adv.team1_id == teamId)
                            teamSeed = (int?)adv.team1_seed;
                        else if ((Guid?)adv.team2_id == teamId)
                            teamSeed = (int?)adv.team2_seed;
                    }
                    return new
                    {
                        TargetMatchId = (Guid)adv.target_match_id,
                        TargetSlot = (int)adv.target_slot,
                        TeamId = teamId,
                        TeamSeed = teamSeed,
                    };
                })
                .Where(u => u.TeamId is not null)
                .ToList();

            int advanced = 0;
            if (updates.Count > 0)
            {
                // Batch all slot updates in a single UNNEST query
                var targetIds = updates.Select(u => u.TargetMatchId).ToArray();
                var slots = updates.Select(u => u.TargetSlot).ToArray();
                var teamIds = updates.Select(u => u.TeamId!.Value).ToArray();
                var teamSeeds = updates.Select(u => u.TeamSeed).ToArray();

                advanced = await conn.ExecuteAsync("""
                    UPDATE public.brkt_matches m
                    SET team1_id = CASE WHEN u.slot = 1 THEN u.team_id ELSE m.team1_id END,
                        team2_id = CASE WHEN u.slot = 2 THEN u.team_id ELSE m.team2_id END,
                        team1_seed = CASE WHEN u.slot = 1 THEN u.team_seed ELSE m.team1_seed END,
                        team2_seed = CASE WHEN u.slot = 2 THEN u.team_seed ELSE m.team2_seed END
                    FROM UNNEST(@targetIds::uuid[], @slots::int[], @teamIds::uuid[], @teamSeeds::int[])
                         AS u(target_match_id, slot, team_id, team_seed)
                    WHERE m.id = u.target_match_id
                    """,
                    new { targetIds, slots, teamIds, teamSeeds });

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
            Guid versionId,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanEditBracketByVersionAsync(userCtx, versionId, ct))
                return Results.Forbid();

            var count = await RebuildUiCacheAsync(versionId, db, ct);
            return Results.Ok(new { success = true, versionId, matchCount = count });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/brackets/{versionId} ─────────────────────────────────────
        app.MapGet("/api/brackets/{versionId}", async (
            Guid versionId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var cached = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT cached_ui_state FROM public.brkt_versions WHERE id = @versionId",
                new { versionId });

            if (cached is null)
                return Results.NotFound(new { error = "Bracket not found." });

            var doc = JsonDocument.Parse(cached);
            return Results.Ok(doc.RootElement);
        });

        // ── GET /api/brackets/{versionId}/graph ──────────────────────────────
        // Full graph structure (replaces MatchRepository.getGraphStructure)
        app.MapGet("/api/brackets/{versionId}/graph", async (
            Guid versionId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            var version = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM brkt_versions WHERE id = @versionId",
                new { versionId });
            if (version is null) return Results.NotFound(new { error = "Bracket not found." });

            var nodes = await conn.QueryAsync<dynamic>(
                $"""
                SELECT m.*,
                       l.x, l.y,
                       {BracketTeamResolutionSql.Team1Columns},
                       {BracketTeamResolutionSql.Team2Columns}
                FROM brkt_matches m
                LEFT JOIN brkt_layout l ON l.match_id = m.id AND l.version_id = m.version_id
                {BracketTeamResolutionSql.Team1Joins}
                {BracketTeamResolutionSql.Team2Joins}
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
            Guid versionId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid? versionId,
            Guid? stageId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            if (versionId.HasValue)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    $"""
                    SELECT m.*,
                           {BracketTeamResolutionSql.Team1Columns},
                           {BracketTeamResolutionSql.Team2Columns}
                    FROM brkt_matches m
                    {BracketTeamResolutionSql.Team1Joins}
                    {BracketTeamResolutionSql.Team2Joins}
                    WHERE m.version_id = @versionId
                    ORDER BY m.round_index, m.match_number
                    """, new { versionId });
                return Results.Ok(rows);
            }

            if (stageId.HasValue)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    $"""
                    SELECT m.*,
                           {BracketTeamResolutionSql.Team1Columns},
                           {BracketTeamResolutionSql.Team2Columns}
                    FROM brkt_matches m
                    JOIN brkt_versions v ON v.id = m.version_id
                    {BracketTeamResolutionSql.Team1Joins}
                    {BracketTeamResolutionSql.Team2Joins}
                    WHERE v.stage_id = @stageId
                    ORDER BY v.version_number DESC, m.round_index, m.match_number
                    """, new { stageId });
                return Results.Ok(rows);
            }

            return Results.BadRequest(new { error = "Please provide a version or stage identifier." });
        });

        // ── GET /api/brackets/matches/{id} ────────────────────────────────────
        app.MapGet("/api/brackets/matches/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            if (!await StaffAuthHelper.CanAccessMatchRoomAsync(conn, userCtx.UserIdGuid, id, userCtx))
                return Results.Forbid();

            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                $"""
                SELECT m.*,
                       {BracketTeamResolutionSql.Team1Columns},
                       {BracketTeamResolutionSql.Team2Columns},
                       COALESCE(m.best_of, ts.best_of) AS stage_best_of
                FROM brkt_matches m
                {BracketTeamResolutionSql.Team1Joins}
                {BracketTeamResolutionSql.Team2Joins}
                LEFT JOIN brkt_versions bv ON bv.id = m.version_id
                LEFT JOIN tournament_stages ts ON ts.id = bv.stage_id
                WHERE m.id = @id
                """, new { id });
            return match is null ? Results.NotFound() : Results.Ok(match);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/brackets/events ──────────────────────────────────────────
        // Returns bracket match events (scores, status changes, etc.)
        app.MapGet("/api/brackets/events", async (
            Guid? matchId,
            Guid? versionId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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

            return Results.BadRequest(new { error = "Please provide a match or version identifier." });
        });

        // ── GET /api/brackets/match-games ─────────────────────────────────────
        // Returns individual game results within a match (by matchId) or
        // all completed games for a tournament (by tournament_id + status)
        app.MapGet("/api/brackets/match-games", async (
            Guid? matchId,
            Guid? tournament_id,
            string? status,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid tournamentId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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

        // ── GET /api/brackets/debug/round-config ─────────────────────────────
        // Debug endpoint to inspect per-round BO configuration for a stage
        app.MapGet("/api/brackets/debug/round-config", async (
            Guid stageId,
            IDbConnectionFactory db,
            CancellationToken ct) => await GetRoundConfigDebugAsync(stageId, db, ct));

        // ── GET /api/brackets/versions/{id} ──────────────────────────────────
        // Alias for GET /api/brackets/{versionId} — same data, different URL pattern
        app.MapGet("/api/brackets/versions/{id}", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            var version = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM brkt_versions WHERE id = @id", new { id });
            if (version is null) return Results.NotFound(new { error = "Bracket not found." });

            var nodes = await conn.QueryAsync<dynamic>(
                $"""
                SELECT m.*,
                       l.x, l.y,
                       {BracketTeamResolutionSql.Team1Columns},
                       {BracketTeamResolutionSql.Team2Columns}
                FROM brkt_matches m
                LEFT JOIN brkt_layout l ON l.match_id = m.id AND l.version_id = m.version_id
                {BracketTeamResolutionSql.Team1Joins}
                {BracketTeamResolutionSql.Team2Joins}
                WHERE m.version_id = @id
                """, new { id });

            var edges = await conn.QueryAsync<dynamic>(
                "SELECT * FROM brkt_advancements WHERE version_id = @id", new { id });

            return Results.Ok(new { version, nodes, edges });
        });

        // ── PATCH /api/brackets/matches/{matchId}/best-of ────────────────────────
        // Allow organizers to override BO format on individual matches after bracket generation
        app.MapPatch("/api/brackets/matches/{matchId}/best-of", async (
            Guid matchId,
            [FromBody] UpdateMatchBestOfRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<BracketHub> bracketHub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT m.id, m.status, m.best_of, v.tournament_id, v.id AS version_id
                FROM brkt_matches m
                JOIN brkt_versions v ON v.id = m.version_id
                WHERE m.id = @matchId
                """, new { matchId });

            if (match is null) return Results.NotFound(new { error = "Match not found." });

            var tournamentId = (Guid)match.tournament_id;
            var allowed = await StaffAuthHelper.CanActOnBracketMatchAsync(
                conn, userCtx.UserIdGuid, matchId, StaffAuthHelper.PermBracketEdit);
            if (!allowed && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            if ((string)match.status == "completed")
                return Results.BadRequest(new { error = "Cannot change BO format on completed matches." });

            if (req.BestOf is not (1 or 3 or 5))
                return Results.BadRequest(new { error = "BestOf must be 1, 3, or 5." });

            var vetoInProgress = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM match_map_vetos WHERE match_id = @matchId AND status = 'in_progress')",
                new { matchId });
            if (vetoInProgress)
                return Results.BadRequest(new { error = "Cannot change BO format while veto is in progress." });

            await conn.ExecuteAsync(
                "UPDATE brkt_matches SET best_of = @bestOf WHERE id = @matchId",
                new { bestOf = req.BestOf, matchId });

            await bracketHub.Clients
                .Group(BracketHub.BracketGroup(((Guid)match.version_id).ToString()))
                .SendAsync(BracketHubEvents.MatchUpdated,
                    new { matchId, bestOf = req.BestOf },
                    ct);

            return Results.Ok(new { success = true, matchId, bestOf = req.BestOf });
        }).RequireAuthorization("Authenticated");
    }

    private static async Task ClearTournamentWinnerIfMatchesContainWinnerAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        TournamentWinnerService winnerService,
        Guid tournamentId,
        Guid[] matchIds,
        CancellationToken ct)
    {
        if (matchIds.Length == 0)
            return;

        var winnerCameFromMatches = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.tournaments t
                JOIN public.brkt_matches m ON m.id = ANY(@matchIds)
                WHERE t.id = @tournamentId
                  AND t.winner_id IS NOT NULL
                  AND m.winner_id = t.winner_id
            )
            """,
            new { tournamentId, matchIds }, tx);

        if (!winnerCameFromMatches)
            return;

        await winnerService.ClearWinnerAsync(
            conn,
            tx,
            tournamentId,
            reopenCompleted: true,
            reason: "bracket round deletion removed matches containing tournament winner",
            ct);
    }

    private static async Task DeleteMatchDerivedRowsAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid[] matchIds)
    {
        if (matchIds.Length == 0)
            return;

        await conn.ExecuteAsync(
            """
            DELETE FROM public.dispute_comments dc
            USING public.tournament_disputes td
            WHERE dc.dispute_id = td.id
              AND td.match_id = ANY(@matchIds)
            """,
            new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.tournament_disputes WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.match_disputes WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.match_result_reports WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.match_completed_events WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.brkt_match_games WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
        await conn.ExecuteAsync("DELETE FROM public.brkt_match_events WHERE match_id = ANY(@matchIds)", new { matchIds }, tx);
    }

    // ── UI cache builder ──────────────────────────────────────────────────────

    private static async Task<int> RebuildUiCacheAsync(
        Guid versionId, IDbConnectionFactory db, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var matches = (await conn.QueryAsync($"""
            SELECT m.id, m.match_number, m.round_index, m.team1_id, m.team2_id,
                   m.winner_id, m.loser_id, m.team1_score, m.team2_score,
                   m.status, m.scheduled_time, m.best_of, m.party_code,
                   m.bracket_type, m.group_id,
                   l.x AS x_pos, l.y AS y_pos,
                   bv.stage_id,
                   {BracketTeamResolutionSql.Team1Columns},
                   {BracketTeamResolutionSql.Team2Columns}
            FROM public.brkt_matches m
            LEFT JOIN public.brkt_layout l ON l.match_id = m.id AND l.version_id = m.version_id
            LEFT JOIN public.brkt_versions bv ON bv.id = m.version_id
            {BracketTeamResolutionSql.Team1Joins}
            {BracketTeamResolutionSql.Team2Joins}
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
            id = $"db-{m.id}",
            round = m.round_index,
            matchNumber = m.match_number,
            team1 = m.team1_id is null ? (object?)null : new { id = m.team1_id, name = m.team1_name, logoUrl = m.team1_logo, seed = (int?)m.team1_seed },
            team2 = m.team2_id is null ? (object?)null : new { id = m.team2_id, name = m.team2_name, logoUrl = m.team2_logo, seed = (int?)m.team2_seed },
            winner = m.winner_id,
            team1_score = m.team1_score,
            team2_score = m.team2_score,
            status = m.status ?? "pending",
            scheduledTime = m.scheduled_time,
            bestOf = m.best_of,
            partyCode = m.party_code,
            bracketType = m.bracket_type,
            nextMatchId = nextMatchMap.TryGetValue(((Guid)m.id).ToString(), out string? nm) ? nm : null,
            loserNextMatchId = loserNextMap.TryGetValue(((Guid)m.id).ToString(), out string? lm) ? lm : null,
            stageId = m.stage_id,
            groupId = m.group_id,
            x = m.x_pos,
            y = m.y_pos,
        }).ToList();

        var json = JsonSerializer.Serialize(uiMatches);
        await conn.ExecuteAsync(
            "UPDATE public.brkt_versions SET cached_ui_state = @json::jsonb WHERE id = @versionId",
            new { json, versionId });

        return uiMatches.Count;
    }

    /// <summary>
    /// Debug endpoint to inspect per-round BO configuration for a stage.
    /// Returns the exact configuration that would be used during bracket generation.
    /// </summary>
    public static async Task<IResult> GetRoundConfigDebugAsync(
        Guid stageId,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var stage = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT format, capacity, best_of, bo_mode, round_bo_overrides
            FROM tournament_stages
            WHERE id = @stageId
            """,
            new { stageId });

        if (stage is null)
            return Results.NotFound(new { error = "Stage not found" });

        string format = ((string?)stage.format ?? "single_elimination").ToLowerInvariant();
        int capacity = (int?)stage.capacity ?? 8;
        int defaultBestOf = (int?)stage.best_of ?? 1;
        string boMode = (string?)stage.bo_mode ?? "per_stage";

        Dictionary<string, int>? overrides = null;
        if (stage.round_bo_overrides is not null)
        {
            var jsonStr = stage.round_bo_overrides.ToString();
            if (!string.IsNullOrWhiteSpace(jsonStr) && jsonStr != "{}")
            {
                overrides = JsonSerializer.Deserialize<Dictionary<string, int>>(jsonStr);
            }
        }

        var roundStructure = StageRoundConfiguration.GetRoundStructure(format, capacity);

        var resolvedRounds = roundStructure.Select(r => new
        {
            r.Key,
            r.Label,
            r.BracketType,
            ConfiguredBestOf = overrides?.GetValueOrDefault(r.Key),
            EffectiveBestOf = boMode == "per_round" && overrides?.ContainsKey(r.Key) == true
                ? overrides[r.Key]
                : defaultBestOf,
            HasOverride = overrides?.ContainsKey(r.Key) ?? false
        }).ToList();

        var warnings = new List<string>();
        if (boMode == "per_round")
        {
            if (overrides is null || overrides.Count == 0)
            {
                warnings.Add("Per-round mode enabled but no overrides configured - bracket generation will fail.");
            }
            else
            {
                var missingKeys = roundStructure
                    .Select(r => r.Key)
                    .Where(k => !overrides.ContainsKey(k))
                    .ToList();
                if (missingKeys.Count > 0)
                {
                    warnings.Add($"Missing overrides for rounds: {string.Join(", ", missingKeys)}. " +
                                 $"These rounds will use default BO={defaultBestOf}.");
                }
            }
        }

        return Results.Ok(new
        {
            stageId,
            format,
            capacity,
            defaultBestOf,
            boMode,
            configuredOverrides = overrides,
            roundStructure = resolvedRounds,
            warnings
        });
    }
}
