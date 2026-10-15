using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Core.Broadcast;

namespace Esportra.Api.Endpoints;

public static partial class BroadcastShowEndpoints
{
    private static async Task<ShowRundownSlot[]> ReadRundownPlans(IDbConnection conn, Guid showId)
    {
        var rows = await conn.QueryAsync<RundownPlanRow>("""
            SELECT id::text AS Id, title AS Title, type AS Type, duration_seconds AS DurationSeconds,
                   config->>'notes' AS Notes, config->>'overlay' AS Overlay, config->>'matchId' AS MatchId,
                   CASE WHEN config->'plan' IS NULL OR config->'plan' = 'null'::jsonb THEN NULL
                        WHEN LENGTH((config->'plan')::text) <= 8192 THEN (config->'plan')::text
                        WHEN config ? 'plan' THEN 'invalid' ELSE NULL END AS PlanJson
            FROM rundown_segments WHERE show_id = @showId ORDER BY display_order LIMIT 200
            """, new { showId });
        return rows.Select(row =>
        {
            var plan = ParsePlan(row.PlanJson);
            return new ShowRundownSlot(row.Id, row.Title, row.Type, row.DurationSeconds, row.Notes,
                plan is null ? row.Overlay : plan.Graphics.FirstOrDefault(), row.MatchId, plan, row.PlanJson is not null && plan is null ? "Invalid saved flow settings. This item stays manual; review its settings before sending." : null);
        }).ToArray();
    }

    private static BroadcastSegmentPlan? ParsePlan(string? json)
    {
        if (json is null) return null;
        try
        {
            var plan = JsonSerializer.Deserialize<BroadcastSegmentPlan>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return plan is not null && BroadcastSegmentPlans.Validate(plan) is null && plan.Graphics.All(BroadcastScreenEndpoints.IsKnownOverlay) ? plan : null;
        }
        catch (JsonException) { return null; }
    }
    private sealed record RundownPlanRow(string Id, string Title, string Type, int? DurationSeconds,
        string? Notes, string? Overlay, string? MatchId, string? PlanJson);
}
