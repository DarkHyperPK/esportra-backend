using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Core.Broadcast;

namespace Esportra.Api.Endpoints;

/// <summary>
/// PROJ-059 P4 T-BE-T38 — Post-show report endpoint.
///
/// GET /api/broadcast/shows/{showId}/report
///   Auth: Supabase JWT, org owner only.
///   403 if caller is not the org owner.
///   409 if show.status != 'ended'.
///   Pure aggregation: no writes, no N+1 loops.
///   All section queries run in parallel using independent connections (AC17).
/// </summary>
public static class BroadcastReportEndpoints
{
    public static void MapBroadcastReportEndpoints(this WebApplication app)
    {
        app.MapGet("/api/broadcast/shows/{showId}/report", GetPostShowReport)
            .RequireAuthorization("Authenticated");
    }

    private static async Task<IResult> GetPostShowReport(
        Guid showId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var show = await conn.QuerySingleOrDefaultAsync<ShowReportRow>(
            """
            SELECT id AS Id, org_id AS OrgId, title AS Title, status AS Status,
                   scheduled_at AS ScheduledAt, started_at AS StartedAt, ended_at AS EndedAt
            FROM broadcast_shows
            WHERE id = @showId
            """,
            new { showId });

        if (show is null) return Results.NotFound(new { error = "show_not_found" });

        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        if (show.Status != "ended")
            return Results.Json(new { error = "Show has not ended" }, statusCode: 409);

        // All sections are independent reads — run in parallel with separate connections.
        var matchesTask = FetchMatchesAsync(db, showId);
        var crewTask = FetchCrewAsync(db, showId);
        var sponsorsTask = FetchSponsorsAsync(db, showId);
        var chaptersTask = FetchChaptersAsync(
            db,
            showId,
            show.StartedAt.HasValue ? new DateTimeOffset(show.StartedAt.Value) : null);
        var correctionsCountTask = FetchCorrectionsCountAsync(db, showId);
        var playersTask = FetchPlayersAsync(db, showId);

        await Task.WhenAll(matchesTask, crewTask, sponsorsTask, chaptersTask, correctionsCountTask, playersTask);

        var durationSeconds = show.StartedAt.HasValue && show.EndedAt.HasValue
            ? (int)(show.EndedAt.Value - show.StartedAt.Value).TotalSeconds
            : 0;

        return Results.Ok(new
        {
            show = new
            {
                id = show.Id,
                title = show.Title,
                scheduledAt = show.ScheduledAt,
                startedAt = show.StartedAt,
                endedAt = show.EndedAt,
                durationSeconds,
            },
            matches = matchesTask.Result,
            crew = crewTask.Result,
            sponsors = sponsorsTask.Result,
            chapters = chaptersTask.Result,
            corrections = new { count = correctionsCountTask.Result },
            players = playersTask.Result,
        });
    }

    // ── Matches with maps and source attribution ───────────────────────────────

    private static async Task<object> FetchMatchesAsync(IDbConnectionFactory db, Guid showId)
    {
        using var conn = db.CreateConnection();

        var gameRows = await conn.QueryAsync<GameAttributionRow>(
            """
            SELECT
                bsm.match_id        AS MatchId,
                bsm.match_code      AS MatchCode,
                bsm.display_order   AS DisplayOrder,
                bsm.status          AS MatchStatus,
                bmg.id              AS GameId,
                bmg.game_number     AS MapNumber,
                bmg.team1_score     AS TeamAScore,
                bmg.team2_score     AS TeamBScore
            FROM broadcast_show_matches bsm
            LEFT JOIN brkt_match_games bmg ON bmg.match_id = bsm.match_id
            WHERE bsm.show_id = @showId
            ORDER BY bsm.display_order, bmg.game_number
            """,
            new { showId });

        // Source attribution data — two auxiliary queries; no per-game correlated loops.
        // See §7.19 source logic: gep_conflict_override → manual; gep_conflict → gep;
        // MatchResultSubmitted-linked session → gep; no broadcast involvement → captain.
        var corrections = await conn.QueryAsync<CorrectionAttributionRow>(
            """
            SELECT target_id AS GameId, correction_type AS CorrectionType
            FROM broadcast_corrections
            WHERE show_id = @showId AND target_type = 'brkt_match_game'
            """,
            new { showId });

        var correctionsByGame = corrections
            .GroupBy(c => c.GameId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.CorrectionType).ToHashSet());

        // Find match IDs that had a broadcast result submission via MatchResultSubmitted events.
        // MatchResultSubmitted payload stores sessionId; session maps to match_id.
        var broadcastMatchIds = new HashSet<Guid>();
        var sessionIdStrings = await conn.QueryAsync<string>(
            """
            SELECT payload->>'sessionId' AS SessionId
            FROM broadcast_events
            WHERE show_id = @showId AND event_type = 'MatchResultSubmitted'
            """,
            new { showId });

        var sessionIds = sessionIdStrings
            .Where(s => !string.IsNullOrEmpty(s) && Guid.TryParse(s, out _))
            .Select(Guid.Parse)
            .ToArray();

        if (sessionIds.Length > 0)
        {
            var sessionMatchIds = await conn.QueryAsync<Guid>(
                """
                SELECT match_id FROM broadcast_sessions
                WHERE id = ANY(@sessionIds) AND match_id IS NOT NULL
                """,
                new { sessionIds });
            foreach (var mid in sessionMatchIds)
                broadcastMatchIds.Add(mid);
        }

        // Group games by match and attribute source.
        var matchDict = new Dictionary<Guid, MatchReportEntry>();
        foreach (var row in gameRows)
        {
            if (!matchDict.TryGetValue(row.MatchId, out var entry))
            {
                entry = new MatchReportEntry(row.MatchId, row.MatchCode ?? "MAIN", row.DisplayOrder, row.MatchStatus);
                matchDict[row.MatchId] = entry;
            }

            if (row.GameId is null) continue; // slot has no recorded games yet

            var gameId = row.GameId.Value;
            var hasOverride = correctionsByGame.TryGetValue(gameId, out var types)
                && types.Contains("gep_conflict_override");
            var hasAnyCorrection = correctionsByGame.ContainsKey(gameId);
            var matchBroadcastSubmitted = broadcastMatchIds.Contains(row.MatchId);

            string source;
            if (hasOverride) source = "manual";
            else if (hasAnyCorrection || matchBroadcastSubmitted) source = "gep";
            else source = "captain";

            entry.Maps.Add(new MapResult(row.MapNumber!.Value, row.TeamAScore, row.TeamBScore, source));
        }

        return matchDict.Values
            .OrderBy(m => m.DisplayOrder)
            .Select(m => new
            {
                matchId = m.MatchId,
                matchCode = m.MatchCode,
                displayOrder = m.DisplayOrder,
                status = m.Status,
                maps = m.Maps
                    .OrderBy(mp => mp.MapNumber)
                    .Select(mp => new
                    {
                        mapNumber = mp.MapNumber,
                        teamAScore = mp.TeamAScore,
                        teamBScore = mp.TeamBScore,
                        source = mp.Source,
                    })
                    .ToArray(),
            })
            .ToArray();
    }

    // ── Crew ──────────────────────────────────────────────────────────────────

    private static async Task<object> FetchCrewAsync(IDbConnectionFactory db, Guid showId)
    {
        using var conn = db.CreateConnection();

        // broadcast_talent is on-air talent (casters/analysts), not crew.
        // broadcast_crew members are production staff; display name from profiles.username.
        // No auth.users join (CIO requirement).
        var rows = await conn.QueryAsync<CrewReportRow>(
            """
            SELECT bc.user_id AS UserId, p.username AS DisplayName, bc.role AS Role
            FROM broadcast_crew bc
            LEFT JOIN profiles p ON p.id = bc.user_id
            WHERE bc.show_id = @showId AND bc.is_active = true
            ORDER BY bc.joined_at
            """,
            new { showId });

        return rows.Select(r => new
        {
            userId = r.UserId,
            displayName = r.DisplayName,
            role = r.Role,
        }).ToArray();
    }

    // ── Sponsors ──────────────────────────────────────────────────────────────

    private static async Task<object> FetchSponsorsAsync(IDbConnectionFactory db, Guid showId)
    {
        using var conn = db.CreateConnection();

        // TODO: extract to shared service — same aggregation SQL as
        // BroadcastSponsorProofEndpoints.GetSponsorProof (PROJ-059 T-BE-T36).
        var rows = await conn.QueryAsync<SponsorAggregationRow>(
            """
            SELECT
                bsi.sponsor_id          AS SponsorId,
                bss.name                AS SponsorName,
                bsi.slot_type           AS SlotType,
                SUM(EXTRACT(EPOCH FROM (
                    COALESCE(bsi.ended_at, bs.ended_at) - bsi.started_at
                )))::double precision   AS TotalSeconds,
                COUNT(*) FILTER (WHERE bsi.ended_at IS NOT NULL)  AS PlacementCount,
                COUNT(*) FILTER (WHERE bsi.ended_at IS NULL)      AS IncompleteCount
            FROM broadcast_sponsor_impressions bsi
            JOIN broadcast_show_sponsors bss ON bss.id = bsi.sponsor_id
            JOIN broadcast_shows bs ON bs.id = bsi.show_id
            WHERE bsi.show_id = @showId
            GROUP BY bsi.sponsor_id, bss.name, bsi.slot_type
            """,
            new { showId });

        return rows
            .GroupBy(r => r.SponsorId)
            .Select(g =>
            {
                var totalOnAir = g.Sum(r => r.TotalSeconds ?? 0.0);
                var totalPlacements = g.Sum(r => r.PlacementCount);
                var totalIncomplete = g.Sum(r => r.IncompleteCount);
                var bySlotType = g.ToDictionary(
                    r => r.SlotType,
                    r => new SponsorSlotBreakdown(r.TotalSeconds ?? 0.0, r.PlacementCount));

                return new SponsorProofEntry(
                    SponsorId: g.Key,
                    Name: g.First().SponsorName,
                    TotalOnAirSeconds: totalOnAir,
                    PlacementCount: totalPlacements,
                    BySlotType: bySlotType,
                    HasIncomplete: totalIncomplete > 0,
                    IncompleteCount: totalIncomplete);
            })
            .ToArray();
    }

    // ── VOD Chapters ──────────────────────────────────────────────────────────

    private static async Task<object> FetchChaptersAsync(
        IDbConnectionFactory db, Guid showId, DateTimeOffset? startedAt)
    {
        using var conn = db.CreateConnection();

        // TODO: extract to shared service — same query and calculator as
        // BroadcastShowEndpoints.GetVodChapters (PROJ-059 T-BE-T37).
        var rawEvents = await conn.QueryAsync<SegmentEvent>(
            """
            SELECT
                event_type                           AS EventType,
                payload->>'segmentId'                AS SegmentId,
                payload->>'segmentName'              AS SegmentName,
                payload->>'segmentType'              AS SegmentType,
                (payload->>'wallClockMs')::bigint    AS WallClockMs
            FROM broadcast_events
            WHERE show_id = @showId
              AND event_type IN ('SegmentTake', 'SegmentDone')
              AND (payload->>'wallClockMs') IS NOT NULL
            ORDER BY (payload->>'wallClockMs')::bigint ASC
            """,
            new { showId });

        var showStartedAtMs = startedAt.HasValue
            ? startedAt.Value.ToUnixTimeMilliseconds()
            : 0L;

        var chapters = VodChapterCalculator.Compute(rawEvents, showStartedAtMs);

        return chapters.Select(c => new
        {
            segmentId = c.SegmentId,
            segmentName = c.SegmentName,
            segmentType = c.SegmentType,
            offsetSeconds = c.OffsetSeconds,
            durationSeconds = c.DurationSeconds,
            incomplete = c.Incomplete,
        }).ToArray();
    }

    // ── Corrections count ──────────────────────────────────────────────────────

    private static async Task<long> FetchCorrectionsCountAsync(IDbConnectionFactory db, Guid showId)
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM broadcast_corrections WHERE show_id = @showId",
            new { showId });
    }

    // ── Players (per-match per-player KDA, aggregated across maps) ────────────

    private static async Task<object> FetchPlayersAsync(IDbConnectionFactory db, Guid showId)
    {
        using var conn = db.CreateConnection();

        // Single JOIN query — no per-match loop; broadcast_show_matches constrains to
        // this show's matches. KDA is summed across all map rows for the same user+match.
        var rows = await conn.QueryAsync<PlayerReportRow>(
            """
            SELECT
                bsm.match_id                              AS MatchId,
                bsm.match_code                            AS MatchCode,
                mps.user_id                               AS UserId,
                p.username                                AS DisplayName,
                SUM(COALESCE(mps.kills, 0))               AS Kills,
                SUM(COALESCE(mps.deaths, 0))              AS Deaths,
                SUM(COALESCE(mps.assists, 0))             AS Assists
            FROM match_player_stats mps
            JOIN broadcast_show_matches bsm
                ON bsm.match_id = mps.match_id
                AND bsm.show_id = @showId
            LEFT JOIN profiles p ON p.id = mps.user_id
            WHERE mps.user_id IS NOT NULL
            GROUP BY bsm.match_id, bsm.match_code, bsm.display_order, mps.user_id, p.username
            ORDER BY bsm.display_order, SUM(COALESCE(mps.kills, 0)) DESC
            """,
            new { showId });

        return rows.Select(r => new
        {
            matchId = r.MatchId,
            matchCode = r.MatchCode,
            userId = r.UserId,
            displayName = r.DisplayName,
            kills = r.Kills,
            deaths = r.Deaths,
            assists = r.Assists,
        }).ToArray();
    }

    // ── Private row types ──────────────────────────────────────────────────────

    private sealed record ShowReportRow(
        Guid Id,
        Guid OrgId,
        string Title,
        string Status,
        DateTime? ScheduledAt,
        DateTime? StartedAt,
        DateTime? EndedAt);

    private sealed class MatchReportEntry(
        Guid matchId,
        string matchCode,
        int displayOrder,
        string status)
    {
        public Guid MatchId { get; } = matchId;
        public string MatchCode { get; } = matchCode;
        public int DisplayOrder { get; } = displayOrder;
        public string Status { get; } = status;
        public List<MapResult> Maps { get; } = [];
    }

    private sealed record MapResult(int MapNumber, int? TeamAScore, int? TeamBScore, string Source);

    private sealed record GameAttributionRow(
        Guid MatchId,
        string? MatchCode,
        int DisplayOrder,
        string MatchStatus,
        Guid? GameId,
        int? MapNumber,
        int? TeamAScore,
        int? TeamBScore);

    private sealed record CorrectionAttributionRow(Guid GameId, string CorrectionType);
    private sealed record CrewReportRow(Guid UserId, string? DisplayName, string Role);

    private sealed record SponsorAggregationRow(
        Guid SponsorId,
        string SponsorName,
        string SlotType,
        double? TotalSeconds,
        long PlacementCount,
        long IncompleteCount);

    private sealed record PlayerReportRow(
        Guid MatchId,
        string? MatchCode,
        Guid UserId,
        string? DisplayName,
        long Kills,
        long Deaths,
        long Assists);
}
