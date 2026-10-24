using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Player availability management (PROJ-065 Phase 1). Own data only.
/// GET    /api/competitive/availability                     — own rules + exceptions
/// POST   /api/competitive/availability/rules              — create rule
/// DELETE /api/competitive/availability/rules/{id}         — delete rule
/// POST   /api/competitive/availability/exceptions         — upsert exception
/// DELETE /api/competitive/availability/exceptions/{id}    — delete exception
/// </summary>
public static class AvailabilityEndpoints
{
    public static void MapAvailabilityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/competitive/availability", GetAvailabilityAsync)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/competitive/availability/rules", CreateRuleAsync)
            .RequireAuthorization("Authenticated");

        app.MapDelete("/api/competitive/availability/rules/{id}", DeleteRuleAsync)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/competitive/availability/exceptions", UpsertExceptionAsync)
            .RequireAuthorization("Authenticated");

        app.MapDelete("/api/competitive/availability/exceptions/{id}", DeleteExceptionAsync)
            .RequireAuthorization("Authenticated");
    }

    // ── GET /api/competitive/availability ─────────────────────────────────────

    private static async Task<IResult> GetAvailabilityAsync(
        [FromQuery] string? game,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var rules = await conn.QueryAsync<dynamic>(
            """
            SELECT id, game_slug, day_of_week, start_time, end_time,
                   timezone, effective_from, effective_until, created_at
            FROM public.player_availability_rules
            WHERE user_id = @userId
              AND (@game IS NULL OR game_slug = @game OR game_slug IS NULL)
            ORDER BY day_of_week ASC, start_time ASC
            LIMIT 500
            """,
            new { userId = userCtx.UserIdGuid, game = (object?)game ?? DBNull.Value });

        var exceptions = await conn.QueryAsync<dynamic>(
            """
            SELECT id, exception_date, is_available, start_time, end_time, note, created_at
            FROM public.player_availability_exceptions
            WHERE user_id = @userId
            ORDER BY exception_date ASC
            LIMIT 500
            """,
            new { userId = userCtx.UserIdGuid });

        return Results.Ok(new { rules, exceptions });
    }

    // ── POST /api/competitive/availability/rules ───────────────────────────────

    private static async Task<IResult> CreateRuleAsync(
        [FromBody] CreateAvailabilityRuleRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.DayOfWeek is < 0 or > 6)
            return Results.BadRequest(new { error = "day_of_week must be between 0 (Sunday) and 6 (Saturday)." });

        if (string.IsNullOrWhiteSpace(req.Timezone))
            return Results.BadRequest(new { error = "timezone is required." });

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(req.Timezone, out _))
            return Results.BadRequest(new { error = "Invalid timezone. Must be a valid IANA timezone ID (e.g. 'Asia/Dubai', 'Europe/London')." });

        if (!TimeSpan.TryParse(req.StartTime, out var start) ||
            !TimeSpan.TryParse(req.EndTime, out var end))
            return Results.BadRequest(new { error = "Invalid time format. Use HH:mm." });

        if (end <= start)
            return Results.BadRequest(new { error = "end_time must be after start_time." });

        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            INSERT INTO public.player_availability_rules
                (user_id, game_slug, day_of_week, start_time, end_time,
                 timezone, effective_from, effective_until)
            VALUES
                (@userId, @gameSlug, @dayOfWeek, @startTime, @endTime,
                 @timezone, @effectiveFrom::date, @effectiveUntil::date)
            RETURNING *
            """,
            new
            {
                userId = userCtx.UserIdGuid,
                gameSlug = req.GameSlug,
                dayOfWeek = req.DayOfWeek,
                startTime = start,
                endTime = end,
                timezone = req.Timezone,
                effectiveFrom = req.EffectiveFrom,
                effectiveUntil = req.EffectiveUntil,
            });

        return Results.Ok(row);
    }

    // ── DELETE /api/competitive/availability/rules/{id} ───────────────────────

    private static async Task<IResult> DeleteRuleAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var deleted = await conn.ExecuteAsync(
            "DELETE FROM public.player_availability_rules WHERE id = @id AND user_id = @userId",
            new { id, userId = userCtx.UserIdGuid });

        return deleted == 0 ? Results.NotFound() : Results.Ok(new { success = true });
    }

    // ── POST /api/competitive/availability/exceptions ─────────────────────────

    private static async Task<IResult> UpsertExceptionAsync(
        [FromBody] UpsertAvailabilityExceptionRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (!DateOnly.TryParse(req.ExceptionDate, out _))
            return Results.BadRequest(new { error = "Invalid date format. Use YYYY-MM-DD." });

        TimeSpan? startTime = null;
        TimeSpan? endTime = null;

        if (req.StartTime is not null && !TimeSpan.TryParse(req.StartTime, out var parsedStart))
            return Results.BadRequest(new { error = "Invalid start_time format. Use HH:mm." });
        else if (req.StartTime is not null)
            startTime = TimeSpan.Parse(req.StartTime);

        if (req.EndTime is not null && !TimeSpan.TryParse(req.EndTime, out var parsedEnd))
            return Results.BadRequest(new { error = "Invalid end_time format. Use HH:mm." });
        else if (req.EndTime is not null)
            endTime = TimeSpan.Parse(req.EndTime);

        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            INSERT INTO public.player_availability_exceptions
                (user_id, exception_date, is_available, start_time, end_time, note)
            VALUES
                (@userId, @exceptionDate::date, @isAvailable, @startTime, @endTime, @note)
            ON CONFLICT (user_id, exception_date) DO UPDATE SET
                is_available = EXCLUDED.is_available,
                start_time   = EXCLUDED.start_time,
                end_time     = EXCLUDED.end_time,
                note         = EXCLUDED.note
            RETURNING *
            """,
            new
            {
                userId = userCtx.UserIdGuid,
                exceptionDate = req.ExceptionDate,
                isAvailable = req.IsAvailable,
                startTime,
                endTime,
                note = req.Note,
            });

        return Results.Ok(row);
    }

    // ── DELETE /api/competitive/availability/exceptions/{id} ──────────────────

    private static async Task<IResult> DeleteExceptionAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var deleted = await conn.ExecuteAsync(
            "DELETE FROM public.player_availability_exceptions WHERE id = @id AND user_id = @userId",
            new { id, userId = userCtx.UserIdGuid });

        return deleted == 0 ? Results.NotFound() : Results.Ok(new { success = true });
    }
}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record CreateAvailabilityRuleRequest(
    [property: JsonPropertyName("game_slug")] string? GameSlug,
    [property: JsonPropertyName("day_of_week")] int DayOfWeek,
    [property: JsonPropertyName("start_time")] string StartTime,
    [property: JsonPropertyName("end_time")] string EndTime,
    [property: JsonPropertyName("timezone")] string Timezone,
    [property: JsonPropertyName("effective_from")] string? EffectiveFrom = null,
    [property: JsonPropertyName("effective_until")] string? EffectiveUntil = null);

public sealed record UpsertAvailabilityExceptionRequest(
    [property: JsonPropertyName("exception_date")] string ExceptionDate,
    [property: JsonPropertyName("is_available")] bool IsAvailable,
    [property: JsonPropertyName("start_time")] string? StartTime = null,
    [property: JsonPropertyName("end_time")] string? EndTime = null,
    [property: JsonPropertyName("note")] string? Note = null);
