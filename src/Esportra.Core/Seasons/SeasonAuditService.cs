namespace Esportra.Core.Seasons;

using Dapper;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Esportra.Contracts.Auth;

/// <summary>
/// Service for logging season-specific audit events to season_audit_logs table.
/// This is append-only and separate from the general audit_logs table.
/// </summary>
public class SeasonAuditService
{
    private readonly IDbConnectionFactory db;
    private readonly ILogger<SeasonAuditService> logger;

    public SeasonAuditService(IDbConnectionFactory db, ILogger<SeasonAuditService> logger)
    {
        this.db = db;
        this.logger = logger;
    }

    /// <summary>
    /// Logs a season audit event. This is append-only.
    /// </summary>
    public async Task LogAsync(
        Guid seasonId,
        Guid actorId,
        string action,
        string entityType,
        Guid? entityId,
        string? before,
        string? after,
        string? reason,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        try
        {
            using var conn = db.CreateConnection();

            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO season_audit_logs (
                    season_id, actor_id, action, entity_type, entity_id,
                    before, after, reason, ip_address, user_agent, created_at
                )
                VALUES (
                    @seasonId, @actorId, @action, @entityType, @entityId,
                    @before::jsonb, @after::jsonb, @reason, @ipAddress, @userAgent, NOW()
                )
                """, new
                {
                    seasonId,
                    actorId,
                    action,
                    entityType,
                    entityId,
                    before = before ?? "{}",
                    after = after ?? "{}",
                    reason,
                    ipAddress,
                    userAgent,
                }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            // Audit failures must never break the main flow
            logger.LogWarning(ex, "[SeasonAudit] Failed to log {Action} on {EntityType}:{EntityId} for season {SeasonId}",
                action, entityType, entityId, seasonId);
        }
    }

    /// <summary>
    /// Queries audit logs for a season with pagination.
    /// </summary>
    public async Task<IEnumerable<SeasonAuditLog>> QueryAuditLogAsync(
        Guid seasonId,
        string? action = null,
        Guid? actorId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();

        var sql = """
            SELECT
                id, season_id, actor_id, action, entity_type, entity_id,
                before, after, reason, ip_address, user_agent, created_at
            FROM season_audit_logs
            WHERE season_id = @seasonId
            """;

        var parameters = new Dictionary<string, object> { { "seasonId", seasonId } };

        if (action != null)
        {
            sql += " AND action = @action";
            parameters["action"] = action;
        }

        if (actorId != null)
        {
            sql += " AND actor_id = @actorId";
            parameters["actorId"] = actorId;
        }

        if (fromDate != null)
        {
            sql += " AND created_at >= @fromDate";
            parameters["fromDate"] = fromDate;
        }

        if (toDate != null)
        {
            sql += " AND created_at <= @toDate";
            parameters["toDate"] = toDate;
        }

        sql += " ORDER BY created_at DESC LIMIT @limit OFFSET @offset";
        parameters["limit"] = limit;
        parameters["offset"] = offset;

        return await conn.QueryAsync<SeasonAuditLog>(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }
}

public record SeasonAuditLog(
    Guid Id,
    Guid SeasonId,
    Guid ActorId,
    string Action,
    string EntityType,
    Guid? EntityId,
    string Before,
    string After,
    string? Reason,
    string? IpAddress,
    string? UserAgent,
    DateTime CreatedAt
);
