using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.ScoreboardOcr;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>Queues the Riot cross-check of a screenshot report after it is saved.</summary>
public interface IScoreboardRiotCheckTrigger
{
    void Queue(Guid reportId);
}

public sealed class ScoreboardRiotCheckTrigger(IBackgroundJobClient jobs) : IScoreboardRiotCheckTrigger
{
    public void Queue(Guid reportId) =>
        jobs.Enqueue<ScoreboardRiotCheckJob>(j => j.RunAsync(reportId, 0, CancellationToken.None));
}

/// <summary>
/// Confirms a screenshot report from Riot's own record of the match. The scoreboard shows game names
/// without the #tag, so the report's linked players' Riot histories are searched (by PUUID) for the
/// custom game with the same map and score, and every row is checked against that game's players and
/// K/D/A. Organizers are alerted when it finds a different account, an unregistered player or edited numbers.
/// </summary>
public sealed class ScoreboardRiotCheckJob(
    IDbConnectionFactory db,
    IRiotMatchLookup riot,
    IBackgroundJobClient jobs,
    ScoreboardReportVerifier verifier,
    IHubContext<MatchHub> matchHub,
    ILogger<ScoreboardRiotCheckJob> logger)
{
    /// <summary>Riot can list a custom game minutes after it ends: look again after these delays.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    private static readonly TimeSpan Window = TimeSpan.FromDays(2);
    private const int MaxPlayersSearched = 3;

    private sealed record ReportRow(
        Guid Id, Guid MatchId, Guid ReportedByTeamId, int? Team1Score, int? Team2Score, string? MapName, string? MatchData,
        string? OcrVerification, DateTime CreatedAt, string? PlayedOn, Guid Team1Id, Guid Team2Id);

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid reportId, int attempt, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var report = await conn.QuerySingleOrDefaultAsync<ReportRow>(
            """
            SELECT r.id AS Id, r.match_id AS MatchId, r.reported_by_team_id AS ReportedByTeamId,
                   r.team1_score AS Team1Score, r.team2_score AS Team2Score, r.map_name AS MapName,
                   r.match_data::text AS MatchData, r.ocr_verification::text AS OcrVerification, r.created_at AS CreatedAt,
                   p.result->'playedOn'->>'value' AS PlayedOn, m.team1_id AS Team1Id, m.team2_id AS Team2Id
            FROM match_result_reports r
            JOIN brkt_matches m ON m.id = r.match_id
            LEFT JOIN scoreboard_ocr_parses p ON p.id = r.ocr_parse_id
            WHERE r.id = @reportId
            """,
            new { reportId });
        if (report?.OcrVerification is null || report.Team1Score is null || report.Team2Score is null) return;
        var verification = JsonSerializer.Deserialize<OcrVerification>(report.OcrVerification, OcrVerificationJson.Options);
        if (verification is null || verification.Riot?.Status == RiotCheckStatus.Confirmed) return;

        var linked = await ScoreboardRosterLoader.LoadLinkedAccountsAsync(conn, report.Team1Id, report.Team2Id);
        var reportingTeam = report.ReportedByTeamId == report.Team1Id ? "team1" : "team2";
        var puuids = linked.OrderBy(a => a.Team == reportingTeam ? 0 : 1).Select(a => a.Puuid).Distinct().Take(MaxPlayersSearched).ToList();
        if (puuids.Count == 0)
        {
            await SaveAsync(conn, report, verification with { Riot = new RiotCheck(RiotCheckStatus.NotFound, null, DateTimeOffset.UtcNow) }, null, ct);
            return;
        }

        var around = DateTime.TryParse(report.PlayedOn, out var played) ? new DateTimeOffset(played.Date.AddHours(12), TimeSpan.Zero) : new DateTimeOffset(report.CreatedAt, TimeSpan.Zero);
        var query = new RiotMatchQuery(report.MapName, report.Team1Score.Value, report.Team2Score.Value, around, Window);
        var found = await riot.FindCustomGameAsync(puuids, query, ct);

        if (found.Status is RiotLookupStatus.NotFound or RiotLookupStatus.Retry && attempt < RetryDelays.Length)
        {
            jobs.Schedule<ScoreboardRiotCheckJob>(j => j.RunAsync(reportId, attempt + 1, CancellationToken.None), RetryDelays[attempt]);
            return;
        }
        if (found is not { Status: RiotLookupStatus.Found, Match: { } match })
        {
            var status = found.Status == RiotLookupStatus.Unavailable ? RiotCheckStatus.Unavailable : RiotCheckStatus.NotFound;
            await SaveAsync(conn, report, verification with { Riot = new RiotCheck(status, null, DateTimeOffset.UtcNow) }, null, ct);
            return;
        }

        var submitted = ScoreboardReportVerifier.SubmittedPlayers(report.MatchData is null ? null : JsonDocument.Parse(report.MatchData).RootElement.Clone());
        var checkedVerification = ScoreboardVerification.ApplyRiotMatch(
            verification, linked, match, submitted, report.Team1Score.Value, report.Team2Score.Value, DateTimeOffset.UtcNow);
        await SaveAsync(conn, report, checkedVerification, match.MatchId, ct);

        var newFindings = checkedVerification.Players.Any(p => p.RiotStatus is { } r && RiotStatus.NeedsReview.Contains(r))
            || checkedVerification.Flags.Except(verification.Flags).Any();
        if (newFindings)
            await verifier.NotifyOrganizersAsync(conn, report.MatchId, report.Id, report.ReportedByTeamId, checkedVerification, ct);
        logger.LogInformation("Screenshot report {ReportId} checked against Riot match {RiotMatchId}", report.Id, match.MatchId);
    }

    private async Task SaveAsync(System.Data.IDbConnection conn, ReportRow report, OcrVerification verification, string? riotMatchId, CancellationToken ct)
    {
        await conn.ExecuteAsync(
            """
            UPDATE match_result_reports
            SET ocr_verification = @verification::jsonb,
                riot_match_id = COALESCE(riot_match_id, @riotMatchId),
                updated_at = now()
            WHERE id = @id
            """,
            new { id = report.Id, verification = JsonSerializer.Serialize(verification, OcrVerificationJson.Options), riotMatchId });
        await matchHub.Clients.Group(MatchHub.MatchGroup(report.MatchId.ToString()))
            .SendAsync(MatchHubEvents.ReportVerified, new { matchId = report.MatchId, reportId = report.Id }, ct);
    }
}
