using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Esportra.Core.Bracket;
using Esportra.Core.Tournaments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Esportra.Api.Endpoints;

public static class StageEndpoints
{
    public static void MapStageEndpoints(this WebApplication app)
    {
        // ── PUT /api/tournaments/{tournamentId}/stages ───────────────────────
        // Batch sync: accepts full stage array, diffs against DB, upserts/deletes.
        // Replaces useTournamentWizard's 3 sequential Supabase calls.
        app.MapPut("/api/tournaments/{tournamentId}/stages", async (
            Guid tournamentId,
            [FromBody] SyncStagesRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentWinnerService winnerService,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(
                    userCtx, tournamentId, StaffAuthHelper.PermBracketEdit, ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT organizer_id, max_teams FROM tournaments WHERE id = @tournamentId FOR UPDATE",
                new { tournamentId }, tx);
            if (tournament is null) return Results.NotFound();
            int? tournamentMaxTeams = (int?)tournament.max_teams is > 0
                ? (int)tournament.max_teams
                : null;

            // Get existing stage IDs as Guid for proper uuid comparison
            var existingGuids = (await conn.QueryAsync<Guid>(
                "SELECT id FROM tournament_stages WHERE tournament_id = @tournamentId",
                new { tournamentId }, tx)).ToHashSet();

            var stages = req.Stages ?? Array.Empty<StageDto>();
            var normalizedStages = NormalizeStageCapacities(stages, tournamentMaxTeams);
            var validationError = ValidateStageCapacities(normalizedStages, tournamentMaxTeams);
            if (validationError is not null)
            {
                tx.Rollback();
                return Results.BadRequest(new { error = validationError, message = validationError, traceId = ctx.TraceIdentifier });
            }

            var incomingGuids = normalizedStages
                .Where(s => s.Id is not null && Guid.TryParse(s.Id, out _))
                .Select(s => Guid.Parse(s.Id!))
                .ToHashSet();

            // Delete removed stages (pass Guid[] so Npgsql sends uuid[])
            var toDelete = existingGuids.Except(incomingGuids).ToArray();
            if (toDelete.Length > 0)
            {
                await ClearTournamentWinnerIfStagesContainWinnerAsync(conn, tx, winnerService, tournamentId, toDelete, ct);
                await conn.ExecuteAsync(
                    "DELETE FROM tournament_stages WHERE id = ANY(@ids)",
                    new { ids = toDelete }, tx);
            }

            // Upsert all stages
            foreach (var s in normalizedStages)
            {
                var stageGuid = s.Id is not null && Guid.TryParse(s.Id, out var parsed) ? parsed : (Guid?)null;

                var roundBoOverridesJson = s.RoundBoOverrides is { Count: > 0 }
                    ? System.Text.Json.JsonSerializer.Serialize(s.RoundBoOverrides)
                    : null;

                if (stageGuid.HasValue && existingGuids.Contains(stageGuid.Value))
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE tournament_stages
                        SET name = @name, format = @format, stage_order = @stageOrder,
                            best_of = @bestOf, bo_mode = @boMode,
                            round_bo_overrides = CASE WHEN @roundBoOverrides::text IS NOT NULL THEN @roundBoOverrides::jsonb ELSE NULL END,
                            capacity = @capacity,
                            advancement_count = @advancementCount,
                            config = CASE WHEN @config::text IS NOT NULL THEN @config::jsonb ELSE config END,
                            starts_at = @startsAt,
                            ends_at = @endsAt,
                            updated_at = NOW()
                        WHERE id = @id
                        """,
                        new
                        {
                            id = stageGuid.Value,
                            name = s.Name,
                            format = s.Format,
                            stageOrder = s.StageOrder,
                            bestOf = s.BestOf ?? 1,
                            boMode = s.BoMode ?? "per_stage",
                            roundBoOverrides = roundBoOverridesJson,
                            capacity = s.Capacity,
                            advancementCount = s.AdvancementCount,
                            config = s.Config.HasValue ? s.Config.Value.ToString() : (string?)null,
                            startsAt = s.StartsAt is not null && DateTimeOffset.TryParse(s.StartsAt, out var sa) ? sa : (DateTimeOffset?)null,
                            endsAt = s.EndsAt is not null && DateTimeOffset.TryParse(s.EndsAt, out var ea) ? ea : (DateTimeOffset?)null,
                        }, tx);
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO tournament_stages (tournament_id, name, format, stage_order, best_of, bo_mode, round_bo_overrides, capacity, advancement_count, config, starts_at, ends_at)
                        VALUES (@tournamentId, @name, @format, @stageOrder, @bestOf, @boMode,
                                CASE WHEN @roundBoOverrides::text IS NOT NULL THEN @roundBoOverrides::jsonb ELSE NULL END,
                                @capacity, @advancementCount,
                                CASE WHEN @config::text IS NOT NULL THEN @config::jsonb ELSE NULL END,
                                @startsAt, @endsAt)
                        """,
                        new
                        {
                            tournamentId,
                            name = s.Name,
                            format = s.Format,
                            stageOrder = s.StageOrder,
                            bestOf = s.BestOf ?? 1,
                            boMode = s.BoMode ?? "per_stage",
                            roundBoOverrides = roundBoOverridesJson,
                            capacity = s.Capacity,
                            advancementCount = s.AdvancementCount,
                            config = s.Config.HasValue ? s.Config.Value.ToString() : (string?)null,
                            startsAt = s.StartsAt is not null && DateTimeOffset.TryParse(s.StartsAt, out var sa2) ? sa2 : (DateTimeOffset?)null,
                            endsAt = s.EndsAt is not null && DateTimeOffset.TryParse(s.EndsAt, out var ea2) ? ea2 : (DateTimeOffset?)null,
                        }, tx);
                }
            }

            var updated = await conn.QueryAsync<dynamic>(
                "SELECT * FROM tournament_stages WHERE tournament_id = @tournamentId ORDER BY stage_order LIMIT 50",
                new { tournamentId }, tx);

            tx.Commit();

            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/tournaments/{tournamentId}/map-pools ────────────────────
        // Replace all map pool entries for a tournament.
        app.MapPut("/api/tournaments/{tournamentId}/map-pools", async (
            Guid tournamentId,
            [FromBody] SyncMapPoolsRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentAuthorizationService tournamentAuth,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, tournamentId, ct: ct))
                return Results.Forbid();

            try
            {
                using var conn = db.CreateConnection();

                await conn.ExecuteAsync(
                    "DELETE FROM tournament_map_pools WHERE tournament_id = @tournamentId",
                    new { tournamentId });

                if (req.MapIds is { Length: > 0 })
                {
                    var mapIds = req.MapIds
                        .Where(s => Guid.TryParse(s, out _))
                        .Select(Guid.Parse)
                        .ToArray();
                    if (mapIds.Length > 0)
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO tournament_map_pools (tournament_id, map_id)
                            SELECT @tournamentId, UNNEST(@mapIds::uuid[])
                            ON CONFLICT DO NOTHING
                            """,
                            new { tournamentId, mapIds });
                }

                return Results.Ok(new { success = true, count = req.MapIds?.Length ?? 0 });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error syncing map pool for tournament {TournamentId}", tournamentId);
                return Results.Json(new { error = "We couldn't sync the map pool. Please try again." }, statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/stages/{stageId}/status ──────────────────────────────
        // Deprecated: stage progress is derived from completion rules and tournament timeline.
        app.MapPatch("/api/stages/{stageId}/status", (
            Guid stageId,
            [FromBody] UpdateStageStatusRequest req) =>
        {
            _ = stageId;
            _ = req;
            return Results.Json(
                new
                {
                    error = "Manual stage status updates are deprecated. Stage progress is derived automatically from rounds, matches, and advancement.",
                },
                statusCode: StatusCodes.Status410Gone);
        }).RequireAuthorization("Authenticated");

        // ── PATCH /api/stages/{stageId}/order ───────────────────────────────
        // Update a single stage's order. Used during reorder after delete.
        app.MapPatch("/api/stages/{stageId}/order", async (
            Guid stageId,
            [FromBody] UpdateStageOrderRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify ownership via stage → tournament
            var tournamentOwnerId = await conn.ExecuteScalarAsync<Guid?>(
                """
                SELECT t.organizer_id FROM tournament_stages s
                JOIN tournaments t ON t.id = s.tournament_id
                WHERE s.id = @stageId
                """,
                new { stageId });
            if (tournamentOwnerId is null || tournamentOwnerId != userCtx.UserIdGuid) return Results.Forbid();

            await conn.ExecuteAsync(
                "UPDATE tournament_stages SET stage_order = @order, updated_at = NOW() WHERE id = @stageId",
                new { stageId, order = req.StageOrder });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/tournaments/{tournamentId}/stages/delete ─────────────
        // Delete specific stages by ID. Used by StageManagementTab.
        app.MapPost("/api/tournaments/{tournamentId}/stages/delete", async (
            Guid tournamentId,
            [FromBody] DeleteStagesRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            TournamentWinnerService winnerService,
            TournamentAuthorizationService tournamentAuth,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!await tournamentAuth.CanManageTournamentAsync(
                    userCtx, tournamentId, StaffAuthHelper.PermBracketEdit, ct))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            if (req.DeleteIds is not { Length: > 0 })
            {
                tx.Rollback();
                return Results.BadRequest(new { error = "No stage IDs provided." });
            }

            await ClearTournamentWinnerIfStagesContainWinnerAsync(conn, tx, winnerService, tournamentId, req.DeleteIds, ct);

            await conn.ExecuteAsync(
                "DELETE FROM tournament_stages WHERE id = ANY(@ids) AND tournament_id = @tournamentId",
                new { ids = req.DeleteIds, tournamentId }, tx);

            tx.Commit();

            return Results.Ok(new { success = true, deleted = req.DeleteIds.Length });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/stages/{stageId}/completion-status ─────────────────────
        app.MapGet("/api/stages/{stageId}/completion-status", async (
            Guid stageId,
            IDbConnectionFactory db,
            StandingsService standings,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            // 1. Get stage info (includes BO config for debugging)
            var stage = await conn.QuerySingleOrDefaultAsync(
                "SELECT id, tournament_id, name, format, stage_order, advancement_count, status, config, best_of, bo_mode, round_bo_overrides FROM tournament_stages WHERE id = @stageId",
                new { stageId });
            if (stage is null) return Results.NotFound("Stage not found");

            string format = ((string?)stage.format ?? "single_elimination").ToLowerInvariant();
            var alreadyAdvanced = await StageCompletionHelper.IsStageAlreadyAdvancedAsync(conn, stage, stageId);

            if (format is "battle_royale")
            {
                var brSnapshot = await StageCompletionHelper.EvaluateBattleRoyaleAsync(
                    conn, stage, stageId, alreadyAdvanced, ct);
                return Results.Ok(brSnapshot.ToResponse());
            }

            int advancementCount = (int?)stage.advancement_count ?? 1;

            // 2. Count participants
            int participantsCount;
            if ((int)stage.stage_order == 1)
            {
                participantsCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM tournament_participants WHERE tournament_id = @tid AND status != 'pending'",
                    new { tid = (Guid)stage.tournament_id });
            }
            else
            {
                participantsCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM stage_participants WHERE stage_id = @stageId",
                    new { stageId });
            }

            if (advancementCount >= participantsCount)
            {
                return Results.Ok(new
                {
                    isComplete = false,
                    alreadyAdvanced,
                    progressLabel = alreadyAdvanced ? "advanced" : "setup",
                    advancingTeams = Array.Empty<object>(),
                    reason = $"Invalid Configuration: Advancement count ({advancementCount}) must be less than participants ({participantsCount}) to ensure elimination."
                });
            }

            // 3. Get latest bracket version
            var version = await conn.QuerySingleOrDefaultAsync(
                "SELECT id FROM brkt_versions WHERE stage_id = @stageId ORDER BY version_number DESC LIMIT 1",
                new { stageId });
            if (version is null)
            {
                return Results.Ok(new
                {
                    isComplete = false,
                    alreadyAdvanced,
                    progressLabel = alreadyAdvanced ? "advanced" : "setup",
                    advancingTeams = Array.Empty<object>(),
                    reason = "No bracket found"
                });
            }

            // 4. Get all matches
            var matches = (await conn.QueryAsync(
                "SELECT * FROM brkt_matches WHERE version_id = @vid",
                new { vid = (Guid)version.id })).AsList();
            if (matches.Count == 0)
            {
                return Results.Ok(new
                {
                    isComplete = false,
                    alreadyAdvanced,
                    progressLabel = alreadyAdvanced ? "advanced" : "setup",
                    advancingTeams = Array.Empty<object>(),
                    reason = "No matches found"
                });
            }

            // 5. Check completion based on format
            var bracketProgressLabel = await StageCompletionHelper.EvaluateBracketProgressLabelAsync(conn, stage, stageId);

            if (format is "single_elimination" or "double_elimination")
            {
                var elimination = await CheckEliminationCompletion(conn, matches, advancementCount);
                return Results.Ok(MergeBracketCompletion(elimination, alreadyAdvanced, bracketProgressLabel));
            }

            if (format is "swiss" or "round_robin")
            {
                var roundRobin = await CheckRoundRobinCompletion(conn, matches, advancementCount, stageId, stage, standings, ct);
                return Results.Ok(MergeBracketCompletion(roundRobin, alreadyAdvanced, bracketProgressLabel));
            }

            return Results.Ok(new
            {
                isComplete = false,
                alreadyAdvanced,
                progressLabel = alreadyAdvanced ? "advanced" : "setup",
                advancingTeams = Array.Empty<object>(),
                reason = "Unknown format"
            });
        }).RequireAuthorization("Authenticated");


        // ── POST /api/stages/{stageId}/advance ──────────────────────────────
        app.MapPost("/api/stages/{stageId}/advance", async (
            Guid stageId,
            HttpContext ctx,
            IDbConnectionFactory db,
            StandingsService standings,
            TournamentWinnerService winnerService,
            PlacementResolutionService placementResolution,
            TournamentAuthorizationService tournamentAuth,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Get stage info (includes BO config for debugging)
            var stage = await conn.QuerySingleOrDefaultAsync(
                "SELECT id, tournament_id, name, format, stage_order, advancement_count, status, config, best_of, bo_mode, round_bo_overrides FROM tournament_stages WHERE id = @stageId",
                new { stageId });
            if (stage is null) return Results.NotFound("Stage not found");

            if (!await tournamentAuth.CanManageTournamentAsync(userCtx, (Guid)stage.tournament_id, ct: ct))
                return Results.Forbid();

            int advancementCount = (int?)stage.advancement_count ?? 1;

            // 2. Count participants
            int participantsCount;
            if ((int)stage.stage_order == 1)
            {
                participantsCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM tournament_participants WHERE tournament_id = @tid AND status != 'pending'",
                    new { tid = (Guid)stage.tournament_id });
            }
            else
            {
                participantsCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM stage_participants WHERE stage_id = @stageId",
                    new { stageId });
            }

            if (advancementCount >= participantsCount)
            {
                return Results.Ok(new { success = false, error = $"Advancement count ({advancementCount}) >= participants ({participantsCount})" });
            }

            // 3. Get latest bracket version
            var version = await conn.QuerySingleOrDefaultAsync(
                "SELECT id FROM brkt_versions WHERE stage_id = @stageId ORDER BY version_number DESC LIMIT 1",
                new { stageId });
            if (version is null)
                return Results.Ok(new { success = false, error = "No bracket found" });

            // 4. Get all matches and check completion
            var matches = (await conn.QueryAsync(
                "SELECT * FROM brkt_matches WHERE version_id = @vid",
                new { vid = (Guid)version.id })).AsList();
            if (matches.Count == 0)
                return Results.Ok(new { success = false, error = "No matches found" });

            string format = (string?)stage.format ?? "single_elimination";
            dynamic completionResult;

            if (format is "single_elimination" or "double_elimination")
                completionResult = await CheckEliminationCompletion(conn, matches, advancementCount);
            else if (format is "swiss" or "round_robin")
                completionResult = await CheckRoundRobinCompletion(conn, matches, advancementCount, stageId, stage, standings, ct);
            else
                return Results.Ok(new { success = false, error = "Unknown format" });

            bool isComplete = completionResult.isComplete;
            if (!isComplete)
                return Results.Ok(new { success = false, error = $"Stage not complete: {completionResult.reason}" });

            var advancingTeams = (List<AdvancingTeam>)completionResult.advancingTeams;
            Guid tournamentId = (Guid)stage.tournament_id;
            int stageOrder = (int)stage.stage_order;
            await EnsureMockBackingTeamsAsync(conn, tournamentId, advancingTeams.Select(t => t.TeamId));

            var tournamentStatus = await conn.ExecuteScalarAsync<string>(
                "SELECT status::text FROM tournaments WHERE id = @tournamentId",
                new { tournamentId });
            bool isDraft = tournamentStatus == "draft";

            // 5. Find next stage
            var nextStage = await conn.QuerySingleOrDefaultAsync(
                "SELECT id, name FROM tournament_stages WHERE tournament_id = @tournamentId AND stage_order = @nextOrder",
                new { tournamentId, nextOrder = stageOrder + 1 });

            if (nextStage is null)
            {
                // Final stage — mark completed
                await conn.ExecuteAsync(
                    "UPDATE tournament_stages SET status = 'completed' WHERE id = @stageId",
                    new { stageId });

                // In draft mode keep the tournament in draft so the organizer can keep
                // testing with mock teams. Winner/completion writes only happen for live tournaments.
                if (!isDraft)
                {
                    if (advancingTeams.Count > 0)
                    {
                        await winnerService.SetWinnerAsync(
                            conn,
                            tx: null,
                            tournamentId,
                            advancingTeams[0].TeamId,
                            reason: "final stage advancement completed",
                            ct);
                    }
                    else
                    {
                        await conn.ExecuteAsync(
                            "UPDATE tournaments SET status = 'completed' WHERE id = @tournamentId",
                            new { tournamentId });
                    }

                    try
                    {
                        await placementResolution.ResolveAsync(tournamentId, force: false, ct);
                    }
                    catch (Exception ex)
                    {
                        loggerFactory.CreateLogger("PrizeDistribution")
                            .LogWarning(ex, "Placement resolution failed for tournament {TournamentId}; manual resolve available.", tournamentId);
                    }
                }

                return Results.Ok(new { success = true, advancedCount = 0, isFinalStage = true });
            }

            // 6. Insert advancing teams into next stage (avoid duplicates)
            Guid nextStageId = (Guid)nextStage.id;
            var existingTeamIds = (await conn.QueryAsync<Guid>(
                "SELECT team_id FROM stage_participants WHERE stage_id = @nextStageId",
                new { nextStageId })).ToHashSet();

            var newEntries = advancingTeams
                .Where(t => !existingTeamIds.Contains(t.TeamId))
                .ToList();

            if (newEntries.Count > 0)
            {
                var ids = newEntries.Select(t => t.TeamId).ToArray();
                var stageIds = Enumerable.Repeat(nextStageId, ids.Length).ToArray();
                await conn.ExecuteAsync(
                    "INSERT INTO stage_participants (stage_id, team_id) SELECT * FROM UNNEST(@stageIds::uuid[], @ids::uuid[])",
                    new { stageIds, ids });
            }

            // 7. Update stage statuses
            await conn.ExecuteAsync(
                "UPDATE tournament_stages SET status = 'completed' WHERE id = @stageId",
                new { stageId });
            await conn.ExecuteAsync(
                "UPDATE tournament_stages SET status = 'upcoming' WHERE id = @nextStageId",
                new { nextStageId });

            return Results.Ok(new
            {
                success = true,
                nextStageId,
                advancedCount = advancingTeams.Count
            });
        }).RequireAuthorization("Authenticated");


        // ── GET /api/stages/{stageId}/next ──────────────────────────────────
        app.MapGet("/api/stages/{stageId}/next", async (
            Guid stageId,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();

            var currentStage = await conn.QuerySingleOrDefaultAsync(
                "SELECT tournament_id, stage_order FROM tournament_stages WHERE id = @stageId",
                new { stageId });
            if (currentStage is null) return Results.NotFound();

            var nextStage = await conn.QuerySingleOrDefaultAsync(
                "SELECT * FROM tournament_stages WHERE tournament_id = @tid AND stage_order = @nextOrder",
                new { tid = (Guid)currentStage.tournament_id, nextOrder = (int)currentStage.stage_order + 1 });

            return nextStage is null ? Results.NotFound() : Results.Ok(nextStage);
        });

        // ── GET /api/stages/{id} ──────────────────────────────────────────────
        app.MapGet("/api/stages/{id}", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var stage = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT ts.*,
                       t.name AS tournament_name, t.slug AS tournament_slug
                FROM tournament_stages ts
                LEFT JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.id = @id
                """, new { id });
            return stage is null ? Results.NotFound() : Results.Ok(stage);
        });

        // ── GET /api/stages/{id}/participants ─────────────────────────────────
        app.MapGet("/api/stages/{id}/participants", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var participants = await conn.QueryAsync<dynamic>(
                """
                SELECT sp.stage_id, sp.team_id, sp.seed,
                       t.name AS team_name, t.logo_url AS team_logo,
                       t.tag  AS team_tag
                FROM stage_participants sp
                JOIN teams t ON t.id = sp.team_id
                WHERE sp.stage_id = @id
                ORDER BY sp.seed ASC NULLS LAST, t.name ASC
                """, new { id });
            return Results.Ok(participants);
        });

        // ── GET /api/stages/round-structure ───────────────────────────────────
        // Returns the list of configurable rounds for a format/size combination.
        // Used by frontend to render per-round BO configuration UI.
        app.MapGet("/api/stages/round-structure", (
            string format,
            int bracketSize) =>
        {
            if (string.IsNullOrWhiteSpace(format))
                return Results.BadRequest(new { error = "format is required" });
            if (bracketSize < 2)
                return Results.BadRequest(new { error = "bracketSize must be at least 2" });

            var rounds = StageRoundConfiguration.GetRoundStructure(format, bracketSize);
            return Results.Ok(new { format, bracketSize, rounds });
        });
    }

    private static StageDto[] NormalizeStageCapacities(StageDto[] stages, int? tournamentMaxTeams)
    {
        return stages
            .OrderBy(s => s.StageOrder)
            .Select((stage, index) =>
            {
                var stageOrder = index + 1;
                var isBattleRoyale = string.Equals(stage.Format, "battle_royale", StringComparison.OrdinalIgnoreCase);
                var capacity = stageOrder == 1 && !isBattleRoyale
                    ? tournamentMaxTeams
                    : stage.Capacity ?? (isBattleRoyale ? tournamentMaxTeams : null);

                return stage with
                {
                    StageOrder = stageOrder,
                    Capacity = capacity,
                };
            })
            .ToArray();
    }

    private static string? ValidateStageCapacities(StageDto[] stages, int? tournamentMaxTeams)
    {
        for (var i = 0; i < stages.Length; i++)
        {
            var stage = stages[i];
            var isBattleRoyale = string.Equals(stage.Format, "battle_royale", StringComparison.OrdinalIgnoreCase);
            if (stage.Capacity is <= 0)
                return $"{stage.Name} capacity must be greater than zero.";

            if (isBattleRoyale)
                continue;

            if (tournamentMaxTeams is > 0 && stage.Capacity is > 0 && stage.Capacity > tournamentMaxTeams)
                return $"{stage.Name} capacity cannot exceed the tournament max capacity of {tournamentMaxTeams}.";

            if (i > 0)
            {
                var previous = stages[i - 1];
                if (previous.AdvancementCount is > 0 && stage.Capacity is > 0 && stage.Capacity > previous.AdvancementCount)
                    return $"{stage.Name} capacity cannot exceed the {previous.AdvancementCount} teams advancing from {previous.Name}.";
            }
        }

        return null;
    }

    private static async Task ClearTournamentWinnerIfStagesContainWinnerAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        TournamentWinnerService winnerService,
        Guid tournamentId,
        Guid[] stageIds,
        CancellationToken ct)
    {
        if (stageIds.Length == 0)
            return;

        var winnerCameFromStages = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.tournaments t
                JOIN public.brkt_versions v ON v.tournament_id = t.id
                JOIN public.brkt_matches m ON m.version_id = v.id
                WHERE t.id = @tournamentId
                  AND v.stage_id = ANY(@stageIds)
                  AND t.winner_id IS NOT NULL
                  AND m.winner_id = t.winner_id
            )
            """,
            new { tournamentId, stageIds }, tx);

        if (!winnerCameFromStages)
            return;

        await winnerService.ClearWinnerAsync(
            conn,
            tx,
            tournamentId,
            reopenCompleted: true,
            reason: "stage deletion removed matches containing tournament winner",
            ct);
    }

    private static object MergeBracketCompletion(object bracketCore, bool alreadyAdvanced, string progressLabel)
    {
        var dict = bracketCore.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(bracketCore));

        var isComplete = dict.TryGetValue("isComplete", out var completeValue) && completeValue is true;
        var reason = dict.TryGetValue("reason", out var reasonValue) ? reasonValue as string : null;
        var advancingTeams = dict.TryGetValue("advancingTeams", out var teamsValue) ? teamsValue : Array.Empty<object>();

        return new
        {
            isComplete,
            alreadyAdvanced,
            progressLabel,
            reason,
            advancingTeams,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private record AdvancingTeam(Guid TeamId, string TeamName, int Seed);

    private static async Task<object> CheckEliminationCompletion(
        System.Data.IDbConnection conn,
        List<dynamic> matches,
        int advancementCount)
    {
        var pendingCount = matches.Count(m => (string?)m.status is "pending" or "in_progress");
        int maxRound = matches.Max(m => (int)(m.round_index ?? 0));
        var finalRoundMatches = matches.Where(m => (int)(m.round_index ?? 0) == maxRound).ToList();

        bool allFinalComplete = finalRoundMatches.All(m =>
            (string?)m.status == "completed" || m.winner_id is not null);

        if (!allFinalComplete)
        {
            return new
            {
                isComplete = false,
                advancingTeams = new List<AdvancingTeam>(),
                reason = $"{pendingCount} matches remaining"
            };
        }

        var winnerIds = finalRoundMatches
            .OrderBy(m => (int)(m.match_number ?? 0))
            .Where(m => m.winner_id is not null)
            .Select(m => (Guid)m.winner_id)
            .Take(advancementCount)
            .ToList();

        var teams = await GetTeamInfo(conn, winnerIds);
        bool isComplete = (teams.Count <= advancementCount && teams.Count > 0) || pendingCount == 0;

        return new
        {
            isComplete,
            advancingTeams = teams,
            reason = isComplete
                ? $"{teams.Count} teams ready to advance"
                : $"{pendingCount} matches remaining"
        };
    }

    private static async Task<object> CheckRoundRobinCompletion(
        System.Data.IDbConnection conn,
        List<dynamic> matches,
        int advancementCount,
        Guid stageId,
        dynamic stage,
        StandingsService standings,
        CancellationToken ct)
    {
        int pendingCount = matches.Count(m => (string?)m.status != "completed");

        // Check Swiss Rounds target
        string? stageFormat = (string?)stage.format;
        string? configJson = stage.config?.ToString();
        int swissRounds = 0;
        if (stageFormat == "swiss" && configJson is not null)
        {
            try
            {
                var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(configJson);
                if (config?.TryGetValue("swiss_rounds", out var srVal) == true)
                    int.TryParse(srVal?.ToString(), out swissRounds);
            }
            catch { /* Config is optional JSON; default to 0 swiss_rounds on parse failure */ }
        }

        if (stageFormat == "swiss" && swissRounds > 0)
        {
            int maxRound = matches.Max(m => (int)(m.round_number ?? m.round_index + 1 ?? 1));
            if (maxRound < swissRounds)
            {
                return new
                {
                    isComplete = false,
                    advancingTeams = new List<AdvancingTeam>(),
                    reason = $"Round {maxRound} of {swissRounds} completed. Generate next round."
                };
            }
        }

        if (pendingCount > 0)
        {
            return new
            {
                isComplete = false,
                advancingTeams = new List<AdvancingTeam>(),
                reason = $"{pendingCount} matches remaining"
            };
        }

        // All matches complete — calculate advancement via standings
        // Determine group count from config (swiss_groups or group_count) or from actual match data
        int configGroupCount = 1;
        if (configJson is not null)
        {
            try
            {
                var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(configJson);
                if (config?.TryGetValue("swiss_groups", out var sgVal) == true)
                    int.TryParse(sgVal?.ToString(), out configGroupCount);
                // Round robin format uses group_count instead of swiss_groups
                if (configGroupCount <= 1 && config?.TryGetValue("group_count", out var gcVal) == true)
                    int.TryParse(gcVal?.ToString(), out configGroupCount);
            }
            catch { /* Config is optional JSON; default to 1 on parse failure */ }
        }

        // Discover actual groups from match data
        var groupIds = matches
            .Select(m => (string?)m.group_id)
            .Where(g => g is not null)
            .Distinct()
            .OrderBy(g => g)
            .ToList();

        int actualGroupCount = groupIds.Count > 0 ? groupIds.Count : configGroupCount;

        var advancingTeams = new List<AdvancingTeam>();

        if (actualGroupCount > 1 && groupIds.Count > 0)
        {
            // Per-group advancement: distribute slots fairly across all groups
            int baseAdv = advancementCount / actualGroupCount;
            int remainder = advancementCount % actualGroupCount;

            for (int i = 0; i < groupIds.Count; i++)
            {
                int countForGroup = baseAdv + (i < remainder ? 1 : 0);
                var s = await standings.CalculateStandingsAsync(stageId, groupIds[i], ct);
                advancingTeams.AddRange(s.Take(countForGroup)
                    .Select(x => new AdvancingTeam(x.TeamId, x.TeamName, x.Rank)));
            }
        }
        else
        {
            var s = await standings.CalculateStandingsAsync(stageId, ct: ct);
            advancingTeams = s.Take(advancementCount)
                .Select(x => new AdvancingTeam(x.TeamId, x.TeamName, x.Rank))
                .ToList();
        }

        return new
        {
            isComplete = true,
            advancingTeams,
            reason = $"{advancingTeams.Count} teams ready to advance"
        };
    }

    private static async Task<List<AdvancingTeam>> GetTeamInfo(
        System.Data.IDbConnection conn, List<Guid> teamIds)
    {
        if (teamIds.Count == 0) return [];

        var teams = (await conn.QueryAsync(
            """
            SELECT id, name
            FROM teams
            WHERE id = ANY(@ids)
            UNION ALL
            SELECT tp.id, COALESCE(tp.team_name, 'Mock Team') AS name
            FROM tournament_participants tp
            LEFT JOIN teams t ON t.id = tp.id
            WHERE tp.id = ANY(@ids)
              AND COALESCE(tp.is_mock, FALSE) = TRUE
              AND t.id IS NULL
            """,
            new { ids = teamIds.ToArray() })).AsList();

        return teamIds.Select((id, idx) =>
        {
            var t = teams.FirstOrDefault(x => (Guid)x.id == id);
            return new AdvancingTeam(id, t?.name ?? "Unknown", idx + 1);
        }).ToList();
    }

    private static async Task EnsureMockBackingTeamsAsync(
        System.Data.IDbConnection conn,
        Guid tournamentId,
        IEnumerable<Guid> candidateIds)
    {
        var ids = candidateIds.Distinct().ToArray();
        if (ids.Length == 0) return;

        var missingMocks = (await conn.QueryAsync<dynamic>(
            """
            SELECT tp.id,
                   COALESCE(tp.team_name, 'Mock Team') AS team_name,
                   t.game,
                   t.organizer_id,
                   COALESCE(t.team_size, 1) AS team_size
            FROM tournament_participants tp
            JOIN tournaments t ON t.id = tp.tournament_id
            LEFT JOIN teams existing ON existing.id = tp.id
            WHERE tp.tournament_id = @tournamentId
              AND COALESCE(tp.is_mock, FALSE) = TRUE
              AND tp.id = ANY(@ids)
              AND existing.id IS NULL
            """,
            new { tournamentId, ids })).AsList();

        if (missingMocks.Count > 0)
        {
            var rows = missingMocks.Select(row =>
            {
                var mockId = (Guid)row.id;
                return new TeamCreationHelper.MockTeamParams(
                    mockId,
                    (string)row.team_name,
                    TeamCreationHelper.BuildMockTag(mockId),
                    (string)row.game,
                    (Guid)row.organizer_id,
                    (int)row.team_size == 1,
                    Math.Max((int)row.team_size, 1));
            }).ToList();

            await TeamCreationHelper.UpsertMockTeamsAsync(conn, null, rows);
        }

        await conn.ExecuteAsync(
            """
            UPDATE tournament_participants
            SET team_id = id,
                updated_at = NOW()
            WHERE tournament_id = @tournamentId
              AND COALESCE(is_mock, FALSE) = TRUE
              AND id = ANY(@ids)
              AND team_id IS NULL
            """,
            new { tournamentId, ids });
    }
}

// ── Stage request records ──────────────────────────────────────────────────

public sealed record UpdateStageStatusRequest(string Status);
public sealed record DeleteStagesRequest(Guid[] DeleteIds);
public sealed record UpdateStageOrderRequest(int StageOrder);
