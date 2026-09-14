using System.Security.Cryptography;
using System.Text;
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

        var environment = req.Environment?.ToLowerInvariant() == "live" ? "live" : "sandbox";
        var (rawKey, hash, prefix) = DeveloperKeyEndpoints.GenerateKey(environment);
        var keyId = Guid.NewGuid();
        var scopes = req.Scopes is { Length: > 0 } ? req.Scopes : AdminDefaultScopes();

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

        var rows = await conn.QueryAsync<dynamic>(
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

        return Results.Ok(new { keys = rows, total, page, page_size = pageSize });
    }

    private static async Task<IResult> AdminGetKey(
        Guid keyId,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, organization_id, name, key_prefix, environment, status, scopes, rate_limit_per_min, last_used_at, grace_period_until, rotated_to, created_at, revoked_at FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        return row is null ? Results.NotFound() : Results.Ok(row);
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

        var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, name, key_prefix, environment, status, scopes, rate_limit_per_min, created_at FROM developer_api_keys WHERE id = @keyId",
            new { keyId });
        return Results.Ok(updated);
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
        var oldKey = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, organization_id, key_hash, environment, scopes, rate_limit_per_min, rotated_to FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        if (oldKey is null) return Results.NotFound();
        if (oldKey.rotated_to is not null) return Results.Conflict(new { error = "Key has already been rotated." });

        var (_, newHash, newPrefix) = DeveloperKeyEndpoints.GenerateKey((string)oldKey.environment);
        var newKeyId = Guid.NewGuid();
        var gracePeriodUntil = DateTimeOffset.UtcNow.AddMinutes(graceMins);

        await conn.ExecuteAsync(
            """
            INSERT INTO developer_api_keys (id, organization_id, name, key_hash, key_prefix, environment, status, scopes, rate_limit_per_min, created_by)
            VALUES (@newKeyId, @orgId, @name, @hash, @prefix, @environment, 'active', @scopes, @rateLimit, @createdBy)
            """,
            new { newKeyId, orgId = (Guid)oldKey.organization_id, name = $"Admin-rotated key (was {keyId})", hash = newHash, prefix = newPrefix, environment = (string)oldKey.environment, scopes = (string[]?)oldKey.scopes, rateLimit = (int)oldKey.rate_limit_per_min, createdBy = userCtx.UserIdGuid });

        await conn.ExecuteAsync(
            "UPDATE developer_api_keys SET status = 'rotating', rotated_to = @newKeyId, grace_period_until = @gracePeriodUntil WHERE id = @keyId",
            new { keyId, newKeyId, gracePeriodUntil });

        try { await cache.RemoveAsync($"api-key-ctx:{(string)oldKey.key_hash}", ct); } catch { /* best effort */ }

        return Results.Created($"/api/admin/developer-keys/{newKeyId}",
            new { new_key_id = newKeyId, key_prefix = newPrefix, grace_period_until = gracePeriodUntil });
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

        var entries = await conn.QueryAsync<dynamic>(
            "SELECT id, endpoint, method, response_status, response_time_ms, rate_limited, ip_address, created_at FROM developer_api_audit_log WHERE api_key_id = @keyId ORDER BY created_at DESC LIMIT @pageSize OFFSET @offset",
            new { keyId, pageSize, offset });

        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM developer_api_audit_log WHERE api_key_id = @keyId", new { keyId });

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
        var partners = await conn.QueryAsync<dynamic>(
            """
            SELECT l.organization_id,
                   o.name AS org_name,
                   COUNT(*) AS total_requests,
                   ROUND(100.0 * COUNT(*) FILTER (WHERE response_status >= 400) / NULLIF(COUNT(*), 0), 2) AS error_rate
            FROM developer_api_audit_log l
            LEFT JOIN organizations o ON o.id = l.organization_id
            WHERE l.created_at BETWEEN @from AND @to
            GROUP BY l.organization_id, o.name
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
}

// ─────────────────────────────────────────────────────────────────────────────
// Request records
// ─────────────────────────────────────────────────────────────────────────────

public sealed record AdminCreateDeveloperKeyRequest(
    string OrganizationId,
    string? Name = null,
    string? Environment = null,
    string[]? Scopes = null,
    int? RateLimitPerMin = null);

public sealed record AdminPatchDeveloperKeyRequest(
    string? Name = null,
    string[]? Scopes = null,
    int? RateLimitPerMin = null);

public sealed record AdminRotateDeveloperKeyRequest(int? GracePeriodMinutes = null);

public sealed record SetApiApprovalRequest(bool IsApiApproved);
