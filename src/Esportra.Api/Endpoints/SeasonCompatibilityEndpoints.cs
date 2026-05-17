using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

public static class SeasonCompatibilityEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void MapSeasonCompatibilityEndpoints(this WebApplication app)
    {
        app.MapPut("/api/seasons/{id:guid}/points-rules", async (Guid id, [FromBody] JsonElement body, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            var req = ReadArray<SeasonRuleDraft>(body, "rules");
            if (req.Count > SeasonConstants.MaxRuleBatchSize) return Results.BadRequest(new { error = "Cannot sync more than 1000 rules at once." });
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("DELETE FROM public.season_point_rules WHERE season_id = @id", new { id }, tx);
            foreach (var rule in req)
            {
                var placementStart = rule.PlacementFrom ?? rule.PlacementStart ?? 1;
                var placementEnd = rule.PlacementTo ?? rule.PlacementEnd ?? placementStart;
                if (placementStart <= 0 || placementEnd < placementStart)
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = "Invalid placement range." });
                }

                await InsertPointRuleAsync(conn, tx, id, rule, placementStart, placementEnd);
            }
            tx.Commit();
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.rules.sync", id, "Season", new { ruleCount = req.Count }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { success = true, ruleCount = req.Count });
        }).RequireAuthorization("Organizer");

        app.MapGet("/api/seasons/{id:guid}/point-rules", async (Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewSeasonAsync(conn, id, ctx.Items["UserContext"] as UserContext)) return Results.Forbid();
            var rows = await conn.QueryAsync<PointRuleRow>(
                """
                SELECT id, season_id, tournament_id, placement_start, placement_end, points,
                       qualification_status, destination_tournament_id, version, created_at
                FROM public.season_point_rules
                WHERE season_id = @id
                ORDER BY placement_start ASC, created_at ASC
                """,
                new { id });
            return Results.Ok(rows);
        });

        app.MapPost("/api/seasons/{id:guid}/point-rules", async (Guid id, [FromBody] SeasonRuleDraft rule, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var placementStart = rule.PlacementFrom ?? rule.PlacementStart ?? 1;
            var placementEnd = rule.PlacementTo ?? rule.PlacementEnd ?? placementStart;
            if (placementStart <= 0 || placementEnd < placementStart) return Results.BadRequest(new { error = "Invalid placement range." });
            var row = await InsertPointRuleAsync(conn, null, id, rule, placementStart, placementEnd);
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.point_rule.create", id, "Season", new { ruleId = row.Id }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(row);
        }).RequireAuthorization("Organizer");

        app.MapDelete("/api/seasons/{id:guid}/point-rules/{ruleId:guid}", async (Guid id, Guid ruleId, HttpContext ctx, IDbConnectionFactory db, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var affected = await conn.ExecuteAsync("DELETE FROM public.season_point_rules WHERE season_id = @id AND id = @ruleId", new { id, ruleId });
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return affected == 0 ? Results.NotFound() : Results.Ok(new { deleted = true });
        }).RequireAuthorization("Organizer");

        app.MapGet("/api/seasons/{id:guid}/advancement-rules", async (Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewSeasonAsync(conn, id, ctx.Items["UserContext"] as UserContext)) return Results.Forbid();
            var rows = await conn.QueryAsync<AdvancementRuleRow>(
                """
                SELECT id, season_id, source_tournament_id, target_tournament_id,
                       placement_start, placement_end, advancement_count, seed_mode, version, created_at
                FROM public.season_advancement_rules
                WHERE season_id = @id
                ORDER BY created_at ASC
                """,
                new { id });
            return Results.Ok(rows);
        });

        app.MapPost("/api/seasons/{id:guid}/advancement-rules", async (Guid id, [FromBody] SeasonAdvancementRuleDraft rule, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var placementStart = rule.PlacementStart ?? rule.PlacementFrom ?? 1;
            var placementEnd = rule.PlacementEnd ?? rule.PlacementTo ?? placementStart;
            if (placementStart <= 0 || placementEnd < placementStart || rule.AdvancementCount <= 0) return Results.BadRequest(new { error = "Invalid advancement rule." });
            var row = await conn.QuerySingleAsync<AdvancementRuleRow>(
                """
                INSERT INTO public.season_advancement_rules
                    (season_id, source_tournament_id, target_tournament_id, source_node_id, target_node_id,
                     placement_start, placement_end, advancement_count, seed_mode)
                VALUES
                    (@seasonId, @sourceTournamentId, @targetTournamentId, @sourceNodeId, @targetNodeId,
                     @placementStart, @placementEnd, @advancementCount, @seedMode)
                RETURNING id, season_id, source_tournament_id, target_tournament_id,
                          placement_start, placement_end, advancement_count, seed_mode, version, created_at
                """,
                new
                {
                    seasonId = id,
                    rule.SourceTournamentId,
                    rule.TargetTournamentId,
                    rule.SourceNodeId,
                    rule.TargetNodeId,
                    placementStart,
                    placementEnd,
                    rule.AdvancementCount,
                    seedMode = NormalizeSeedMode(rule.SeedMode)
                });
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.advancement_rule.create", id, "Season", new { ruleId = row.Id }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(row);
        }).RequireAuthorization("Organizer");

        app.MapDelete("/api/seasons/{id:guid}/advancement-rules/{ruleId:guid}", async (Guid id, Guid ruleId, HttpContext ctx, IDbConnectionFactory db, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var affected = await conn.ExecuteAsync("DELETE FROM public.season_advancement_rules WHERE season_id = @id AND id = @ruleId", new { id, ruleId });
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return affected == 0 ? Results.NotFound() : Results.Ok(new { deleted = true });
        }).RequireAuthorization("Organizer");

        app.MapPost("/api/seasons/{id:guid}/standings/recalculate", async (Guid id, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, Esportra.Api.Services.SeasonStandingsSyncService seasonSync, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            await seasonSync.RecalculateSeasonStandingsAsync(id, cache, ct);
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.standings.recalculate", id, "Season", new { }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        app.MapPut("/api/seasons/{id:guid}/participants/{participantId:guid}", async (Guid id, Guid participantId, [FromBody] UpdateSeasonParticipantRequest req, HttpContext ctx, IDbConnectionFactory db, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var status = NormalizeParticipantStatus(req.Status);
            if (req.Notes?.Length > 500) return Results.BadRequest(new { error = "Notes must be 500 characters or fewer." });
            var row = await conn.QuerySingleOrDefaultAsync<SeasonParticipantRow>(
                """
                UPDATE public.season_participants
                SET status = COALESCE(@status, status), notes = COALESCE(@notes, notes), updated_at = NOW()
                WHERE id = @participantId AND season_id = @id
                RETURNING id, season_id, team_id, team_name, team_logo_url, team_slug, status, registered_by, notes, created_at, updated_at
                """,
                new { id, participantId, status, req.Notes });
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Organizer");

        app.MapPut("/api/tournaments/{tournamentId:guid}/season", async (Guid tournamentId, [FromBody] LinkTournamentToSeasonRequest req, HttpContext ctx, IDbConnectionFactory db, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, req.SeasonId, userCtx)) return Results.Forbid();
            var role = SeasonEndpointHelpers.NormalizeTournamentRole(req.SeasonRole);
            await conn.ExecuteAsync(
                """
                INSERT INTO public.season_tournaments (season_id, tournament_id, season_role, season_stage_order)
                VALUES (@seasonId, @tournamentId, @role, @seasonStageOrder)
                ON CONFLICT (season_id, tournament_id) DO UPDATE SET
                    season_role = EXCLUDED.season_role,
                    season_stage_order = EXCLUDED.season_stage_order
                """,
                new { req.SeasonId, tournamentId, role, req.SeasonStageOrder });
            await conn.ExecuteAsync(
                """
                UPDATE public.season_nodes
                SET linked_tournament_id = @tournamentId, updated_at = NOW()
                WHERE season_id = @seasonId AND linked_tournament_id IS NULL AND node_type <> 'root'
                  AND id = (SELECT id FROM public.season_nodes WHERE season_id = @seasonId AND linked_tournament_id IS NULL AND node_type <> 'root' ORDER BY display_order ASC LIMIT 1)
                """,
                new { req.SeasonId, tournamentId });
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, req.SeasonId, ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        app.MapDelete("/api/tournaments/{tournamentId:guid}/season", async (Guid tournamentId, HttpContext ctx, IDbConnectionFactory db, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            var seasonId = await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT season_id FROM public.season_tournaments WHERE tournament_id = @tournamentId LIMIT 1", new { tournamentId });
            if (seasonId is null) return Results.NotFound();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, seasonId.Value, userCtx)) return Results.Forbid();
            await conn.ExecuteAsync("DELETE FROM public.season_tournaments WHERE tournament_id = @tournamentId", new { tournamentId });
            await conn.ExecuteAsync("UPDATE public.season_nodes SET linked_tournament_id = NULL, updated_at = NOW() WHERE linked_tournament_id = @tournamentId", new { tournamentId });
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, seasonId.Value, ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        app.MapGet("/api/seasons/{id:guid}/advancement", async (Guid id, HttpContext ctx, IDbConnectionFactory db, HybridCache cache, Esportra.Api.Services.SeasonAdvancementService advancementSvc, CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewSeasonAsync(conn, id, ctx.Items["UserContext"] as UserContext)) return Results.Forbid();

            var cacheKey = $"season-advancement:{id}";
            var connections = await cache.GetOrCreateAsync(
                cacheKey,
                async _ => await advancementSvc.GetAdvancementConnectionsAsync(id),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(60), LocalCacheExpiration = TimeSpan.FromSeconds(60) },
                tags: ["season-advancement", $"season:{id}"],
                cancellationToken: ct);

            ctx.Response.Headers.CacheControl = "max-age=60";
            return Results.Ok(connections);
        });

        app.MapPost("/api/seasons/{id:guid}/advancement/preview", async (Guid id, [FromBody] ProcessAdvancementRequest req, HttpContext ctx, IDbConnectionFactory db, Esportra.Api.Services.SeasonAdvancementService advancementSvc, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var preview = await advancementSvc.PreviewAdvancementAsync(id, req.TournamentId, ct);
            return Results.Ok(preview);
        }).RequireAuthorization("Organizer");

        app.MapPost("/api/seasons/{id:guid}/advancement/process", async (Guid id, [FromBody] ApplyAdvancementRequest req, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, Esportra.Api.Services.SeasonAdvancementService advancementSvc, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            var overrides = req.Overrides?.Select(o => new Esportra.Api.Services.SeasonAdvancementService.AdvancementOverride(o.TeamId, o.Action)).ToList();
            var result = await advancementSvc.ApplyAdvancementAsync(id, req.TournamentId, overrides, cache, ct);

            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.advancement.process", id, "Season",
                new { req.TournamentId, result.AdvancedCount, result.QualificationCount }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { advanced_count = result.AdvancedCount, qualification_count = result.QualificationCount, result.Warnings, result.Message });
        }).RequireAuthorization("Organizer");
    }

    // TODO: remove duplication — delegate to SeasonOpsEndpoints.RecalculateStandingsAsync or extract shared helper.
    private static async Task<bool> CanViewSeasonAsync(IDbConnection conn, Guid seasonId, UserContext? userCtx)
    {
        var isPublic = await conn.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.seasons WHERE id = @seasonId AND deleted_at IS NULL AND is_public = TRUE)", new { seasonId });
        return isPublic || await SeasonEndpointHelpers.CanManageSeasonAsync(conn, seasonId, userCtx);
    }

    private static async Task<PointRuleRow> InsertPointRuleAsync(IDbConnection conn, IDbTransaction? tx, Guid seasonId, SeasonRuleDraft rule, int placementStart, int placementEnd)
    {
        return await conn.QuerySingleAsync<PointRuleRow>(
            """
            INSERT INTO public.season_point_rules
                (season_id, tournament_id, source_node_id, source_stage_id, destination_node_id,
                 placement_start, placement_end, points, qualification_status, destination_tournament_id,
                 auto_create_qualification, region_key)
            VALUES
                (@seasonId, @tournamentId, @sourceNodeId, @sourceStageId, @destinationNodeId,
                 @placementStart, @placementEnd, @points, @qualificationStatus, @destinationTournamentId,
                 @autoCreateQualification, @regionKey)
            RETURNING id, season_id, tournament_id, placement_start, placement_end, points,
                      qualification_status, destination_tournament_id, version, created_at
            """,
            new
            {
                seasonId,
                rule.TournamentId,
                rule.SourceNodeId,
                rule.SourceStageId,
                rule.DestinationNodeId,
                placementStart,
                placementEnd,
                points = rule.PointsAwarded ?? rule.Points ?? 0,
                qualificationStatus = NormalizeQualificationStatus(rule.QualificationStatus),
                rule.DestinationTournamentId,
                autoCreateQualification = rule.AutoCreateQualification ?? false,
                rule.RegionKey
            }, tx);
    }

    private static List<T> ReadArray<T>(JsonElement body, string propertyName)
    {
        if (body.ValueKind == JsonValueKind.Array) return body.Deserialize<List<T>>(JsonOptions) ?? [];
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
            return property.Deserialize<List<T>>(JsonOptions) ?? [];
        return [];
    }

    private static string? NormalizeQualificationStatus(string? status)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qualified", "eliminated", "pending" };
        return !string.IsNullOrWhiteSpace(status) && allowed.Contains(status) ? status.ToLowerInvariant() : null;
    }

    private static string? NormalizeSeedMode(string? seedMode)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "random", "manual", "top_seeded" };
        return !string.IsNullOrWhiteSpace(seedMode) && allowed.Contains(seedMode) ? seedMode.ToLowerInvariant() : null;
    }

    private static string? NormalizeParticipantStatus(string? status)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pending", "approved", "rejected" };
        return !string.IsNullOrWhiteSpace(status) && allowed.Contains(status) ? status.ToLowerInvariant() : null;
    }
}

public sealed record PointRuleRow(Guid Id, Guid SeasonId, Guid? TournamentId, int PlacementStart, int PlacementEnd, int Points, string? QualificationStatus, Guid? DestinationTournamentId, int Version, DateTime CreatedAt);
public sealed record AdvancementRuleRow(Guid Id, Guid SeasonId, Guid? SourceTournamentId, Guid? TargetTournamentId, int PlacementStart, int PlacementEnd, int AdvancementCount, string? SeedMode, int Version, DateTime CreatedAt);
public sealed record UpdateSeasonParticipantRequest(string? Status = null, string? Notes = null);
public sealed record LinkTournamentToSeasonRequest(Guid SeasonId, string? SeasonRole = null, int SeasonStageOrder = 0);
public sealed record ProcessAdvancementRequest(Guid? TournamentId = null);
public sealed record ApplyAdvancementRequest(Guid? TournamentId = null, List<AdvancementOverrideItem>? Overrides = null);
public sealed record AdvancementOverrideItem(Guid TeamId, string Action);

