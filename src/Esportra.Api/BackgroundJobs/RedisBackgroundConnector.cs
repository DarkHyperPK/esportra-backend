using StackExchange.Redis;

namespace Esportra.Api.BackgroundJobs;

/// <summary>
/// Holds the Redis connection string so it can be injected without exposing it globally.
/// </summary>
public sealed record RedisConnectionString(string Value);

/// <summary>
/// Waits for the shared IConnectionMultiplexer to become reachable (via PING),
/// then atomically swaps IDistributedCache from the in-memory fallback to Redis.
/// SE.Redis handles all TCP reconnect logic internally; this service only manages
/// the one-time cache swap and logs connection lifecycle events.
/// </summary>
public sealed class RedisBackgroundConnector(
    IConnectionMultiplexer          mux,
    SwappableDistributedCache       distributedCache,
    ILogger<RedisBackgroundConnector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe to SE.Redis events so reconnect cycles are always visible in logs
        mux.ConnectionFailed   += (_, e) => logger.LogWarning(
            "[Redis] Connection lost to {Endpoint} — reason: {Reason}", e.EndPoint, e.FailureType);
        mux.ConnectionRestored += (_, e) => logger.LogInformation(
            "[Redis] Connection restored to {Endpoint}", e.EndPoint);
        mux.ErrorMessage       += (_, e) => logger.LogError(
            "[Redis] Server error from {Endpoint}: {Message}", e.EndPoint, e.Message);

        logger.LogInformation("[Redis] Waiting for Redis to become reachable...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var db = mux.GetDatabase();
                await db.PingAsync(); // throws if auth fails, host unreachable, etc.

                var redisCache = new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCache(
                    new Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions
                    {
                        ConnectionMultiplexerFactory = () => Task.FromResult(mux),
                        InstanceName = "esportra:",
                    });

                distributedCache.Swap(redisCache);
                logger.LogInformation(
                    "[Redis] ✅ Connected — IDistributedCache swapped to Redis. Rate limiting and caching now use Redis.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("[Redis] Not ready yet ({Error}), retrying in 30s...", ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
