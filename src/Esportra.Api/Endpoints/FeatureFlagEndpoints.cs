using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class FeatureFlagEndpoints
{
    public static void MapFeatureFlagEndpoints(this WebApplication app)
    {
        // Curated platform feature registry for the Admin Centre catalog view.
        // Joins FeatureCatalog metadata with live feature_flags state so the UI can
        // toggle each feature through the standard PUT /feature-flags/{id} endpoint.
        app.MapGet("/api/admin/features/catalog", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.FeatureFlagsView)) return Results.Forbid();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<(Guid Id, string Key, bool IsEnabled)>(
                new CommandDefinition(
                    "SELECT id, key, is_enabled FROM feature_flags WHERE key = ANY(@keys)",
                    new { keys = Features.FeatureCatalog.All.Select(f => f.Key).ToArray() },
                    cancellationToken: ct));

            var stateByKey = rows.ToDictionary(r => r.Key, r => (Enabled: r.IsEnabled, FlagId: r.Id));

            var catalog = Features.FeatureCatalog.All.Select(meta =>
            {
                var hasState = stateByKey.TryGetValue(meta.Key, out var state);
                return new
                {
                    key = meta.Key,
                    name = meta.Name,
                    description = meta.Description,
                    category = meta.Category,
                    enabled = hasState ? state.Enabled : true,
                    flagId = hasState ? (Guid?)state.FlagId : null,
                    seeded = hasState
                };
            });

            return Results.Ok(new { features = catalog });
        }).RequireAuthorization(Permissions.FeatureFlagsView);

        // Public: resolved on/off state for every registry feature. Clients use this
        // to hide entry points; the middleware independently blocks the APIs.
        app.MapGet("/api/features/status", async (
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<(string Key, bool? IsEnabled)>(
                new CommandDefinition(
                    "SELECT key, is_enabled FROM feature_flags WHERE key = ANY(@keys)",
                    new { keys = Features.FeatureCatalog.All.Select(f => f.Key).ToArray() },
                    cancellationToken: ct));

            var enabledByKey = rows.ToDictionary(r => r.Key, r => r.IsEnabled ?? true);
            var status = Features.FeatureCatalog.All.Select(f => new
            {
                key = f.Key,
                enabled = enabledByKey.TryGetValue(f.Key, out var e) ? e : true
            });

            return Results.Ok(new { features = status });
        });
        // ── GET /api/admin/feature-flags ───────────────────────────────────────
        app.MapGet("/api/admin/feature-flags", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:view"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var flags = await conn.QueryAsync<dynamic>(
                """
                SELECT f.id, f.key, f.name, f.description, f.flag_type,
                       f.default_value, f.is_enabled, f.created_at, f.updated_at,
                       p.username AS created_by_name,
                       (SELECT COUNT(*) FROM feature_flag_rules r WHERE r.flag_id = f.id) AS rule_count,
                       (SELECT COUNT(*) FROM feature_flag_overrides o WHERE o.flag_id = f.id) AS override_count
                FROM feature_flags f
                LEFT JOIN profiles p ON p.id = f.created_by
                ORDER BY f.created_at DESC
                """);
            return Results.Ok(flags);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/feature-flags ──────────────────────────────────────
        app.MapPost("/api/admin/feature-flags", async (
            [FromBody] CreateFeatureFlagRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:create"))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(req.Key) || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Key and Name are required" });

            using var conn = db.CreateConnection();
            var defaultValue = req.DefaultValue ?? JsonDocument.Parse("{\"enabled\": false}").RootElement;

            var id = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO feature_flags (key, name, description, flag_type, default_value, is_enabled, created_by)
                VALUES (@key, @name, @description, @flagType, @defaultValue::jsonb, @isEnabled, @createdBy)
                RETURNING id
                """,
                new
                {
                    key = req.Key.Trim().ToLowerInvariant().Replace(" ", "_"),
                    name = req.Name,
                    description = req.Description,
                    flagType = req.FlagType ?? "boolean",
                    defaultValue = JsonSerializer.Serialize(defaultValue),
                    isEnabled = req.IsEnabled ?? true,
                    createdBy = userCtx.UserIdGuid
                });

            return Results.Ok(new { id, success = true });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/feature-flags/{id} ──────────────────────────────────
        app.MapPut("/api/admin/feature-flags/{id}", async (
            Guid id,
            [FromBody] UpdateFeatureFlagRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:edit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();

            var sets = new List<string> { "updated_at = NOW()" };
            var p = new DynamicParameters();
            p.Add("id", id);

            if (req.Name is not null) { sets.Add("name = @name"); p.Add("name", req.Name); }
            if (req.Description is not null) { sets.Add("description = @description"); p.Add("description", req.Description); }
            if (req.IsEnabled.HasValue) { sets.Add("is_enabled = @isEnabled"); p.Add("isEnabled", req.IsEnabled.Value); }
            if (req.DefaultValue is not null)
            {
                sets.Add("default_value = @defaultValue::jsonb");
                p.Add("defaultValue", JsonSerializer.Serialize(req.DefaultValue));
            }

            var affected = await conn.ExecuteAsync(
                $"UPDATE feature_flags SET {string.Join(", ", sets)} WHERE id = @id", p);

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Feature flag not found" });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/feature-flags/{id} ───────────────────────────────
        app.MapDelete("/api/admin/feature-flags/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:delete"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var affected = await conn.ExecuteAsync(
                "DELETE FROM feature_flags WHERE id = @id", new { id });

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Feature flag not found" });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/feature-flags/{id}/rules ────────────────────────────
        app.MapGet("/api/admin/feature-flags/{id}/rules", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:view"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var rules = await conn.QueryAsync<dynamic>(
                """
                SELECT id, flag_id, priority, conditions, value, percentage, created_at
                FROM feature_flag_rules
                WHERE flag_id = @id
                ORDER BY priority DESC
                """, new { id });
            return Results.Ok(rules);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/feature-flags/{id}/rules ───────────────────────────
        app.MapPost("/api/admin/feature-flags/{id}/rules", async (
            Guid id,
            [FromBody] CreateFlagRuleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:edit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var ruleId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO feature_flag_rules (flag_id, priority, conditions, value, percentage)
                VALUES (@flagId, @priority, @conditions::jsonb, @value::jsonb, @percentage)
                RETURNING id
                """,
                new
                {
                    flagId = id,
                    priority = req.Priority ?? 0,
                    conditions = req.Conditions.HasValue
                        ? JsonSerializer.Serialize(req.Conditions.Value)
                        : "{}",
                    value = JsonSerializer.Serialize(req.Value),
                    percentage = req.Percentage
                });

            return Results.Ok(new { id = ruleId, success = true });
        }).RequireAuthorization("Admin");

        // ── PUT /api/admin/feature-flags/{id}/rules/{ruleId} ───────────────────
        app.MapPut("/api/admin/feature-flags/{id}/rules/{ruleId}", async (
            Guid id,
            Guid ruleId,
            [FromBody] UpdateFlagRuleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:edit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var sets = new List<string>();
            var p = new DynamicParameters();
            p.Add("ruleId", ruleId);
            p.Add("flagId", id);

            if (req.Priority.HasValue) { sets.Add("priority = @priority"); p.Add("priority", req.Priority.Value); }
            if (req.Conditions is not null) { sets.Add("conditions = @conditions::jsonb"); p.Add("conditions", JsonSerializer.Serialize(req.Conditions)); }
            if (req.Value is not null) { sets.Add("value = @value::jsonb"); p.Add("value", JsonSerializer.Serialize(req.Value)); }
            if (req.Percentage.HasValue) { sets.Add("percentage = @percentage"); p.Add("percentage", req.Percentage); }

            if (sets.Count == 0)
                return Results.BadRequest(new { error = "No fields to update" });

            var affected = await conn.ExecuteAsync(
                $"UPDATE feature_flag_rules SET {string.Join(", ", sets)} WHERE id = @ruleId AND flag_id = @flagId", p);

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Rule not found" });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/feature-flags/{id}/rules/{ruleId} ────────────────
        app.MapDelete("/api/admin/feature-flags/{id}/rules/{ruleId}", async (
            Guid id,
            Guid ruleId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:edit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var affected = await conn.ExecuteAsync(
                "DELETE FROM feature_flag_rules WHERE id = @ruleId AND flag_id = @flagId",
                new { ruleId, flagId = id });

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Rule not found" });
        }).RequireAuthorization("Admin");

        // ── GET /api/admin/feature-flags/{id}/overrides ────────────────────────
        app.MapGet("/api/admin/feature-flags/{id}/overrides", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:view"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var overrides = await conn.QueryAsync<dynamic>(
                """
                SELECT o.id, o.flag_id, o.user_id, o.value, o.reason, o.expires_at, o.created_at,
                       p.username, p.email, p.avatar_url,
                       cb.username AS created_by_name
                FROM feature_flag_overrides o
                JOIN profiles p ON p.id = o.user_id
                LEFT JOIN profiles cb ON cb.id = o.created_by
                WHERE o.flag_id = @id
                ORDER BY o.created_at DESC
                """, new { id });
            return Results.Ok(overrides);
        }).RequireAuthorization("Admin");

        // ── POST /api/admin/feature-flags/{id}/overrides ───────────────────────
        app.MapPost("/api/admin/feature-flags/{id}/overrides", async (
            Guid id,
            [FromBody] CreateFlagOverrideRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:edit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var overrideId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO feature_flag_overrides (flag_id, user_id, value, reason, expires_at, created_by)
                VALUES (@flagId, @userId, @value::jsonb, @reason, @expiresAt, @createdBy)
                ON CONFLICT (flag_id, user_id) DO UPDATE SET
                    value = EXCLUDED.value,
                    reason = EXCLUDED.reason,
                    expires_at = EXCLUDED.expires_at,
                    created_by = EXCLUDED.created_by
                RETURNING id
                """,
                new
                {
                    flagId = id,
                    userId = req.UserId,
                    value = JsonSerializer.Serialize(req.Value),
                    reason = req.Reason,
                    expiresAt = req.ExpiresAt,
                    createdBy = userCtx.UserIdGuid
                });

            return Results.Ok(new { id = overrideId, success = true });
        }).RequireAuthorization("Admin");

        // ── DELETE /api/admin/feature-flags/{id}/overrides/{overrideId} ────────
        app.MapDelete("/api/admin/feature-flags/{id}/overrides/{overrideId}", async (
            Guid id,
            Guid overrideId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Permissions.Contains("feature_flags:edit"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var affected = await conn.ExecuteAsync(
                "DELETE FROM feature_flag_overrides WHERE id = @overrideId AND flag_id = @flagId",
                new { overrideId, flagId = id });

            return affected > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Override not found" });
        }).RequireAuthorization("Admin");

        // ── GET /api/feature-flags/evaluate ────────────────────────────────────
        // Client-side: get evaluated flags for current user
        app.MapGet("/api/feature-flags/evaluate", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            var userId = userCtx?.UserIdGuid;

            using var conn = db.CreateConnection();

            // Get all enabled flags
            var flags = await conn.QueryAsync<FeatureFlagRow>(
                """
                SELECT id, key, default_value, flag_type
                FROM feature_flags
                WHERE is_enabled = true
                """);

            var results = new Dictionary<string, object>();

            foreach (var flag in flags)
            {
                var value = await EvaluateFlagAsync(conn, flag, userId, userCtx);
                results[flag.Key] = value;
            }

            return Results.Ok(results);
        }).RequireAuthorization("Authenticated");
    }

    private static async Task<object> EvaluateFlagAsync(
        System.Data.IDbConnection conn,
        FeatureFlagRow flag,
        Guid? userId,
        UserContext? userCtx)
    {
        // Safe default value parsing
        JsonElement defaultValue;
        try
        {
            defaultValue = string.IsNullOrEmpty(flag.DefaultValue)
                ? JsonDocument.Parse("{\"enabled\":false}").RootElement
                : JsonSerializer.Deserialize<JsonElement>(flag.DefaultValue);
        }
        catch (JsonException)
        {
            defaultValue = JsonDocument.Parse("{\"enabled\":false}").RootElement;
        }

        // 1. Check user override
        if (userId.HasValue)
        {
            var userOverride = await conn.QuerySingleOrDefaultAsync<string>(
                """
                SELECT value FROM feature_flag_overrides
                WHERE flag_id = @flagId AND user_id = @userId
                  AND (expires_at IS NULL OR expires_at > NOW())
                """,
                new { flagId = flag.Id, userId = userId.Value });

            if (!string.IsNullOrEmpty(userOverride))
            {
                try
                {
                    return JsonSerializer.Deserialize<JsonElement>(userOverride);
                }
                catch (JsonException) { /* fall through to rules */ }
            }
        }

        // 2. Check segment rules (in priority order)
        var rules = await conn.QueryAsync<FlagRuleRow>(
            """
            SELECT conditions, value, percentage
            FROM feature_flag_rules
            WHERE flag_id = @flagId
            ORDER BY priority DESC
            """,
            new { flagId = flag.Id });

        foreach (var rule in rules)
        {
            if (MatchesConditions(rule.Conditions, userCtx))
            {
                // Check percentage rollout
                if (rule.Percentage.HasValue && userId.HasValue)
                {
                    var bucket = GetUserBucket(userId.Value, flag.Key);
                    if (bucket >= rule.Percentage.Value)
                        continue; // Doesn't qualify for this rule
                }

                try
                {
                    return JsonSerializer.Deserialize<JsonElement>(rule.Value);
                }
                catch (JsonException)
                {
                    continue; // Skip malformed rule
                }
            }
        }

        // 3. Return default
        return defaultValue;
    }

    private static bool MatchesConditions(string conditionsJson, UserContext? userCtx)
    {
        if (string.IsNullOrEmpty(conditionsJson) || conditionsJson == "{}")
            return true; // Empty conditions = match all

        if (userCtx is null)
            return false;

        var conditions = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(conditionsJson);
        if (conditions is null || conditions.Count == 0)
            return true;

        foreach (var (key, value) in conditions)
        {
            var matches = key.ToLowerInvariant() switch
            {
                "role" => userCtx.Roles.Contains(value.GetString() ?? "", StringComparer.OrdinalIgnoreCase),
                "is_admin" => userCtx.AdminRoles.Length > 0 == value.GetBoolean(),
                "is_super_admin" => userCtx.IsSuperAdmin == value.GetBoolean(),
                _ => true // Unknown conditions (e.g. country) require DB lookup - skip for now
            };

            if (!matches)
                return false;
        }

        return true;
    }

    // Deterministic bucket assignment: same user + flag always gets same bucket
    private static int GetUserBucket(Guid userId, string flagKey)
    {
        var input = $"{userId}:{flagKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // Use unsigned to avoid overflow on Int32.MinValue
        var bucket = (int)(BitConverter.ToUInt32(hash, 0) % 100);
        return bucket;
    }

    private sealed class FeatureFlagRow
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = "";
        public string DefaultValue { get; set; } = "{}";
        public string FlagType { get; set; } = "boolean";
    }

    private sealed class FlagRuleRow
    {
        public string Conditions { get; set; } = "{}";
        public string Value { get; set; } = "{}";
        public int? Percentage { get; set; }
    }
}

public sealed record CreateFeatureFlagRequest(
    string Key,
    string Name,
    string? Description = null,
    [property: JsonPropertyName("flag_type")] string? FlagType = null,
    [property: JsonPropertyName("default_value")] JsonElement? DefaultValue = null,
    [property: JsonPropertyName("is_enabled")] bool? IsEnabled = null);

public sealed record UpdateFeatureFlagRequest(
    string? Name = null,
    string? Description = null,
    [property: JsonPropertyName("is_enabled")] bool? IsEnabled = null,
    [property: JsonPropertyName("default_value")] JsonElement? DefaultValue = null);

public sealed record CreateFlagRuleRequest(
    int? Priority,
    JsonElement? Conditions,
    JsonElement Value,
    int? Percentage = null);

public sealed record UpdateFlagRuleRequest(
    int? Priority = null,
    JsonElement? Conditions = null,
    JsonElement? Value = null,
    int? Percentage = null);

public sealed record CreateFlagOverrideRequest(
    [property: JsonPropertyName("user_id")] Guid UserId,
    JsonElement Value,
    string? Reason = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null);
