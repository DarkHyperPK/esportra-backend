using Esportra.Contracts.Auth;
using Esportra.Core.Audit;

namespace Esportra.Api.Services;

/// <summary>
/// Captures request context (client IP + user agent) into audit entries.
/// Superadmin bypass note: IP is taken from RemoteIpAddress — ForwardedHeaders
/// middleware already resolves the real client IP from X-Forwarded-For.
/// </summary>
public static class AuditRequestExtensions
{
    public static Task LogFromHttp(
        this AuditService audit,
        HttpContext ctx,
        UserContext actor,
        ActionType action,
        TargetType target,
        Guid targetId,
        string targetName,
        object? details = null,
        AuditSeverity? severity = null,
        CancellationToken ct = default)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var ua = ctx.Request.Headers.UserAgent.ToString();
        return audit.LogAsync(actor.UserIdGuid, actor.Email, action, target, targetId, targetName, details, severityOverride: severity, ct: ct, ip: ip, userAgent: ua);
    }

    public static Task LogCustomFromHttp(
        this AuditService audit,
        HttpContext ctx,
        UserContext actor,
        string actionType,
        TargetType target,
        Guid targetId,
        string targetName,
        object? details = null,
        AuditSeverity severity = AuditSeverity.Low,
        CancellationToken ct = default)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var ua = ctx.Request.Headers.UserAgent.ToString();
        return audit.LogCustomAsync(actor.UserIdGuid, actor.Email, actionType, target, targetId, targetName, details, severity, ct: ct, ip: ip, userAgent: ua);
    }
}
