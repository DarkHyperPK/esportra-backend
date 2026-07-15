using Esportra.Contracts.Database;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Esportra.Api.HealthChecks;

public sealed class PostgresHealthCheck(IDbConnectionFactory db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var conn = db.CreateConnection();
            // Lightweight round-trip to confirm Postgres is responding
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await Task.Run(() => cmd.ExecuteScalar(), cancellationToken);
            return HealthCheckResult.Healthy("Postgres reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Postgres unavailable: {ex.Message}");
        }
    }
}
