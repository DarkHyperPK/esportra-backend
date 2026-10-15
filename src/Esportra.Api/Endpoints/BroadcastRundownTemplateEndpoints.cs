using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Core.Broadcast;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastRundownTemplateEndpoints
{
    public static void MapBroadcastRundownTemplateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/broadcast/rundown/templates", () => Results.Ok(new[] { BroadcastRundownTemplates.Build(1), BroadcastRundownTemplates.Build(3) })).RequireAuthorization("Authenticated");
        app.MapPost("/api/broadcast/shows/{showId:guid}/rundown/templates", Import)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
        app.MapPut("/api/broadcast/shows/{showId:guid}/segments/{segmentId:guid}/plan", UpdatePlan)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
    }

    private static async Task<IResult> Import(Guid showId, [FromBody] ImportRundownTemplateRequest input, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (input.MatchId == Guid.Empty || input.RequestId == Guid.Empty || input.Template is not ("bo1" or "bo3")) return Results.BadRequest();
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var scope = await EditableScope(conn, tx, showId, user.UserIdGuid);
        if (scope.Error is not null) return scope.Error;
        var bestOf = await conn.QuerySingleOrDefaultAsync<int?>("""
            SELECT COALESCE(bm.best_of, 1) FROM broadcast_show_matches sm JOIN brkt_matches bm ON bm.id = sm.match_id
            JOIN brkt_versions v ON v.id = bm.version_id JOIN tournaments t ON t.id = v.tournament_id
            JOIN broadcast_shows s ON s.id = sm.show_id
            WHERE sm.show_id = @showId AND sm.match_id = @matchId AND t.organization_id = @orgId
              AND t.deleted_at IS NULL AND bm.winner_id IS NULL AND bm.team1_id IS NOT NULL AND bm.team2_id IS NOT NULL
              AND (s.tournament_id IS NULL OR s.tournament_id = v.tournament_id) AND v.status IN ('published', 'active')
              AND bm.status IN ('pending', 'scheduled', 'in_progress')
            """, new { showId, matchId = input.MatchId, orgId = scope.OrgId }, tx);
        if (bestOf is null || input.Template != $"bo{bestOf}") return Results.Conflict(new { error = "Choose the template matching this eligible match’s series format." });
        var existing = await conn.QuerySingleAsync<ImportCounts>("""
            SELECT COUNT(*) AS Total, COUNT(*) FILTER (WHERE config->>'matchId' = @match AND config->>'rundownTemplate' = @template) AS Matching
            FROM rundown_segments WHERE show_id = @showId AND config->>'templateImportId' = @requestId
            """, new { showId, requestId = input.RequestId.ToString(), match = input.MatchId.ToString(), template = input.Template }, tx);
        if (existing.Total > 0) return existing.Matching == existing.Total
            ? Results.Ok(new { count = existing.Total, alreadyImported = true })
            : Results.Conflict(new { error = "This import request belongs to a different flow. Start a new import." });
        var items = BroadcastRundownTemplates.Build(bestOf.Value).Items;
        var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM rundown_segments WHERE show_id = @showId", new { showId }, tx);
        if (count + items.Length > 200) return Results.BadRequest(new { error = "A show supports up to 200 flow items." });
        var order = await conn.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(display_order) + 1, 0) FROM rundown_segments WHERE show_id = @showId", new { showId }, tx);
        var appearance = BroadcastShowEndpoints.ReadAppearance(await conn.ExecuteScalarAsync<string>("SELECT appearance::text FROM broadcast_shows WHERE id = @showId", new { showId }, tx));
        var rows = items.Select((item, index) => new { showId, item.Title, item.Type, order = order + index, config = JsonSerializer.Serialize(new { overlay = item.Overlay, matchId = input.MatchId, plan = appearance.ForNewPlan(item.Plan), templateImportId = input.RequestId, rundownTemplate = input.Template }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await conn.ExecuteAsync("INSERT INTO rundown_segments (show_id, title, type, display_order, config) VALUES (@showId, @Title, @Type, @order, @config::jsonb)", rows, tx);
        tx.Commit(); return Results.Ok(new { count = items.Length, alreadyImported = false });
    }

    private static async Task<IResult> UpdatePlan(Guid showId, Guid segmentId, [FromBody] BroadcastSegmentPlan plan, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        var error = BroadcastSegmentPlans.Validate(plan);
        if (error is not null || plan.Graphics.Any(name => !BroadcastScreenEndpoints.IsKnownOverlay(name))) return Results.BadRequest(new { error = error ?? "Choose an existing screen." });
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var scope = await EditableScope(conn, tx, showId, user.UserIdGuid);
        if (scope.Error is not null) return scope.Error;
        var config = JsonSerializer.Serialize(new { plan, overlay = plan.Graphics.FirstOrDefault() }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var changed = await conn.ExecuteAsync("UPDATE rundown_segments SET config = COALESCE(config, '{}'::jsonb) || @config::jsonb, updated_at = NOW() WHERE id = @segmentId AND show_id = @showId", new { config, segmentId, showId }, tx);
        if (changed == 0) return Results.NotFound();
        tx.Commit(); return Results.Ok(new { success = true });
    }

    private static async Task<(Guid OrgId, IResult? Error)> EditableScope(IDbConnection conn, IDbTransaction tx, Guid showId, Guid userId)
    {
        var show = await conn.QuerySingleOrDefaultAsync<ShowScope>("SELECT org_id AS OrgId, status AS Status FROM broadcast_shows WHERE id = @showId FOR UPDATE", new { showId }, tx);
        if (show is null) return (Guid.Empty, Results.NotFound());
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, userId, tx)) return (Guid.Empty, Results.StatusCode(403));
        return BroadcastShowRules.CanEditPlan(show.Status) ? (show.OrgId, null) : (show.OrgId, Results.Conflict(new { error = "Flow settings are locked during and after a live show." }));
    }
    private sealed record ShowScope(Guid OrgId, string Status);
    private sealed record ImportCounts(long Total, long Matching);
}
public sealed record ImportRundownTemplateRequest(Guid MatchId, string Template, Guid RequestId);
