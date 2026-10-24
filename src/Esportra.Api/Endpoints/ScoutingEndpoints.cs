using System.Text.Json.Serialization;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Scouting: shortlists and private scout notes (Phase 6).
///
/// GET    /api/scouting/shortlists                         — my shortlists
/// POST   /api/scouting/shortlists                         — create shortlist
/// GET    /api/scouting/shortlists/{id}                    — shortlist + entries
/// PUT    /api/scouting/shortlists/{id}                    — update shortlist
/// DELETE /api/scouting/shortlists/{id}                    — delete shortlist
/// POST   /api/scouting/shortlists/{id}/entries            — add player to shortlist
/// DELETE /api/scouting/shortlists/{id}/entries/{playerId} — remove player
/// GET    /api/scouting/notes/{subjectUserId}              — my notes on this player (or 404)
/// PUT    /api/scouting/notes/{subjectUserId}              — upsert note
/// DELETE /api/scouting/notes/{subjectUserId}              — delete note
/// </summary>
public static class ScoutingEndpoints
{
    public static void MapScoutingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/scouting/shortlists", GetShortlistsAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/scouting/shortlists", CreateShortlistAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/scouting/shortlists/{id:guid}", GetShortlistAsync)
            .RequireAuthorization("Authenticated");
        app.MapPut("/api/scouting/shortlists/{id:guid}", UpdateShortlistAsync)
            .RequireAuthorization("Authenticated");
        app.MapDelete("/api/scouting/shortlists/{id:guid}", DeleteShortlistAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/scouting/shortlists/{id:guid}/entries", AddEntryAsync)
            .RequireAuthorization("Authenticated");
        app.MapDelete("/api/scouting/shortlists/{id:guid}/entries/{playerId:guid}", RemoveEntryAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/scouting/notes/{subjectUserId:guid}", GetNoteAsync)
            .RequireAuthorization("Authenticated");
        app.MapPut("/api/scouting/notes/{subjectUserId:guid}", UpsertNoteAsync)
            .RequireAuthorization("Authenticated");
        app.MapDelete("/api/scouting/notes/{subjectUserId:guid}", DeleteNoteAsync)
            .RequireAuthorization("Authenticated");
    }

    // ── GET /api/scouting/shortlists ───────────────────────────────────────────

    private static async Task<IResult> GetShortlistsAsync(
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var rows = await conn.QueryAsync<dynamic>(
            """
            SELECT s.id, s.name, s.description, s.game_slug, s.is_private,
                   s.created_at, s.updated_at,
                   (SELECT COUNT(*) FROM public.scouting_shortlist_entries e
                    WHERE e.shortlist_id = s.id) AS entry_count
            FROM public.scouting_shortlists s
            WHERE s.owner_user_id = @ownerId
            ORDER BY s.created_at DESC
            """,
            new { ownerId = userCtx.UserIdGuid });

        return Results.Ok(rows);
    }

    // ── POST /api/scouting/shortlists ──────────────────────────────────────────

    private static async Task<IResult> CreateShortlistAsync(
        [FromBody] CreateShortlistRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { error = "name is required." });
        if (req.Name.Length > 100)
            return Results.BadRequest(new { error = "name max 100 chars." });
        if (req.Description is { Length: > 500 })
            return Results.BadRequest(new { error = "description max 500 chars." });

        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleAsync<dynamic>(
            """
            INSERT INTO public.scouting_shortlists
                (owner_user_id, name, description, game_slug, is_private)
            VALUES (@ownerId, @name, @description, @gameSlug, TRUE)
            RETURNING *
            """,
            new
            {
                ownerId = userCtx.UserIdGuid,
                name = req.Name.Trim(),
                description = req.Description,
                gameSlug = req.GameSlug,
            });

        return Results.Created($"/api/scouting/shortlists/{row.id}", row);
    }

    // ── GET /api/scouting/shortlists/{id} ──────────────────────────────────────

    private static async Task<IResult> GetShortlistAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var shortlist = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT * FROM public.scouting_shortlists
            WHERE id = @id AND owner_user_id = @ownerId
            """,
            new { id, ownerId = userCtx.UserIdGuid });

        if (shortlist is null) return Results.NotFound();

        var entries = await conn.QueryAsync<dynamic>(
            """
            SELECT e.id, e.player_user_id, e.note, e.added_at,
                   p.username AS player_username, p.avatar_seed AS player_avatar_seed
            FROM public.scouting_shortlist_entries e
            JOIN public.profiles p ON p.id = e.player_user_id
            WHERE e.shortlist_id = @shortlistId
            ORDER BY e.added_at DESC
            """,
            new { shortlistId = id });

        return Results.Ok(new { shortlist, entries });
    }

    // ── PUT /api/scouting/shortlists/{id} ──────────────────────────────────────

    private static async Task<IResult> UpdateShortlistAsync(
        Guid id,
        [FromBody] UpdateShortlistRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.Name is { Length: 0 or > 100 })
            return Results.BadRequest(new { error = "name must be 1–100 chars." });
        if (req.Description is { Length: > 500 })
            return Results.BadRequest(new { error = "description max 500 chars." });

        using var conn = db.CreateConnection();

        var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            UPDATE public.scouting_shortlists
            SET name        = COALESCE(@name, name),
                description = COALESCE(@description, description),
                updated_at  = now()
            WHERE id = @id AND owner_user_id = @ownerId
            RETURNING *
            """,
            new { id, ownerId = userCtx.UserIdGuid, name = req.Name, description = req.Description });

        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }

    // ── DELETE /api/scouting/shortlists/{id} ───────────────────────────────────

    private static async Task<IResult> DeleteShortlistAsync(
        Guid id,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var deleted = await conn.ExecuteScalarAsync<Guid?>(
            """
            DELETE FROM public.scouting_shortlists
            WHERE id = @id AND owner_user_id = @ownerId
            RETURNING id
            """,
            new { id, ownerId = userCtx.UserIdGuid });

        return deleted is null ? Results.NotFound() : Results.NoContent();
    }

    // ── POST /api/scouting/shortlists/{id}/entries ─────────────────────────────

    private static async Task<IResult> AddEntryAsync(
        Guid id,
        [FromBody] AddShortlistEntryRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.PlayerUserId == Guid.Empty)
            return Results.BadRequest(new { error = "player_user_id is required." });
        if (req.Note is { Length: > 300 })
            return Results.BadRequest(new { error = "note max 300 chars." });

        using var conn = db.CreateConnection();

        var owns = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.scouting_shortlists
                WHERE id = @id AND owner_user_id = @ownerId
            )
            """,
            new { id, ownerId = userCtx.UserIdGuid });

        if (!owns) return Results.NotFound();

        dynamic entry;
        try
        {
            entry = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO public.scouting_shortlist_entries
                    (shortlist_id, player_user_id, note)
                VALUES (@shortlistId, @playerUserId, @note)
                RETURNING *
                """,
                new { shortlistId = id, playerUserId = req.PlayerUserId, note = req.Note });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Results.BadRequest(new { error = "Player is already on this shortlist." });
        }

        return Results.Created($"/api/scouting/shortlists/{id}/entries/{(Guid)entry.id}", entry);
    }

    // ── DELETE /api/scouting/shortlists/{id}/entries/{playerId} ───────────────

    private static async Task<IResult> RemoveEntryAsync(
        Guid id,
        Guid playerId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var deleted = await conn.ExecuteScalarAsync<Guid?>(
            """
            DELETE FROM public.scouting_shortlist_entries e
            USING public.scouting_shortlists s
            WHERE e.shortlist_id  = s.id
              AND s.id            = @shortlistId
              AND s.owner_user_id = @ownerId
              AND e.player_user_id = @playerId
            RETURNING e.id
            """,
            new { shortlistId = id, ownerId = userCtx.UserIdGuid, playerId });

        return deleted is null ? Results.NotFound() : Results.NoContent();
    }

    // ── GET /api/scouting/notes/{subjectUserId} ────────────────────────────────

    private static async Task<IResult> GetNoteAsync(
        Guid subjectUserId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var notes = (await conn.QueryAsync<dynamic>(
            """
            SELECT * FROM public.scouting_notes
            WHERE scout_user_id   = @scoutId
              AND subject_user_id = @subjectId
            ORDER BY updated_at DESC
            """,
            new { scoutId = userCtx.UserIdGuid, subjectId = subjectUserId })).ToList();

        return notes.Count == 0 ? Results.NotFound() : Results.Ok(notes);
    }

    // ── PUT /api/scouting/notes/{subjectUserId} ────────────────────────────────

    private static async Task<IResult> UpsertNoteAsync(
        Guid subjectUserId,
        [FromBody] UpsertScoutingNoteRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Content))
            return Results.BadRequest(new { error = "content is required." });
        if (req.Content.Length > 2000)
            return Results.BadRequest(new { error = "content max 2000 chars." });

        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleAsync<dynamic>(
            """
            INSERT INTO public.scouting_notes
                (scout_user_id, subject_user_id, game_slug, content)
            VALUES (@scoutId, @subjectId, @gameSlug, @content)
            ON CONFLICT (scout_user_id, subject_user_id, game_slug) DO UPDATE
            SET content    = EXCLUDED.content,
                updated_at = now()
            RETURNING id
            """,
            new
            {
                scoutId = userCtx.UserIdGuid,
                subjectId = subjectUserId,
                gameSlug = req.GameSlug,
                content = req.Content,
            });

        return Results.Ok(row);
    }

    // ── DELETE /api/scouting/notes/{subjectUserId} ─────────────────────────────

    private static async Task<IResult> DeleteNoteAsync(
        Guid subjectUserId,
        [FromQuery] string? gameSlug,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var deleted = await conn.ExecuteScalarAsync<Guid?>(
            """
            DELETE FROM public.scouting_notes
            WHERE scout_user_id   = @scoutId
              AND subject_user_id = @subjectId
              AND (game_slug = @gameSlug OR (@gameSlug IS NULL AND game_slug IS NULL))
            RETURNING id
            """,
            new { scoutId = userCtx.UserIdGuid, subjectId = subjectUserId, gameSlug });

        return deleted is null ? Results.NotFound() : Results.NoContent();
    }
}

// ── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record CreateShortlistRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("game_slug")] string? GameSlug = null);

public sealed record UpdateShortlistRequest(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("description")] string? Description = null);

public sealed record AddShortlistEntryRequest(
    [property: JsonPropertyName("player_user_id")] Guid PlayerUserId,
    [property: JsonPropertyName("note")] string? Note = null);

public sealed record UpsertScoutingNoteRequest(
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("game_slug")] string? GameSlug = null);
