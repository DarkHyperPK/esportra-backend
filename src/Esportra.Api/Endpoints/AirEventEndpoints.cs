using Dapper;
using System.Text.Json;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

public static class AirEventEndpoints
{
    private static readonly HashSet<string> Allowed = ["type", "scene", "segmentId", "actor", "ts", "action", "cause", "actorKind", "segmentName", "durationMs", "placementId", "sponsorId", "kind", "status", "screenCount", "round", "slotType", "startedAt", "endedAt"];
    public static void MapAirEventEndpoints(this WebApplication app) => app.MapPost("/api/air/projects/{projectId:guid}/events", Drain).RequireAuthorization("Authenticated");
    private static async Task<IResult> Drain(Guid projectId, AirEventBatch input, HttpContext ctx, IDbConnectionFactory db)
    {
        if (input.Events is null || input.Events.Length > 100 || input.Events.Any(item => item is null || item.Id == Guid.Empty || item.ProjectId != projectId || string.IsNullOrWhiteSpace(item.Type) || item.Type.Length > 80 || item.Data.ValueKind != JsonValueKind.Object || item.Data.GetRawText().Length > 8192)) return Results.BadRequest(new { error = "Use a batch of at most 100 project production events." });
        if (!Guid.TryParse(ctx.Request.Headers["X-Air-Device-Id"], out var deviceId)) return Results.BadRequest(new { error = "Device identity is required." });
        using var connection = db.CreateConnection(); if (connection.State != System.Data.ConnectionState.Open) connection.Open(); using var tx = connection.BeginTransaction(); var userId = AirAccountEndpoints.User(ctx);
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM air_projects p JOIN air_workspaces w ON w.id=p.workspace_id JOIN air_devices d ON d.user_id=w.owner_id WHERE p.id=@projectId AND w.owner_id=@userId AND d.id=@deviceId AND d.status='active')", new { projectId, userId, deviceId }, tx)) return Results.NotFound();
        foreach (var item in input.Events)
        {
            var metadata = new Dictionary<string, JsonElement>();
            foreach (var property in item.Data.EnumerateObject())
            {
                if (property.Name == "data" && property.Value.ValueKind == JsonValueKind.Object) { foreach (var nested in property.Value.EnumerateObject()) if (Allowed.Contains(nested.Name) && Scalar(nested.Value)) metadata[nested.Name] = nested.Value.Clone(); }
                else if (Allowed.Contains(property.Name) && Scalar(property.Value)) metadata[property.Name] = property.Value.Clone();
            }
            await connection.ExecuteAsync("INSERT INTO air_production_events(id,project_id,device_id,occurred_at,event_type,metadata) VALUES(@id,@projectId,@deviceId,@at,@type,CAST(@metadata AS jsonb)) ON CONFLICT(id) DO NOTHING", new { id = item.Id, projectId, deviceId, at = item.At, type = item.Type, metadata = JsonSerializer.Serialize(metadata) }, tx);
        }
        tx.Commit(); return Results.Ok(new { accepted = input.Events.Select(item => item.Id).ToArray() });
    }
    private static bool Scalar(JsonElement value) => value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null || value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 256;
}
public sealed record AirProductionEvent(Guid Id, Guid ProjectId, DateTimeOffset At, string Type, JsonElement Data);
public sealed record AirEventBatch(AirProductionEvent[] Events);
