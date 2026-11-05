using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Admin Centre: Squad Up reports and restrictions.
/// GET  /api/admin/squad-up/reports?status=&amp;limit=&amp;offset=   — queue (squad_up:view)
/// GET  /api/admin/squad-up/reports/{id}                       — detail with 5-minute signed screenshot URLs (squad_up:view)
/// POST /api/admin/squad-up/reports/{id}/resolve               — dismiss / warn / squad_up_ban / suspend (squad_up:manage; suspend also users:ban)
/// GET  /api/admin/squad-up/restrictions                       — active restrictions (squad_up:view)
/// POST /api/admin/squad-up/restrictions/{id}/lift             — lift early (squad_up:manage)
/// </summary>
public static class SquadUpReportAdminEndpoints
{
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(5);

    public static void MapSquadUpReportAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/squad-up/reports", ListAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/admin/squad-up/reports/{id:guid}", DetailAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/admin/squad-up/reports/{id:guid}/resolve", ResolveAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/admin/squad-up/restrictions", RestrictionsAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/admin/squad-up/restrictions/{id:guid}/lift", LiftAsync).RequireAuthorization("Authenticated");
    }

    private static bool Allowed(UserContext user, string permission) => user.IsSuperAdmin || user.Permissions.Contains(permission);

    private static async Task<IResult> ListAsync(
        [FromQuery] string? status, [FromQuery] int? limit, [FromQuery] int? offset, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext admin) return Results.Unauthorized();
        if (!Allowed(admin, SquadUpPermissions.View)) return Results.Forbid();
        var filter = status is "open" or "resolved" ? status : "all";
        using var conn = db.CreateConnection();
        var args = new { filter, limit = Math.Clamp(limit ?? 25, 1, 100), offset = Math.Max(offset ?? 0, 0) };
        var total = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM public.squad_up_reports r WHERE (@filter = 'all' OR r.status = @filter)", args);
        var items = await conn.QueryAsync<SquadUpAdminReportRow>(
            """
            SELECT r.id, r.reason, r.status, r.resolution, r.created_at, r.resolved_at, r.reported_id,
                   ru.username AS reported_username, rp.username AS reporter_username,
                   (SELECT count(*) FROM public.squad_up_report_evidence e WHERE e.report_id = r.id)::int AS evidence_count,
                   (SELECT count(*) FROM public.squad_up_reports o WHERE o.reported_id = r.reported_id AND o.id <> r.id
                      AND o.resolution IN ('warned', 'squad_up_ban', 'suspended'))::int AS prior_actions
            FROM public.squad_up_reports r
            JOIN public.profiles ru ON ru.id = r.reported_id
            LEFT JOIN public.profiles rp ON rp.id = r.reporter_id
            WHERE (@filter = 'all' OR r.status = @filter)
            ORDER BY (r.status = 'open') DESC, r.created_at DESC
            LIMIT @limit OFFSET @offset
            """, args);
        return Results.Ok(new { total, items });
    }

    private static async Task<IResult> DetailAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, IReportProofStore store, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext admin) return Results.Unauthorized();
        if (!Allowed(admin, SquadUpPermissions.View)) return Results.Forbid();
        using var conn = db.CreateConnection();
        var report = await conn.QuerySingleOrDefaultAsync<SquadUpAdminReportDetailRow>(
            """
            SELECT r.id, r.reason, r.status, r.resolution, r.resolution_note, r.context, r.details, r.proof_link,
                   r.snapshot::text AS snapshot, r.match_id, r.created_at, r.resolved_at,
                   r.reporter_id, rp.username AS reporter_username, r.reported_id, ru.username AS reported_username,
                   ap.username AS resolved_by_username
            FROM public.squad_up_reports r
            JOIN public.profiles ru ON ru.id = r.reported_id
            LEFT JOIN public.profiles rp ON rp.id = r.reporter_id
            LEFT JOIN public.profiles ap ON ap.id = r.resolved_by
            WHERE r.id = @id
            """, new { id });
        if (report is null) return Results.NotFound();
        var paths = (await conn.QueryAsync<(Guid Id, string Path, string ContentType)>(
            "SELECT id, storage_path, content_type FROM public.squad_up_report_evidence WHERE report_id = @id ORDER BY created_at", new { id })).ToList();
        var evidence = await Task.WhenAll(paths.Select(async p => new
        {
            p.Id,
            p.ContentType,
            url = await store.CreateSignedUrlAsync(p.Path, EvidenceLifetime, ct),
        }));
        var history = await conn.QueryAsync<SquadUpAdminReportRow>(
            """
            SELECT r.id, r.reason, r.status, r.resolution, r.created_at, r.resolved_at, r.reported_id, '' AS reported_username
            FROM public.squad_up_reports r WHERE r.reported_id = @reported AND r.id <> @id ORDER BY r.created_at DESC LIMIT 10
            """, new { reported = report.ReportedId, id });
        var restrictions = await conn.QueryAsync<SquadUpRestrictionRow>(
            ActiveRestrictionsSql + " AND x.user_id = @reported ORDER BY x.created_at DESC", new { reported = report.ReportedId });
        return Results.Ok(new
        {
            report = new
            {
                report.Id,
                report.Reason,
                report.Status,
                report.Resolution,
                report.ResolutionNote,
                report.Context,
                report.Details,
                report.ProofLink,
                snapshot = ParseSnapshot(report.Snapshot),
                report.MatchId,
                report.CreatedAt,
                report.ResolvedAt,
                report.ReporterId,
                report.ReporterUsername,
                report.ReportedId,
                report.ReportedUsername,
                report.ResolvedByUsername,
            },
            evidence,
            history,
            restrictions,
        });
    }

    private static JsonElement ParseSnapshot(string? snapshot) =>
        JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshot) ? "{}" : snapshot).RootElement.Clone();

    private static async Task<IResult> ResolveAsync(
        Guid id, [FromBody] ResolveSquadUpReportRequest? req, HttpContext ctx, IDbConnectionFactory db,
        UserSuspensionService suspension, AuditService audit, IHubContext<NotificationHub> hub, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext admin) return Results.Unauthorized();
        if (!Allowed(admin, SquadUpPermissions.Manage)) return Results.Forbid();
        if (req is null) return Results.BadRequest(new { error = "INVALID_ACTION" });
        var invalid = SquadUpReportActions.Validate(req, admin);
        if (invalid is not null) return invalid;
        var (result, pushes) = await SquadUpReportActions.ResolveAsync(ctx, admin, id, req, db, suspension, audit, ct);
        await SquadUpModeration.PushAsync(hub, pushes);
        return result;
    }

    internal const string ActiveRestrictionsSql =
        """
        SELECT x.id, x.user_id, p.username, x.until, x.reason_category, x.report_id, x.created_at, ap.username AS created_by_username
        FROM public.squad_up_restrictions x
        JOIN public.profiles p ON p.id = x.user_id
        LEFT JOIN public.profiles ap ON ap.id = x.created_by
        WHERE x.lifted_at IS NULL AND (x.until IS NULL OR x.until > now())
        """;

    private static async Task<IResult> RestrictionsAsync(HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext admin) return Results.Unauthorized();
        if (!Allowed(admin, SquadUpPermissions.View)) return Results.Forbid();
        using var conn = db.CreateConnection();
        return Results.Ok(await conn.QueryAsync<SquadUpRestrictionRow>(ActiveRestrictionsSql + " ORDER BY x.created_at DESC LIMIT 200"));
    }

    private static async Task<IResult> LiftAsync(Guid id, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext admin) return Results.Unauthorized();
        if (!Allowed(admin, SquadUpPermissions.Manage)) return Results.Forbid();
        using var conn = db.CreateConnection();
        var lifted = await conn.ExecuteAsync(
            "UPDATE public.squad_up_restrictions SET lifted_at = now(), lifted_by = @adminId WHERE id = @id AND lifted_at IS NULL",
            new { id, adminId = admin.UserIdGuid });
        return lifted == 0 ? Results.NotFound() : Results.NoContent();
    }
}

public sealed record SquadUpAdminReportRow
{
    public Guid Id { get; init; }
    public string Reason { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Resolution { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public Guid ReportedId { get; init; }
    public string? ReportedUsername { get; init; }
    public string? ReporterUsername { get; init; }
    public int EvidenceCount { get; init; }
    public int PriorActions { get; init; }
}

public sealed record SquadUpAdminReportDetailRow
{
    public Guid Id { get; init; }
    public string Reason { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Resolution { get; init; }
    public string? ResolutionNote { get; init; }
    public string Context { get; init; } = "";
    public string Details { get; init; } = "";
    public string? ProofLink { get; init; }
    public string? Snapshot { get; init; }
    public Guid? MatchId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public Guid? ReporterId { get; init; }
    public string? ReporterUsername { get; init; }
    public Guid ReportedId { get; init; }
    public string? ReportedUsername { get; init; }
    public string? ResolvedByUsername { get; init; }
}

public sealed record SquadUpRestrictionRow
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public string? Username { get; init; }
    public DateTime? Until { get; init; }
    public string ReasonCategory { get; init; } = "";
    public Guid? ReportId { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedByUsername { get; init; }
}
