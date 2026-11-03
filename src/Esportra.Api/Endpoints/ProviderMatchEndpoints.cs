using System.Data;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Services;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// The Riot match behind a reported scrim map. Access is decided here (the backend connects with a role that bypasses RLS).
///
/// GET  /api/scrims/sessions/{id}/games/{n}/provider-match   — the two teams' active members, and admins
/// POST /api/admin/provider-matches/associations              — staff: associate a Riot match manually (audited)
/// POST /api/admin/provider-matches/backfill                  — staff: queue ingestion of reported scrim maps without one
/// </summary>
public static class ProviderMatchEndpoints
{
    private sealed record ManualAssociationRequest(
        [property: JsonPropertyName("scrim_session_id")] Guid ScrimSessionId,
        [property: JsonPropertyName("game_number")] int GameNumber,
        [property: JsonPropertyName("riot_match_id")] string RiotMatchId);

    public static void MapProviderMatchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/scrims/sessions/{id:guid}/games/{n:int}/provider-match", ScrimGameAsync).RequireAuthorization("Authenticated");
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

    private static async Task<IResult> ManualAssociationAsync(
        [FromBody] ManualAssociationRequest req, HttpContext ctx, IDbConnectionFactory db, IProviderMatchIngestTrigger trigger)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (!RiotMatchAccess.IsWellFormedMatchId(req.RiotMatchId)) return Results.BadRequest(new { error = "invalid riot_match_id" });
        using var conn = db.CreateConnection();

        var row = await conn.QuerySingleOrDefaultAsync<(Guid GameId, Guid Team1, Guid Team2, int S1, int S2, string? Map)?>(
            """
            SELECT g.id, s.team1_id, s.team2_id, g.team1_score, g.team2_score, g.map_name
            FROM public.scrim_session_games g JOIN public.scrim_sessions s ON s.id = g.session_id
            WHERE g.session_id = @sessionId AND g.game_number = @n
            """, new { sessionId = req.ScrimSessionId, n = req.GameNumber });
        if (row is null) return Results.NotFound(new { error = "game not found" });

        var result = await ProviderMatchRegistry.RegisterAsync(conn, new ProviderMatchRegistration(
            ProviderMatchContext.ScrimGame, row.Value.GameId, null, req.GameNumber, req.RiotMatchId,
            row.Value.Team1, row.Value.Team2, row.Value.S1, row.Value.S2, row.Value.Map, user.UserIdGuid, "manual"));
        if (result is null) return Results.BadRequest(new { error = "invalid riot_match_id" });
        if (result.NeedsIngest) trigger.Queue(result.AssociationId);
        return Results.Ok(new { association_id = result.AssociationId, queued = result.NeedsIngest });
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

        foreach (var (sessionId, n) in scrims)
        {
            var g = await conn.QuerySingleAsync<(Guid T1, Guid T2, int S1, int S2, string? Map, string RiotId)>(
                """
                SELECT s.team1_id, s.team2_id, g.team1_score, g.team2_score, g.map_name, g.riot_match_id
                FROM public.scrim_session_games g JOIN public.scrim_sessions s ON s.id = g.session_id
                WHERE g.session_id = @sessionId AND g.game_number = @n
                """, new { sessionId, n });
            await ProviderMatchIntake.TryScrimGameAsync(conn, trigger, logger, sessionId, n, g.T1, g.T2, g.RiotId, g.S1, g.S2, g.Map, user.UserIdGuid);
        }
        return Results.Ok(new { queued = scrims.Count, limit = take });
    }
}
