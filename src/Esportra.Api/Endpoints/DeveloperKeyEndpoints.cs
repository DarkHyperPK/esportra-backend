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
/// Self-serve developer API key management and analytics.
/// Authenticated via standard Supabase JWT (not API key).
/// </summary>
public static class DeveloperKeyEndpoints
{
    public static void MapDeveloperKeyEndpoints(this WebApplication app)
    {
        app.MapPost("/api/developer/keys", CreateKey)
            .RequireAuthorization("Authenticated").WithTags("Developer Keys");

        app.MapGet("/api/developer/keys", ListKeys)
            .RequireAuthorization("Authenticated").WithTags("Developer Keys");

        app.MapGet("/api/developer/keys/{keyId}", GetKey)
            .RequireAuthorization("Authenticated").WithTags("Developer Keys");

        app.MapDelete("/api/developer/keys/{keyId}", RevokeKey)
            .RequireAuthorization("Authenticated").WithTags("Developer Keys");

        app.MapPost("/api/developer/keys/{keyId}/rotate", RotateKey)
            .RequireAuthorization("Authenticated").WithTags("Developer Keys");

        app.MapPatch("/api/developer/keys/{keyId}", RenameKey)
            .RequireAuthorization("Authenticated").WithTags("Developer Keys");

        app.MapGet("/api/developer/analytics/summary", GetAnalyticsSummary)
            .RequireAuthorization("Authenticated").WithTags("Developer Analytics");

        app.MapGet("/api/developer/analytics/timeseries", GetAnalyticsTimeseries)
            .RequireAuthorization("Authenticated").WithTags("Developer Analytics");

        app.MapGet("/api/developer/analytics/endpoints", GetAnalyticsEndpoints)
            .RequireAuthorization("Authenticated").WithTags("Developer Analytics");
    }

    private static async Task<IResult> CreateKey(
        [FromBody] CreateDeveloperKeyRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.OrganizationId)) return Results.BadRequest(new { error = "organization_id is required" });
        if (!Guid.TryParse(req.OrganizationId, out var orgId)) return Results.BadRequest(new { error = "invalid organization_id" });

        using var conn = db.CreateConnection();
        if (!await IsOrgOwnerOrAdmin(conn, orgId, userCtx.UserIdGuid)) return Results.Forbid();

        var environment = req.Environment?.ToLowerInvariant() == "live" ? "live" : "sandbox";
        var rateLimit = req.RateLimitPerMin ?? 60;
        if (rateLimit < 1 || rateLimit > 300)
            return Results.BadRequest(new { error = "rate_limit_per_min must be between 1 and 300" });

        if (!await CheckKeyLimitAsync(conn, orgId, environment)) return Results.Conflict(new { error = $"Key limit reached for {environment} environment." });

        if (environment == "live")
        {
            var isApproved = await conn.ExecuteScalarAsync<bool>(
                "SELECT is_api_approved FROM organizations WHERE id = @orgId",
                new { orgId });
            if (!isApproved)
                return Results.UnprocessableEntity(new { error = "Organization is not approved for live API access." });
        }

        var (rawKey, hash, prefix) = GenerateKey(environment);
        var keyId = Guid.NewGuid();
        var scopes = req.Scopes is { Length: > 0 } ? req.Scopes : DefaultScopes();

        if (req.Scopes is { Length: > 0 } && HasInvalidScopes(scopes))
            return Results.BadRequest(new { error = "One or more scopes are not valid." });

        await conn.ExecuteAsync(
            """
            INSERT INTO developer_api_keys (id, organization_id, name, key_hash, key_prefix, environment, status, scopes, rate_limit_per_min, created_by)
            VALUES (@keyId, @orgId, @name, @hash, @prefix, @environment, 'active', @scopes, @rateLimit, @createdBy)
            """,
            new { keyId, orgId, name = req.Name ?? $"{environment} key", hash, prefix, environment, scopes, rateLimit, createdBy = userCtx.UserIdGuid });

        return Results.Created($"/api/developer/keys/{keyId}",
            new { id = keyId, name = req.Name ?? $"{environment} key", key = rawKey, key_prefix = prefix, environment, scopes, rate_limit_per_min = rateLimit, created_at = DateTimeOffset.UtcNow });
    }

    private static async Task<IResult> ListKeys(
        [FromQuery] string organizationId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (!Guid.TryParse(organizationId, out var orgId)) return Results.BadRequest(new { error = "invalid organization_id" });

        using var conn = db.CreateConnection();
        if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid)) return Results.Forbid();

        var keys = await conn.QueryAsync<dynamic>(
            "SELECT id, name, key_prefix, environment, status, scopes, rate_limit_per_min, last_used_at, created_at FROM developer_api_keys WHERE organization_id = @orgId AND status != 'revoked' ORDER BY created_at DESC",
            new { orgId });

        return Results.Ok(new { keys });
    }

    private static async Task<IResult> GetKey(
        Guid keyId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, organization_id, name, key_prefix, environment, status, scopes, rate_limit_per_min, last_used_at, grace_period_until, rotated_to, created_at FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        if (row is null) return Results.NotFound();
        Guid rowOrgId = (Guid)row.organization_id;
        if (!await IsOrgMember(conn, rowOrgId, userCtx.UserIdGuid)) return Results.Forbid();

        return Results.Ok(row);
    }

    private static async Task<IResult> RevokeKey(
        Guid keyId,
        HttpContext ctx,
        IDbConnectionFactory db,
        HybridCache cache,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(Guid OrgId, string KeyHash)>(
            "SELECT organization_id AS OrgId, key_hash AS KeyHash FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        if (row == default) return Results.NotFound();
        if (!await IsOrgMember(conn, row.OrgId, userCtx.UserIdGuid)) return Results.Forbid();

        await conn.ExecuteAsync(
            "UPDATE developer_api_keys SET status = 'revoked', revoked_at = NOW() WHERE id = @keyId AND organization_id = @orgId",
            new { keyId, orgId = row.OrgId });

        try { await cache.RemoveAsync($"api-key-ctx:{row.KeyHash}", ct); } catch { /* best effort */ }
        return Results.NoContent();
    }

    private static async Task<IResult> RotateKey(
        Guid keyId,
        [FromBody] RotateDeveloperKeyRequest? req,
        HttpContext ctx,
        IDbConnectionFactory db,
        HybridCache cache,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        var gracePeriodHours = Math.Clamp(req?.GracePeriodHours ?? 24, 1, 72);

        using var conn = db.CreateConnection();
        var oldKey = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, organization_id, key_hash, environment, scopes, rate_limit_per_min, rotated_to FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        if (oldKey is null) return Results.NotFound();
        Guid orgId = (Guid)oldKey.organization_id;
        if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid)) return Results.Forbid();
        if (oldKey.rotated_to is not null) return Results.Conflict(new { error = "Key has already been rotated." });

        var (rawKey, hash, prefix) = GenerateKey((string)oldKey.environment);
        var newKeyId = Guid.NewGuid();
        var gracePeriodUntil = DateTimeOffset.UtcNow.AddHours(gracePeriodHours);

        await conn.ExecuteAsync(
            """
            INSERT INTO developer_api_keys (id, organization_id, name, key_hash, key_prefix, environment, status, scopes, rate_limit_per_min, created_by)
            VALUES (@newKeyId, @orgId, @name, @hash, @prefix, @environment, 'active', @scopes, @rateLimit, @createdBy)
            """,
            new { newKeyId, orgId, name = $"Rotated key (was {keyId})", hash, prefix, environment = (string)oldKey.environment, scopes = (string[]?)oldKey.scopes, rateLimit = (int)oldKey.rate_limit_per_min, createdBy = userCtx.UserIdGuid });

        await conn.ExecuteAsync(
            "UPDATE developer_api_keys SET status = 'rotating', rotated_to = @newKeyId, grace_period_until = @gracePeriodUntil WHERE id = @keyId",
            new { keyId, newKeyId, gracePeriodUntil });

        try { await cache.RemoveAsync($"api-key-ctx:{(string)oldKey.key_hash}", ct); } catch { /* best effort */ }

        return Results.Created($"/api/developer/keys/{newKeyId}",
            new { new_key_id = newKeyId, key = rawKey, grace_period_until = gracePeriodUntil });
    }

    private static async Task<IResult> GetAnalyticsSummary(
        [FromQuery] string organizationId,
        [FromQuery] string from,
        [FromQuery] string to,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (!Guid.TryParse(organizationId, out var orgId)) return Results.BadRequest(new { error = "invalid organization_id" });

        using var conn = db.CreateConnection();
        if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid)) return Results.Forbid();

        if (!DateTimeOffset.TryParse(from, out var fromDate) || !DateTimeOffset.TryParse(to, out var toDate))
            return Results.BadRequest(new { error = "Invalid date format." });

        var summary = await conn.QuerySingleAsync<dynamic>(
            """
            SELECT COUNT(*) AS total_requests,
                   COUNT(*) FILTER (WHERE response_status >= 400) AS error_count,
                   COUNT(*) FILTER (WHERE rate_limited = TRUE) AS rate_limited_count,
                   ROUND(AVG(response_time_ms)) AS avg_response_ms
            FROM developer_api_audit_log
            WHERE organization_id = @orgId AND created_at BETWEEN @from AND @to
            """,
            new { orgId, from = fromDate, to = toDate });

        return Results.Ok(new
        {
            total_requests = (long)summary.total_requests,
            error_count = (long)summary.error_count,
            rate_limited_count = (long)summary.rate_limited_count,
            avg_response_ms = summary.avg_response_ms,
            data_as_of = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<IResult> GetAnalyticsTimeseries(
        [FromQuery] string organizationId,
        [FromQuery] string from,
        [FromQuery] string to,
        [FromQuery] string granularity,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (!Guid.TryParse(organizationId, out var orgId)) return Results.BadRequest(new { error = "invalid organization_id" });

        using var conn = db.CreateConnection();
        if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid)) return Results.Forbid();

        if (!DateTimeOffset.TryParse(from, out var fromDate) || !DateTimeOffset.TryParse(to, out var toDate))
            return Results.BadRequest(new { error = "Invalid date format." });

        var gran = granularity?.ToLowerInvariant() == "day" ? "day" : "hour";
        var datapoints = await conn.QueryAsync<dynamic>(
            """
            SELECT date_trunc(@gran, created_at) AS timestamp,
                   COUNT(*) AS request_count,
                   COUNT(*) FILTER (WHERE response_status >= 400) AS error_count
            FROM developer_api_audit_log
            WHERE organization_id = @orgId AND created_at BETWEEN @from AND @to
            GROUP BY date_trunc(@gran, created_at)
            ORDER BY 1
            """,
            new { orgId, from = fromDate, to = toDate, gran });

        return Results.Ok(new { datapoints });
    }

    private static async Task<IResult> GetAnalyticsEndpoints(
        [FromQuery] string organizationId,
        [FromQuery] string from,
        [FromQuery] string to,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (!Guid.TryParse(organizationId, out var orgId)) return Results.BadRequest(new { error = "invalid organization_id" });

        using var conn = db.CreateConnection();
        if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid)) return Results.Forbid();

        if (!DateTimeOffset.TryParse(from, out var fromDate) || !DateTimeOffset.TryParse(to, out var toDate))
            return Results.BadRequest(new { error = "Invalid date format." });

        var endpoints = await conn.QueryAsync<dynamic>(
            """
            SELECT endpoint AS path, method,
                   COUNT(*) AS request_count,
                   ROUND(AVG(response_time_ms)) AS avg_response_ms,
                   ROUND(100.0 * COUNT(*) FILTER (WHERE response_status >= 400) / NULLIF(COUNT(*), 0), 2) AS error_rate
            FROM developer_api_audit_log
            WHERE organization_id = @orgId AND created_at BETWEEN @from AND @to
            GROUP BY endpoint, method
            ORDER BY request_count DESC
            """,
            new { orgId, from = fromDate, to = toDate });

        return Results.Ok(new { endpoints });
    }

    private static async Task<IResult> RenameKey(
        Guid keyId,
        [FromBody] RenameKeyRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { error = "name is required" });
        if (req.Name.Length > 100)
            return Results.BadRequest(new { error = "name must not exceed 100 characters" });

        using var conn = db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(Guid OrgId, string KeyHash)>(
            "SELECT organization_id AS OrgId, key_hash AS KeyHash FROM developer_api_keys WHERE id = @keyId",
            new { keyId });

        if (row == default) return Results.NotFound();
        if (!await IsOrgOwnerOrAdmin(conn, row.OrgId, userCtx.UserIdGuid)) return Results.Forbid();

        await conn.ExecuteAsync(
            "UPDATE developer_api_keys SET name = @name WHERE id = @keyId AND organization_id = @orgId",
            new { keyId, orgId = row.OrgId, name = req.Name });

        return Results.Ok(new { id = keyId, name = req.Name });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    internal static (string RawKey, string Hash, string Prefix) GenerateKey(string environment)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        var prefix = environment == "live" ? "ek_live_" : "ek_sand_";
        var rawKey = $"{prefix}{hex}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        return (rawKey, hash, rawKey[..16]);
    }

    private static async Task<bool> IsOrgOwnerOrAdmin(System.Data.IDbConnection conn, Guid orgId, Guid userId)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId
                UNION ALL
                SELECT 1 FROM organization_staff WHERE organization_id = @orgId AND user_id = @userId AND role = 'admin' AND status = 'active'
            )
            """,
            new { orgId, userId });
    }

    private static async Task<bool> IsOrgMember(System.Data.IDbConnection conn, Guid orgId, Guid userId)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId
                UNION ALL
                SELECT 1 FROM organization_staff WHERE organization_id = @orgId AND user_id = @userId AND status = 'active'
            )
            """,
            new { orgId, userId });
    }

    private static async Task<bool> CheckKeyLimitAsync(System.Data.IDbConnection conn, Guid orgId, string environment)
    {
        var maxKeys = environment == "live" ? 3 : 5;
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM developer_api_keys WHERE organization_id = @orgId AND environment = @environment AND status != 'revoked'",
            new { orgId, environment });
        return count < maxKeys;
    }

    private static string[] DefaultScopes() =>
    [
        ApiKeyScopes.TournamentsRead,
        ApiKeyScopes.TournamentsWrite,
        ApiKeyScopes.BracketsRead,
        ApiKeyScopes.BracketsWrite,
        ApiKeyScopes.MatchesRead,
        ApiKeyScopes.MatchesWrite,
    ];

    internal static bool HasInvalidScopes(string[] scopes)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ApiKeyScopes.TournamentsRead, ApiKeyScopes.TournamentsWrite,
            ApiKeyScopes.BracketsRead, ApiKeyScopes.BracketsWrite,
            ApiKeyScopes.MatchesRead, ApiKeyScopes.MatchesWrite,
            ApiKeyScopes.VetoRead, ApiKeyScopes.VetoWrite,
        };
        return scopes.Any(s => !known.Contains(s));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Request records
// ─────────────────────────────────────────────────────────────────────────────

public sealed record CreateDeveloperKeyRequest(
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("environment")] string? Environment = null,
    [property: JsonPropertyName("scopes")] string[]? Scopes = null,
    [property: JsonPropertyName("rate_limit_per_min")] int? RateLimitPerMin = null);

public sealed record RotateDeveloperKeyRequest(int? GracePeriodHours = null);

public sealed record RenameKeyRequest(string? Name = null);
