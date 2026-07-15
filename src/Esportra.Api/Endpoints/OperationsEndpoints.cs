using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/operations")
            .RequireAuthorization("Admin");

        group.MapGet("/system-config", async (
            [FromQuery] string? category,
            HttpContext ctx,
            IDbConnectionFactory db,
            OperationsAuthorizationService authz,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!authz.HasPermission(userCtx, Permissions.SystemConfigView))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(new CommandDefinition(
                """
                SELECT key, value, category, label, description, data_type,
                       is_kill_switch, is_sensitive, updated_by, updated_at
                FROM public.system_config
                WHERE (@category IS NULL OR category = @category)
                ORDER BY is_kill_switch DESC, category, key
                """,
                new { category },
                cancellationToken: ct));

            return Results.Ok(rows);
        });

        group.MapPut("/system-config/{key}", async (
            string key,
            [FromBody] UpdateSystemConfigRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            OperationsAuthorizationService authz,
            OperationsAuditService audit,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(new CommandDefinition(
                """
                SELECT key, value, data_type, is_kill_switch, is_sensitive
                FROM public.system_config
                WHERE key = @key
                """,
                new { key },
                cancellationToken: ct));
            if (existing is null) return Results.NotFound(new { error = $"Config '{key}' was not found." });

            var isKillSwitch = (bool)existing.is_kill_switch;
            var requiredPermission = isKillSwitch ? Permissions.SystemKillSwitch : Permissions.SystemConfigEdit;
            if (!authz.HasPermission(userCtx, requiredPermission))
                return Results.Forbid();

            string newValue;
            try
            {
                newValue = NormalizeConfigValue(req.Value, (string)existing.data_type);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            var before = existing.value;

            await audit.WriteFromHttpAsync(
                ctx,
                userCtx,
                isKillSwitch ? "system.kill_switch.update" : "system.config.update",
                "system_config",
                key,
                new
                {
                    before = new { key, value = before },
                    after = new { key, value = JsonSerializer.Deserialize<object>(newValue) },
                    reason = req.Reason
                },
                isKillSwitch ? "critical" : "high",
                ct);

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE public.system_config
                SET value = @value::jsonb, updated_by = @updatedBy
                WHERE key = @key
                """,
                new { key, value = newValue, updatedBy = userCtx.UserIdGuid },
                cancellationToken: ct));

            return Results.Ok(new { success = true, key });
        });

        group.MapPost("/impersonation/start", async (
            [FromBody] StartImpersonationRequest req,
            HttpContext ctx,
            GhostModeTokenService ghostMode,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            try
            {
                var result = await ghostMode.StartAsync(ctx, userCtx, req.TargetUserId, req.Reason, req.Scopes, ct);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/operations/impersonation/end", async (
            HttpContext ctx,
            GhostModeTokenService ghostMode,
            OperationsAuditService audit,
            CancellationToken ct) =>
        {
            var ghost = ctx.Items["GhostMode"] as GhostModeContext;
            if (ghost is null) return Results.BadRequest(new { error = "No active Ghost Mode session is attached to this request." });

            await ghostMode.EndAsync(ctx, ghost, ct);
            var adminCtx = new UserContext
            {
                UserId = ghost.AdminId.ToString(),
                Email = "ghost-mode-admin",
                AdminRoles = [AdminRoles.SuperAdmin],
                Permissions = [Permissions.ImpersonationStop]
            };
            await audit.WriteFromHttpAsync(
                ctx,
                adminCtx,
                "impersonation.stop",
                "user",
                ghost.TargetUserId.ToString(),
                new
                {
                    before = new { active = true, session_id = ghost.SessionId },
                    after = new { active = false, session_id = ghost.SessionId }
                },
                "critical",
                ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        return app;
    }

    private static string NormalizeConfigValue(JsonElement value, string dataType)
    {
        return dataType switch
        {
            "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                value.GetBoolean() ? "true" : "false",
            "number" when value.ValueKind is JsonValueKind.Number =>
                value.GetRawText(),
            "string" when value.ValueKind is JsonValueKind.String =>
                JsonSerializer.Serialize(value.GetString() ?? string.Empty),
            "json" =>
                value.GetRawText(),
            _ => throw new ArgumentException($"Invalid value for data type '{dataType}'.")
        };
    }
}

public sealed record UpdateSystemConfigRequest(JsonElement Value, string? Reason);

public sealed record StartImpersonationRequest(
    [property: JsonPropertyName("targetUserId")] Guid TargetUserId,
    string Reason,
    string[]? Scopes = null);
