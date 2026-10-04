using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScoreboardOcr;

/// <summary>
/// Server-side verification of a screenshot report: who is on it (against fresh rosters), whether the
/// screenshot was already used for another match, and whether its printed date fits the match.
/// Organizers are notified as soon as a report names someone who isn't on the competitors' rosters.
/// </summary>
public sealed class ScoreboardReportVerifier(IHubContext<NotificationHub> notifHub, ILogger<ScoreboardReportVerifier> logger)
{
    public const string FlagReused = "screenshot_reused";
    public const string FlagDate = "date_mismatch";
    private static readonly TimeSpan DateTolerance = TimeSpan.FromDays(2);

    private sealed record ParseRecord(string Result, string Sha, Guid Team1Id, Guid Team2Id, DateTime? ScheduledTime);

    private sealed record AlertContext(Guid TournamentId, string? TournamentSlug, string? TournamentName, int MatchNumber, string? ReporterName);

    /// <summary>Flags that don't depend on names: a reused screenshot or a date far from the match.</summary>
    public static async Task<IReadOnlyList<string>> ReportFlagsAsync(IDbConnection conn, Guid matchId, string sha, JsonElement result, DateTime? scheduled)
    {
        var flags = new List<string>();
        var reused = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM scoreboard_ocr_parses WHERE screenshot_sha256 = @sha AND match_id <> @matchId)",
            new { sha, matchId });
        if (reused) flags.Add(FlagReused);
        if (PlayedOn(result) is { } played && Math.Abs((played - (scheduled ?? DateTime.UtcNow).Date).TotalDays) > DateTolerance.TotalDays)
            flags.Add(FlagDate);
        return flags;
    }

    /// <summary>Verification at parse time, shown to the captain before they submit.</summary>
    public static async Task<OcrVerification> ForParseAsync(IDbConnection conn, Guid matchId, string sha, JsonElement result, OcrRosters rosters)
    {
        var scheduled = await conn.ExecuteScalarAsync<DateTime?>("SELECT scheduled_time FROM brkt_matches WHERE id = @matchId", new { matchId });
        var verification = ScoreboardVerification.FromParse(result, rosters);
        return verification with { Flags = await ReportFlagsAsync(conn, matchId, sha, result, scheduled) };
    }

    /// <summary>Recomputed at submit from the stored parse, the submitted names and a fresh roster.</summary>
    public static async Task<OcrVerification?> ForSubmissionAsync(IDbConnection conn, Guid matchId, Guid parseId, object? matchData)
    {
        var parse = await conn.QuerySingleOrDefaultAsync<ParseRecord>(
            """
            SELECT p.result::text AS Result, p.screenshot_sha256 AS Sha, m.team1_id AS Team1Id, m.team2_id AS Team2Id,
                   m.scheduled_time AS ScheduledTime
            FROM scoreboard_ocr_parses p
            JOIN brkt_matches m ON m.id = p.match_id
            WHERE p.id = @parseId AND p.match_id = @matchId
            """,
            new { parseId, matchId });
        if (parse is null) return null;
        using var doc = JsonDocument.Parse(parse.Result);
        var rosters = await ScoreboardRosterLoader.LoadAsync(conn, parse.Team1Id, parse.Team2Id);
        var verification = ScoreboardVerification.Verify(doc.RootElement, SubmittedPlayers(matchData), rosters);
        return verification with { Flags = await ReportFlagsAsync(conn, matchId, parse.Sha, doc.RootElement, parse.ScheduledTime) };
    }

    internal static IReadOnlyList<SubmittedPlayer> SubmittedPlayers(object? matchData)
    {
        if (matchData is null) return [];
        var root = matchData is JsonElement element ? element : JsonSerializer.SerializeToElement(matchData);
        if (!root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) return [];
        return players.EnumerateArray().Select(p => new SubmittedPlayer(
            p.TryGetProperty("gameName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
            Bool(p, "isTeam1") ? "team1" : Bool(p, "isTeam2") ? "team2" : null)).ToList();
    }

    private static bool Bool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTime? PlayedOn(JsonElement result) =>
        result.TryGetProperty("playedOn", out var field) && field.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), out var date)
            ? date.Date
            : null;

    /// <summary>Alert the tournament's organizers once per report and set of findings.</summary>
    public async Task NotifyOrganizersAsync(IDbConnection conn, Guid matchId, Guid reportId, Guid reportingCompetitorId, OcrVerification verification, CancellationToken ct)
    {
        var review = verification.NeedsReview;
        if (review.Count == 0 && verification.Flags.Count == 0) return;
        var context = await conn.QuerySingleOrDefaultAsync<AlertContext>(
            """
            SELECT t.id AS TournamentId, t.slug AS TournamentSlug, t.name AS TournamentName, m.match_number AS MatchNumber,
                   COALESCE(tm.name, p.username) AS ReporterName
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            JOIN tournaments t ON t.id = v.tournament_id
            LEFT JOIN teams tm ON tm.id = @competitorId
            LEFT JOIN tournament_participants tp ON tp.id = @competitorId
            LEFT JOIN profiles p ON p.id = tp.user_id
            WHERE m.id = @matchId
            """,
            new { matchId, competitorId = reportingCompetitorId });
        if (context is null) return;

        var signature = string.Join(",", review.Select(p => $"{p.Index}:{p.Status}").Concat(verification.Flags));
        var title = $"{context.TournamentName ?? "Tournament"} · Match {context.MatchNumber}: check the screenshot report";
        var message = string.Join('\n', Lines(context.ReporterName, review, verification.Flags));
        var link = string.IsNullOrWhiteSpace(context.TournamentSlug) ? "/organizer/tournaments" : $"/organizer/tournament/{context.TournamentSlug}/brackets";
        var data = JsonSerializer.Serialize(new
        {
            match_id = matchId,
            report_id = reportId,
            tournament_id = context.TournamentId,
            audience = "organizer",
            reason = "screenshot_report_review",
            signature,
            players = review.Select(p => new { p.Name, p.Status, p.ReadName }),
            flags = verification.Flags,
        });

        foreach (var userId in await TournamentOrganizerRecipients.LoadAsync(conn, context.TournamentId))
        {
            var sent = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (SELECT 1 FROM notifications WHERE user_id = @userId AND type = 'match_report_flag'
                  AND data->>'report_id' = @reportId::text AND data->>'signature' = @signature)
                """,
                new { userId, reportId, signature });
            if (sent) continue;
            var id = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'match_report_flag', @title, @message, @link, @data::jsonb, FALSE)
                RETURNING id
                """,
                new { userId, title, message, link, data });
            await notifHub.Clients.Group(NotificationHub.UserGroup(userId.ToString())).SendAsync(
                NotificationHubEvents.NewNotification,
                new { id = id.ToString(), type = "match_report_flag", title, message, link, data = JsonSerializer.Deserialize<object>(data) },
                ct);
        }
        logger.LogInformation("Screenshot report {ReportId} flagged for organizers: {Signature}", reportId, signature);
    }

    internal static IEnumerable<string> Lines(string? reporter, IReadOnlyList<VerifiedPlayer> review, IReadOnlyList<string> flags)
    {
        yield return $"{reporter ?? "A team"} reported a result from a screenshot.";
        foreach (var player in review)
        {
            yield return player.Status switch
            {
                VerificationStatus.Unknown => $"• {player.Name} isn't on either team's roster.",
                VerificationStatus.OtherTeam => $"• {player.Name} is on the other team's roster.",
                VerificationStatus.Duplicate => $"• {player.Name} is listed twice.",
                _ => $"• {player.Name} was typed by the captain (screenshot showed \"{player.ReadName}\").",
            };
        }
        if (flags.Contains(FlagReused)) yield return "• The same screenshot was used for another match.";
        if (flags.Contains(FlagDate)) yield return "• The date on the screenshot is far from the match date.";
    }
}
