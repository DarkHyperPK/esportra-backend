using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Team Recruitment pipeline (PROJ-066 Phase 2).
///
/// POST   /api/recruitment/openings                             — create opening
/// GET    /api/recruitment/openings?team_id={id}               — team's openings (staff only)
/// GET    /api/recruitment/openings/discover?game={slug}&...   — public discovery (open openings)
/// GET    /api/recruitment/openings/{id}                       — opening detail
/// PUT    /api/recruitment/openings/{id}                       — update opening (staff only)
/// POST   /api/recruitment/openings/{id}/publish               — set status = open
/// POST   /api/recruitment/openings/{id}/pause                 — set status = paused
/// POST   /api/recruitment/openings/{id}/close                 — set status = closed
/// POST   /api/recruitment/openings/{id}/apply                 — apply (player)
/// GET    /api/recruitment/openings/{id}/applications          — list applications (staff only)
/// PUT    /api/recruitment/applications/{id}/status            — advance or withdraw status
/// GET    /api/recruitment/applications/mine                   — own applications
/// </summary>
public static class RecruitmentEndpoints
{
    private static readonly HashSet<string> StaffAdvanceable =
        ["submitted", "under_review", "shortlisted", "trial_invited", "offer_extended"];

    public static void MapRecruitmentEndpoints(this WebApplication app)
    {
        // Static segments before parameterized.
        app.MapGet("/api/recruitment/openings/discover", DiscoverOpeningsAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/recruitment/applications/mine", GetMineApplicationsAsync)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/recruitment/openings", CreateOpeningAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/recruitment/openings", GetTeamOpeningsAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/recruitment/openings/{id:guid}", GetOpeningAsync)
            .RequireAuthorization("Authenticated");
        app.MapPut("/api/recruitment/openings/{id:guid}", UpdateOpeningAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/recruitment/openings/{id:guid}/publish", PublishOpeningAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/recruitment/openings/{id:guid}/pause", PauseOpeningAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/recruitment/openings/{id:guid}/close", CloseOpeningAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/recruitment/openings/{id:guid}/apply", ApplyAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/recruitment/openings/{id:guid}/applications", GetApplicationsForOpeningAsync)
            .RequireAuthorization("Authenticated");
        app.MapPut("/api/recruitment/applications/{id:guid}/status", UpdateApplicationStatusAsync)
            .RequireAuthorization("Authenticated");
    }

    // ── POST /api/recruitment/openings ─────────────────────────────────────────

    private static async Task<IResult> CreateOpeningAsync(
        [FromBody] CreateOpeningRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.TeamId == Guid.Empty) return Results.BadRequest(new { error = "team_id is required." });
        if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "title is required." });
        if (req.Title.Length > 200) return Results.BadRequest(new { error = "title max 200 chars." });
        if (req.Description?.Length > 2000) return Results.BadRequest(new { error = "description max 2000 chars." });
        if (req.PositionsAvailable is < 1) return Results.BadRequest(new { error = "positions_available must be at least 1." });

        using var conn = db.CreateConnection();

        var isStaff = await IsTeamStaffAsync(conn, req.TeamId, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        var row = await conn.QuerySingleAsync<RecruitmentOpeningRow>(
            """
            INSERT INTO public.recruitment_openings
                (team_id, game_slug, title, description, requirements,
                 positions_available, mode, tournament_id, status, created_by, expires_at)
            VALUES
                (@teamId, @gameSlug, @title, @description, @requirements,
                 COALESCE(@positionsAvailable, 1), @mode::recruitment_opening_mode,
                 @tournamentId, 'draft', @createdBy,
                 COALESCE(@expiresAt, now() + INTERVAL '30 days'))
            RETURNING *
            """,
            new
            {
                teamId = req.TeamId,
                gameSlug = req.GameSlug ?? "",
                title = req.Title,
                description = req.Description,
                requirements = req.Requirements ?? Array.Empty<string>(),
                positionsAvailable = req.PositionsAvailable,
                mode = (req.Mode ?? "structured"),
                tournamentId = req.TournamentId,
                expiresAt = req.ExpiresAt,
                createdBy = userCtx.UserIdGuid,
            });

        return Results.Created($"/api/recruitment/openings/{row.Id}", row);
    }

    // ── GET /api/recruitment/openings?team_id={id} ─────────────────────────────

    private static async Task<IResult> GetTeamOpeningsAsync(
        [FromQuery] Guid? team_id,
        [FromQuery] string? status,
        [FromQuery] int limit,
        [FromQuery] int offset,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (team_id is null) return Results.BadRequest(new { error = "team_id query parameter is required." });

        using var conn = db.CreateConnection();

        var isStaff = await IsTeamStaffAsync(conn, team_id.Value, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        offset = Math.Max(offset, 0);

        var rows = await conn.QueryAsync<RecruitmentOpeningRow>(
            """
            SELECT ro.*, t.name AS team_name
            FROM public.recruitment_openings ro
            JOIN public.teams t ON t.id = ro.team_id
            WHERE ro.team_id = @teamId
              AND (@status IS NULL OR ro.status::text = @status)
            ORDER BY ro.created_at DESC
            LIMIT @limit OFFSET @offset
            """,
            new { teamId = team_id.Value, status, limit, offset });

        return Results.Ok(rows);
    }

    // ── GET /api/recruitment/openings/discover ─────────────────────────────────

    private static async Task<IResult> DiscoverOpeningsAsync(
        [FromQuery] string? game,
        [FromQuery] string? mode,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game))
            return Results.BadRequest(new { error = "game query parameter is required." });

        var actualLimit = Math.Clamp(limit is null or 0 ? 20 : limit.Value, 1, 50);
        var actualOffset = Math.Max(offset ?? 0, 0);

        using var conn = db.CreateConnection();

        var rows = await conn.QueryAsync<RecruitmentOpeningRow>(
            """
            SELECT ro.id, ro.team_id, ro.game_slug, ro.title, ro.description,
                   ro.requirements, ro.positions_available, ro.positions_filled,
                   ro.mode, ro.tournament_id, ro.status, ro.created_by,
                   ro.expires_at, ro.created_at, ro.updated_at,
                   t.name AS team_name, t.logo_url AS team_logo_url, t.tag AS team_tag
            FROM public.recruitment_openings ro
            JOIN public.teams t ON t.id = ro.team_id
            WHERE ro.status = 'open'
              AND ro.game_slug = @game
              AND (@mode IS NULL OR ro.mode::text = @mode)
            ORDER BY ro.created_at DESC
            LIMIT @actualLimit OFFSET @actualOffset
            """,
            new { game, mode, actualLimit, actualOffset });

        return Results.Ok(rows);
    }

    // ── GET /api/recruitment/openings/{id} ─────────────────────────────────────

    private static async Task<IResult> GetOpeningAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<RecruitmentOpeningRow>(
            """
            SELECT ro.*, t.name AS team_name, t.logo_url AS team_logo_url, t.tag AS team_tag
            FROM public.recruitment_openings ro
            JOIN public.teams t ON t.id = ro.team_id
            WHERE ro.id = @id
              AND (
                  ro.status = 'open'
                  OR EXISTS (
                      SELECT 1 FROM public.team_members tm
                      WHERE tm.team_id = ro.team_id
                        AND tm.user_id = @userId
                        AND tm.role IN ('captain', 'manager')
                        AND tm.is_active = TRUE
                  )
              )
            """,
            new { id, userId = userCtx.UserIdGuid });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    // ── PUT /api/recruitment/openings/{id} ─────────────────────────────────────

    private static async Task<IResult> UpdateOpeningAsync(
        Guid id,
        [FromBody] UpdateOpeningRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.Title is { Length: > 200 }) return Results.BadRequest(new { error = "title max 200 chars." });
        if (req.Description is { Length: > 2000 }) return Results.BadRequest(new { error = "description max 2000 chars." });

        using var conn = db.CreateConnection();

        var opening = await conn.QuerySingleOrDefaultAsync<OpeningRef>(
            "SELECT team_id, status FROM public.recruitment_openings WHERE id = @id",
            new { id });

        if (opening is null) return Results.NotFound();

        var isStaff = await IsTeamStaffAsync(conn, opening.TeamId, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        var updated = await conn.QuerySingleOrDefaultAsync<RecruitmentOpeningRow>(
            """
            UPDATE public.recruitment_openings
            SET title               = COALESCE(@title, title),
                description         = COALESCE(@description, description),
                requirements        = COALESCE(@requirements, requirements),
                positions_available = COALESCE(@positionsAvailable, positions_available),
                mode                = COALESCE(@mode::recruitment_opening_mode, mode),
                expires_at          = COALESCE(@expiresAt, expires_at),
                updated_at          = now()
            WHERE id = @id
            RETURNING *
            """,
            new
            {
                id,
                title = req.Title,
                description = req.Description,
                requirements = req.Requirements,
                positionsAvailable = req.PositionsAvailable,
                mode = req.Mode,
                expiresAt = req.ExpiresAt,
            });

        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    // ── POST /api/recruitment/openings/{id}/publish ────────────────────────────

    private static async Task<IResult> PublishOpeningAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
        => await TransitionOpeningAsync(id, ctx, db, "open", ["draft", "paused"]);

    // ── POST /api/recruitment/openings/{id}/pause ──────────────────────────────

    private static async Task<IResult> PauseOpeningAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
        => await TransitionOpeningAsync(id, ctx, db, "paused", ["open"]);

    // ── POST /api/recruitment/openings/{id}/close ──────────────────────────────

    private static async Task<IResult> CloseOpeningAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
        => await TransitionOpeningAsync(id, ctx, db, "closed", ["open", "paused", "draft"]);

    // ── POST /api/recruitment/openings/{id}/apply ──────────────────────────────

    private static async Task<IResult> ApplyAsync(
        Guid id,
        [FromBody] ApplyRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.CoverLetter is { Length: > 1000 }) return Results.BadRequest(new { error = "cover_letter max 1000 chars." });

        using var conn = db.CreateConnection();

        // Opening must be open.
        var opening = await conn.QuerySingleOrDefaultAsync<OpeningApplyRef>(
            """
            SELECT id, team_id, status, positions_available, positions_filled
            FROM public.recruitment_openings WHERE id = @id
            """,
            new { id });

        if (opening is null) return Results.NotFound();
        if (opening.Status != "open")
            return Results.BadRequest(new { error = "Opening is not accepting applications." });

        var teamId = opening.TeamId;

        // Must not be a member of the team already.
        var isMember = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE
            )
            """,
            new { teamId, userId = userCtx.UserIdGuid });

        if (isMember)
            return Results.BadRequest(new { error = "You are already a member of this team." });

        // Insert application (unique constraint handles duplicate guard).
        RecruitmentApplicationRow application;
        try
        {
            application = await conn.QuerySingleAsync<RecruitmentApplicationRow>(
                """
                INSERT INTO public.recruitment_applications
                    (opening_id, applicant_user_id, status, cover_letter)
                VALUES (@openingId, @applicantUserId, 'submitted', @coverLetter)
                RETURNING *
                """,
                new
                {
                    openingId = id,
                    applicantUserId = userCtx.UserIdGuid,
                    coverLetter = req.CoverLetter,
                });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.BadRequest(new { error = "You have already applied to this opening." });
        }

        // Record the event.
        await conn.ExecuteAsync(
            """
            INSERT INTO public.recruitment_application_events
                (application_id, actor_user_id, event_type)
            VALUES (@applicationId, @actorUserId, 'applied')
            """,
            new { applicationId = application.Id, actorUserId = userCtx.UserIdGuid });

        // Notify team staff.
        var staffIds = await conn.QueryAsync<Guid>(
            """
            SELECT user_id FROM public.team_members
            WHERE team_id = @teamId AND role IN ('captain', 'manager') AND is_active = TRUE
            """,
            new { teamId });

        var applicantName = await conn.ExecuteScalarAsync<string?>(
            "SELECT username FROM public.profiles WHERE id = @userId",
            new { userId = userCtx.UserIdGuid });

        var teamName = await conn.ExecuteScalarAsync<string?>(
            "SELECT name FROM public.teams WHERE id = @teamId",
            new { teamId });

        foreach (var staffId in staffIds)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications
                    (user_id, type, title, message, link, is_read)
                VALUES
                    (@userId, 'new_application',
                     'New Application',
                     @message,
                     @link,
                     FALSE)
                """,
                new
                {
                    userId = staffId,
                    message = $"{applicantName ?? "A player"} applied to your opening on {teamName ?? "your team"}.",
                    link = $"/teams/{teamId}/recruitment/{id}/applications/{application.Id}",
                });
        }

        return Results.Created($"/api/recruitment/applications/{application.Id}", application);
    }

    // ── GET /api/recruitment/openings/{id}/applications ───────────────────────

    private static async Task<IResult> GetApplicationsForOpeningAsync(
        Guid id,
        [FromQuery] string? status,
        [FromQuery] int limit,
        [FromQuery] int offset,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var teamId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT team_id FROM public.recruitment_openings WHERE id = @id",
            new { id });

        if (teamId is null) return Results.NotFound();

        var isStaff = await IsTeamStaffAsync(conn, teamId.Value, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        offset = Math.Max(offset, 0);

        var rows = await conn.QueryAsync<RecruitmentApplicationRow>(
            """
            SELECT ra.*, pr.username AS applicant_username, pr.avatar_seed AS applicant_avatar_seed
            FROM public.recruitment_applications ra
            JOIN public.profiles pr ON pr.id = ra.applicant_user_id
            WHERE ra.opening_id = @openingId
              AND (@status IS NULL OR ra.status::text = @status)
            ORDER BY ra.created_at DESC
            LIMIT @limit OFFSET @offset
            """,
            new { openingId = id, status, limit, offset });

        return Results.Ok(rows);
    }

    // ── PUT /api/recruitment/applications/{id}/status ──────────────────────────

    private static async Task<IResult> UpdateApplicationStatusAsync(
        Guid id,
        [FromBody] UpdateApplicationStatusRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Status)) return Results.BadRequest(new { error = "status is required." });
        if (req.Note is { Length: > 500 }) return Results.BadRequest(new { error = "note max 500 chars." });

        using var conn = db.CreateConnection();

        var application = await conn.QuerySingleOrDefaultAsync<ApplicationRef>(
            """
            SELECT ra.id, ra.opening_id, ra.applicant_user_id, ra.status,
                   ro.team_id
            FROM public.recruitment_applications ra
            JOIN public.recruitment_openings ro ON ro.id = ra.opening_id
            WHERE ra.id = @id
            """,
            new { id });

        if (application is null) return Results.NotFound();

        var isApplicant = userCtx.UserIdGuid == application.ApplicantUserId;
        var isStaff = await IsTeamStaffAsync(conn, application.TeamId, userCtx.UserIdGuid);

        if (!isApplicant && !isStaff) return Results.Forbid();

        var newStatus = req.Status.ToLower();
        var currentStatus = application.Status;

        // Validate transitions.
        if (isApplicant && !isStaff)
        {
            // Applicant can only withdraw.
            if (newStatus != "withdrawn")
                return Results.BadRequest(new { error = "Applicants may only withdraw their application." });

            var terminalStatuses = new HashSet<string> { "accepted", "rejected", "withdrawn", "expired", "removed" };
            if (terminalStatuses.Contains(currentStatus))
                return Results.BadRequest(new { error = "Application is in a terminal state." });
        }
        else if (isStaff)
        {
            // Staff can advance or reject.
            var allowedStaffStatuses = new HashSet<string>
                { "under_review", "shortlisted", "trial_invited", "offer_extended", "accepted", "rejected", "archived" };
            if (!allowedStaffStatuses.Contains(newStatus))
                return Results.BadRequest(new { error = $"Invalid status transition to '{newStatus}'." });
        }

        var updated = await conn.QuerySingleOrDefaultAsync<RecruitmentApplicationRow>(
            """
            UPDATE public.recruitment_applications
            SET status = @newStatus::recruitment_application_status, updated_at = now()
            WHERE id = @id
            RETURNING *
            """,
            new { id, newStatus });

        if (updated is null) return Results.NotFound();

        // Record event.
        await conn.ExecuteAsync(
            """
            INSERT INTO public.recruitment_application_events
                (application_id, actor_user_id, event_type, note)
            VALUES (@applicationId, @actorUserId, @eventType, @note)
            """,
            new
            {
                applicationId = id,
                actorUserId = userCtx.UserIdGuid,
                eventType = $"status_changed_to_{newStatus}",
                note = req.Note,
            });

        // Notify applicant of status change (not for staff-internal states like archived).
        if (newStatus is "accepted" or "rejected" or "shortlisted" or "trial_invited" or "offer_extended")
        {
            var applicantId = application.ApplicantUserId;
            var teamName = await conn.ExecuteScalarAsync<string?>(
                """
                SELECT t.name FROM public.teams t
                JOIN public.recruitment_openings ro ON ro.team_id = t.id
                JOIN public.recruitment_applications ra ON ra.opening_id = ro.id
                WHERE ra.id = @applicationId
                """,
                new { applicationId = id });

            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications
                    (user_id, type, title, message, is_read)
                VALUES
                    (@userId, 'application_status_changed',
                     'Application Update',
                     @message,
                     FALSE)
                """,
                new
                {
                    userId = applicantId,
                    message = $"Your application to {teamName ?? "a team"} has been updated: {newStatus.Replace('_', ' ')}.",
                });
        }

        return Results.Ok(updated);
    }

    // ── GET /api/recruitment/applications/mine ─────────────────────────────────

    private static async Task<IResult> GetMineApplicationsAsync(
        [FromQuery] string? status,
        [FromQuery] int limit,
        [FromQuery] int offset,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        offset = Math.Max(offset, 0);

        using var conn = db.CreateConnection();

        var rows = await conn.QueryAsync<RecruitmentApplicationRow>(
            """
            SELECT ra.*, ro.title AS opening_title, ro.game_slug,
                   t.name AS team_name, t.logo_url AS team_logo_url
            FROM public.recruitment_applications ra
            JOIN public.recruitment_openings ro ON ro.id = ra.opening_id
            JOIN public.teams t ON t.id = ro.team_id
            WHERE ra.applicant_user_id = @userId
              AND (@status IS NULL OR ra.status::text = @status)
            ORDER BY ra.created_at DESC
            LIMIT @limit OFFSET @offset
            """,
            new { userId = userCtx.UserIdGuid, status, limit, offset });

        return Results.Ok(rows);
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    private static async Task<IResult> TransitionOpeningAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        string targetStatus,
        string[] allowedFrom)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var opening = await conn.QuerySingleOrDefaultAsync<OpeningRef>(
            "SELECT team_id, status FROM public.recruitment_openings WHERE id = @id",
            new { id });

        if (opening is null) return Results.NotFound();

        var isStaff = await IsTeamStaffAsync(conn, opening.TeamId, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        if (!allowedFrom.Contains(opening.Status))
            return Results.BadRequest(new { error = $"Cannot transition from '{opening.Status}' to '{targetStatus}'." });

        var updated = await conn.QuerySingleOrDefaultAsync<RecruitmentOpeningRow>(
            """
            UPDATE public.recruitment_openings
            SET status = @targetStatus::recruitment_opening_status, updated_at = now()
            WHERE id = @id
            RETURNING *
            """,
            new { id, targetStatus });

        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    private static async Task<bool> IsTeamStaffAsync(
        System.Data.IDbConnection conn,
        Guid teamId,
        Guid userId)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE team_id = @teamId
                  AND user_id = @userId
                  AND role IN ('captain', 'manager')
                  AND is_active = TRUE
            )
            """,
            new { teamId, userId });
    }

    // ── Private auth records ───────────────────────────────────────────────────

    private sealed record OpeningRef(Guid TeamId, string Status);
    private sealed record OpeningApplyRef(Guid Id, Guid TeamId, string Status, int? PositionsAvailable, int PositionsFilled);
    private sealed record ApplicationRef(Guid Id, Guid OpeningId, Guid ApplicantUserId, string Status, Guid TeamId);
}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record CreateOpeningRequest(
    [property: JsonPropertyName("team_id")] Guid TeamId,
    [property: JsonPropertyName("game_slug")] string? GameSlug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("requirements")] string[]? Requirements = null,
    [property: JsonPropertyName("positions_available")] int? PositionsAvailable = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("tournament_id")] Guid? TournamentId = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null);

public sealed record UpdateOpeningRequest(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("requirements")] string[]? Requirements = null,
    [property: JsonPropertyName("positions_available")] int? PositionsAvailable = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt = null);

public sealed record ApplyRequest(
    [property: JsonPropertyName("cover_letter")] string? CoverLetter = null);

public sealed record UpdateApplicationStatusRequest(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("note")] string? Note = null);
