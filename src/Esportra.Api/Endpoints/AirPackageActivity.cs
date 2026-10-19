using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

public static partial class AirBroadcastEndpoints
{
    private static readonly HashSet<string> PackageMetadataFields = ["scene", "action", "cause", "round", "kind", "status", "durationMs", "sponsorId", "slotType", "startedAt", "endedAt"];

    private static async Task<bool> OwnsPackage(System.Data.IDbConnection conn, Guid packageId, HttpContext ctx) =>
        await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM air_match_packages WHERE id=@packageId AND owner_id=@userId)", new { packageId, userId = AirAccountEndpoints.User(ctx) });

    private static async Task<IResult> SavePackageEvents(Guid packageId, AirPackageEventBatch request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        if (request.Events is null || request.Events.Length > 100 || request.Events.Any(e => e is null || e.Id == Guid.Empty || e.Version < 1 || string.IsNullOrWhiteSpace(e.Type) || e.Type.StartsWith("official_result_", StringComparison.Ordinal) || e.Type.Length > 80 || e.Data.ValueKind != JsonValueKind.Object || e.Data.GetRawText().Length > 8192))
            return Results.BadRequest(new { error = "Use at most 100 small production metadata events." });
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Forbid();
        if (!await OwnsPackage(conn, packageId, ctx)) return Results.NotFound();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        foreach (var item in request.Events)
        {
            if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM air_match_package_versions WHERE package_id=@packageId AND version=@version)", new { packageId, item.Version }, tx))
                return Results.BadRequest(new { error = "Unknown package version." });
            var metadata = item.Data.EnumerateObject().Where(p => PackageMetadataFields.Contains(p.Name) &&
                (p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null || p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length <= 256))
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            await conn.ExecuteAsync("INSERT INTO air_package_events(id,package_id,version,occurred_at,event_type,metadata) VALUES(@id,@packageId,@version,@at,@type,CAST(@metadata AS jsonb)) ON CONFLICT(id) DO NOTHING", new { item.Id, packageId, item.Version, at = item.At, item.Type, metadata = JsonSerializer.Serialize(metadata) }, tx);
            if (!await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM air_package_events WHERE id=@id AND package_id=@packageId AND version=@version AND event_type=@type AND metadata=CAST(@metadata AS jsonb))", new { item.Id, packageId, item.Version, item.Type, metadata = JsonSerializer.Serialize(metadata) }, tx))
                return Results.Conflict(new { error = "An event identity was already used with different content." });
        }
        tx.Commit();
        return Results.Ok(new { accepted = request.Events.Select(e => e.Id).ToArray() });
    }

    private static async Task<IResult> PackageActivity(Guid packageId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Forbid();
        if (!await OwnsPackage(conn, packageId, ctx)) return Results.NotFound();
        var items = await conn.QueryAsync<AirActivityRow>("SELECT id,version,occurred_at AS at,event_type AS type,metadata::text AS Document FROM air_package_events WHERE package_id=@packageId ORDER BY occurred_at DESC LIMIT 500", new { packageId });
        return Results.Ok(new { items = items.Select(row => new { row.Id, row.Version, row.At, row.Type, data = JsonSerializer.Deserialize<JsonElement>(row.Document) }), limit = 500 });
    }
    private sealed record AirActivityRow(Guid Id, int Version, DateTime At, string Type, string Document);
}

public sealed record AirPackageEvent(Guid Id, int Version, DateTimeOffset At, string Type, JsonElement Data);
public sealed record AirPackageEventBatch(AirPackageEvent[] Events);
