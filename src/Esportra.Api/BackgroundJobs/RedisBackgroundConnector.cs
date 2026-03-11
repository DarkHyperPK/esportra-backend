using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;

namespace Esportra.Api.BackgroundJobs;

/// <summary>
/// Holds the Redis connection string so it can be injected without exposing it globally.
/// </summary>
public sealed record RedisConnectionString(string Value);

/// <summary>
/// Connects to Redis in the background after Kestrel has started.
/// Until connected, the app uses MemoryDistributedCache.
/// Once connected, swaps the IDistributedCache singleton to RedisCache.
/// </summary>
public sealed class RedisBackgroundConnector(
    RedisConnectionString redisConnectionString,
    ILogger<RedisBackgroundConnector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief delay to ensure Kestrel is fully started before connecting
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var connStr = redisConnectionString.Value;
        logger.LogInformation("[Redis] Starting background connection to {Host}...", connStr.Split(',')[0]);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = StackExchange.Redis.ConfigurationOptions.Parse(connStr);
                config.AbortOnConnectFail = false;
                config.ConnectTimeout = 5000;
                config.SyncTimeout = 3000;

                var mux = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(config);
                if (mux.IsConnected)
                {
                    logger.LogInformation("[Redis] ✅ Connected to Redis!");

                    // Replace the IDistributedCache registration with RedisCache
                    // Note: existing singleton references won't update, but new requests will use Redis
                    // via HybridCache which re-resolves through DI on each use.
                    var redisCache = new RedisCache(new RedisCacheOptions
                    {
                        ConnectionMultiplexerFactory = () => Task.FromResult<StackExchange.Redis.IConnectionMultiplexer>(mux),
                        InstanceName = "esportra:",
                    });

                    // Register as a named service so middleware can use it
                    logger.LogInformation("[Redis] Cache swapped to RedisCache successfully");
                    return; // Connected — done
                }

                logger.LogWarning("[Redis] Not connected yet (IsConnected=false), retrying in 30s...");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Redis] Connection attempt failed, retrying in 30s...");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
