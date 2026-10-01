using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Jobs;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Endpoints for the Esportra desktop broadcast app (PROJ-043).
/// Separate from the admin push-broadcast system in BroadcastEndpoints.cs.
///
/// All endpoints require X-Client-Version header >= 0.1.0 (T-036).
/// </summary>
public static class DesktopBroadcastEndpoints
{
    private const string MinDesktopVersion = "0.1.0";
    private const string DownloadUrl = "https://esportra.com/download";

    public static void MapDesktopBroadcastEndpoints(this WebApplication app)
    {
        // ── POST /api/broadcast/sessions ──────────────────────────────────────
        app.MapPost("/api/broadcast/sessions", async (
            [FromBody] CreateBroadcastSessionRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var versionResult = CheckDesktopVersion(ctx);
            if (versionResult is not null) return versionResult;

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Roles.Contains("organizer", StringComparer.OrdinalIgnoreCase))
                return Results.Json(new { error = "Only organizers can create broadcast sessions." }, statusCode: 403);

            if (string.IsNullOrWhiteSpace(req.MatchCode))
                return Results.BadRequest(new { error = "matchCode is required." });
            if (req.SeriesTotal is < 1 or > 5)
                return Results.BadRequest(new { error = "seriesTotal must be between 1 and 5." });

            using var conn = db.CreateConnection();

            var sessionId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO broadcast_sessions
                    (organizer_id, match_id, match_code, series_total, app_version, status, last_seen_at)
                VALUES
                    (@organizerId, @matchId, @matchCode, @seriesTotal, @appVersion, 'active', NOW())
                RETURNING id
                """,
                new
                {
                    organizerId = userCtx.UserIdGuid,
                    matchId = req.MatchId,
                    matchCode = req.MatchCode.Trim().ToUpperInvariant(),
                    seriesTotal = req.SeriesTotal,
                    appVersion = req.AppVersion,
                });

            return Results.Ok(new { sessionId, matchCode = req.MatchCode.Trim().ToUpperInvariant() });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/broadcast/sessions ───────────────────────────────────────
        app.MapGet("/api/broadcast/sessions", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var versionResult = CheckDesktopVersion(ctx);
            if (versionResult is not null) return versionResult;

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Roles.Contains("organizer", StringComparer.OrdinalIgnoreCase))
                return Results.Json(new { error = "Only organizers can view broadcast sessions." }, statusCode: 403);

            using var conn = db.CreateConnection();

            var sessions = await conn.QueryAsync<dynamic>(
                """
                SELECT bs.id AS session_id, bs.match_code, bs.match_id, bs.status,
                       bs.last_seen_at, bs.series_map, bs.series_total,
                       t1.name AS team1_name, t1.logo_url AS team1_logo,
                       t2.name AS team2_name, t2.logo_url AS team2_logo
                FROM broadcast_sessions bs
                LEFT JOIN brkt_matches bm ON bm.id = bs.match_id
                LEFT JOIN teams t1 ON t1.id = bm.team1_id
                LEFT JOIN teams t2 ON t2.id = bm.team2_id
                WHERE bs.organizer_id = @organizerId AND bs.status = 'active'
                ORDER BY bs.created_at DESC
                """,
                new { organizerId = userCtx.UserIdGuid });

            var upcoming = await conn.QueryAsync<dynamic>(
                """
                SELECT bm.id AS match_id, bm.status, bm.scheduled_at,
                       t.name AS tournament_name,
                       t1.name AS team1_name, t2.name AS team2_name
                FROM brkt_matches bm
                JOIN brkt_versions bv ON bv.id = bm.version_id
                JOIN tournament_stages ts ON ts.id = bv.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                LEFT JOIN teams t1 ON t1.id = bm.team1_id
                LEFT JOIN teams t2 ON t2.id = bm.team2_id
                WHERE t.organizer_id = @organizerId
                  AND bm.scheduled_at BETWEEN NOW() AND NOW() + INTERVAL '7 days'
                  AND bm.status IN ('scheduled', 'in_progress')
                ORDER BY bm.scheduled_at
                """,
                new { organizerId = userCtx.UserIdGuid });

            return Results.Ok(new { sessions, upcomingMatches = upcoming });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/broadcast/sessions/{sessionId}/game-result ─────────────
        app.MapPost("/api/broadcast/sessions/{sessionId}/game-result", async (
            Guid sessionId,
            [FromBody] GameResultRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<MatchHub> hubCtx,
            CancellationToken ct) =>
        {
            var versionResult = CheckDesktopVersion(ctx);
            if (versionResult is not null) return versionResult;

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (!userCtx.Roles.Contains("organizer", StringComparer.OrdinalIgnoreCase))
                return Results.Json(new { error = "Only organizers can write game results." }, statusCode: 403);

            if (req.MapNumber < 1) return Results.BadRequest(new { error = "mapNumber must be >= 1." });
            if (req.TeamAScore < 0 || req.TeamBScore < 0)
                return Results.BadRequest(new { error = "Scores must be non-negative." });

            using var conn = db.CreateConnection();

            var session = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, organizer_id, match_id, series_total FROM broadcast_sessions WHERE id = @sessionId",
                new { sessionId });

            if (session is null) return Results.NotFound(new { error = "Session not found." });

            if ((Guid)session.organizer_id != userCtx.UserIdGuid)
                return Results.Json(new { error = "You do not own this session." }, statusCode: 403);

            if (session.match_id is null)
                return Results.BadRequest(new { error = "Session has no match attached." });

            Guid matchId = (Guid)session.match_id;

            // Idempotency: same mapNumber + same scores = no-op
            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, team1_score, team2_score FROM brkt_match_games
                WHERE match_id = @matchId AND game_number = @gameNumber
                """,
                new { matchId, gameNumber = req.MapNumber });

            if (existing is not null
                && (int)existing.team1_score == req.TeamAScore
                && (int)existing.team2_score == req.TeamBScore)
            {
                var matchStatusNoop = await conn.QuerySingleOrDefaultAsync<string>(
                    "SELECT status FROM brkt_matches WHERE id = @matchId", new { matchId });
                return Results.Ok(new { ok = true, matchStatus = matchStatusNoop ?? "unknown" });
            }

            await UpsertGameResult(conn, matchId, req);

            await conn.ExecuteAsync(
                "UPDATE broadcast_sessions SET last_seen_at = NOW(), series_map = @mapNumber WHERE id = @sessionId",
                new { sessionId, mapNumber = req.MapNumber });

            var matchStatus = await TryCompleteMatchAsync(conn, matchId, (int)session.series_total, req.MapNumber);

            var payload = new
            {
                matchId,
                mapNumber = req.MapNumber,
                teamAScore = req.TeamAScore,
                teamBScore = req.TeamBScore,
            };

            await hubCtx.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.BroadcastGameResult, payload, ct);

            return Results.Ok(new { ok = true, matchStatus });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/tournaments/active-match ─────────────────────────────────
        // Called by the companion every time Valorant launches. Cached 30s per user.
        app.MapGet("/api/tournaments/active-match", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var versionResult = CheckDesktopVersion(ctx);
            if (versionResult is not null) return versionResult;

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var cacheKey = $"active-match:{userCtx.UserId}";
            var result = await cache.GetOrCreateAsync(
                cacheKey,
                async innerCt => await QueryActiveMatchAsync(db, userCtx.UserIdGuid, innerCt),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                cancellationToken: ct);

            return Results.Ok(result);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/matches/{matchId}/player-stats ─────────────────────────
        // Companion stat POST. Caller must be a match participant (server-side enforced).
        // Writes only the calling user's own stats.
        app.MapPost("/api/matches/{matchId}/player-stats", async (
            Guid matchId,
            [FromBody] CompanionStatRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IBackgroundJobClient jobClient,
            CancellationToken ct) =>
        {
            var versionResult = CheckDesktopVersion(ctx);
            if (versionResult is not null) return versionResult;

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.RiotMatchId))
                return Results.BadRequest(new { error = "riotMatchId is required." });
            if (req.Kills < 0 || req.Deaths < 0 || req.Assists < 0)
                return Results.BadRequest(new { error = "K/D/A values must be non-negative." });

            using var conn = db.CreateConnection();

            // SECURITY: verify caller is a participant in matchId (server-side, never trust JWT payload)
            var isParticipant = await conn.QuerySingleOrDefaultAsync<int>(
                """
                SELECT 1 FROM brkt_matches bm
                JOIN team_members tm
                  ON (tm.team_id = bm.team1_id OR tm.team_id = bm.team2_id)
                WHERE bm.id = @matchId AND tm.user_id = @userId AND tm.is_active = TRUE
                LIMIT 1
                """,
                new { matchId, userId = userCtx.UserIdGuid });

            if (isParticipant == 0)
                return Results.Json(
                    new { error = "You are not a participant in this match." }, statusCode: 403);

            var riotAccount = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT riot_puuid, region FROM player_riot_accounts WHERE user_id = @userId",
                new { userId = userCtx.UserIdGuid });

            if (riotAccount is null)
                return Results.BadRequest(new { error = "No Riot account linked. Link your Riot account first." });

            string puuid = (string)riotAccount.riot_puuid;
            string? region = (string?)riotAccount.region;

            // Look up series_map for the match's active broadcast session
            var sessionMap = await conn.QuerySingleOrDefaultAsync<int?>(
                """
                SELECT series_map FROM broadcast_sessions
                WHERE match_id = @matchId AND status = 'active'
                ORDER BY created_at DESC LIMIT 1
                """,
                new { matchId });
            int mapNumber = sessionMap ?? 1;

            var extraData = new
            {
                agent = req.Agent,
                map = req.Map,
                outcome = req.Outcome,
                round_stats = req.RoundStats,
                collected_at = req.CollectedAt,
            };

            var statId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO match_player_stats
                    (match_id, map_number, user_id, stat_source, riot_match_id, verified,
                     kills, deaths, assists, extra_data, created_at)
                VALUES
                    (@matchId, @mapNumber, @userId, 'companion', @riotMatchId, false,
                     @kills, @deaths, @assists,
                     @extraData::jsonb, NOW())
                ON CONFLICT (match_id, riot_match_id, user_id)
                WHERE riot_match_id IS NOT NULL AND user_id IS NOT NULL
                DO UPDATE SET
                    kills       = EXCLUDED.kills,
                    deaths      = EXCLUDED.deaths,
                    assists     = EXCLUDED.assists,
                    extra_data  = EXCLUDED.extra_data,
                    verified    = false
                RETURNING id
                """,
                new
                {
                    matchId,
                    mapNumber,
                    userId = userCtx.UserIdGuid,
                    riotMatchId = req.RiotMatchId,
                    kills = req.Kills,
                    deaths = req.Deaths,
                    assists = req.Assists,
                    extraData = JsonSerializer.Serialize(extraData),
                });

            // Enqueue Riot cross-validation with 5-minute delay (match indexing)
            jobClient.Schedule<RiotMatchValidationJob>(
                j => j.ExecuteAsync(matchId, req.RiotMatchId, puuid, region, CancellationToken.None),
                TimeSpan.FromMinutes(5));

            return Results.Ok(new { statId, source = "companion" });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/player/stats/recent ──────────────────────────────────────
        // Performance Mode panel. Limit clamped server-side; players see only their own rows.
        app.MapGet("/api/player/stats/recent", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            HybridCache cache,
            [FromQuery] int limit = 10,
            CancellationToken ct = default) =>
        {
            var versionResult = CheckDesktopVersion(ctx);
            if (versionResult is not null) return versionResult;

            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var clampedLimit = Math.Clamp(limit, 1, 50);
            var cacheKey = $"player-stats-recent:{userCtx.UserId}:{clampedLimit}";

            var stats = await cache.GetOrCreateAsync(
                cacheKey,
                async innerCt => await QueryRecentPlayerStatsAsync(db, userCtx.UserIdGuid, clampedLimit, innerCt),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
                cancellationToken: ct);

            return Results.Ok(stats);
        }).RequireAuthorization("Authenticated").RequireRateLimiting("fixed");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static IResult? CheckDesktopVersion(HttpContext ctx)
    {
        var headerValue = ctx.Request.Headers["X-Client-Version"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(headerValue))
            return Results.Json(
                new { error = "X-Client-Version header is required.", downloadUrl = DownloadUrl },
                statusCode: 426);

        if (!Version.TryParse(headerValue, out var clientVersion)
            || !Version.TryParse(MinDesktopVersion, out var minVersion)
            || clientVersion < minVersion)
        {
            return Results.Json(
                new { error = "Please update Esportra Desktop.", downloadUrl = DownloadUrl },
                statusCode: 426);
        }

        return null;
    }

    private static async Task UpsertGameResult(
        System.Data.IDbConnection conn, Guid matchId, GameResultRequest req)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO brkt_match_games
                (match_id, game_number, team1_score, team2_score, status, completed_at)
            VALUES
                (@matchId, @gameNumber, @teamAScore, @teamBScore, 'completed', NOW())
            ON CONFLICT (match_id, game_number) DO UPDATE SET
                team1_score  = @teamAScore,
                team2_score  = @teamBScore,
                status       = 'completed',
                completed_at = NOW()
            """,
            new
            {
                matchId,
                gameNumber = req.MapNumber,
                teamAScore = req.TeamAScore,
                teamBScore = req.TeamBScore,
            });
    }

    private static async Task<string> TryCompleteMatchAsync(
        System.Data.IDbConnection conn, Guid matchId, int seriesTotal, int currentMap)
    {
        if (currentMap < seriesTotal)
            return "in_progress";

        // All maps played — determine series winner by map wins
        var games = await conn.QueryAsync<dynamic>(
            "SELECT team1_score, team2_score FROM brkt_match_games WHERE match_id = @matchId AND status = 'completed'",
            new { matchId });

        int team1Wins = 0, team2Wins = 0;
        foreach (var g in games)
        {
            if ((int)g.team1_score > (int)g.team2_score) team1Wins++;
            else if ((int)g.team2_score > (int)g.team1_score) team2Wins++;
        }

        var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT team1_id, team2_id FROM brkt_matches WHERE id = @matchId", new { matchId });
        if (match is null) return "in_progress";

        Guid? winnerId = team1Wins > team2Wins ? (Guid?)match.team1_id
                       : team2Wins > team1Wins ? (Guid?)match.team2_id
                       : null;
        Guid? loserId = winnerId == (Guid?)match.team1_id ? (Guid?)match.team2_id
                      : winnerId == (Guid?)match.team2_id ? (Guid?)match.team1_id
                      : null;

        if (winnerId.HasValue)
        {
            await conn.ExecuteAsync(
                """
                UPDATE brkt_matches
                SET status = 'completed', winner_id = @winnerId, loser_id = @loserId, ended_at = NOW()
                WHERE id = @matchId
                """,
                new { matchId, winnerId, loserId });

            return "completed";
        }

        return "in_progress";
    }

    private static async Task<ActiveMatchDto> QueryActiveMatchAsync(
        IDbConnectionFactory db, Guid userId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var riotAccount = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT riot_puuid FROM player_riot_accounts WHERE user_id = @userId",
            new { userId });

        if (riotAccount is null)
            return new ActiveMatchDto(null, null, null, null);

        var match = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            SELECT bm.id AS match_id, bs.match_code, bs.id AS session_id, tm.team_id
            FROM brkt_matches bm
            JOIN team_members tm
              ON (tm.team_id = bm.team1_id OR tm.team_id = bm.team2_id)
               AND tm.user_id = @userId AND tm.is_active = TRUE
            JOIN broadcast_sessions bs
              ON bs.match_id = bm.id AND bs.status = 'active'
            WHERE bm.status = 'in_progress'
            ORDER BY bm.updated_at DESC
            LIMIT 1
            """,
            new { userId });

        if (match is null)
            return new ActiveMatchDto(null, null, null, null);

        return new ActiveMatchDto(
            MatchId: (Guid)match.match_id,
            MatchCode: (string)match.match_code,
            SessionId: (Guid)match.session_id,
            TeamId: match.team_id is null ? null : (Guid?)match.team_id);
    }

    private static async Task<IReadOnlyList<PlayerStatSummary>> QueryRecentPlayerStatsAsync(
        IDbConnectionFactory db, Guid userId, int limit, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var rows = await conn.QueryAsync<PlayerStatSummary>(
            """
            SELECT
                mps.match_id                                      AS MatchId,
                t.id                                              AS TournamentId,
                t.name                                            AS TournamentName,
                COALESCE((mps.extra_data->>'collected_at')::timestamptz,
                         bm.scheduled_at)                        AS PlayedAt,
                mps.extra_data->>'agent'                          AS Agent,
                mps.extra_data->>'map'                            AS Map,
                mps.extra_data->>'outcome'                        AS Outcome,
                mps.kills                                         AS Kills,
                mps.deaths                                        AS Deaths,
                mps.assists                                       AS Assists,
                mps.stat_source                                   AS StatSource,
                mps.verified                                      AS Verified,
                mps.riot_match_id                                 AS RiotMatchId
            FROM match_player_stats mps
            JOIN brkt_matches bm ON bm.id = mps.match_id
            JOIN brkt_versions bv ON bv.id = bm.version_id
            JOIN tournament_stages ts ON ts.id = bv.stage_id
            JOIN tournaments t ON t.id = ts.tournament_id
            WHERE mps.user_id = @UserId
            ORDER BY PlayedAt DESC NULLS LAST
            LIMIT @Limit
            """,
            new { UserId = userId, Limit = limit });

        return rows.ToList();
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed record CreateBroadcastSessionRequest(
    Guid? MatchId,
    string MatchCode,
    int SeriesTotal = 1,
    string? AppVersion = null);

public sealed record GameResultRequest(
    int MapNumber,
    int TeamAScore,
    int TeamBScore,
    RoundResult[]? RoundResults = null);

public sealed record RoundResult(string Winner, string Cause);

public sealed record CompanionStatRequest(
    string RiotMatchId,
    string? Agent,
    string? Map,
    string? Outcome,
    int Kills,
    int Deaths,
    int Assists,
    CompanionRoundStat[]? RoundStats,
    string? CollectedAt);

public sealed record CompanionRoundStat(int Round, int Damage, int Headshots);

public sealed record ActiveMatchDto(
    Guid? MatchId,
    string? MatchCode,
    Guid? SessionId,
    Guid? TeamId);

public sealed record PlayerStatSummary(
    Guid MatchId,
    Guid TournamentId,
    string TournamentName,
    DateTime? PlayedAt,
    string? Agent,
    string? Map,
    string? Outcome,
    int Kills,
    int Deaths,
    int Assists,
    string? StatSource,
    bool Verified,
    string? RiotMatchId);
