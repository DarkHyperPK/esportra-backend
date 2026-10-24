using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Competitive;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Tryout Sessions (PROJ-067 Phase 3).
///
/// GET    /api/tryouts/my-invites                         — caller's tryout invitations
/// POST   /api/tryouts/sessions                          — create session
/// GET    /api/tryouts/sessions?team_id={id}             — team's sessions (staff)
/// GET    /api/tryouts/sessions/{id}                     — session detail
/// PUT    /api/tryouts/sessions/{id}                     — update session (staff)
/// POST   /api/tryouts/sessions/{id}/start               — set in_progress
/// POST   /api/tryouts/sessions/{id}/complete            — set completed
/// POST   /api/tryouts/sessions/{id}/cancel              — set cancelled
/// POST   /api/tryouts/sessions/{id}/invite              — invite participant (staff)
/// PUT    /api/tryouts/sessions/{id}/participants/{userId}/respond — confirm/decline (participant)
/// GET    /api/tryouts/sessions/{id}/participants        — list participants (staff)
/// POST   /api/tryouts/sessions/{id}/evaluations         — submit evaluation (staff)
/// GET    /api/tryouts/sessions/{id}/evaluations         — list evaluations (staff only)
/// </summary>
public static class TryoutEndpoints
{
    public static void MapTryoutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tryouts/my-invites", GetMyInvitesAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/tryouts/sessions", CreateSessionAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/tryouts/sessions", GetTeamSessionsAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/tryouts/sessions/{id:guid}", GetSessionAsync)
            .RequireAuthorization("Authenticated");
        app.MapPut("/api/tryouts/sessions/{id:guid}", UpdateSessionAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/tryouts/sessions/{id:guid}/start", StartSessionAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/tryouts/sessions/{id:guid}/complete", CompleteSessionAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/tryouts/sessions/{id:guid}/cancel", CancelSessionAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/tryouts/sessions/{id:guid}/invite", InviteParticipantAsync)
            .RequireAuthorization("Authenticated");
        app.MapPut("/api/tryouts/sessions/{id:guid}/participants/{userId:guid}/respond", RespondToInviteAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/tryouts/sessions/{id:guid}/participants", GetParticipantsAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/tryouts/sessions/{id:guid}/evaluations", SubmitEvaluationAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/tryouts/sessions/{id:guid}/evaluations", GetEvaluationsAsync)
            .RequireAuthorization("Authenticated");
    }

    // ── GET /api/tryouts/my-invites ────────────────────────────────────────────

    private static async Task<IResult> GetMyInvitesAsync(
        HttpContext ctx,
        IDbConnectionFactory db,
        [FromQuery] int limit = 20,
        [FromQuery] DateTimeOffset? cursor = null,
        CancellationToken ct = default)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (limit is < 1 or > 50) limit = 20;

        using var conn = db.CreateConnection();

        var rows = await conn.QueryAsync<TryoutInviteRow>(
            """
            SELECT
                ts.id          AS session_id,
                tp.id          AS participant_id,
                tp.status      AS participant_status,
                ts.title,
                ts.game_slug,
                ts.scheduled_at,
                t.name         AS team_name,
                t.logo_url     AS team_logo_url,
                ts.status      AS session_status
            FROM public.tryout_participants tp
            JOIN public.tryout_sessions ts ON ts.id = tp.session_id
            JOIN public.teams t ON t.id = ts.team_id
            WHERE tp.user_id = @callerId
              AND (@cursor IS NULL OR ts.scheduled_at < @cursor)
            ORDER BY ts.scheduled_at DESC NULLS LAST
            LIMIT @limit
            """,
            new { callerId = userCtx.UserIdGuid, cursor, limit });

        return Results.Ok(rows);
    }

    // ── POST /api/tryouts/sessions ─────────────────────────────────────────────

    private static async Task<IResult> CreateSessionAsync(
        [FromBody] CreateTryoutSessionRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.TeamId == Guid.Empty) return Results.BadRequest(new { error = "team_id is required." });
        if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "title is required." });
        if (req.Title.Length > 200) return Results.BadRequest(new { error = "title max 200 chars." });
        if (req.Description?.Length > 1000) return Results.BadRequest(new { error = "description max 1000 chars." });

        using var conn = db.CreateConnection();

        var isStaff = await IsTeamStaffAsync(conn, req.TeamId, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        var row = await conn.QuerySingleAsync<TryoutSessionRow>(
            """
            INSERT INTO public.tryout_sessions
                (team_id, game_slug, title, description, scheduled_at,
                 duration_minutes, max_participants, min_rank_key, max_rank_key, created_by)
            VALUES
                (@teamId, @gameSlug, @title, @description, @scheduledAt,
                 @durationMinutes, @maxParticipants, @minRankKey, @maxRankKey, @createdBy)
            RETURNING *
            """,
            new
            {
                teamId = req.TeamId,
                gameSlug = req.GameSlug ?? "",
                title = req.Title,
                description = req.Description,
                scheduledAt = req.ScheduledAt,
                durationMinutes = req.DurationMinutes,
                maxParticipants = req.MaxParticipants,
                minRankKey = req.MinRankKey,
                maxRankKey = req.MaxRankKey,
                createdBy = userCtx.UserIdGuid,
            });

        return Results.Created($"/api/tryouts/sessions/{row.Id}", row);
    }

    // ── GET /api/tryouts/sessions?team_id={id} ─────────────────────────────────

    private static async Task<IResult> GetTeamSessionsAsync(
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
        if (team_id is null) return Results.BadRequest(new { error = "team_id is required." });

        using var conn = db.CreateConnection();

        var isStaff = await IsTeamStaffAsync(conn, team_id.Value, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        limit = Math.Clamp(limit == 0 ? 20 : limit, 1, 100);
        offset = Math.Max(offset, 0);

        var rows = await conn.QueryAsync<TryoutSessionRow>(
            """
            SELECT ts.*, t.name AS team_name,
                   COUNT(tp.id) AS participant_count
            FROM public.tryout_sessions ts
            JOIN public.teams t ON t.id = ts.team_id
            LEFT JOIN public.tryout_participants tp ON tp.session_id = ts.id
            WHERE ts.team_id = @teamId
              AND (@status IS NULL OR ts.status::text = @status)
            GROUP BY ts.id, t.name
            ORDER BY ts.created_at DESC
            LIMIT @limit OFFSET @offset
            """,
            new { teamId = team_id.Value, status, limit, offset });

        return Results.Ok(rows);
    }

    // ── GET /api/tryouts/sessions/{id} ─────────────────────────────────────────

    private static async Task<IResult> GetSessionAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<TryoutSessionRow>(
            """
            SELECT ts.*, t.name AS team_name, t.logo_url AS team_logo_url
            FROM public.tryout_sessions ts
            JOIN public.teams t ON t.id = ts.team_id
            WHERE ts.id = @id
              AND (
                  EXISTS (
                      SELECT 1 FROM public.team_members tm
                      WHERE tm.team_id = ts.team_id
                        AND tm.user_id = @userId
                        AND tm.role IN ('captain', 'manager')
                        AND tm.is_active = TRUE
                  )
                  OR EXISTS (
                      SELECT 1 FROM public.tryout_participants tp
                      WHERE tp.session_id = ts.id AND tp.user_id = @userId
                  )
              )
            """,
            new { id, userId = userCtx.UserIdGuid });

        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    // ── PUT /api/tryouts/sessions/{id} ─────────────────────────────────────────

    private static async Task<IResult> UpdateSessionAsync(
        Guid id,
        [FromBody] UpdateTryoutSessionRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.Title is { Length: > 200 }) return Results.BadRequest(new { error = "title max 200 chars." });
        if (req.Description is { Length: > 1000 }) return Results.BadRequest(new { error = "description max 1000 chars." });

        using var conn = db.CreateConnection();

        var teamId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT team_id FROM public.tryout_sessions WHERE id = @id", new { id });
        if (teamId is null) return Results.NotFound();

        var isStaff = await IsTeamStaffAsync(conn, teamId.Value, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        var updated = await conn.QuerySingleOrDefaultAsync<TryoutSessionRow>(
            """
            UPDATE public.tryout_sessions
            SET title            = COALESCE(@title, title),
                description      = COALESCE(@description, description),
                scheduled_at     = COALESCE(@scheduledAt, scheduled_at),
                duration_minutes = COALESCE(@durationMinutes, duration_minutes),
                max_participants = COALESCE(@maxParticipants, max_participants),
                min_rank_key     = COALESCE(@minRankKey, min_rank_key),
                max_rank_key     = COALESCE(@maxRankKey, max_rank_key),
                updated_at       = now()
            WHERE id = @id
            RETURNING *
            """,
            new
            {
                id,
                title = req.Title,
                description = req.Description,
                scheduledAt = req.ScheduledAt,
                durationMinutes = req.DurationMinutes,
                maxParticipants = req.MaxParticipants,
                minRankKey = req.MinRankKey,
                maxRankKey = req.MaxRankKey,
            });

        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    // ── Status transitions ─────────────────────────────────────────────────────

    private static Task<IResult> StartSessionAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
        => TransitionSessionAsync(id, ctx, db, "in_progress", ["scheduled"]);

    private static Task<IResult> CompleteSessionAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
        => TransitionSessionAsync(id, ctx, db, "completed", ["in_progress"]);

    private static Task<IResult> CancelSessionAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct)
        => TransitionSessionAsync(id, ctx, db, "cancelled", ["draft", "scheduled", "in_progress"]);

    // ── POST /api/tryouts/sessions/{id}/invite ─────────────────────────────────

    private static async Task<IResult> InviteParticipantAsync(
        Guid id,
        [FromBody] InviteParticipantRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.UserId == Guid.Empty) return Results.BadRequest(new { error = "user_id is required." });

        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<TryoutSessionRef>(
            "SELECT team_id, status FROM public.tryout_sessions WHERE id = @id", new { id });
        if (session is null) return Results.NotFound();

        var isStaff = await IsTeamStaffAsync(conn, session.TeamId, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        if (session.Status is "completed" or "cancelled")
            return Results.BadRequest(new { error = "Cannot invite to a completed or cancelled session." });

        TryoutParticipantRow participant;
        try
        {
            participant = await conn.QuerySingleAsync<TryoutParticipantRow>(
                """
                INSERT INTO public.tryout_participants
                    (session_id, user_id, invited_by, status)
                VALUES (@sessionId, @userId, @invitedBy, 'invited')
                RETURNING *
                """,
                new { sessionId = id, userId = req.UserId, invitedBy = userCtx.UserIdGuid });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.BadRequest(new { error = "User already invited to this session." });
        }

        // Notify participant.
        var teamName = await conn.ExecuteScalarAsync<string?>(
            "SELECT t.name FROM public.teams t JOIN public.tryout_sessions ts ON ts.team_id = t.id WHERE ts.id = @id",
            new { id });

        await conn.ExecuteAsync(
            """
            INSERT INTO public.notifications
                (user_id, type, title, message, is_read)
            VALUES
                (@userId, 'tryout_invite', 'Tryout Invitation',
                 @message, FALSE)
            """,
            new
            {
                userId = req.UserId,
                message = $"You have been invited to a tryout session by {teamName ?? "a team"}.",
            });

        return Results.Created($"/api/tryouts/sessions/{id}/participants", participant);
    }

    // ── PUT /api/tryouts/sessions/{id}/participants/{userId}/respond ───────────

    private static async Task<IResult> RespondToInviteAsync(
        Guid id,
        Guid userId,
        [FromBody] RespondToInviteRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        // Participant can only respond for themselves.
        if (userCtx.UserIdGuid != userId) return Results.Forbid();

        var response = req.Response?.ToLower();
        if (response is not ("confirmed" or "declined"))
            return Results.BadRequest(new { error = "response must be 'confirmed' or 'declined'." });

        using var conn = db.CreateConnection();

        var updated = await conn.QuerySingleOrDefaultAsync<TryoutParticipantRow>(
            """
            UPDATE public.tryout_participants
            SET status = @status::tryout_participant_status,
                responded_at = now()
            WHERE session_id = @sessionId AND user_id = @userId
              AND status = 'invited'
            RETURNING *
            """,
            new { sessionId = id, userId, status = response });

        if (updated is null)
            return Results.BadRequest(new { error = "Invite not found or already responded." });

        return Results.Ok(updated);
    }

    // ── GET /api/tryouts/sessions/{id}/participants ────────────────────────────

    private static async Task<IResult> GetParticipantsAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var teamId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT team_id FROM public.tryout_sessions WHERE id = @id", new { id });
        if (teamId is null) return Results.NotFound();

        var isStaff = await IsTeamStaffAsync(conn, teamId.Value, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        var rows = await conn.QueryAsync<TryoutParticipantRow>(
            """
            SELECT tp.*, pr.username, pr.avatar_seed
            FROM public.tryout_participants tp
            JOIN public.profiles pr ON pr.id = tp.user_id
            WHERE tp.session_id = @sessionId
            ORDER BY tp.invited_at ASC
            """,
            new { sessionId = id });

        return Results.Ok(rows);
    }

    // ── POST /api/tryouts/sessions/{id}/evaluations ────────────────────────────

    private static async Task<IResult> SubmitEvaluationAsync(
        Guid id,
        [FromBody] SubmitEvaluationRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        if (req.EvaluateeUserId == Guid.Empty) return Results.BadRequest(new { error = "evaluatee_user_id is required." });
        if (req.Notes?.Length > 1000) return Results.BadRequest(new { error = "notes max 1000 chars." });

        using var conn = db.CreateConnection();

        var teamId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT team_id FROM public.tryout_sessions WHERE id = @id", new { id });
        if (teamId is null) return Results.NotFound();

        var isStaff = await IsTeamStaffAsync(conn, teamId.Value, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        var row = await conn.QuerySingleAsync<TryoutEvaluationRow>(
            """
            INSERT INTO public.tryout_evaluations
                (session_id, evaluatee_user_id, evaluator_user_id,
                 overall_score, communication, mechanics, game_sense,
                 attitude, teamwork, peak_play, consistency, notes, recommendation)
            VALUES
                (@sessionId, @evaluateeUserId, @evaluatorUserId,
                 @overallScore, @communication, @mechanics, @gameSense,
                 @attitude, @teamwork, @peakPlay, @consistency, @notes,
                 @recommendation::tryout_recommendation)
            ON CONFLICT (session_id, evaluatee_user_id, evaluator_user_id) DO UPDATE SET
                overall_score  = @overallScore,
                communication  = @communication,
                mechanics      = @mechanics,
                game_sense     = @gameSense,
                attitude       = @attitude,
                teamwork       = @teamwork,
                peak_play      = @peakPlay,
                consistency    = @consistency,
                notes          = COALESCE(@notes, tryout_evaluations.notes),
                recommendation = @recommendation::tryout_recommendation,
                updated_at     = now()
            RETURNING *
            """,
            new
            {
                sessionId = id,
                evaluateeUserId = req.EvaluateeUserId,
                evaluatorUserId = userCtx.UserIdGuid,
                overallScore = req.OverallScore,
                communication = req.Communication,
                mechanics = req.Mechanics,
                gameSense = req.GameSense,
                attitude = req.Attitude,
                teamwork = req.Teamwork,
                peakPlay = req.PeakPlay,
                consistency = req.Consistency,
                notes = req.Notes,
                recommendation = req.Recommendation ?? "undecided",
            });

        return Results.Ok(row);
    }

    // ── GET /api/tryouts/sessions/{id}/evaluations ────────────────────────────

    private static async Task<IResult> GetEvaluationsAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var teamId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT team_id FROM public.tryout_sessions WHERE id = @id", new { id });
        if (teamId is null) return Results.NotFound();

        var isStaff = await IsTeamStaffAsync(conn, teamId.Value, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        var rows = await conn.QueryAsync<TryoutEvaluationRow>(
            """
            SELECT te.*,
                   ep.username AS evaluatee_username,
                   ep.avatar_seed AS evaluatee_avatar_seed,
                   evp.username AS evaluator_username
            FROM public.tryout_evaluations te
            JOIN public.profiles ep ON ep.id = te.evaluatee_user_id
            JOIN public.profiles evp ON evp.id = te.evaluator_user_id
            WHERE te.session_id = @sessionId
            ORDER BY te.created_at ASC
            """,
            new { sessionId = id });

        return Results.Ok(rows);
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    private static async Task<IResult> TransitionSessionAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        string targetStatus,
        string[] allowedFrom)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var session = await conn.QuerySingleOrDefaultAsync<TryoutSessionRef>(
            "SELECT team_id, status FROM public.tryout_sessions WHERE id = @id", new { id });
        if (session is null) return Results.NotFound();

        var isStaff = await IsTeamStaffAsync(conn, session.TeamId, userCtx.UserIdGuid);
        if (!isStaff) return Results.Forbid();

        if (!allowedFrom.Contains(session.Status))
            return Results.BadRequest(new { error = $"Cannot transition from '{session.Status}' to '{targetStatus}'." });

        var updated = await conn.QuerySingleOrDefaultAsync<TryoutSessionRow>(
            """
            UPDATE public.tryout_sessions
            SET status = @targetStatus::tryout_session_status, updated_at = now()
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

    private sealed record TryoutSessionRef(Guid TeamId, string Status);
}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record CreateTryoutSessionRequest(
    [property: JsonPropertyName("team_id")] Guid TeamId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("game_slug")] string? GameSlug = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset? ScheduledAt = null,
    [property: JsonPropertyName("duration_minutes")] int? DurationMinutes = null,
    [property: JsonPropertyName("max_participants")] int? MaxParticipants = null,
    [property: JsonPropertyName("min_rank_key")] string? MinRankKey = null,
    [property: JsonPropertyName("max_rank_key")] string? MaxRankKey = null);

public sealed record UpdateTryoutSessionRequest(
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset? ScheduledAt = null,
    [property: JsonPropertyName("duration_minutes")] int? DurationMinutes = null,
    [property: JsonPropertyName("max_participants")] int? MaxParticipants = null,
    [property: JsonPropertyName("min_rank_key")] string? MinRankKey = null,
    [property: JsonPropertyName("max_rank_key")] string? MaxRankKey = null);

public sealed record InviteParticipantRequest(
    [property: JsonPropertyName("user_id")] Guid UserId);

public sealed record RespondToInviteRequest(
    [property: JsonPropertyName("response")] string Response);

public sealed record SubmitEvaluationRequest(
    [property: JsonPropertyName("evaluatee_user_id")] Guid EvaluateeUserId,
    [property: JsonPropertyName("overall_score")] short OverallScore,
    [property: JsonPropertyName("communication")] short Communication,
    [property: JsonPropertyName("mechanics")] short Mechanics,
    [property: JsonPropertyName("game_sense")] short GameSense,
    [property: JsonPropertyName("attitude")] short Attitude,
    [property: JsonPropertyName("teamwork")] short Teamwork,
    [property: JsonPropertyName("peak_play")] short PeakPlay,
    [property: JsonPropertyName("consistency")] short Consistency,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("recommendation")] string? Recommendation = null);
