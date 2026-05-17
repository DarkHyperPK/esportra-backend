using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

public static class SeasonAnnouncementEndpoints
{
    public static void MapSeasonAnnouncementEndpoints(this WebApplication app)
    {
        app.MapGet("/api/seasons/{id:guid}/announcements", async (Guid id, int limit, int offset, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct) =>
        {
            limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
            offset = Math.Max(offset, 0);
            using var conn = db.CreateConnection();
            var userCtx = ctx.Items["UserContext"] as UserContext;
            var canManage = await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx);
            var isPublic = await conn.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.seasons WHERE id = @id AND deleted_at IS NULL AND is_public = TRUE)", new { id });
            if (!isPublic && !canManage) return Results.Forbid();

            var rows = await conn.QueryAsync<SeasonAnnouncementRow>(
                """
                SELECT id, season_id, title, content, is_public, published_at, created_by, created_at, updated_at
                FROM public.season_announcements
                WHERE season_id = @id
                  AND (@canManage = TRUE OR is_public = TRUE)
                ORDER BY COALESCE(published_at, created_at) DESC
                LIMIT @limit OFFSET @offset
                """,
                new { id, canManage, limit, offset });
            return Results.Ok(rows);
        });

        app.MapPost("/api/seasons/{id:guid}/announcements", async (Guid id, [FromBody] CreateSeasonAnnouncementRequest req, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title is required." });
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            var row = await conn.QuerySingleAsync<SeasonAnnouncementRow>(
                """
                INSERT INTO public.season_announcements
                    (season_id, title, content, is_public, published_at, created_by)
                VALUES
                    (@seasonId, @title, @content, @isPublic, @publishedAt, @createdBy)
                RETURNING id, season_id, title, content, is_public, published_at, created_by, created_at, updated_at
                """,
                new
                {
                    seasonId = id,
                    title = req.Title.Trim(),
                    req.Content,
                    isPublic = req.IsPublic ?? true,
                    publishedAt = req.PublishNow == true ? DateTime.UtcNow : req.PublishedAt,
                    createdBy = userCtx.UserIdGuid
                });

            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.announcement.create", id, "Season", new { announcementId = row.Id }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(row);
        }).RequireAuthorization("Organizer");

        app.MapPut("/api/seasons/{id:guid}/announcements/{announcementId:guid}", async (Guid id, Guid announcementId, [FromBody] UpdateSeasonAnnouncementRequest req, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            var row = await conn.QuerySingleOrDefaultAsync<SeasonAnnouncementRow>(
                """
                UPDATE public.season_announcements
                SET title = COALESCE(@title, title),
                    content = COALESCE(@content, content),
                    is_public = COALESCE(@isPublic, is_public),
                    published_at = COALESCE(@publishedAt, published_at),
                    updated_at = NOW()
                WHERE id = @announcementId AND season_id = @seasonId
                RETURNING id, season_id, title, content, is_public, published_at, created_by, created_at, updated_at
                """,
                new { seasonId = id, announcementId, req.Title, req.Content, req.IsPublic, req.PublishedAt });
            if (row is null) return Results.NotFound();

            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.announcement.update", id, "Season", new { announcementId }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(row);
        }).RequireAuthorization("Organizer");

        app.MapDelete("/api/seasons/{id:guid}/announcements/{announcementId:guid}", async (Guid id, Guid announcementId, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            var affected = await conn.ExecuteAsync("DELETE FROM public.season_announcements WHERE id = @announcementId AND season_id = @seasonId", new { seasonId = id, announcementId });
            if (affected == 0) return Results.NotFound();

            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.announcement.delete", id, "Season", new { announcementId }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { deleted = true });
        }).RequireAuthorization("Organizer");
    }
}

public sealed record CreateSeasonAnnouncementRequest(string Title, string? Content = null, bool? IsPublic = true, bool? PublishNow = true, DateTime? PublishedAt = null);
public sealed record UpdateSeasonAnnouncementRequest(string? Title = null, string? Content = null, bool? IsPublic = null, DateTime? PublishedAt = null);

public sealed record SeasonAnnouncementRow(
    Guid Id,
    Guid SeasonId,
    string Title,
    string? Content,
    bool IsPublic,
    DateTime? PublishedAt,
    Guid? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);
