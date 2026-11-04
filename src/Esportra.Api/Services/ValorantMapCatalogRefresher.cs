namespace Esportra.Api.Services;

/// <summary>
/// Keeps the map table current: at start, then daily, and sooner (at most hourly) when a map path could not be named.
/// A failed fetch changes nothing; the built-in names keep working.
/// </summary>
public sealed class ValorantMapCatalogRefresher(IHttpClientFactory http, Esportra.Contracts.Database.IDbConnectionFactory db, ILogger<ValorantMapCatalogRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Check = TimeSpan.FromMinutes(15), Daily = TimeSpan.FromHours(24), RetryAfterUnknown = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = DateTime.MinValue;
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var since = DateTime.UtcNow - last;
            if (since >= Daily || (ValorantMaps.UnknownSeen && since >= RetryAfterUnknown))
            {
                await RefreshAsync(stoppingToken);
                await LearnFromGameMapsAsync(stoppingToken);
                await LearnFromPoolAsync(stoppingToken);
                last = DateTime.UtcNow;
            }
            try { await Task.Delay(Check, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var client = http.CreateClient("ValorantMaps");
            client.Timeout = TimeSpan.FromSeconds(15);
            var json = await client.GetStringAsync(ValorantMapCatalog.Url, ct);
            var maps = ValorantMapCatalog.Parse(json);
            ValorantMaps.Learn(maps);
            ValorantMapCatalog.Remember(json);
            logger.LogInformation("Learned {Count} VALORANT maps from Riot's published list.", maps.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not refresh the VALORANT map list; the built-in map names stay in use.");
        }
    }

    /// <summary>
    /// Seeds ValorantMaps from game_maps rows that carry a map_provider_code. This is the primary
    /// DB-backed path: admin adds a new map with its Riot code and it resolves everywhere on the next
    /// cycle without any external API call. Runs after RefreshAsync so the external list wins when both
    /// know a map, but fills the gap when the external API is unreachable or behind.
    /// </summary>
    public async Task LearnFromGameMapsAsync(CancellationToken ct)
    {
        try
        {
            using var conn = db.CreateConnection();
            var pairs = await Dapper.SqlMapper.QueryAsync<PoolPair>(conn, new Dapper.CommandDefinition(GameMapsSql, cancellationToken: ct));
            var toLearn = pairs.Where(p => ValorantMaps.DisplayName(p.Code) is null).ToList();
            ValorantMaps.LearnFromPool(toLearn.Select(p => (p.Code, p.Name)));
            if (toLearn.Count > 0)
                logger.LogInformation("Learned {Count} new VALORANT map code(s) from game_maps: {Maps}.",
                    toLearn.Count, string.Join(", ", toLearn.Select(p => p.Name)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not learn map names from game_maps.");
        }
    }

    /// <summary>Names maps Riot's list lacks from the catalog's own pool: verified scrim games whose Riot map code is unnamed and whose recorded map is one pool name, every time.</summary>
    public async Task LearnFromPoolAsync(CancellationToken ct)
    {
        try
        {
            using var conn = db.CreateConnection();
            var pairs = await Dapper.SqlMapper.QueryAsync<PoolPair>(conn, new Dapper.CommandDefinition(PoolPairsSql, cancellationToken: ct));
            ValorantMaps.LearnFromPool(pairs.Where(p => ValorantMaps.DisplayName(p.Code) is null).Select(p => (p.Code, p.Name)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not learn map names from the map pool.");
        }
    }

    private sealed record PoolPair(string Code, string Name);

    internal const string GameMapsSql =
        """
        SELECT map_provider_code AS Code, map_name AS Name
        FROM public.game_maps
        WHERE lower(game) = 'valorant' AND is_active = TRUE
          AND map_provider_code IS NOT NULL AND map_provider_code <> ''
        """;

    internal const string PoolPairsSql =
        """
        SELECT regexp_replace(m.map_provider_id, '^.*/', '') AS Code, min(gm.map_name) AS Name
        FROM public.provider_matches m
        JOIN public.provider_match_associations a ON a.match_id = m.id AND a.context_type = 'scrim_game' AND a.verification_status IN ('verified', 'partially_verified')
        JOIN public.scrim_session_games g ON g.id = a.context_id
        JOIN public.game_maps gm ON lower(gm.game) = 'valorant' AND gm.is_active = TRUE AND lower(gm.map_name) = lower(btrim(g.map_name))
        WHERE m.map_provider_id IS NOT NULL
        GROUP BY 1
        HAVING count(DISTINCT lower(gm.map_name)) = 1
        """;
}
