using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Organizer-specific stats. Replaces useOrganizerStats.ts multi-query Supabase logic.
/// Server-side aggregation prevents N+1 and reduces payload size.
/// </summary>
public static class OrganizerEndpoints
{
    public static void MapOrganizerEndpoints(this WebApplication app)
    {
        // ── GET /api/organizer/stats ─────────────────────────────────────────
        app.MapGet("/api/organizer/stats", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var summary = await conn.QuerySingleAsync<dynamic>(
                """
                SELECT
                    COUNT(*)                                                                          AS total_tournaments,
                    COUNT(*) FILTER (WHERE status IN ('ongoing', 'check_in'))                         AS active_tournaments,
                    COUNT(*) FILTER (WHERE status IN ('open', 'draft') AND start_date > NOW())        AS upcoming_tournaments,
                    COALESCE(SUM(prize_pool), 0)                                                     AS total_prize_pool
                FROM tournaments
                WHERE organizer_id = @organizerId
                """,
                new { organizerId = userCtx.UserIdGuid });

            var totalParticipants = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM tournament_participants tp
                INNER JOIN tournaments t ON t.id = tp.tournament_id
                WHERE t.organizer_id = @organizerId
                """,
                new { organizerId = userCtx.UserIdGuid });

            var gameDistribution = await conn.QueryAsync<dynamic>(
                """
                SELECT game, COUNT(*) AS count
                FROM tournaments
                WHERE organizer_id = @organizerId AND game IS NOT NULL
                GROUP BY game
                ORDER BY count DESC
                """,
                new { organizerId = userCtx.UserIdGuid });

            var monthlyParticipation = await conn.QueryAsync<dynamic>(
                """
                SELECT
                    TO_CHAR(DATE_TRUNC('month', tp.created_at), 'YYYY-MM') AS month,
                    COUNT(*) AS participants
                FROM tournament_participants tp
                INNER JOIN tournaments t ON t.id = tp.tournament_id
                WHERE t.organizer_id = @organizerId
                  AND tp.created_at >= NOW() - INTERVAL '12 months'
                GROUP BY DATE_TRUNC('month', tp.created_at)
                ORDER BY month
                """,
                new { organizerId = userCtx.UserIdGuid });

            return Results.Ok(new
            {
                totalTournaments    = (long)summary.total_tournaments,
                activeTournaments   = (long)summary.active_tournaments,
                upcomingTournaments = (long)summary.upcoming_tournaments,
                totalPrizePool      = (decimal)summary.total_prize_pool,
                totalParticipants,
                gameDistribution,
                monthlyParticipation,
            });
        }).RequireAuthorization("Authenticated");
    }
}
