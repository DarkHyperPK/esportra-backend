using System.Data;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Services;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Competitive intelligence, Phase 1: the verified Riot data behind a reported game, and the player's choice about
/// who sees their analytics. Access is decided here (the backend connects with a role that bypasses RLS).
///
/// GET  /api/scrims/sessions/{id}/games/{n}/provider-match   — scrim members and staff
/// GET  /api/matches/{id}/games/{n}/provider-match           — any signed-in user (tournament scoreboards are public)
/// GET  /api/competitive/analytics-visibility?game=          — the caller's setting
/// PUT  /api/competitive/analytics-visibility                — private (default) or public
/// POST /api/admin/provider-matches/associations             — staff: associate a Riot match manually (audited)
/// POST /api/admin/provider-matches/backfill                 — staff: queue ingestion of reported games without one
/// </summary>
public static partial class ProviderMatchEndpoints
{
    [GeneratedRegex("^[a-z0-9_-]{2,40}$")]
    private static partial Regex GameSlugPattern();

    private sealed record VisibilityRequest(
        [property: JsonPropertyName("game")] string? Game,
        [property: JsonPropertyName("visibility")] string? Visibility);

    private sealed record ManualAssociationRequest(
        [property: JsonPropertyName("context_type")] string ContextType,
        [property: JsonPropertyName("scrim_session_id")] Guid? ScrimSessionId,
        [property: JsonPropertyName("tournament_match_id")] Guid? TournamentMatchId,
        [property: JsonPropertyName("game_number")] int GameNumber,
        [property: JsonPropertyName("riot_match_id")] string RiotMatchId);

    public static void MapProviderMatchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/scrims/sessions/{id:guid}/games/{n:int}/provider-match", ScrimGameAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/matches/{id:guid}/games/{n:int}/provider-match", TournamentGameAsync).RequireAuthorization("Authenticated");
        app.MapGet("/api/competitive/analytics-visibility", GetVisibilityAsync).RequireAuthorization("Authenticated");
        app.MapPut("/api/competitive/analytics-visibility", SetVisibilityAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/admin/provider-matches/associations", ManualAssociationAsync).RequireAuthorization("Admin");
        app.MapPost("/api/admin/provider-matches/backfill", BackfillAsync).RequireAuthorization("Admin");
    }

    private static async Task<IResult> ScrimGameAsync(Guid id, int n, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var allowed = user.AdminRoles.Length > 0 || await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM public.scrim_sessions s
                JOIN public.team_members tm ON tm.team_id IN (s.team1_id, s.team2_id)
                WHERE s.id = @id AND tm.user_id = @userId AND tm.is_active = TRUE)
            """, new { id, userId = user.UserIdGuid });
        if (!allowed) return Results.Forbid();

        var gameId = await conn.ExecuteScalarAsync<Guid?>(
            "SELECT id FROM public.scrim_session_games WHERE session_id = @id AND game_number = @n", new { id, n });
        if (gameId is null) return Results.NotFound();
        return Results.Ok(await ProviderMatchDetails.LoadAsync(conn, ProviderMatchContext.ScrimGame, gameId.Value));
    }

    private static async Task<IResult> TournamentGameAsync(Guid id, int n, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext) return Results.Unauthorized();
        if (n is < 1 or > 20) return Results.BadRequest(new { error = "game number out of range" });
        using var conn = db.CreateConnection();
        return Results.Ok(await ProviderMatchDetails.LoadAsync(
            conn, ProviderMatchContext.TournamentGame, ProviderMatchRegistry.TournamentGameContextId(id, n)));
    }

    private static async Task<IResult> GetVisibilityAsync(string? game, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        var slug = game ?? "valorant";
        if (!GameSlugPattern().IsMatch(slug)) return Results.BadRequest(new { error = "invalid game" });
        using var conn = db.CreateConnection();
        var visibility = await conn.ExecuteScalarAsync<string?>(
            "SELECT visibility FROM public.player_analytics_settings WHERE user_id = @userId AND game_slug = @slug",
            new { userId = user.UserIdGuid, slug });
        return Results.Ok(new { game = slug, visibility = visibility ?? "private" });
    }

    private static async Task<IResult> SetVisibilityAsync([FromBody] VisibilityRequest req, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        var slug = req.Game ?? "valorant";
        if (!GameSlugPattern().IsMatch(slug)) return Results.BadRequest(new { error = "invalid game" });
        if (req.Visibility is not ("private" or "public")) return Results.BadRequest(new { error = "visibility must be private or public" });
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.player_analytics_settings (user_id, game_slug, visibility, consented_at)
            VALUES (@userId, @slug, @visibility, CASE WHEN @visibility = 'public' THEN now() END)
            ON CONFLICT (user_id, game_slug) DO UPDATE
            SET visibility = EXCLUDED.visibility,
                consented_at = CASE WHEN EXCLUDED.visibility = 'public' THEN now() ELSE NULL END,
                updated_at = now()
            """, new { userId = user.UserIdGuid, slug, visibility = req.Visibility });
        return Results.Ok(new { game = slug, visibility = req.Visibility });
    }

    private static async Task<IResult> ManualAssociationAsync(
        [FromBody] ManualAssociationRequest req, HttpContext ctx, IDbConnectionFactory db, IProviderMatchIngestTrigger trigger)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (!RiotMatchAccess.IsWellFormedMatchId(req.RiotMatchId)) return Results.BadRequest(new { error = "invalid riot_match_id" });
        using var conn = db.CreateConnection();

        ProviderMatchRegistration? registration = req.ContextType switch
        {
            ProviderMatchContext.ScrimGame when req.ScrimSessionId is { } sid => await ScrimRegistrationAsync(conn, sid, req, user.UserIdGuid),
            ProviderMatchContext.TournamentGame when req.TournamentMatchId is { } mid => await TournamentRegistrationAsync(conn, mid, req, user.UserIdGuid),
            _ => null,
        };
        if (registration is null) return Results.NotFound(new { error = "game not found" });

        var result = await ProviderMatchRegistry.RegisterAsync(conn, registration);
        if (result is null) return Results.BadRequest(new { error = "invalid riot_match_id" });
        if (result.NeedsIngest) trigger.Queue(result.AssociationId);
        return Results.Ok(new { association_id = result.AssociationId, queued = result.NeedsIngest });
    }

    private static async Task<ProviderMatchRegistration?> ScrimRegistrationAsync(IDbConnection conn, Guid sessionId, ManualAssociationRequest req, Guid actor)
    {
        var row = await conn.QuerySingleOrDefaultAsync<(Guid GameId, Guid Team1, Guid Team2, int S1, int S2, string? Map)>(
            """
            SELECT g.id, s.team1_id, s.team2_id, g.team1_score, g.team2_score, g.map_name
            FROM public.scrim_session_games g JOIN public.scrim_sessions s ON s.id = g.session_id
            WHERE g.session_id = @sessionId AND g.game_number = @n
            """, new { sessionId, n = req.GameNumber });
        return row.GameId == Guid.Empty ? null
            : new ProviderMatchRegistration(ProviderMatchContext.ScrimGame, row.GameId, null, req.GameNumber, req.RiotMatchId,
                row.Team1, row.Team2, row.S1, row.S2, row.Map, actor, "manual");
    }

    private static async Task<ProviderMatchRegistration?> TournamentRegistrationAsync(IDbConnection conn, Guid matchId, ManualAssociationRequest req, Guid actor)
    {
        var teams = await conn.QuerySingleOrDefaultAsync<(Guid? T1, Guid? T2)>(
            "SELECT team1_id, team2_id FROM public.brkt_matches WHERE id = @matchId", new { matchId });
        if (teams.T1 is null || teams.T2 is null) return null;
        var report = await conn.QueryFirstOrDefaultAsync<(int S1, int S2, string? Map)?>(
            """
            SELECT team1_score, team2_score, map_name FROM public.match_result_reports
            WHERE match_id = @matchId AND game_number = @n ORDER BY updated_at DESC NULLS LAST LIMIT 1
            """, new { matchId, n = req.GameNumber });
        return new ProviderMatchRegistration(
            ProviderMatchContext.TournamentGame, ProviderMatchRegistry.TournamentGameContextId(matchId, req.GameNumber), matchId,
            req.GameNumber, req.RiotMatchId, teams.T1.Value, teams.T2.Value, report?.S1, report?.S2, report?.Map, actor, "manual");
    }

    private static async Task<IResult> BackfillAsync(int? limit, HttpContext ctx, IDbConnectionFactory db, IProviderMatchIngestTrigger trigger, ILoggerFactory loggerFactory)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var logger = loggerFactory.CreateLogger("ProviderMatches.Backfill");
        using var conn = db.CreateConnection();

        var scrims = (await conn.QueryAsync<(Guid SessionId, int N)>(
            """
            SELECT g.session_id, g.game_number FROM public.scrim_session_games g
            WHERE g.riot_match_id IS NOT NULL AND g.riot_match_id <> ''
              AND NOT EXISTS (SELECT 1 FROM public.provider_match_associations a WHERE a.context_type = 'scrim_game' AND a.context_id = g.id)
            ORDER BY g.created_at LIMIT @take
            """, new { take })).ToList();
        var queued = 0;
        foreach (var (sessionId, n) in scrims)
        {
            var g = await conn.QuerySingleAsync<(Guid T1, Guid T2, int S1, int S2, string? Map, string RiotId)>(
                """
                SELECT s.team1_id, s.team2_id, g.team1_score, g.team2_score, g.map_name, g.riot_match_id
                FROM public.scrim_session_games g JOIN public.scrim_sessions s ON s.id = g.session_id
                WHERE g.session_id = @sessionId AND g.game_number = @n
                """, new { sessionId, n });
            await ProviderMatchIntake.TryScrimGameAsync(conn, trigger, logger, sessionId, n, g.T1, g.T2, g.RiotId, g.S1, g.S2, g.Map, user.UserIdGuid);
            queued++;
        }

        var remaining = Math.Max(0, take - queued);
        var reports = remaining == 0 ? [] : (await conn.QueryAsync<(Guid MatchId, int N, string RiotId, int S1, int S2, string? Map)>(
            """
            SELECT DISTINCT ON (r.match_id, r.game_number) r.match_id, r.game_number, r.riot_match_id, r.team1_score, r.team2_score, r.map_name
            FROM public.match_result_reports r
            WHERE r.riot_match_id IS NOT NULL AND r.riot_match_id <> ''
            ORDER BY r.match_id, r.game_number, r.updated_at DESC NULLS LAST
            LIMIT 1000
            """)).ToList();
        foreach (var r in reports)
        {
            if (queued >= take) break;
            var contextId = ProviderMatchRegistry.TournamentGameContextId(r.MatchId, r.N);
            var has = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM public.provider_match_associations WHERE context_type = 'tournament_game' AND context_id = @contextId)", new { contextId });
            if (has) continue;
            await ProviderMatchIntake.TryTournamentGameAsync(conn, trigger, logger, r.MatchId, r.N, r.RiotId, r.S1, r.S2, r.Map, user.UserIdGuid);
            queued++;
        }
        return Results.Ok(new { queued, limit = take });
    }
}
