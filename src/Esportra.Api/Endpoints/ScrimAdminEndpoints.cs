using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Admin endpoints for scrim dispute resolution (PROJ-078).
///
/// GET  /api/admin/scrims/disputes?status=open&amp;page=1&amp;pageSize=20 — paginated list (requires disputes:view)
/// POST /api/admin/scrims/disputes/{id}/resolve                      — resolve dispute (requires disputes:resolve)
/// POST /api/admin/scrims/sessions/{id}/reset-time                   — clear a scrim's time at any stage (requires disputes:resolve)
/// </summary>
public static class ScrimAdminEndpoints
{
    public static void MapScrimAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/scrims/disputes", GetDisputesAsync)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/admin/scrims/disputes/{id:guid}", GetDisputeDetailAsync)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/admin/scrims/disputes/{id:guid}/resolve", ResolveDisputeAsync)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/admin/scrims/sessions/{id:guid}/reset-time", ResetTimeAsync)
            .RequireAuthorization("Authenticated");
    }

    // ── GET /api/admin/scrims/disputes ─────────────────────────────────────────

    private static async Task<IResult> GetDisputesAsync(
        [FromQuery] string? status,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.DisputesView))
            return Results.Forbid();

        var actualPage = Math.Max(page <= 0 ? 1 : page, 1);
        var actualSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);
        var offset = (actualPage - 1) * actualSize;

        // Default to 'open' if no status filter supplied.
        var statusFilter = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().ToLowerInvariant();

        using var conn = db.CreateConnection();

        var totalCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM public.scrim_disputes WHERE status = @statusFilter::scrim_dispute_status",
            new { statusFilter });

        var rows = await conn.QueryAsync<ScrimDisputeAdminRow>(
            """
            SELECT sd.id, sd.session_id, sd.raised_by_team_id, sd.reason, sd.status,
                   sd.evidence_urls, sd.responder_evidence_urls, sd.responder_notes,
                   sd.resolved_by, sd.outcome, sd.resolution_notes,
                   sd.resolved_at, sd.created_at, sd.game_number,
                   rt.name AS raised_by_team_name,
                   ss.team1_id, ss.team2_id, ss.game_slug, ss.scheduled_at,
                   t1.name AS team1_name, t2.name AS team2_name
            FROM public.scrim_disputes sd
            JOIN public.scrim_sessions ss ON ss.id = sd.session_id
            JOIN public.teams rt ON rt.id = sd.raised_by_team_id
            JOIN public.teams t1 ON t1.id = ss.team1_id
            JOIN public.teams t2 ON t2.id = ss.team2_id
            WHERE sd.status = @statusFilter::scrim_dispute_status
            ORDER BY sd.created_at DESC
            LIMIT @actualSize OFFSET @offset
            """,
            new { statusFilter, actualSize, offset });

        return Results.Ok(new { items = rows, total = totalCount, page = actualPage, pageSize = actualSize });
    }

    // ── GET /api/admin/scrims/disputes/{id} ────────────────────────────────────

    private static async Task<IResult> GetDisputeDetailAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.DisputesView))
            return Results.Forbid();

        using var conn = db.CreateConnection();

        var dispute = await conn.QuerySingleOrDefaultAsync<ScrimDisputeDetailRow>(
            """
            SELECT
                sd.id, sd.session_id, sd.raised_by_team_id, sd.reason, sd.status,
                sd.evidence_urls, sd.responder_evidence_urls, sd.responder_notes,
                sd.resolved_by, sd.outcome, sd.resolution_notes,
                sd.resolved_at, sd.created_at, sd.game_number,
                rt.name                             AS raised_by_team_name,
                ss.team1_id, ss.team2_id,
                ss.game_slug, ss.scheduled_at,
                ss.best_of, ss.team1_score, ss.team2_score,
                ss.status                           AS session_status,
                ss.match_format,
                t1.name                             AS team1_name,
                t2.name                             AS team2_name,
                pu.username                         AS resolved_by_username
            FROM public.scrim_disputes sd
            JOIN public.scrim_sessions ss  ON ss.id  = sd.session_id
            JOIN public.teams rt           ON rt.id  = sd.raised_by_team_id
            JOIN public.teams t1           ON t1.id  = ss.team1_id
            JOIN public.teams t2           ON t2.id  = ss.team2_id
            LEFT JOIN public.profiles pu   ON pu.id  = sd.resolved_by
            WHERE sd.id = @id
            """,
            new { id });

        if (dispute is null) return Results.NotFound();

        var games = await conn.QueryAsync<ScrimDisputeGameRow>(
            """
            SELECT game_number, status, map_name, team1_score, team2_score, winner_slot,
                   source, screenshot_urls, notes, reported_at, confirmed_at, auto_confirmed,
                   disputed_by_team_id
            FROM public.scrim_games
            WHERE session_id = @sessionId
            ORDER BY game_number
            """,
            new { sessionId = dispute.SessionId });

        return Results.Ok(new { dispute, games });
    }

    // ── POST /api/admin/scrims/disputes/{id}/resolve ────────────────────────────

    private static async Task<IResult> ResolveDisputeAsync(
        Guid id,
        [FromBody] ResolveScrimDisputeRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<NotificationHub> hub,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.DisputesResolve))
            return Results.Forbid();

        if (string.IsNullOrWhiteSpace(req.Outcome))
            return Results.UnprocessableEntity(new { error = "outcome is required." });
        if (req.Outcome is not ("team1_wins" or "team2_wins" or "no_contest"))
            return Results.UnprocessableEntity(new { error = "outcome must be 'team1_wins', 'team2_wins', or 'no_contest'." });
        if (req.ResolutionNotes?.Length > 500)
            return Results.UnprocessableEntity(new { error = "resolution_notes max 500 chars." });

        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // Resolve dispute — idempotency guard via AND status IN ('open','escalated').
            var resolved = await conn.QuerySingleOrDefaultAsync<ScrimDisputeResolveRef>(
                """
                UPDATE public.scrim_disputes
                SET status           = 'resolved'::scrim_dispute_status,
                    resolved_by      = @resolvedBy,
                    resolved_at      = now(),
                    outcome          = @outcome,
                    resolution_notes = @resolutionNotes
                WHERE id = @id
                  AND status IN ('open', 'escalated')
                RETURNING session_id, raised_by_team_id, game_number
                """,
                new
                {
                    id,
                    resolvedBy = userCtx.UserIdGuid,
                    outcome = req.Outcome,
                    resolutionNotes = req.ResolutionNotes,
                },
                tx);

            if (resolved is null)
                return Results.Conflict(new { error = "Dispute not found or already resolved." });

            // A dispute about one map settles that map; the series follows from the settled maps.
            if (resolved.GameNumber is { } disputedGame)
            {
                await ScrimGameEndpoints.ResolveGameAsync(conn, tx, resolved.SessionId, disputedGame, req.Outcome);
            }
            // Update session status based on outcome.
            else if (req.Outcome != "no_contest")
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE public.scrim_sessions
                    SET status = 'completed'::scrim_session_status, updated_at = now()
                    WHERE id = @sessionId
                    """,
                    new { sessionId = resolved.SessionId },
                    tx);
            }

            tx.Commit();

            // Notify both teams' captains/managers within 30 seconds of resolution.
            var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionParties>(
                "SELECT team1_id, team2_id FROM public.scrim_sessions WHERE id = @sessionId",
                new { sessionId = resolved.SessionId });

            if (session is not null)
            {
                var allStaffIds = (await conn.QueryAsync<Guid>(
                    """
                    SELECT user_id FROM public.team_members
                    WHERE team_id IN (@t1, @t2)
                      AND role IN ('captain', 'manager')
                      AND is_active = TRUE
                    """,
                    new { t1 = session.Team1Id, t2 = session.Team2Id })).ToList();

                var outcomeLabel = req.Outcome switch
                {
                    "team1_wins" => "Team 1 wins",
                    "team2_wins" => "Team 2 wins",
                    _ => "No contest",
                };
                var message = $"Scrim dispute resolved — {outcomeLabel}.";

                foreach (var staffId in allStaffIds)
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO public.notifications (user_id, type, title, message, link, is_read)
                        VALUES (@userId, 'scrim_dispute_resolved'::notification_type,
                                'Scrim Dispute Resolved', @message,
                                '/competitive/scrims/workspace', FALSE)
                        """,
                        new { userId = staffId, message });
                }

                await TeamNotifications.PushAsync(hub, allStaffIds, "scrim_dispute_resolved",
                    "Scrim Dispute Resolved", message);
            }

            var dispute = await conn.QuerySingleOrDefaultAsync<ScrimDisputeRow>(
                "SELECT * FROM public.scrim_disputes WHERE id = @id", new { id });
            return Results.Ok(dispute);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── POST /api/admin/scrims/sessions/{id}/reset-time ────────────────────────

    /// <summary>
    /// Platform staff clear a scrim's time even after check-in started (a stuck or mistaken schedule).
    /// Check-ins are cleared too, so both teams start the time agreement again.
    /// </summary>
    private static async Task<IResult> ResetTimeAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<NotificationHub> hub,
        IBackgroundJobClient jobs,
        CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext userCtx) return Results.Unauthorized();
        if (!userCtx.IsSuperAdmin && !userCtx.Permissions.Contains(Permissions.DisputesResolve))
            return Results.Forbid();

        using var conn = db.CreateConnection();
        var session = await conn.QuerySingleOrDefaultAsync<ScrimSessionParties>(
            "SELECT team1_id, team2_id FROM public.scrim_sessions WHERE id = @id AND status IN ('scheduled', 'in_progress')",
            new { id });
        if (session is null) return Results.NotFound(new { error = "Only a scheduled or in-progress scrim can be reset." });

        await ScrimTimeReset.ApplyAsync(conn, jobs, id, clearCheckins: true);

        foreach (var team in new[] { session.Team1Id, session.Team2Id })
            await ScrimTeamNotifier.SendAsync(conn, hub, team, id, "scrim_time_reset",
                "Match time cleared", "Staff cleared the time of your scrim. Agree a new time in the room.");
        return Results.Ok(new { session_id = id });
    }

    // ── Private projections ────────────────────────────────────────────────────

    private sealed record ScrimDisputeDetailRow(
        Guid Id, Guid SessionId, Guid RaisedByTeamId, string Reason, string Status,
        string[]? EvidenceUrls, string[]? ResponderEvidenceUrls, string? ResponderNotes,
        Guid? ResolvedBy, string? Outcome, string? ResolutionNotes,
        DateTime? ResolvedAt, DateTime CreatedAt, int? GameNumber,
        string? RaisedByTeamName,
        Guid Team1Id, Guid Team2Id, string? GameSlug, DateTime? ScheduledAt,
        int? BestOf, int Team1Score, int Team2Score, string SessionStatus, string? MatchFormat,
        string? Team1Name, string? Team2Name, string? ResolvedByUsername);

    private sealed record ScrimDisputeGameRow(
        int GameNumber, string Status, string? MapName,
        int Team1Score, int Team2Score, int? WinnerSlot,
        string Source, string[]? ScreenshotUrls, string? Notes,
        DateTime ReportedAt, DateTime? ConfirmedAt, bool AutoConfirmed,
        Guid? DisputedByTeamId);

    private sealed record ScrimDisputeAdminRow(
        Guid Id, Guid SessionId, Guid RaisedByTeamId, string Reason, string Status,
        string[]? EvidenceUrls, string[]? ResponderEvidenceUrls, string? ResponderNotes,
        Guid? ResolvedBy, string? Outcome, string? ResolutionNotes,
        DateTime? ResolvedAt, DateTime CreatedAt, int? GameNumber,
        string? RaisedByTeamName,
        Guid Team1Id, Guid Team2Id, string? GameSlug, DateTime? ScheduledAt,
        string? Team1Name, string? Team2Name);

    private sealed record ScrimDisputeResolveRef(Guid SessionId, Guid RaisedByTeamId, int? GameNumber);

    private sealed record ScrimSessionParties(Guid Team1Id, Guid Team2Id);
}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record ResolveScrimDisputeRequest(
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("resolution_notes")] string? ResolutionNotes = null);
