using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Review CRUD for venues, tournaments, and users.
/// Replaces useReviews.ts Supabase queries.
/// </summary>
public static class ReviewEndpoints
{
    private static readonly string[] ValidEntityTypes = ["venue", "user", "tournament"];

    private static string EntityFilter(string entityType) => entityType switch
    {
        "venue"      => "r.venue_id = @entityId",
        "user"       => "r.reviewee_id = @entityId",
        "tournament" => "r.tournament_id = @entityId",
        _            => "FALSE"
    };

    public static void MapReviewEndpoints(this WebApplication app)
    {
        // ── GET /api/reviews/{entityType}/{entityId} ─────────────────────────
        app.MapGet("/api/reviews/{entityType}/{entityId}", async (
            string               entityType,
            Guid                 entityId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            if (!ValidEntityTypes.Contains(entityType))
                return Results.BadRequest(new { error = "entityType must be venue, user, or tournament" });

            using var conn = db.CreateConnection();

            var reviews = await conn.QueryAsync<dynamic>(
                $"""
                SELECT r.*,
                    jsonb_build_object(
                        'id', p.id, 'username', p.username,
                        'full_name', p.full_name, 'avatar_url', p.avatar_url
                    ) AS reviewer
                FROM reviews r
                LEFT JOIN profiles p ON p.id = r.reviewer_id
                WHERE r.review_type = @entityType AND {EntityFilter(entityType)}
                ORDER BY r.created_at DESC
                """,
                new { entityType, entityId });

            return Results.Ok(reviews);
        });

        // ── GET /api/reviews/stats/{entityType}/{entityId} ───────────────────
        app.MapGet("/api/reviews/stats/{entityType}/{entityId}", async (
            string               entityType,
            Guid                 entityId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            if (!ValidEntityTypes.Contains(entityType))
                return Results.BadRequest(new { error = "entityType must be venue, user, or tournament" });

            var colFilter = entityType switch
            {
                "venue"      => "venue_id = @entityId",
                "user"       => "reviewee_id = @entityId",
                "tournament" => "tournament_id = @entityId",
                _            => "FALSE"
            };

            using var conn = db.CreateConnection();

            var stats = await conn.QuerySingleAsync<dynamic>(
                $"""
                SELECT
                    COALESCE(ROUND(AVG(rating)::numeric, 2), 0) AS average_rating,
                    COUNT(*)                                     AS total_reviews,
                    COUNT(*) FILTER (WHERE rating = 5)           AS five_star,
                    COUNT(*) FILTER (WHERE rating = 4)           AS four_star,
                    COUNT(*) FILTER (WHERE rating = 3)           AS three_star,
                    COUNT(*) FILTER (WHERE rating = 2)           AS two_star,
                    COUNT(*) FILTER (WHERE rating = 1)           AS one_star
                FROM reviews
                WHERE review_type = @entityType AND {colFilter}
                """,
                new { entityType, entityId });

            return Results.Ok(new
            {
                average_rating  = (decimal)(stats.average_rating ?? 0m),
                total_reviews   = (long)(stats.total_reviews ?? 0L),
                rating_breakdown = new
                {
                    five  = (long)(stats.five_star  ?? 0L),
                    four  = (long)(stats.four_star  ?? 0L),
                    three = (long)(stats.three_star ?? 0L),
                    two   = (long)(stats.two_star   ?? 0L),
                    one   = (long)(stats.one_star   ?? 0L),
                }
            });
        });

        // ── GET /api/reviews/mine ────────────────────────────────────────────
        // Returns all reviews by the current user.
        app.MapGet("/api/reviews/mine", async (
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var reviews = await conn.QueryAsync<dynamic>(
                """
                SELECT r.*,
                    jsonb_build_object(
                        'id', pe.id, 'username', pe.username,
                        'full_name', pe.full_name, 'avatar_url', pe.avatar_url
                    ) AS reviewee,
                    jsonb_build_object('id', v.id, 'name', v.name, 'city', v.city) AS venue,
                    jsonb_build_object('id', t.id, 'name', t.name, 'game', t.game) AS tournament
                FROM reviews r
                LEFT JOIN profiles pe ON pe.id = r.reviewee_id
                LEFT JOIN venues v ON v.id = r.venue_id
                LEFT JOIN tournaments t ON t.id = r.tournament_id
                WHERE r.reviewer_id = @userId
                ORDER BY r.created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(reviews);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/reviews ────────────────────────────────────────────────
        app.MapPost("/api/reviews", async (
            [FromBody] CreateReviewRequest req,
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (!ValidEntityTypes.Contains(req.ReviewType))
                return Results.BadRequest(new { error = "review_type must be venue, user, or tournament" });

            using var conn = db.CreateConnection();

            // Duplicate check
            var colFilter = req.ReviewType switch
            {
                "venue"      => "venue_id = @entityId",
                "user"       => "reviewee_id = @entityId",
                "tournament" => "tournament_id = @entityId",
                _            => "FALSE"
            };
            Guid? entityId = req.ReviewType switch
            {
                "venue" when req.VenueId is not null           => Guid.Parse(req.VenueId),
                "user" when req.RevieweeId is not null         => Guid.Parse(req.RevieweeId),
                "tournament" when req.TournamentId is not null => Guid.Parse(req.TournamentId),
                _                                              => null
            };

            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                $"SELECT id FROM reviews WHERE reviewer_id = @userId AND review_type = @reviewType AND {colFilter}",
                new { userId = userCtx.UserIdGuid, reviewType = req.ReviewType, entityId });

            if (existing is not null)
                return Results.Conflict(new { error = "You have already reviewed this entity." });

            var review = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO reviews (reviewer_id, reviewee_id, venue_id, tournament_id, rating, title, comment, review_type)
                VALUES (@reviewerId, @revieweeId, @venueId, @tournamentId, @rating, @title, @comment, @reviewType)
                RETURNING id, reviewer_id, reviewee_id, venue_id, tournament_id, rating, title, comment, review_type, created_at
                """,
                new
                {
                    reviewerId    = userCtx.UserIdGuid,
                    revieweeId    = req.RevieweeId is not null ? Guid.Parse(req.RevieweeId) : (Guid?)null,
                    venueId       = req.VenueId is not null ? Guid.Parse(req.VenueId) : (Guid?)null,
                    tournamentId  = req.TournamentId is not null ? Guid.Parse(req.TournamentId) : (Guid?)null,
                    rating        = req.Rating,
                    title         = req.Title,
                    comment       = req.Comment,
                    reviewType    = req.ReviewType,
                });

            return Results.Created($"/api/reviews/{review.id}", review);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/reviews/{id} ────────────────────────────────────────────
        app.MapPut("/api/reviews/{id}", async (
            Guid                  id,
            [FromBody] UpdateReviewRequest req,
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var review = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT reviewer_id FROM reviews WHERE id = @id", new { id });
            if (review is null) return Results.NotFound();
            if ((Guid)review.reviewer_id != userCtx.UserIdGuid) return Results.Forbid();

            var updated = await conn.QuerySingleAsync<dynamic>(
                """
                UPDATE reviews
                SET rating     = COALESCE(@rating, rating),
                    title      = COALESCE(@title, title),
                    comment    = COALESCE(@comment, comment),
                    updated_at = NOW()
                WHERE id = @id
                RETURNING id, reviewer_id, reviewee_id, venue_id, tournament_id, rating, title, comment, review_type, created_at, updated_at
                """,
                new { id, rating = req.Rating, title = req.Title, comment = req.Comment });

            return Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/reviews/{id} ─────────────────────────────────────────
        app.MapDelete("/api/reviews/{id}", async (
            Guid                 id,
            HttpContext           ctx,
            IDbConnectionFactory  db,
            CancellationToken     ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var review = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT reviewer_id FROM reviews WHERE id = @id", new { id });
            if (review is null) return Results.NotFound();
            if ((Guid)review.reviewer_id != userCtx.UserIdGuid) return Results.Forbid();

            await conn.ExecuteAsync("DELETE FROM reviews WHERE id = @id", new { id });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/reviews/can-review ───────────────────────────────────────
        // Checks if the current user can review a given entity
        app.MapGet("/api/reviews/can-review", async (
            string               entityType,
            Guid                 entityId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Check if already reviewed
            var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT id FROM reviews
                WHERE reviewer_id = @userId
                  AND review_type = @entityType
                  AND (venue_id = @entityId OR reviewee_id = @entityId OR tournament_id = @entityId)
                LIMIT 1
                """, new { userId = userCtx.UserIdGuid, entityType, entityId });

            bool hasInteracted = false;
            if (entityType == "venue")
            {
                hasInteracted = await conn.QuerySingleOrDefaultAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM venue_bookings WHERE venue_id = @entityId AND user_id = @userId AND status = 'confirmed')",
                    new { entityId, userId = userCtx.UserIdGuid });
            }
            else if (entityType == "tournament")
            {
                hasInteracted = await conn.QuerySingleOrDefaultAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM tournament_participants WHERE tournament_id = @entityId AND user_id = @userId)",
                    new { entityId, userId = userCtx.UserIdGuid });
            }
            else
            {
                hasInteracted = true;
            }

            return Results.Ok(new
            {
                canReview   = existing is null && hasInteracted,
                alreadyReviewed = existing is not null,
                hasInteracted,
            });
        }).RequireAuthorization("Authenticated");
    }
}
