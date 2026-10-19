using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

public static partial class AirBroadcastEndpoints
{
    // Discovery only. Details, reports and result review keep their existing
    // organization authorization in the compatibility services.
    private static async Task<IResult> SavedAirRecords(HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Forbid();
        var items = await conn.QueryAsync<SavedAirRecord>("SELECT s.id,s.title,s.status,s.created_at AS CreatedAt FROM broadcast_shows s JOIN organizations o ON o.id=s.org_id WHERE o.owner_id=@userId ORDER BY s.created_at DESC LIMIT 200", new { userId = AirAccountEndpoints.User(ctx) });
        return Results.Ok(new { items });
    }
    private sealed record SavedAirRecord(Guid Id, string Title, string Status, DateTime CreatedAt);
}
