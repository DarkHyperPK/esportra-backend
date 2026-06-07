using Dapper;
using Esportra.Contracts.Database;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Esportra.Core.Audit;

public enum ActionType
{
    Create, Update, Delete,
    Approve, Reject, Cancel,
    Suspend, Unsuspend, Ban, Unban,
    Verify, Unverify,
    Resolve, Escalate,
    Login, Logout,
    SettingsUpdate, RoleChange,
    Feature, Unfeature
}

public enum TargetType { User, Tournament, Venue, Payment, Team, Match, Dispute, System, Sponsor }

public enum AuditSeverity { Low, Medium, High, Critical }

public sealed class AuditService(IDbConnectionFactory db, ILogger<AuditService> logger)
{
    public async Task LogAsync(
        Guid       adminId,
        string     adminName,
        ActionType action,
        TargetType target,
        Guid       targetId,
        string     targetName,
        object?    details          = null,
        AuditSeverity? severityOverride = null,
        CancellationToken ct        = default)
    {
        try
        {
            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(@"
                INSERT INTO public.audit_logs
                    (admin_id, admin_name, action_type, target_type, target_id, target_name, details, severity, created_at)
                VALUES
                    (@adminId, @adminName, @actionType, @targetType, @targetId, @targetName, @details::jsonb, @severity, now())",
                new
                {
                    adminId,
                    adminName,
                    actionType = action.ToString().ToLowerInvariant(),
                    targetType = target.ToString().ToLowerInvariant(),
                    targetId,
                    targetName,
                    details    = details is null ? "{}" : JsonSerializer.Serialize(details),
                    severity   = (severityOverride ?? GetSeverity(action)).ToString().ToLowerInvariant(),
                });
        }
        catch (Exception ex)
        {
            // Audit failures must never break the main flow
            logger.LogWarning(ex, "[Audit] Failed to log {Action} on {Target}:{TargetId}", action, target, targetId);
        }
    }

    public async Task LogCustomAsync(
        Guid       adminId,
        string     adminName,
        string     actionType,
        TargetType target,
        Guid       targetId,
        string     targetName,
        object?    details          = null,
        AuditSeverity severity      = AuditSeverity.Low,
        CancellationToken ct        = default)
    {
        try
        {
            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(@"
                INSERT INTO public.audit_logs
                    (admin_id, admin_name, action_type, target_type, target_id, target_name, details, severity, created_at)
                VALUES
                    (@adminId, @adminName, @actionType, @targetType, @targetId, @targetName, @details::jsonb, @severity, now())",
                new
                {
                    adminId,
                    adminName,
                    actionType,
                    targetType = target.ToString().ToLowerInvariant(),
                    targetId,
                    targetName,
                    details    = details is null ? "{}" : JsonSerializer.Serialize(details),
                    severity   = severity.ToString().ToLowerInvariant(),
                });
        }
        catch (Exception ex)
        {
            // Audit failures must never break the main flow
            logger.LogWarning(ex, "[Audit] Failed to log {Action} on {Target}:{TargetId}", actionType, target, targetId);
        }
    }

    private static AuditSeverity GetSeverity(ActionType action) => action switch
    {
        ActionType.Ban    or ActionType.Delete                                    => AuditSeverity.Critical,
        ActionType.Suspend or ActionType.Reject or ActionType.Escalate
            or ActionType.Cancel                                                 => AuditSeverity.High,
        ActionType.Approve or ActionType.Verify or ActionType.Resolve
            or ActionType.RoleChange or ActionType.SettingsUpdate
            or ActionType.Feature or ActionType.Unfeature                        => AuditSeverity.Medium,
        _                                                                         => AuditSeverity.Low,
    };
}
