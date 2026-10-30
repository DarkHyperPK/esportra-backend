using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Event licensing endpoints (PROJ-058).
/// BE-4: GET  /api/my/event-licenses/quota
/// BE-10: POST /api/admin/tournaments/{id}/organizer-pro
/// </summary>
public static class EventLicenseEndpoints
{
    public static void MapEventLicenseEndpoints(this WebApplication app)
    {
        app.MapProEventRequestEndpoints();
        // ── BE-4: GET /api/my/event-licenses/quota ───────────────────────────
        app.MapGet("/api/my/event-licenses/quota", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var used = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)::int
                FROM event_licenses
                WHERE user_id = @userId
                  AND is_free_quota = TRUE
                  AND date_trunc('month', created_at) = date_trunc('month', NOW())
                """,
                new { userId = userCtx.UserIdGuid });

            var isOrganizerPro = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT COALESCE(bool_or(has_organizer_pro), FALSE)
                FROM event_licenses
                WHERE user_id = @userId
                """,
                new { userId = userCtx.UserIdGuid });

            var now = DateTime.UtcNow;
            var resetsAt = new DateTime(
                now.Month == 12 ? now.Year + 1 : now.Year,
                now.Month == 12 ? 1 : now.Month + 1,
                1,
                0, 0, 0,
                DateTimeKind.Utc);

            return Results.Ok(new
            {
                usedThisMonth = used,
                monthlyLimit = 2,
                resetsAt = resetsAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                isOrganizerPro,
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/admin/tournaments/{id}/organizer-pro ────────────────────
        app.MapGet("/api/admin/tournaments/{id}/organizer-pro", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<(bool Exists, bool HasPro)>(
                """
                SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id) AS Exists,
                       COALESCE((SELECT has_organizer_pro FROM event_licenses
                                 WHERE tournament_id = @id LIMIT 1), FALSE) AS HasPro
                """,
                new { id });
            if (!row.Exists) return Results.NotFound();
            return Results.Ok(new { hasOrganizerPro = row.HasPro });
        }).RequireAuthorization("Admin");

        // ── BE-10: POST /api/admin/tournaments/{id}/organizer-pro ────────────
        app.MapPost("/api/admin/tournaments/{id}/organizer-pro", async (
            Guid id,
            [FromBody] SetOrganizerProRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            EventLicenseService licenseService,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var tournamentExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id)",
                new { id });
            if (!tournamentExists)
                return Results.NotFound(new { error = "Tournament not found." });

            await licenseService.UpsertOrganizerProAsync(id, req.Enabled, ct);

            await conn.ExecuteAsync(
                """
                INSERT INTO staff_audit_log
                    (organization_id, actor_id, action, target_type, target_id, details)
                VALUES
                    (NULL, @actorId, 'set_organizer_pro', 'tournament', @targetId::text, @details::jsonb)
                """,
                new
                {
                    actorId = userCtx.UserIdGuid,
                    targetId = id.ToString(),
                    details = JsonSerializer.Serialize(new
                    {
                        tournament_id = id,
                        enabled = req.Enabled,
                        actor_user_id = userCtx.UserIdGuid,
                    }),
                });

            return Results.Ok(new { tournamentId = id, hasOrganizerPro = req.Enabled });
        }).RequireAuthorization("Admin");
    }
}

public sealed record SetOrganizerProRequest(bool Enabled);
