using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Services;

public sealed record OperationsAuditEntry(
    Guid AdminId,
    string ActionString,
    string ResourceType,
    string? TargetId = null,
    Guid? ImpersonatedUserId = null,
    string? IpAddress = null,
    string? UserAgent = null,
    string? RequestMethod = null,
    string? RequestPath = null,
    string Severity = "low",
    object? DataDiff = null);

public sealed class OperationsAuditService(
    IDbConnectionFactory db,
    ILogger<OperationsAuditService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(OperationsAuditEntry entry, CancellationToken ct = default)
    {
        try
        {
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO public.operations_audit_log
                    (admin_id, impersonated_user_id, action_string, target_id, resource_type,
                     ip_address, user_agent, request_method, request_path, severity, data_diff)
                VALUES
                    (@AdminId, @ImpersonatedUserId, @ActionString, @TargetId, @ResourceType,
                     NULLIF(@IpAddress, '')::inet, @UserAgent, @RequestMethod, @RequestPath,
                     @Severity, @DataDiff::jsonb)
                """,
                new
                {
                    entry.AdminId,
                    entry.ImpersonatedUserId,
                    entry.ActionString,
                    entry.TargetId,
                    entry.ResourceType,
                    entry.IpAddress,
                    entry.UserAgent,
                    entry.RequestMethod,
                    entry.RequestPath,
                    Severity = NormalizeSeverity(entry.Severity),
                    DataDiff = JsonSerializer.Serialize(entry.DataDiff ?? new { }, JsonOptions),
                },
                cancellationToken: ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[OperationsAudit] Failed to write {Action} for {Resource}:{TargetId}",
                entry.ActionString, entry.ResourceType, entry.TargetId);
            throw;
        }
    }

    public Task WriteFromHttpAsync(
        HttpContext http,
        UserContext admin,
        string action,
        string resourceType,
        string? targetId,
        object? dataDiff,
        string severity = "low",
        CancellationToken ct = default)
    {
        var impersonation = http.Items["GhostMode"] as GhostModeContext;
        return WriteAsync(new OperationsAuditEntry(
            AdminId: admin.UserIdGuid,
            ActionString: action,
            ResourceType: resourceType,
            TargetId: targetId,
            ImpersonatedUserId: impersonation?.TargetUserId,
            IpAddress: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            RequestMethod: http.Request.Method,
            RequestPath: http.Request.Path,
            Severity: severity,
            DataDiff: dataDiff), ct);
    }

    private static string NormalizeSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => "critical",
        "high" => "high",
        "medium" => "medium",
        _ => "low",
    };
}
