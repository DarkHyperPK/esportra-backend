using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Tournament-Competitive Integration (PROJ-069 Phase 5).
///
/// GET /api/competitive/eligibility/{tournamentId}   — per-team eligibility + gaps
/// GET /api/competitive/records/{userId}             — competitive history timeline
/// PUT /api/competitive/eligibility/{tournamentId}/refresh — recompute eligible flag
/// </summary>
public static class CompetitiveIntegrationEndpoints
{
    public static void MapCompetitiveIntegrationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/competitive/eligibility/{tournamentId:guid}", GetEligibilityAsync)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/competitive/eligibility/{tournamentId:guid}/refresh", RefreshEligibilityAsync)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/records/{userId:guid}", GetCompetitiveRecordsAsync)
            .RequireAuthorization("Authenticated");
    }

    // ── GET /api/competitive/eligibility/{tournamentId} ────────────────────────
    // Returns per-team eligibility with gaps (missing players, unverified rank).
    // Accessible to tournament organizer or super admin.

    private static async Task<IResult> GetEligibilityAsync(
        Guid tournamentId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        // Verify caller is the tournament organizer or admin.
        var isOrganizer = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.tournaments t
                WHERE t.id = @tournamentId
                  AND (t.organizer_id = @userId OR @isPlatformAdmin)
            )
            """,
            new { tournamentId, userId = userCtx.UserIdGuid, isPlatformAdmin = StaffAuthHelper.IsPlatformAdmin(userCtx) });

        if (!isOrganizer) return Results.Forbid();

        // Fetch tournament settings (max_teams, min roster size from game config if any).
        var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, game, min_team_size, max_team_size FROM public.tournaments WHERE id = @tournamentId",
            new { tournamentId });

        if (tournament is null) return Results.NotFound();

        // Fetch all registered teams with their roster data.
        var teamRows = await conn.QueryAsync<dynamic>(
            """
            SELECT tp.id AS participant_id, tp.team_id, tp.competitive_eligible,
                   t.name AS team_name, t.game AS team_game,
                   (
                       SELECT COUNT(*) FROM public.team_members tm
                       WHERE tm.team_id = tp.team_id AND tm.is_active = TRUE
                   ) AS roster_size,
                   (
                       SELECT COUNT(*) FROM public.player_game_profiles pgp
                       WHERE pgp.user_id IN (
                           SELECT user_id FROM public.team_members
                           WHERE team_id = tp.team_id AND is_active = TRUE
                       ) AND pgp.game_slug = @game AND pgp.rank_tier_key IS NOT NULL
                   ) AS players_with_rank
            FROM public.tournament_participants tp
            JOIN public.teams t ON t.id = tp.team_id
            WHERE tp.tournament_id = @tournamentId
              AND tp.status NOT IN ('disqualified', 'cancelled')
            ORDER BY t.name
            """,
            new { tournamentId, game = (string?)tournament.game ?? "" });

        var minSize = (int?)tournament.min_team_size ?? 2;

        var result = teamRows.Select(row =>
        {
            var gaps = new List<string>();

            var rosterSize = (long)row.roster_size;
            if (rosterSize < minSize)
                gaps.Add($"Roster incomplete: {rosterSize}/{minSize} players.");

            // Note: rank verification gaps are advisory for now (rank_source is self_reported).
            // In a future phase this can enforce provider_verified.

            return new
            {
                participantId = row.participant_id,
                teamId = row.team_id,
                teamName = row.team_name,
                eligible = row.competitive_eligible,
                rosterSize,
                playersWithRank = (long)row.players_with_rank,
                gaps,
                gapCount = gaps.Count,
            };
        });

        return Results.Ok(result);
    }

    // ── POST /api/competitive/eligibility/{tournamentId}/refresh ───────────────
    // Recomputes competitive_eligible based on roster size vs min requirement.

    private static async Task<IResult> RefreshEligibilityAsync(
        Guid tournamentId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var isOrganizer = await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.tournaments t
                WHERE t.id = @tournamentId
                  AND (t.organizer_id = @userId OR @isPlatformAdmin)
            )
            """,
            new { tournamentId, userId = userCtx.UserIdGuid, isPlatformAdmin = StaffAuthHelper.IsPlatformAdmin(userCtx) });

        if (!isOrganizer) return Results.Forbid();

        var tournament = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, min_team_size FROM public.tournaments WHERE id = @tournamentId",
            new { tournamentId });

        if (tournament is null) return Results.NotFound();

        var minSize = (int?)tournament.min_team_size ?? 2;

        var affected = await conn.ExecuteAsync(
            """
            UPDATE public.tournament_participants tp
            SET competitive_eligible = (
                SELECT COUNT(*) >= @minSize
                FROM public.team_members tm
                WHERE tm.team_id = tp.team_id AND tm.is_active = TRUE
            )
            WHERE tp.tournament_id = @tournamentId
              AND tp.status NOT IN ('disqualified', 'cancelled')
            """,
            new { tournamentId, minSize });

        return Results.Ok(new { updated = affected });
    }

    // ── GET /api/competitive/records/{userId} ──────────────────────────────────
    // Returns verified competitive history: tournament placements + team memberships
    // + scrim results — all public fields, confidential eval details excluded.

    private static async Task<IResult> GetCompetitiveRecordsAsync(
        Guid userId,
        [FromQuery] string? game,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var callerCtx = ctx.Items["UserContext"] as UserContext;
        if (callerCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        // Team membership history.
        var memberships = await conn.QueryAsync<dynamic>(
            """
            SELECT tmh.team_id, tmh.role, tmh.joined_at, tmh.left_at, tmh.left_reason,
                   t.name AS team_name, t.game AS team_game, t.logo_url AS team_logo_url
            FROM public.team_membership_history tmh
            JOIN public.teams t ON t.id = tmh.team_id
            WHERE tmh.user_id = @userId
              AND (@game IS NULL OR LOWER(t.game) = LOWER(@game))
            ORDER BY tmh.joined_at DESC
            """,
            new { userId, game });

        // Tournament placements (completed participants with a placement).
        var placements = await conn.QueryAsync<dynamic>(
            """
            SELECT tp.tournament_id, tp.placement, tp.status, tp.team_id,
                   t2.name AS tournament_name, t2.game AS tournament_game,
                   tm.name AS team_name
            FROM public.tournament_participants tp
            JOIN public.tournaments t2 ON t2.id = tp.tournament_id
            LEFT JOIN public.teams tm ON tm.id = tp.team_id
            WHERE tp.user_id = @userId
              AND tp.status = 'completed'
              AND tp.placement IS NOT NULL
              AND (@game IS NULL OR LOWER(t2.game) = LOWER(@game))
            ORDER BY t2.start_date DESC
            """,
            new { userId, game });

        // Scrim history (completed sessions where this user was a team member).
        var scrims = await conn.QueryAsync<dynamic>(
            """
            SELECT ss.id, ss.game_slug, ss.scheduled_at, ss.status,
                   ss.team1_score, ss.team2_score,
                   t1.name AS team1_name, t2.name AS team2_name,
                   CASE WHEN ss.team1_id = tm.team_id THEN 1 ELSE 2 END AS user_team_side
            FROM public.scrim_sessions ss
            JOIN public.teams t1 ON t1.id = ss.team1_id
            JOIN public.teams t2 ON t2.id = ss.team2_id
            JOIN public.team_members tm ON tm.team_id IN (ss.team1_id, ss.team2_id)
                AND tm.user_id = @userId
            WHERE ss.status = 'completed'
              AND (@game IS NULL OR ss.game_slug = @game)
            ORDER BY ss.scheduled_at DESC
            LIMIT 50
            """,
            new { userId, game });

        // Tryout outcomes (recommendations, not scores — confidentiality preserved).
        var tryoutOutcomes = await conn.QueryAsync<dynamic>(
            """
            SELECT te.session_id, te.recommendation, te.created_at,
                   ts.game_slug, ts.title AS session_title,
                   t.name AS team_name
            FROM public.tryout_evaluations te
            JOIN public.tryout_sessions ts ON ts.id = te.session_id
            JOIN public.teams t ON t.id = ts.team_id
            WHERE te.evaluatee_user_id = @userId
              AND te.recommendation IN ('offer', 'pass')
              AND (@game IS NULL OR ts.game_slug = @game)
            ORDER BY te.created_at DESC
            """,
            new { userId, game });

        return Results.Ok(new
        {
            userId = userId.ToString(),
            memberships,
            placements,
            scrims,
            tryoutOutcomes,
        });
    }
}
