using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Esportra.Core.Bracket;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class StageEndpoints
{
    public static void MapStageEndpoints(this WebApplication app)
    {
        // ── PUT /api/tournaments/{tournamentId}/stages ───────────────────────
        // Batch sync: accepts full stage array, diffs against DB, upserts/deletes.
        // Replaces useTournamentWizard's 3 sequential Supabase calls.
        app.MapPut("/api/tournaments/{tournamentId}/stages", async (
            Guid                              tournamentId,
            [FromBody] SyncStagesRequest      req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify ownership/organizer
            var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT organizer_id FROM tournaments WHERE id = @tournamentId",
                new { tournamentId });
            if (tournament is null) return Results.NotFound();

            // Get existing stage IDs as Guid for proper uuid comparison
            var existingGuids = (await conn.QueryAsync<Guid>(
                "SELECT id FROM tournament_stages WHERE tournament_id = @tournamentId",
                new { tournamentId })).ToHashSet();

            var stages = req.Stages ?? Array.Empty<StageDto>();

            var incomingGuids = stages
                .Where(s => s.Id is not null && Guid.TryParse(s.Id, out _))
                .Select(s => Guid.Parse(s.Id!))
                .ToHashSet();

            // Delete removed stages (pass Guid[] so Npgsql sends uuid[])
            var toDelete = existingGuids.Except(incomingGuids).ToArray();
            if (toDelete.Length > 0)
            {
                await conn.ExecuteAsync(
                    "DELETE FROM tournament_stages WHERE id = ANY(@ids)",
                    new { ids = toDelete });
            }

            // Upsert all stages
            foreach (var s in stages)
            {
                var stageGuid = s.Id is not null && Guid.TryParse(s.Id, out var parsed) ? parsed : (Guid?)null;

                if (stageGuid.HasValue && existingGuids.Contains(stageGuid.Value))
                {
                    await conn.ExecuteAsync(
                        """
                        UPDATE tournament_stages
                        SET name = @name, format = @format, stage_order = @stageOrder,
                            best_of = @bestOf, capacity = @capacity,
                            advancement_count = @advancementCount, updated_at = NOW()
                        WHERE id = @id
                        """,
                        new
                        {
                            id               = stageGuid.Value,
                            name             = s.Name,
                            format           = s.Format,
                            stageOrder       = s.StageOrder,
                            bestOf           = s.BestOf ?? 1,
                            capacity         = s.Capacity,
                            advancementCount = s.AdvancementCount,
                        });
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO tournament_stages (tournament_id, name, format, stage_order, best_of, capacity, advancement_count)
                        VALUES (@tournamentId, @name, @format, @stageOrder, @bestOf, @capacity, @advancementCount)
                        """,
                        new
                        {
                            tournamentId,
                            name             = s.Name,
                            format           = s.Format,
                            stageOrder       = s.StageOrder,
                            bestOf           = s.BestOf ?? 1,
                            capacity         = s.Capacity,
                            advancementCount = s.AdvancementCount,
                        });
                }
            }

            var updated = await conn.QueryAsync<dynamic>(
                "SELECT * FROM tournament_stages WHERE tournament_id = @tournamentId ORDER BY stage_order",
                new { tournamentId });

            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/tournaments/{tournamentId}/map-pools ────────────────────
        // Replace all map pool entries for a tournament.
        app.MapPut("/api/tournaments/{tournamentId}/map-pools", async (
            Guid                              tournamentId,
            [FromBody] SyncMapPoolsRequest    req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(
                "DELETE FROM tournament_map_pools WHERE tournament_id = @tournamentId",
                new { tournamentId });

            if (req.MapIds is { Length: > 0 })
            {
                foreach (var mapId in req.MapIds)
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO tournament_map_pools (tournament_id, map_id) VALUES (@tournamentId, @mapId) ON CONFLICT DO NOTHING",
                        new { tournamentId, mapId });
                }
            }

            return Results.Ok(new { success = true, count = req.MapIds?.Length ?? 0 });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/stages/{stageId}/completion-status ─────────────────────
        app.MapGet("/api/stages/{stageId}/completion-status", async (
            Guid                stageId,
            IDbConnectionFactory db,
            StandingsService    standings,
            CancellationToken   ct) =>
        {
            using var conn = db.CreateConnection();

            // 1. Get stage info
            var stage = await conn.QuerySingleOrDefaultAsync(
                "SELECT id, tournament_id, name, format, stage_order, advancement_count, status, config FROM tournament_stages WHERE id = @stageId",
                new { stageId });
            if (stage is null) return Results.NotFound("Stage not found");

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
                    advancingTeams = Array.Empty<object>(),
                    reason = $"Invalid Configuration: Advancement count ({advancementCount}) must be less than participants ({participantsCount}) to ensure elimination."
                });
            }

            // 3. Get latest bracket version
            var version = await conn.QuerySingleOrDefaultAsync(
                "SELECT id FROM brkt_versions WHERE stage_id = @stageId ORDER BY version_number DESC LIMIT 1",
                new { stageId });
            if (version is null)
                return Results.Ok(new { isComplete = false, advancingTeams = Array.Empty<object>(), reason = "No bracket found" });

            // 4. Get all matches
            var matches = (await conn.QueryAsync(
                "SELECT * FROM brkt_matches WHERE version_id = @vid",
                new { vid = (Guid)version.id })).AsList();
            if (matches.Count == 0)
                return Results.Ok(new { isComplete = false, advancingTeams = Array.Empty<object>(), reason = "No matches found" });

            // 5. Check completion based on format
            string format = (string?)stage.format ?? "single_elimination";

            if (format is "single_elimination" or "double_elimination")
            {
                return Results.Ok(await CheckEliminationCompletion(conn, matches, advancementCount));
            }
            else if (format is "swiss" or "round_robin")
            {
                return Results.Ok(await CheckRoundRobinCompletion(conn, matches, advancementCount, stageId, stage, standings, ct));
            }

            return Results.Ok(new { isComplete = false, advancingTeams = Array.Empty<object>(), reason = "Unknown format" });
        }).RequireAuthorization("Organizer");


        // ── POST /api/stages/{stageId}/advance ──────────────────────────────
        app.MapPost("/api/stages/{stageId}/advance", async (
            Guid                stageId,
            IDbConnectionFactory db,
            StandingsService    standings,
            CancellationToken   ct) =>
        {
            using var conn = db.CreateConnection();

            // 1. Get stage info
            var stage = await conn.QuerySingleOrDefaultAsync(
                "SELECT id, tournament_id, name, format, stage_order, advancement_count, status, config FROM tournament_stages WHERE id = @stageId",
                new { stageId });
            if (stage is null) return Results.NotFound("Stage not found");

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

                if (advancingTeams.Count > 0)
                {
                    await conn.ExecuteAsync(
                        "UPDATE tournaments SET winner_id = @winnerId, status = 'completed' WHERE id = @tournamentId",
                        new { winnerId = advancingTeams[0].TeamId, tournamentId });
                }
                else
                {
                    await conn.ExecuteAsync(
                        "UPDATE tournaments SET status = 'completed' WHERE id = @tournamentId",
                        new { tournamentId });
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
        }).RequireAuthorization("Organizer");


        // ── GET /api/stages/{stageId}/next ──────────────────────────────────
        app.MapGet("/api/stages/{stageId}/next", async (
            Guid                stageId,
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
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
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
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private record AdvancingTeam(Guid TeamId, string TeamName, int Seed);

    private static async Task<object> CheckEliminationCompletion(
        System.Data.IDbConnection conn,
        List<dynamic>             matches,
        int                       advancementCount)
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
        List<dynamic>             matches,
        int                       advancementCount,
        Guid                      stageId,
        dynamic                   stage,
        StandingsService          standings,
        CancellationToken         ct)
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
            catch { }
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
        int swissGroups = 1;
        if (configJson is not null)
        {
            try
            {
                var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(configJson);
                if (config?.TryGetValue("swiss_groups", out var sgVal) == true)
                    int.TryParse(sgVal?.ToString(), out swissGroups);
            }
            catch { }
        }

        var advancingTeams = new List<AdvancingTeam>();

        if (swissGroups > 1)
        {
            var groupIds = matches
                .Select(m => (string?)m.group_id)
                .Where(g => g is not null)
                .Distinct()
                .OrderBy(g => g)
                .ToList();

            if (groupIds.Count == 0)
            {
                var s = await standings.CalculateStandingsAsync(stageId, ct: ct);
                advancingTeams = s.Take(advancementCount)
                    .Select(x => new AdvancingTeam(x.TeamId, x.TeamName, x.Rank))
                    .ToList();
            }
            else
            {
                int baseAdv = advancementCount / swissGroups;
                int remainder = advancementCount % swissGroups;

                for (int i = 0; i < groupIds.Count; i++)
                {
                    int countForGroup = baseAdv + (i < remainder ? 1 : 0);
                    var s = await standings.CalculateStandingsAsync(stageId, groupIds[i], ct);
                    advancingTeams.AddRange(s.Take(countForGroup)
                        .Select(x => new AdvancingTeam(x.TeamId, x.TeamName, x.Rank)));
                }
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
            "SELECT id, name FROM teams WHERE id = ANY(@ids)",
            new { ids = teamIds.ToArray() })).AsList();

        return teamIds.Select((id, idx) =>
        {
            var t = teams.FirstOrDefault(x => (Guid)x.id == id);
            return new AdvancingTeam(id, t?.name ?? "Unknown", idx + 1);
        }).ToList();
    }
}
