using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.ScheduledJobs;
using Esportra.Api.Services;
using Esportra.Api.Services.ProviderMatches;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// A scrim series reported map by map. Each map is reported by one team, confirmed (or disputed) by
/// the other, and the series score follows from the settled maps. A disputed map does not hold up
/// the next one; the series completes as soon as a team has the wins it needs.
///
/// GET  /api/scrims/sessions/{id}/games                       — the maps reported so far
/// POST /api/scrims/sessions/{id}/games/{n}/report            — report map n (manual, Riot or screenshot)
/// POST /api/scrims/sessions/{id}/games/{n}/confirm           — the OTHER team confirms the map
/// POST /api/scrims/sessions/{id}/games/{n}/dispute           — the OTHER team disputes the map
/// </summary>
public static class ScrimGameEndpoints
{
    private const int MaxMatchDataBytes = 4_000_000;
    private const int MaxScreenshots = 5;

    public static void MapScrimGameEndpoints(this WebApplication app)
    {
        app.MapGet("/api/scrims/sessions/{id:guid}/games", ListAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/games/{n:int}/report", ReportAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/games/{n:int}/confirm", ConfirmAsync).RequireAuthorization("Authenticated");
        app.MapPost("/api/scrims/sessions/{id:guid}/games/{n:int}/dispute", DisputeAsync).RequireAuthorization("Authenticated");
    }

    private sealed record ReportGameRequest(
        [property: JsonPropertyName("reported_by_team_id")] Guid ReportedByTeamId,
        [property: JsonPropertyName("team1_score")] int Team1Score,
        [property: JsonPropertyName("team2_score")] int Team2Score,
        [property: JsonPropertyName("source")] string? Source = null,
        [property: JsonPropertyName("riot_match_id")] string? RiotMatchId = null,
        [property: JsonPropertyName("map_name")] string? MapName = null,
        [property: JsonPropertyName("map_id")] string? MapId = null,
        [property: JsonPropertyName("match_data")] JsonElement? MatchData = null,
        [property: JsonPropertyName("screenshot_urls")] string[]? ScreenshotUrls = null,
        [property: JsonPropertyName("notes")] string? Notes = null);

    private sealed record DisputeGameRequest(
        [property: JsonPropertyName("raised_by_team_id")] Guid RaisedByTeamId,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("evidence_urls")] string[]? EvidenceUrls = null);

    private sealed record SessionRow
    {
        public Guid Team1Id { get; init; }
        public Guid Team2Id { get; init; }
        public string Status { get; init; } = null!;
        public int BestOf { get; init; }
        public string? Team1Name { get; init; }
        public string? Team2Name { get; init; }
    }

    private sealed record GameRow
    {
        public int GameNumber { get; init; }
        public string Status { get; init; } = null!;
        public string? MapName { get; init; }
        public string? MapId { get; init; }
        public int Team1Score { get; init; }
        public int Team2Score { get; init; }
        public int? WinnerSlot { get; init; }
        public string Source { get; init; } = "manual";
        public string? RiotMatchId { get; init; }
        public string? MatchData { get; init; }
        public string[]? ScreenshotUrls { get; init; }
        public string? Notes { get; init; }
        public Guid ReportedByTeamId { get; init; }
        public DateTime ReportedAt { get; init; }
        public Guid? ConfirmedByTeamId { get; init; }
        public DateTime? ConfirmedAt { get; init; }
        public Guid? DisputedByTeamId { get; init; }
        public DateTime? AcceptBy { get; init; }
        public bool AutoConfirmed { get; init; }
    }

    private const string GameColumns = """
        game_number, status, map_name, map_id, team1_score, team2_score, winner_slot, source, riot_match_id,
        match_data::text AS match_data, screenshot_urls, notes, reported_by_team_id, reported_at,
        confirmed_by_team_id, confirmed_at, disputed_by_team_id, accept_by, auto_confirmed
        """;

    private static object Shape(GameRow g) => new
    {
        game_number = g.GameNumber,
        status = g.Status,
        map_name = g.MapName,
        map_id = g.MapId,
        team1_score = g.Team1Score,
        team2_score = g.Team2Score,
        winner_slot = g.WinnerSlot,
        source = g.Source,
        riot_match_id = g.RiotMatchId,
        match_data = g.MatchData is null ? (JsonElement?)null : JsonDocument.Parse(g.MatchData).RootElement.Clone(),
        screenshot_urls = g.ScreenshotUrls ?? [],
        notes = g.Notes,
        reported_by_team_id = g.ReportedByTeamId,
        reported_at = g.ReportedAt,
        confirmed_by_team_id = g.ConfirmedByTeamId,
        confirmed_at = g.ConfirmedAt,
        disputed_by_team_id = g.DisputedByTeamId,
        accept_by = g.AcceptBy,
        auto_confirmed = g.AutoConfirmed,
    };

    private static async Task<SessionRow?> LoadSessionAsync(IDbConnection conn, Guid id, IDbTransaction? tx = null) =>
        await conn.QuerySingleOrDefaultAsync<SessionRow>(
            """
            SELECT ss.team1_id, ss.team2_id, ss.status::text AS status, COALESCE(ss.best_of, 1) AS best_of,
                   t1.name AS team1_name, t2.name AS team2_name
            FROM public.scrim_sessions ss
            JOIN public.teams t1 ON t1.id = ss.team1_id
            JOIN public.teams t2 ON t2.id = ss.team2_id
            WHERE ss.id = @id
            """,
            new { id }, tx);

    private static async Task<List<GameRow>> LoadGamesAsync(IDbConnection conn, Guid id, IDbTransaction? tx = null) =>
        (await conn.QueryAsync<GameRow>(
            $"SELECT {GameColumns} FROM public.scrim_session_games WHERE session_id = @id ORDER BY game_number",
            new { id }, tx)).ToList();

    private static IReadOnlyList<ScrimGameState> States(IEnumerable<GameRow> games) =>
        games.Select(g => new ScrimGameState(g.GameNumber, g.Status, g.WinnerSlot)).ToList();

    private static async Task<bool> IsStaffAsync(IDbConnection conn, Guid userId, Guid teamId) =>
        await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(SELECT 1 FROM public.team_members
                          WHERE user_id = @userId AND team_id = @teamId
                            AND role IN ('captain', 'manager') AND is_active = TRUE)
            """,
            new { userId, teamId });

    private static async Task<Guid?> CallerTeamAsync(IDbConnection conn, Guid userId, SessionRow s) =>
        await conn.ExecuteScalarAsync<Guid?>(
            """
            SELECT team_id FROM public.team_members
            WHERE user_id = @userId AND team_id IN (@t1, @t2)
              AND role IN ('captain', 'manager') AND is_active = TRUE
            LIMIT 1
            """,
            new { userId, t1 = s.Team1Id, t2 = s.Team2Id });

    private static async Task<bool> CanViewSessionAsync(IDbConnection conn, Guid userId, SessionRow s) =>
        await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.team_members
                WHERE user_id = @userId AND team_id IN (@t1, @t2) AND is_active = TRUE
            ) OR EXISTS(
                SELECT 1 FROM public.teams
                WHERE id IN (@t1, @t2) AND owner_id = @userId
            )
            """,
            new { userId, t1 = s.Team1Id, t2 = s.Team2Id });

    /// <summary>
    /// Mirrors "a report waits for the other team" onto the session flags the room, the queue and the
    /// workspace already read: set for the reporting team while its map is unconfirmed, cleared otherwise.
    /// </summary>
    private static Task SetPendingFlagsAsync(IDbConnection conn, Guid sessionId, bool team1Pending, bool team2Pending, IDbTransaction? tx = null) =>
        conn.ExecuteAsync(
            "UPDATE public.scrim_sessions SET result_reported_by_team1 = @team1Pending, result_reported_by_team2 = @team2Pending, updated_at = now() WHERE id = @sessionId",
            new { sessionId, team1Pending, team2Pending }, tx);

    private static async Task<IResult> ListAsync(Guid id, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var session = await LoadSessionAsync(conn, id);
        if (session is null) return Results.NotFound();
        if (!await CanViewSessionAsync(conn, user.UserIdGuid, session)) return Results.Forbid();

        var games = await LoadGamesAsync(conn, id);
        var states = States(games);
        var (t1, t2) = ScrimSeries.Wins(states);
        return Results.Ok(new
        {
            best_of = session.BestOf,
            wins_needed = ScrimSeries.WinsNeeded(session.BestOf),
            team1_wins = t1,
            team2_wins = t2,
            next_game_number = session.Status == "in_progress" ? ScrimSeries.NextGameNumber(session.BestOf, states) : null,
            games = games.Select(Shape).ToList(),
        });
    }

    private static string? ValidateEvidence(string[]? urls)
    {
        if (urls is null) return null;
        if (urls.Length > MaxScreenshots) return $"At most {MaxScreenshots} screenshots.";
        return urls.All(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            ? null : "Screenshot links must be http(s) URLs.";
    }

    private static async Task<IResult> ReportAsync(
        Guid id, int n, [FromBody] ReportGameRequest req, HttpContext ctx, IDbConnectionFactory db,
        IHubContext<NotificationHub> hub, IConfiguration config, IProviderMatchIngestTrigger providerMatches,
        ILoggerFactory loggerFactory)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (req.Notes?.Length > 500) return Results.BadRequest(new { error = "notes max 500 chars." });
        var source = req.Source ?? "manual";
        if (source is not ("manual" or "riot" or "ocr")) return Results.BadRequest(new { error = "Unknown result source." });
        var evidenceError = ValidateEvidence(req.ScreenshotUrls);
        if (evidenceError is not null) return Results.BadRequest(new { error = evidenceError });
        var matchDataJson = req.MatchData is { ValueKind: JsonValueKind.Object } md ? md.GetRawText() : null;
        if (matchDataJson is not null && matchDataJson.Length > MaxMatchDataBytes)
            return Results.BadRequest(new { error = "Match details are too large." });

        using var conn = db.CreateConnection();
        var session = await LoadSessionAsync(conn, id);
        if (session is null) return Results.NotFound();
        if (session.Status != "in_progress")
            return Results.Conflict(new { error = "Results can only be reported while the scrim is in progress." });
        if (req.ReportedByTeamId != session.Team1Id && req.ReportedByTeamId != session.Team2Id)
            return Results.BadRequest(new { error = "Reporting team is not a party to this session." });
        if (!await IsStaffAsync(conn, user.UserIdGuid, req.ReportedByTeamId)) return Results.Forbid();

        var games = await LoadGamesAsync(conn, id);
        var existing = games.FirstOrDefault(g => g.GameNumber == n);
        if (existing is not null && existing.Status != ScrimSeries.Reported)
            return Results.Conflict(new { error = $"Game {n} is already settled." });
        if (existing is not null && existing.ReportedByTeamId != req.ReportedByTeamId)
            return Results.Conflict(new { error = $"The other team already reported game {n}. Confirm or dispute it." });

        var invalid = ScrimSeries.ValidateReport(session.BestOf, n, States(games), req.Team1Score, req.Team2Score);
        if (invalid is not null) return Results.UnprocessableEntity(new { error = invalid });

        var row = await conn.QuerySingleOrDefaultAsync<GameRow>(
            $"""
            INSERT INTO public.scrim_session_games
                (session_id, game_number, status, map_name, map_id, team1_score, team2_score, source,
                 riot_match_id, match_data, screenshot_urls, notes, reported_by_team_id, accept_by)
            VALUES (@id, @n, 'reported', @mapName, @mapId, @t1, @t2, @source,
                    @riotMatchId, @matchData::jsonb, @screenshots, @notes, @teamId, @acceptBy)
            ON CONFLICT (session_id, game_number) DO UPDATE SET
                map_name = @mapName, map_id = @mapId, team1_score = @t1, team2_score = @t2, source = @source,
                riot_match_id = @riotMatchId, match_data = @matchData::jsonb, screenshot_urls = @screenshots,
                notes = @notes, reported_at = now(), accept_by = @acceptBy, updated_at = now()
            WHERE scrim_session_games.status = 'reported' AND scrim_session_games.reported_by_team_id = @teamId
            RETURNING {GameColumns}
            """,
            new
            {
                id,
                n,
                mapName = req.MapName,
                mapId = req.MapId,
                t1 = req.Team1Score,
                t2 = req.Team2Score,
                source,
                riotMatchId = req.RiotMatchId,
                matchData = matchDataJson,
                screenshots = req.ScreenshotUrls ?? [],
                notes = req.Notes,
                teamId = req.ReportedByTeamId,
                acceptBy = ScrimSeries.AcceptDeadline(DateTime.UtcNow, config.GetValue("Scrims:GameAcceptWindowMinutes", ScrimSeries.DefaultAcceptWindowMinutes)),
            });

        if (row is null) return Results.Conflict(new { error = $"Game {n} was just changed. Refresh and try again." });

        var reportedByTeam1 = req.ReportedByTeamId == session.Team1Id;
        await SetPendingFlagsAsync(conn, id, reportedByTeam1, !reportedByTeam1);

        // Competitive intelligence: record which Riot match settles this map and freeze the rosters.
        await ProviderMatchIntake.TryScrimGameAsync(
            conn, providerMatches, loggerFactory.CreateLogger("ScrimGames.ProviderMatch"), id, n,
            session.Team1Id, session.Team2Id, req.RiotMatchId, req.Team1Score, req.Team2Score, req.MapName, user.UserIdGuid);

        var otherTeam = req.ReportedByTeamId == session.Team1Id ? session.Team2Id : session.Team1Id;
        await ScrimTeamNotifier.SendAsync(conn, hub, otherTeam, id, "scrim_result_submitted",
            "Map result to confirm", $"Game {n} was reported. Confirm it or open a dispute.");
        return Results.Ok(Shape(row));
    }

    /// <summary>Recomputes the series from the settled maps and completes the session when it is decided.</summary>
    public static async Task<(int Team1, int Team2, bool Decided)> SettleSeriesAsync(
        IDbConnection conn, IDbTransaction tx, Guid sessionId, int bestOf)
    {
        var games = await LoadGamesAsync(conn, sessionId, tx);
        var states = States(games);
        var (t1, t2) = ScrimSeries.Wins(states);
        var decided = ScrimSeries.SeriesWinner(bestOf, states) is not null;
        await conn.ExecuteAsync(
            """
            UPDATE public.scrim_sessions
            SET team1_score = @t1, team2_score = @t2,
                status = CASE WHEN @decided THEN 'completed'::scrim_session_status ELSE status END,
                updated_at = now()
            WHERE id = @sessionId
            """,
            new { sessionId, t1, t2, decided }, tx);
        return (t1, t2, decided);
    }

    /// <summary>Staff decision on a disputed map: it counts for a team, or is void and gets replayed.</summary>
    public static async Task ResolveGameAsync(
        IDbConnection conn, IDbTransaction tx, Guid sessionId, int gameNumber, string outcome)
    {
        int? winner = outcome switch { "team1_wins" => 1, "team2_wins" => 2, _ => null };
        await conn.ExecuteAsync(
            """
            UPDATE public.scrim_session_games
            SET status = 'resolved', winner_slot = @winner, updated_at = now()
            WHERE session_id = @sessionId AND game_number = @gameNumber AND status = 'disputed'
            """,
            new { sessionId, gameNumber, winner }, tx);
        var bestOf = await conn.ExecuteScalarAsync<int>(
            "SELECT COALESCE(best_of, 1) FROM public.scrim_sessions WHERE id = @sessionId", new { sessionId }, tx);
        await SettleSeriesAsync(conn, tx, sessionId, bestOf);
    }

    private static async Task<IResult> ConfirmAsync(
        Guid id, int n, HttpContext ctx, IDbConnectionFactory db, IHubContext<NotificationHub> hub)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var session = await LoadSessionAsync(conn, id);
        if (session is null) return Results.NotFound();
        var caller = await CallerTeamAsync(conn, user.UserIdGuid, session);
        if (caller is null) return Results.Forbid();

        using var tx = conn.BeginTransaction();
        var game = await conn.QuerySingleOrDefaultAsync<GameRow>(
            $"SELECT {GameColumns} FROM public.scrim_session_games WHERE session_id = @id AND game_number = @n FOR UPDATE",
            new { id, n }, tx);
        if (game is null) return Results.NotFound(new { error = $"Game {n} has not been reported." });
        if (game.Status == ScrimSeries.Confirmed) return Results.Ok(Shape(game));
        if (game.Status != ScrimSeries.Reported) return Results.Conflict(new { error = $"Game {n} can't be confirmed now." });
        if (game.ReportedByTeamId == caller) return Results.Forbid();

        var winner = ScrimSeries.WinnerSlot(game.Team1Score, game.Team2Score);
        await conn.ExecuteAsync(
            """
            UPDATE public.scrim_session_games
            SET status = 'confirmed', winner_slot = @winner, confirmed_by_team_id = @caller,
                confirmed_at = now(), updated_at = now()
            WHERE session_id = @id AND game_number = @n
            """,
            new { id, n, winner, caller }, tx);
        var (t1, t2, decided) = await SettleSeriesAsync(conn, tx, id, session.BestOf);
        await SetPendingFlagsAsync(conn, id, false, false, tx);
        tx.Commit();

        var message = decided
            ? $"Series complete: {session.Team1Name} {t1}–{t2} {session.Team2Name}."
            : $"Game {n} confirmed. Series: {t1}–{t2}.";
        foreach (var team in new[] { session.Team1Id, session.Team2Id })
            await ScrimTeamNotifier.SendAsync(conn, hub, team, id, "scrim_result_confirmed",
                decided ? "Series complete" : "Map confirmed", message);

        var updated = (await LoadGamesAsync(conn, id)).First(g => g.GameNumber == n);
        return Results.Ok(Shape(updated));
    }

    private static async Task<IResult> DisputeAsync(
        Guid id, int n, [FromBody] DisputeGameRequest req, HttpContext ctx, IDbConnectionFactory db,
        IHubContext<NotificationHub> hub)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Reason)) return Results.BadRequest(new { error = "reason is required." });
        if (req.Reason.Length > 1000) return Results.BadRequest(new { error = "reason max 1000 chars." });
        var evidenceError = ValidateEvidence(req.EvidenceUrls);
        if (evidenceError is not null) return Results.UnprocessableEntity(new { error = evidenceError });

        using var conn = db.CreateConnection();
        var session = await LoadSessionAsync(conn, id);
        if (session is null) return Results.NotFound();
        if (req.RaisedByTeamId != session.Team1Id && req.RaisedByTeamId != session.Team2Id)
            return Results.BadRequest(new { error = "Team is not a party to this session." });
        if (!await IsStaffAsync(conn, user.UserIdGuid, req.RaisedByTeamId)) return Results.Forbid();

        using var tx = conn.BeginTransaction();
        var game = await conn.QuerySingleOrDefaultAsync<GameRow>(
            $"SELECT {GameColumns} FROM public.scrim_session_games WHERE session_id = @id AND game_number = @n FOR UPDATE",
            new { id, n }, tx);
        if (game is null) return Results.NotFound(new { error = $"Game {n} has not been reported." });
        if (game.Status != ScrimSeries.Reported) return Results.Conflict(new { error = $"Game {n} can't be disputed now." });
        if (game.ReportedByTeamId == req.RaisedByTeamId)
            return Results.Conflict(new { error = "You can't dispute your own report. Submit it again instead." });

        await conn.ExecuteAsync(
            """
            UPDATE public.scrim_session_games
            SET status = 'disputed', disputed_by_team_id = @team, disputed_at = now(), updated_at = now()
            WHERE session_id = @id AND game_number = @n
            """,
            new { id, n, team = req.RaisedByTeamId }, tx);
        await conn.ExecuteAsync(
            """
            INSERT INTO public.scrim_disputes (session_id, raised_by_team_id, reason, evidence_urls, game_number)
            VALUES (@id, @team, @reason, @evidence, @n)
            """,
            new { id, n, team = req.RaisedByTeamId, reason = req.Reason, evidence = req.EvidenceUrls ?? [] }, tx);
        await SetPendingFlagsAsync(conn, id, false, false, tx);
        tx.Commit();

        await ScrimTeamNotifier.SendAsync(conn, hub, game.ReportedByTeamId, id, "scrim_dispute_raised",
            "Map disputed", $"The other team disputed game {n}. You can keep playing the next maps.");
        var updated = (await LoadGamesAsync(conn, id)).First(g => g.GameNumber == n);
        return Results.Ok(Shape(updated));
    }
}
