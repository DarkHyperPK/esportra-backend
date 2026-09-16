using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.DeveloperApi;

public interface IApiKeyValidationService
{
    Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken ct);
}

public sealed record ApiKeyRecord(
    Guid Id,
    Guid OrganizationId,
    Guid OwnerId,
    string KeyPrefix,
    string[] Scopes,
    string Environment,
    int RateLimitPerMin,
    string Status,
    DateTimeOffset? GracePeriodUntil);

public sealed class ApiKeyValidationService(IDbConnectionFactory db) : IApiKeyValidationService
{
    public async Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<ApiKeyRecord>(
            """
            SELECT id, organization_id, created_by AS owner_id, key_prefix, scopes,
                   environment, rate_limit_per_min, status, grace_period_until
            FROM developer_api_keys
            WHERE key_hash = @keyHash
            LIMIT 1
            """,
            new { keyHash });
    }
}
