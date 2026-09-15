using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Admin endpoints for developer API key management and partner analytics.
/// Requires DeveloperKeysManage permission.
/// </summary>
public static class DeveloperAdminEndpoints
{
    public static void MapDeveloperAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/developer-keys", AdminCreateKey)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");

        app.MapGet("/api/admin/developer-keys", AdminListKeys)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");

        app.MapGet("/api/admin/developer-keys/{keyId}", AdminGetKey)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");

        app.MapPatch("/api/admin/developer-keys/{keyId}", AdminPatchKey)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");

        app.MapDelete("/api/admin/developer-keys/{keyId}", AdminRevokeKey)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");

        app.MapPost("/api/admin/developer-keys/{keyId}/rotate", AdminRotateKey)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");

        app.MapGet("/api/admin/developer-keys/{keyId}/audit-log", AdminGetAuditLog)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");

        app.MapGet("/api/admin/developer-analytics/partners", AdminGetPartnerAnalytics)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");

        app.MapPatch("/api/admin/organizations/{orgId}/api-approval", AdminSetApiApproval)
            .RequireAuthorization(Permissions.DeveloperKeysManage).WithTags("Developer Admin");
    }

    private static async Task<IResult> AdminCreateKey(
        [FromBody] AdminCreateDeveloperKeyRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (!Guid.TryParse(req.OrganizationId, out var orgId)) return Results.BadRequest(new { error = "invalid organization_id" });
        if (req.Name is { Length: > 100 })
            return Results.BadRequest(new { error = "name must not exceed 100 characters" });

        var environment = req.Environment?.ToLowerInvariant() == "live" ? "live" : "sandbox";
        var (rawKey, hash, prefix) = DeveloperKeyEndpoints.GenerateKey(environment);
        var keyId = Guid.NewGuid();
        var scopes = req.Scopes is { Length: > 0 } ? req.Scopes : AdminDefaultScopes();

        if (req.Scopes is { Length: > 0 } && DeveloperKeyEndpoints.HasInvalidScopes(scopes))
            return Results.BadRequest(new { error = "One or more scopes are not valid." });

        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO developer_api_keys (id, organization_id, name, key_hash, key_prefix, environment, status, scopes, rate_limit_per_min, created_by)
            VALUES (@keyId, @orgId, @name, @hash, @prefix, @environment, 'active', @scopes, @rateLimit, @createdBy)
            """,
            new { keyId, orgId, name = req.Name ?? $"Admin-created {environment} key", hash, prefix, environment, scopes, rateLimit = req.RateLimitPerMin ?? 60, createdBy = userCtx.UserIdGuid });

        return Results.Created($"/api/admin/developer-keys/{keyId}",
            new { id = keyId, name = req.Name, key = rawKey, key_prefix = prefix, environment, scopes, rate_limit_per_min = req.RateLimitPerMin ?? 60, created_at = DateTimeOffset.UtcNow });
    }

    private static async Task<IResult> AdminListKeys(
        [FromQuery] string? organizationId,
        [FromQuery] string? environment,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var offset = (page - 1) * pageSize;

        using var conn = db.CreateConnection();
        Guid? orgId = Guid.TryParse(organizationId, out var g) ? g : null;

        var rows = await conn.QueryAsync<AdminKeyListRow>(
            """
            SELECT id, organization_id, name, key_prefix, environment, status, scopes, rate_limit_per_min, last_used_at, created_at
            FROM developer_api_keys
            WHERE (@orgId IS NULL OR organization_id = @orgId)
              AND (@env IS NULL OR environment = @env)
            ORDER BY created_at DESC
            LIMIT @pageSize OFFSET @offset
            """,
            new { orgId, env = environment, pageSize, offset });

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM developer_api_keys WHERE (@orgId IS NULL OR organization_id = @orgId) AND (@env IS NULL OR environment = @env)",
            new { orgId, env = environment });

        var keys = rows.Select(r => new
        {
            id = r.Id,
            organization_id = r.OrganizationId,
            name = r.Name,
            key_prefix = r.KeyPrefix,
            environment = r.Environment,
            status = r.Status,
            scopes = r.Scopes,
            rate_limit_per_min = r.RateLimitPerMin,
            last_used_at = r.LastUsedAt,
            created_at = r.CreatedAt,
        });
        return Results.Ok(new { keys, total, page, page_size = pageSize });
    }

    private static async Task<IResult> AdminGetKey(
        Guid keyId,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AdminKeyDetailRow>(
            "SELECT id, organization_id, name, key_prefix, environment, status, scopes, rate_limit_per_min, last_used_at, grace_period_until, rotated_to, created_at, revoked_at FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        if (row is null) return Results.NotFound();
        return Results.Ok(new
        {
            id = row.Id,
            organization_id = row.OrganizationId,
            name = row.Name,
            key_prefix = row.KeyPrefix,
            environment = row.Environment,
            status = row.Status,
            scopes = row.Scopes,
            rate_limit_per_min = row.RateLimitPerMin,
            last_used_at = row.LastUsedAt,
            grace_period_until = row.GracePeriodUntil,
            rotated_to = row.RotatedTo,
            created_at = row.CreatedAt,
            revoked_at = row.RevokedAt,
        });
    }

    private static async Task<IResult> AdminPatchKey(
        Guid keyId,
        [FromBody] AdminPatchDeveloperKeyRequest req,
        IDbConnectionFactory db,
        HybridCache cache,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<(string KeyHash, string[] Scopes, int RateLimit)>(
            "SELECT key_hash AS KeyHash, scopes AS Scopes, rate_limit_per_min AS RateLimit FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        if (existing == default) return Results.NotFound();

        if (req.Name is { Length: > 100 })
            return Results.BadRequest(new { error = "name must not exceed 100 characters" });

        if (req.Scopes is { Length: > 0 } && DeveloperKeyEndpoints.HasInvalidScopes(req.Scopes))
            return Results.BadRequest(new { error = "One or more scopes are not valid." });

        var affected = await conn.ExecuteAsync(
            """
            UPDATE developer_api_keys
            SET name             = COALESCE(@name, name),
                scopes           = COALESCE(@scopes, scopes),
                rate_limit_per_min = COALESCE(@rateLimit, rate_limit_per_min)
            WHERE id = @keyId
            """,
            new { keyId, name = req.Name, scopes = req.Scopes, rateLimit = req.RateLimitPerMin });

        if (affected == 0) return Results.NotFound();

        if (req.Scopes is not null || req.RateLimitPerMin is not null)
            try { await cache.RemoveAsync($"api-key-ctx:{existing.KeyHash}", ct); } catch { /* best effort */ }

        var updated = await conn.QuerySingleOrDefaultAsync<AdminKeyPatchResultRow>(
            "SELECT id, name, key_prefix, environment, status, scopes, rate_limit_per_min, created_at FROM developer_api_keys WHERE id = @keyId",
            new { keyId });
        if (updated is null) return Results.NotFound();
        return Results.Ok(ProjectKeyPatchResult(updated));
    }

    private static async Task<IResult> AdminRevokeKey(
        Guid keyId,
        IDbConnectionFactory db,
        HybridCache cache,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT key_hash FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        if (row is null) return Results.NotFound();

        await conn.ExecuteAsync(
            "UPDATE developer_api_keys SET status = 'revoked', revoked_at = NOW() WHERE id = @keyId",
            new { keyId });

        try { await cache.RemoveAsync($"api-key-ctx:{row}", ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    private static async Task<IResult> AdminRotateKey(
        Guid keyId,
        [FromBody] AdminRotateDeveloperKeyRequest? req,
        HttpContext ctx,
        IDbConnectionFactory db,
        HybridCache cache,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        var graceMins = Math.Clamp(req?.GracePeriodMinutes ?? 1440, 15, 4320);

        using var conn = db.CreateConnection();
        var oldKey = await conn.QuerySingleOrDefaultAsync<AdminKeyRotateRow>(
            "SELECT id, organization_id, key_hash, environment, scopes, rate_limit_per_min, rotated_to FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        if (oldKey is null) return Results.NotFound();
        if (oldKey.RotatedTo is not null) return Results.Conflict(new { error = "Key has already been rotated." });

        var (rawKey, newHash, newPrefix) = DeveloperKeyEndpoints.GenerateKey(oldKey.Environment);
        var newKeyId = Guid.NewGuid();
        var gracePeriodUntil = DateTimeOffset.UtcNow.AddMinutes(graceMins);

        await conn.ExecuteAsync(
            """
            INSERT INTO developer_api_keys (id, organization_id, name, key_hash, key_prefix, environment, status, scopes, rate_limit_per_min, created_by)
            VALUES (@newKeyId, @orgId, @name, @hash, @prefix, @environment, 'active', @scopes, @rateLimit, @createdBy)
            """,
            new { newKeyId, orgId = oldKey.OrganizationId, name = $"Admin-rotated key (was {keyId})", hash = newHash, prefix = newPrefix, environment = oldKey.Environment, scopes = oldKey.Scopes, rateLimit = oldKey.RateLimitPerMin, createdBy = userCtx.UserIdGuid });

        await conn.ExecuteAsync(
            "UPDATE developer_api_keys SET status = 'rotating', rotated_to = @newKeyId, grace_period_until = @gracePeriodUntil WHERE id = @keyId",
            new { keyId, newKeyId, gracePeriodUntil });

        try { await cache.RemoveAsync($"api-key-ctx:{oldKey.KeyHash}", ct); } catch { /* best effort */ }

        return Results.Created($"/api/admin/developer-keys/{newKeyId}",
            new { new_key_id = newKeyId, key = rawKey, key_prefix = newPrefix, grace_period_until = gracePeriodUntil });
    }

    private static async Task<IResult> AdminGetAuditLog(
        Guid keyId,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var offset = (page - 1) * pageSize;

        using var conn = db.CreateConnection();
        var exists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM developer_api_keys WHERE id = @keyId)", new { keyId });
        if (!exists) return Results.NotFound();

        var entryRows = await conn.QueryAsync<AuditLogEntryRow>(
            "SELECT id, endpoint, method, response_status, response_time_ms, rate_limited, ip_address, created_at FROM developer_api_audit_log WHERE api_key_id = @keyId ORDER BY created_at DESC LIMIT @pageSize OFFSET @offset",
            new { keyId, pageSize, offset });

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM developer_api_audit_log WHERE api_key_id = @keyId", new { keyId });

        var entries = entryRows.Select(e => new
        {
            id = e.Id,
            endpoint = e.Endpoint,
            method = e.Method,
            response_status = e.ResponseStatus,
            response_time_ms = e.ResponseTimeMs,
            rate_limited = e.RateLimited,
            ip_address = e.IpAddress,
            created_at = e.CreatedAt,
        });
        return Results.Ok(new { entries, total, page, page_size = pageSize });
    }

    private static async Task<IResult> AdminGetPartnerAnalytics(
        [FromQuery] string from,
        [FromQuery] string to,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        if (!DateTimeOffset.TryParse(from, out var fromDate) || !DateTimeOffset.TryParse(to, out var toDate))
            return Results.BadRequest(new { error = "Invalid date format." });

        using var conn = db.CreateConnection();
        var partners = await conn.QueryAsync<PartnerAnalyticsRow>(
            """
            SELECT l.organization_id,
                   o.name AS org_name,
                   COUNT(*) AS total_requests,
                   ROUND(100.0 * COUNT(*) FILTER (WHERE response_status >= 400) / NULLIF(COUNT(*), 0), 2) AS error_rate,
                   COALESCE(k.sandbox_key_count, 0) AS sandbox_key_count,
                   COALESCE(k.live_key_count, 0)    AS live_key_count,
                   k.last_active_at
            FROM developer_api_audit_log l
            LEFT JOIN organizations o ON o.id = l.organization_id
            LEFT JOIN (
                SELECT organization_id,
                       COUNT(*) FILTER (WHERE environment = 'sandbox') AS sandbox_key_count,
                       COUNT(*) FILTER (WHERE environment = 'live')    AS live_key_count,
                       MAX(last_used_at)                               AS last_active_at
                FROM developer_api_keys
                WHERE status IN ('active', 'rotating')
                GROUP BY organization_id
            ) k ON k.organization_id = l.organization_id
            WHERE l.created_at BETWEEN @from AND @to
            GROUP BY l.organization_id, o.name, k.sandbox_key_count, k.live_key_count, k.last_active_at
            ORDER BY total_requests DESC
            """,
            new { from = fromDate, to = toDate });

        return Results.Ok(new { partners });
    }

    private static async Task<IResult> AdminSetApiApproval(
        Guid orgId,
        [FromBody] SetApiApprovalRequest req,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var affected = await conn.ExecuteAsync(
            "UPDATE organizations SET is_api_approved = @value WHERE id = @orgId",
            new { orgId, value = req.IsApiApproved });

        return affected == 0
            ? Results.NotFound()
            : Results.Ok(new { organization_id = orgId, is_api_approved = req.IsApiApproved });
    }

    private static string[] AdminDefaultScopes() =>
    [
        ApiKeyScopes.TournamentsRead,
        ApiKeyScopes.TournamentsWrite,
        ApiKeyScopes.BracketsRead,
        ApiKeyScopes.BracketsWrite,
        ApiKeyScopes.MatchesRead,
        ApiKeyScopes.MatchesWrite,
        ApiKeyScopes.VetoRead,
        ApiKeyScopes.VetoWrite,
    ];

    private static object ProjectKeyPatchResult(AdminKeyPatchResultRow r) =>
        new
        {
            id = r.Id,
            name = r.Name,
            key_prefix = r.KeyPrefix,
            environment = r.Environment,
            status = r.Status,
            scopes = r.Scopes,
            rate_limit_per_min = r.RateLimitPerMin,
            created_at = r.CreatedAt,
        };

    // ─────────────────────────────────────────────────────────────────────────────
    // Internal query row types
    // ─────────────────────────────────────────────────────────────────────────────

    private sealed record AdminKeyListRow
    {
        public Guid Id { get; init; }
        public Guid OrganizationId { get; init; }
        public string? Name { get; init; }
        public string KeyPrefix { get; init; } = string.Empty;
        public string Environment { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string[] Scopes { get; init; } = [];
        public int RateLimitPerMin { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed record AdminKeyDetailRow
    {
        public Guid Id { get; init; }
        public Guid OrganizationId { get; init; }
        public string? Name { get; init; }
        public string KeyPrefix { get; init; } = string.Empty;
        public string Environment { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string[] Scopes { get; init; } = [];
        public int RateLimitPerMin { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset? GracePeriodUntil { get; init; }
        public Guid? RotatedTo { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }
    }

    private sealed record AdminKeyPatchResultRow
    {
        public Guid Id { get; init; }
        public string? Name { get; init; }
        public string KeyPrefix { get; init; } = string.Empty;
        public string Environment { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string[] Scopes { get; init; } = [];
        public int RateLimitPerMin { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed record AdminKeyRotateRow
    {
        public Guid Id { get; init; }
        public Guid OrganizationId { get; init; }
        public string KeyHash { get; init; } = string.Empty;
        public string Environment { get; init; } = string.Empty;
        public string[] Scopes { get; init; } = [];
        public int RateLimitPerMin { get; init; }
        public Guid? RotatedTo { get; init; }
    }

    private sealed record AuditLogEntryRow
    {
        public Guid Id { get; init; }
        public string Endpoint { get; init; } = string.Empty;
        public string Method { get; init; } = string.Empty;
        public int ResponseStatus { get; init; }
        public int? ResponseTimeMs { get; init; }
        public bool RateLimited { get; init; }
        public string? IpAddress { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Request records
// ─────────────────────────────────────────────────────────────────────────────

public sealed record AdminCreateDeveloperKeyRequest(
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("environment")] string? Environment = null,
    [property: JsonPropertyName("scopes")] string[]? Scopes = null,
    [property: JsonPropertyName("rate_limit_per_min")] int? RateLimitPerMin = null);

public sealed record AdminPatchDeveloperKeyRequest(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("scopes")] string[]? Scopes = null,
    [property: JsonPropertyName("rate_limit_per_min")] int? RateLimitPerMin = null);

public sealed record AdminRotateDeveloperKeyRequest(
    [property: JsonPropertyName("grace_period_minutes")] int? GracePeriodMinutes = null);

public sealed record SetApiApprovalRequest(
    [property: JsonPropertyName("is_api_approved")] bool IsApiApproved);

public sealed record PartnerAnalyticsRow(
    Guid OrganizationId,
    string? OrgName,
    long TotalRequests,
    decimal? ErrorRate,
    long SandboxKeyCount,
    long LiveKeyCount,
    DateTime? LastActiveAt);
