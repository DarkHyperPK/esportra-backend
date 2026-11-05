using System.Data;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;

namespace Esportra.Api.Endpoints;

public sealed record ResolveSquadUpReportRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("action")] string Action,
    [property: System.Text.Json.Serialization.JsonPropertyName("duration_hours")] int? DurationHours = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("note")] string? Note = null);

/// <summary>Admin decision on a report: dismiss, warn, restrict from Squad Up, or suspend the account.</summary>
internal static class SquadUpReportActions
{
    public static readonly IReadOnlyDictionary<string, string> ResolutionByAction = new Dictionary<string, string>
    {
        ["dismiss"] = "dismissed",
        ["warn"] = "warned",
        ["squad_up_ban"] = "squad_up_ban",
        ["suspend"] = "suspended",
    };

    private static readonly int[] AllowedDurations = [0, 24, 168, 720]; // 0 = permanent

    private sealed record ReportRef(Guid ReportedId, Guid? ReporterId, string Reason, string Status);

    /// <returns>An error result, or null when the request is valid for this admin.</returns>
    public static IResult? Validate(ResolveSquadUpReportRequest req, UserContext admin)
    {
        if (!ResolutionByAction.ContainsKey(req.Action ?? "")) return Results.BadRequest(new { error = "INVALID_ACTION" });
        var note = req.Note?.Trim() ?? "";
        if (note.Length > 500) return Results.BadRequest(new { error = "NOTE_TOO_LONG" });
        if (req.Action != "dismiss" && note.Length < 5) return Results.BadRequest(new { error = "NOTE_REQUIRED" });
        if (req.Action == "squad_up_ban" && (req.DurationHours is not { } hours || !AllowedDurations.Contains(hours)))
            return Results.BadRequest(new { error = "INVALID_DURATION" });
        if (req.Action == "suspend" && !admin.IsSuperAdmin && !admin.Permissions.Contains(Permissions.UsersBan))
            return Results.Forbid();
        return null;
    }

    public static async Task<(IResult Result, IReadOnlyList<SwipePush> Pushes)> ResolveAsync(
        HttpContext ctx, UserContext admin, Guid reportId, ResolveSquadUpReportRequest req,
        IDbConnectionFactory db, UserSuspensionService suspension, AuditService audit, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var report = await conn.QuerySingleOrDefaultAsync<ReportRef>(
            "SELECT reported_id, reporter_id, reason, status FROM public.squad_up_reports WHERE id = @reportId", new { reportId });
        if (report is null) return (Results.NotFound(), []);
        if (report.Status != "open") return (Results.Conflict(new { error = "ALREADY_RESOLVED" }), []);

        var note = req.Note?.Trim() ?? "";
        if (req.Action == "suspend")
            await suspension.SuspendAsync(ctx, admin, report.ReportedId, $"Squad Up report: {note}", null, null, ct);

        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var locked = await conn.ExecuteScalarAsync<string?>(
            "SELECT status FROM public.squad_up_reports WHERE id = @reportId FOR UPDATE", new { reportId }, tx);
        if (locked != "open") return (Results.Conflict(new { error = "ALREADY_RESOLVED" }), []);

        var pushes = await ApplyAsync(conn, tx, admin, reportId, report, req);
        await conn.ExecuteAsync(
            """
            UPDATE public.squad_up_reports
            SET status = 'resolved', resolution = @resolution, resolution_note = @note, resolved_by = @adminId, resolved_at = now()
            WHERE id = @reportId
            """, new { reportId, resolution = ResolutionByAction[req.Action], note = note.Length == 0 ? null : note, adminId = admin.UserIdGuid }, tx);
        if (report.ReporterId is { } reporter)
            pushes.Add(await SquadUpModeration.ReporterAsync(conn, tx, reporter, reportId, req.Action != "dismiss"));
        tx.Commit();

        await audit.LogFromHttp(ctx, admin, ActionType.Resolve, TargetType.User, report.ReportedId, report.ReportedId.ToString(),
            new { report_id = reportId, action = req.Action, note, duration_hours = req.DurationHours }, ct: ct);
        return (Results.Ok(new { id = reportId, status = "resolved", resolution = ResolutionByAction[req.Action] }), pushes);
    }

    private static async Task<List<SwipePush>> ApplyAsync(
        IDbConnection conn, IDbTransaction tx, UserContext admin, Guid reportId, ReportRef report, ResolveSquadUpReportRequest req)
    {
        var pushes = new List<SwipePush>();
        if (req.Action == "warn")
            pushes.Add(await SquadUpModeration.WarningAsync(conn, tx, report.ReportedId, reportId, report.Reason));
        if (req.Action == "squad_up_ban")
        {
            DateTime? until = req.DurationHours is > 0 ? DateTime.UtcNow.AddHours(req.DurationHours.Value) : null;
            await conn.ExecuteAsync(
                "INSERT INTO public.squad_up_restrictions (user_id, until, reason_category, report_id, created_by) VALUES (@userId, @until, @reason, @reportId, @adminId)",
                new { userId = report.ReportedId, until, reason = report.Reason, reportId, adminId = admin.UserIdGuid }, tx);
            await conn.ExecuteAsync(
                "DELETE FROM public.lfg_swipe_sessions WHERE user_id = @userId AND game_slug = 'valorant'", new { userId = report.ReportedId }, tx);
            pushes.Add(await SquadUpModeration.RestrictedAsync(conn, tx, report.ReportedId, reportId, report.Reason, until));
        }
        return pushes;
    }
}
