using System.Text.Json;
using Dapper;
using Esportra.Contracts.Requests;
using Esportra.Core.Bracket;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Replaces: worker-bracket-advancement and compute-bracket-ui-cache Edge Functions.
/// Phase 2 adds: bracket generation, BYE advance, reset, clear, standings, Swiss next-round.
/// </summary>
public static class BracketEndpoints
{
    public static void MapBracketEndpoints(this WebApplication app)
    {
        // ── POST /api/brackets/generate ───────────────────────────────────────
        // Generates a bracket graph in-memory and persists it to DB.
        app.MapPost("/api/brackets/generate", async (
            [FromBody] GenerateBracketRequest req,
            BracketPersistenceService         persistence,
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

            return Results.Ok(new
            {
                versionId  = version.Id,
                nodeCount  = graph.Nodes.Count,
                edgeCount  = graph.Edges.Count,
            });
        }).RequireAuthorization("Organizer");

        // ── POST /api/brackets/{versionId}/advance-byes ───────────────────────
        app.MapPost("/api/brackets/{versionId}/advance-byes", async (
            string                    versionId,
            BracketPersistenceService persistence,
            CancellationToken         ct) =>
        {
            int count = await persistence.AutoAdvanceByesAsync(versionId, ct);
            return Results.Ok(new { advanced = count });
        }).RequireAuthorization("Organizer");

        // ── POST /api/brackets/{versionId}/reset ──────────────────────────────
        app.MapPost("/api/brackets/{versionId}/reset", async (
            string                    versionId,
            BracketPersistenceService persistence,
            CancellationToken         ct) =>
        {
            await persistence.ResetAsync(versionId, ct);
            return Results.Ok(new { message = "Bracket reset." });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/brackets/{versionId} ─────────────────────────────────
        app.MapDelete("/api/brackets/{versionId}", async (
            string                    versionId,
            BracketPersistenceService persistence,
            CancellationToken         ct) =>
        {
            await persistence.ClearAsync(versionId, ct);
            return Results.Ok(new { message = "Bracket deleted." });
        }).RequireAuthorization("Organizer");

        // ── GET /api/brackets/{versionId}/standings ───────────────────────────
        app.MapGet("/api/brackets/{versionId}/standings", async (
            string           versionId,
            string?          groupId,
            StandingsService standingsSvc,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var stageId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT stage_id FROM public.brkt_versions WHERE id = @versionId",
                new { versionId });

            if (stageId is null) return Results.NotFound(new { error = "Version not found." });

            var standings = await standingsSvc.CalculateStandingsAsync(stageId, groupId, ct);
            return Results.Ok(standings);
        });

        // ── POST /api/swiss/next-round ────────────────────────────────────────
        app.MapPost("/api/swiss/next-round", async (
            [FromBody]      SwissNextRoundRequest req,
            SwissNextRoundService                 swissSvc,
            CancellationToken                     ct) =>
        {
            var (ok, msg) = await swissSvc.GenerateNextRoundAsync(req.StageId, req.VersionId, req.CurrentRound, ct);
            return ok
                ? Results.Ok(new { message = $"Round {req.CurrentRound + 1} generated." })
                : Results.BadRequest(new { error = msg });
        }).RequireAuthorization("Organizer");


        // ── POST /api/brackets/advance ────────────────────────────────────────
        // Replaces: worker-bracket-advancement Edge Function
        // Called by a DB webhook trigger after match completion.
        app.MapPost("/api/brackets/advance", async (
            [FromBody] AdvanceBracketRequest req,
            IDbConnectionFactory             db,
            CancellationToken                ct) =>
        {
            using var conn = db.CreateConnection();

            // Get version_id for this match
            var versionId = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT version_id FROM public.brkt_matches WHERE id = @matchId",
                new { matchId = req.MatchId });

            if (versionId is null)
                return Results.NotFound(new { error = $"Match {req.MatchId} not found." });

            // Get all advancements for this match (winner + loser edges)
            var advancements = (await conn.QueryAsync("""
                SELECT target_match_id, target_slot, type, winner_team_id, loser_team_id
                FROM public.brkt_advancements
                WHERE source_match_id = @matchId
                """, new { matchId = req.MatchId })).ToList();

            int advanced = 0;
            foreach (var adv in advancements)
            {
                string? teamId = adv.type == "winner" ? adv.winner_team_id : adv.loser_team_id;
                if (teamId is null) continue;

                // Advance team to target slot (1 = team1, 2 = team2)
                var column = adv.target_slot == 1 ? "team1_id" : "team2_id";
                await conn.ExecuteAsync(
                    $"UPDATE public.brkt_matches SET {column} = @teamId WHERE id = @targetMatchId",
                    new { teamId, targetMatchId = adv.target_match_id });

                advanced++;
            }

            // Mark event as processed
            if (!string.IsNullOrWhiteSpace(req.EventId))
            {
                await conn.ExecuteAsync(
                    "UPDATE public.match_completed_events SET status = 'processed', processed_at = NOW() WHERE id = @id",
                    new { id = req.EventId });
            }

            // Fire-and-forget: rebuild bracket UI cache
            _ = Task.Run(() => RebuildUiCacheAsync(versionId, db, ct), ct);

            return Results.Ok(new { success = true, matchId = req.MatchId, advanced });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/brackets/{versionId}/cache ──────────────────────────────
        // Replaces: compute-bracket-ui-cache Edge Function
        // Rebuilds cached_ui_state on brkt_versions.
        app.MapPost("/api/brackets/{versionId}/cache", async (
            string               versionId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var count = await RebuildUiCacheAsync(versionId, db, ct);
            return Results.Ok(new { success = true, versionId, matchCount = count });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/brackets/{versionId} ─────────────────────────────────────
        // Returns the cached UI state for a bracket version.
        app.MapGet("/api/brackets/{versionId}", async (
            string               versionId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var cached = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT cached_ui_state FROM public.brkt_versions WHERE id = @versionId",
                new { versionId });

            if (cached is null)
                return Results.NotFound(new { error = "Bracket version not found." });

            var doc = JsonDocument.Parse(cached ?? "[]");
            return Results.Ok(doc.RootElement);
        });
    }

    // ── UI cache builder (ported from compute-bracket-ui-cache) ──────────────

    private static async Task<int> RebuildUiCacheAsync(
        string versionId, IDbConnectionFactory db, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var matches = (await conn.QueryAsync("""
            SELECT m.id, m.match_number, m.round_index, m.team1_id, m.team2_id,
                   m.winner_id, m.loser_id, m.team1_score, m.team2_score,
                   m.status, m.scheduled_time, m.best_of, m.party_code,
                   m.bracket_type, m.stage_id, m.group_id, m.x_pos, m.y_pos,
                   t1.name AS team1_name, t1.logo_url AS team1_logo,
                   t2.name AS team2_name, t2.logo_url AS team2_logo
            FROM public.brkt_matches m
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

        var nextMatchMap  = advancements
            .Where(a => (string?)a.type == "winner")
            .GroupBy(a => (string)a.source_match_id)
            .ToDictionary(g => g.Key, g => (string)g.First().target_match_id);

        var loserNextMap  = advancements
            .Where(a => (string?)a.type == "loser")
            .GroupBy(a => (string)a.source_match_id)
            .ToDictionary(g => g.Key, g => (string)g.First().target_match_id);

        var uiMatches = matches.Select(m => new
        {
            id              = $"db-{m.id}",
            round           = m.round_index,
            matchNumber     = m.match_number,
            team1           = m.team1_id is null ? (object?)null : new { id = m.team1_id, name = m.team1_name, logoUrl = m.team1_logo },
            team2           = m.team2_id is null ? (object?)null : new { id = m.team2_id, name = m.team2_name, logoUrl = m.team2_logo },
            winner          = m.winner_id,
            team1_score     = m.team1_score,
            team2_score     = m.team2_score,
            status          = m.status ?? "pending",
            scheduledTime   = m.scheduled_time,
            bestOf          = m.best_of,
            partyCode       = m.party_code,
            bracketType     = m.bracket_type,
            nextMatchId     = nextMatchMap.TryGetValue((string)m.id, out var nm) ? nm : null,
            loserNextMatchId= loserNextMap.TryGetValue((string)m.id, out var lm) ? lm : null,
            stageId         = m.stage_id,
            groupId         = m.group_id,
            x               = m.x_pos,
            y               = m.y_pos,
        }).ToList();

        var json = JsonSerializer.Serialize(uiMatches);
        await conn.ExecuteAsync(
            "UPDATE public.brkt_versions SET cached_ui_state = @json::jsonb WHERE id = @versionId",
            new { json, versionId });

        return uiMatches.Count;
    }
}
