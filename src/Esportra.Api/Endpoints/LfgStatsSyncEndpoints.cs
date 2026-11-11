using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

public static class LfgStatsSyncEndpoints
{
    public static async Task<IResult> SyncMineAsync(HttpContext ctx, ValorantSyncRequestService sync, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        return Response(await sync.RequestAsync(user.UserIdGuid, ct));
    }

    public static async Task<IResult> SyncListingAsync(Guid id, HttpContext ctx, IDbConnectionFactory db,
        ValorantSyncRequestService sync, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        var owner = await ListingOwnerAsync(id, viewer.UserIdGuid, db, ct);
        return owner is null ? Results.NotFound() : Response(await sync.RequestAsync(owner.Value, ct));
    }

    public static async Task<IResult> MineStateAsync(HttpContext ctx, ValorantSyncRequestService sync, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        return StateResponse(await sync.GetStateAsync(user.UserIdGuid, ct));
    }

    public static async Task<IResult> ListingStateAsync(Guid id, HttpContext ctx, IDbConnectionFactory db,
        ValorantSyncRequestService sync, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext viewer) return Results.Unauthorized();
        var owner = await ListingOwnerAsync(id, viewer.UserIdGuid, db, ct);
        return owner is null ? Results.NotFound() : StateResponse(await sync.GetStateAsync(owner.Value, ct));
    }

    public static async Task<IResult> SyncRosterAsync(Guid id, HttpContext ctx, IDbConnectionFactory db,
        ValorantSyncRequestService sync, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var memberIds = (await conn.QueryAsync<Guid>(new CommandDefinition("""
            SELECT DISTINCT ra.user_id
            FROM public.lfg_listings l
            JOIN public.team_rosters tr ON tr.team_id = l.team_id AND lower(tr.game) = lower(l.game_slug)
            JOIN public.team_roster_members rm ON rm.roster_id = tr.id AND rm.is_active = true
            JOIN public.riot_accounts ra ON ra.user_id = rm.user_id
            WHERE l.id = @id AND l.game_slug = 'valorant'
              AND l.intent::text IN ('looking_for_player', 'competitive_roster')
              AND l.status = 'published' AND (l.expires_at IS NULL OR l.expires_at > now())
            """, new { id }, cancellationToken: ct))).ToArray();
        if (memberIds.Length == 0) return Results.NotFound();
        var maxRetry = 0;
        var members = new List<object>(memberIds.Length);
        foreach (var userId in memberIds)
        {
            var result = await sync.RequestAsync(userId, ct);
            if (result.RetryAfterSeconds > maxRetry) maxRetry = result.RetryAfterSeconds;
            members.Add(new { user_id = userId, status = result.Status, retry_after_seconds = result.RetryAfterSeconds, synced_at = result.SyncedAt });
        }
        return Results.Ok(new { status = "queued", retry_after_seconds = maxRetry, members });
    }

    private static async Task<Guid?> ListingOwnerAsync(Guid id, Guid viewer, IDbConnectionFactory db, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition("""
            SELECT l.user_id FROM public.lfg_listings l
            WHERE l.id = @id AND l.game_slug = 'valorant' AND l.intent::text = 'looking_for_team'
              AND EXISTS (SELECT 1 FROM public.riot_accounts ra WHERE ra.user_id = l.user_id)
              AND (l.user_id = @viewer OR (l.status = 'published' AND (l.expires_at IS NULL OR l.expires_at > now())))
            """, new { id, viewer }, cancellationToken: ct));
    }

    private static IResult Response(ValorantSyncRequestService.Result result) => result.Status switch
    {
        "unlinked" => Results.BadRequest(new { error = "NO_LINKED_RIOT_ACCOUNT" }),
        "error" => Results.Json(new { error = "Stats sync could not be queued. Try again." }, statusCode: 503),
        _ => StateResponse(result)
    };

    private static IResult StateResponse(ValorantSyncRequestService.Result result) => Results.Ok(new
    { status = result.Status, retry_after_seconds = result.RetryAfterSeconds, synced_at = result.SyncedAt });
}
