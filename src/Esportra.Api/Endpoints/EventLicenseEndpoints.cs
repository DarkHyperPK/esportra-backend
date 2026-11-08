using System.Text.Json;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Event licensing endpoints (PROJ-058).
/// BE-4: GET  /api/my/event-licenses/quota
/// GET  /api/tournaments/{id}/organizer-pro
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

        // ── GET /api/tournaments/{id}/organizer-pro ──────────────────────────
        // Per-tournament Organizer Pro state for the organizer console, using the same
        // rule the gated endpoints enforce (EventLicenseService.IsOrganizerProAsync:
        // pro enabled, or no license row = grandfathered). Organizer, org owner,
        // tournament staff and platform admins only.
        app.MapGet("/api/tournaments/{id:guid}/organizer-pro", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            EventLicenseService licenseService,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id AND deleted_at IS NULL)",
                new { id });
            if (!exists) return Results.NotFound();

            if (!StaffAuthHelper.IsPlatformAdmin(userCtx)
                && !await StaffAuthHelper.CanActOnTournamentAsync(conn, userCtx.UserIdGuid, id))
                return Results.Forbid();

            var isOrganizerPro = await licenseService.IsOrganizerProAsync(id, ct);
            return Results.Ok(new { tournamentId = id, isOrganizerPro });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/admin/tournaments/{id}/organizer-pro ────────────────────
        app.MapGet("/api/admin/tournaments/{id}/organizer-pro", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM tournaments WHERE id = @id)", new { id });
            if (!exists) return Results.NotFound();

            var lic = await conn.QuerySingleOrDefaultAsync<(bool HasPro, string Tier, int MaxTeams)>(
                """
                SELECT has_organizer_pro AS HasPro,
                       bracket_tier      AS Tier,
                       bracket_max_teams AS MaxTeams
                FROM event_licenses
                WHERE tournament_id = @id
                LIMIT 1
                """,
                new { id });

            return Results.Ok(new
            {
                hasOrganizerPro  = lic == default ? false : lic.HasPro,
                bracketTier      = lic == default ? "free" : lic.Tier,
                bracketMaxTeams  = lic == default ? 32 : lic.MaxTeams,
            });
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

            if (req.BracketTier is not null)
                await licenseService.UpsertLicenseAsync(id, req.BracketTier, req.Enabled ?? false, ct);
            else
                await licenseService.UpsertOrganizerProAsync(id, req.Enabled ?? false, ct);

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
                        enabled       = req.Enabled,
                        bracket_tier  = req.BracketTier,
                        actor_user_id = userCtx.UserIdGuid,
                    }),
                });

            return Results.Ok(new { tournamentId = id, hasOrganizerPro = req.Enabled ?? false, bracketTier = req.BracketTier });
        }).RequireAuthorization("Admin");
    }
}

public sealed record SetOrganizerProRequest(bool? Enabled = null, string? BracketTier = null);
