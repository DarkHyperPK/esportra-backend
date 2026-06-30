using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Esportra.Api.HealthChecks;

public sealed class RedisHealthCheck(IConnectionMultiplexer mux) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var db = mux.GetDatabase();
            var latency = await db.PingAsync();
            return HealthCheckResult.Healthy($"Redis reachable — latency {latency.TotalMilliseconds:F0}ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"Redis unavailable: {ex.Message}");
        }
    }
}
