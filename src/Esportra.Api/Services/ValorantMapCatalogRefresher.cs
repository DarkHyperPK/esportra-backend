namespace Esportra.Api.Services;

/// <summary>
/// Keeps the map table current: at start, then daily, and sooner (at most hourly) when a map path could not be named.
/// A failed fetch changes nothing; the built-in names keep working.
/// </summary>
public sealed class ValorantMapCatalogRefresher(IHttpClientFactory http, ILogger<ValorantMapCatalogRefresher> logger) : BackgroundService
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
            logger.LogInformation("Learned {Count} VALORANT maps from Riot's published list.", maps.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not refresh the VALORANT map list; the built-in map names stay in use.");
        }
    }
}
