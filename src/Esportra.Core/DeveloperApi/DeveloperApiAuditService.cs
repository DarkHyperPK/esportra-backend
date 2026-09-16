using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.DeveloperApi;

public interface IDeveloperApiAuditService
{
    Task RecordAsync(ApiKeyAuditEntry entry, CancellationToken ct = default);
}

public sealed record ApiKeyAuditEntry(
    Guid ApiKeyId,
    Guid OrganizationId,
    string Method,
    string Endpoint,
    int StatusCode,
    int ResponseMs,
    string? IpAddress,
    bool RateLimited);

public sealed class DeveloperApiAuditService(IDbConnectionFactory db) : IDeveloperApiAuditService
{
    public async Task RecordAsync(ApiKeyAuditEntry entry, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO developer_api_audit_log
                (id, api_key_id, organization_id, endpoint, method, response_status,
                 response_time_ms, rate_limited, ip_address, created_at)
            VALUES (gen_random_uuid(), @ApiKeyId, @OrganizationId, @Endpoint, @Method,
                    @StatusCode, @ResponseMs, @RateLimited, @IpAddress, NOW())
            """,
            entry);
    }
}
