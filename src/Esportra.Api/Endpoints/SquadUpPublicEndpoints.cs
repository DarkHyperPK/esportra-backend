using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// GET /api/competitive/lfg/swipe/public-stats — anonymous live numbers for the public Squad Up page.
/// Cached for 30 seconds so a busy landing page never loads the database.
/// </summary>
public static class SquadUpPublicEndpoints
{
    public static void MapSquadUpPublicEndpoints(this WebApplication app)
    {
        app.MapGet("/api/competitive/lfg/swipe/public-stats", GetAsync)
            .WithMetadata(new RateLimitPolicyMetadata("public"))
            .AllowAnonymous();
    }

    private static async Task<IResult> GetAsync(IDbConnectionFactory db, HybridCache cache, CancellationToken ct)
    {
        var stats = await cache.GetOrCreateAsync("squad-up:public-stats", async token =>
        {
            using var conn = db.CreateConnection();
            return await conn.QuerySingleAsync<SquadUpPublicStats>(new CommandDefinition(
                $"""
                SELECT (SELECT count(*) FROM public.lfg_swipe_sessions s WHERE {LfgSwipeSql.Active("s")})::int AS players_live,
                       (SELECT count(*) FROM public.squad_up_lobbies l WHERE {SquadUpLobbySql.Open("l")})::int AS open_lobbies,
                       (SELECT count(*) FROM public.lfg_swipe_matches m WHERE m.matched_at >= date_trunc('day', now() AT TIME ZONE 'utc') AT TIME ZONE 'utc')::int AS matches_today,
                       COALESCE((SELECT sum(lobbies_filled) FROM public.squad_up_daily_stats WHERE day = (now() AT TIME ZONE 'utc')::date), 0)::int AS lobbies_filled_today
                """, cancellationToken: token));
        }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30), LocalCacheExpiration = TimeSpan.FromSeconds(30) }, cancellationToken: ct);
        return Results.Ok(stats);
    }
}

public sealed record SquadUpPublicStats
{
    public int PlayersLive { get; init; }
    public int OpenLobbies { get; init; }
    public int MatchesToday { get; init; }
    public int LobbiesFilledToday { get; init; }
}
