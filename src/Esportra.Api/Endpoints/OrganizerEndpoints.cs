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

        // ── GET /api/organizer/schedule ───────────────────────────────────────
        // Returns upcoming matches for organizer's tournaments
        app.MapGet("/api/organizer/schedule", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var matches = await conn.QueryAsync<dynamic>(
                """
                SELECT m.id, m.scheduled_time, m.status,
                       t1.name AS team1_name, t2.name AS team2_name,
                       t.name AS tournament_name, t.slug AS tournament_slug,
                       ts.name AS stage_name
                FROM brkt_matches m
                JOIN brkt_versions v ON v.id = m.version_id
                JOIN tournament_stages ts ON ts.id = v.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                LEFT JOIN teams t1 ON t1.id = m.team1_id
                LEFT JOIN teams t2 ON t2.id = m.team2_id
                WHERE t.organizer_id = @organizerId
                  AND m.status IN ('pending', 'in_progress')
                  AND (m.scheduled_time IS NULL OR m.scheduled_time >= NOW() - INTERVAL '1 day')
                ORDER BY m.scheduled_time ASC NULLS LAST
                LIMIT 50
                """, new { organizerId = userCtx.UserIdGuid });
            return Results.Ok(matches);
        }).RequireAuthorization("Authenticated");
    }
}
