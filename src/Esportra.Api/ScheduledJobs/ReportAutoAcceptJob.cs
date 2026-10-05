using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Endpoints;
using Esportra.Api.Hubs;
using Esportra.Api.ScoreboardOcr;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>How long the opponent has to accept or dispute a submitted result.</summary>
public static class ReportAcceptWindow
{
    public static TimeSpan Of(IConfiguration config) => TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Reports:AcceptWindowMinutes", 15)));
}

/// <summary>Schedules the deadline check of a submitted report.</summary>
public interface IReportAutoAcceptScheduler
{
    void Schedule(Guid reportId, DateTimeOffset acceptBy);
}

public sealed class ReportAutoAcceptScheduler(IBackgroundJobClient jobs) : IReportAutoAcceptScheduler
{
    public void Schedule(Guid reportId, DateTimeOffset acceptBy) =>
        jobs.Schedule<ReportAutoAcceptJob>(j => j.RunAsync(reportId, CancellationToken.None), acceptBy);
}

/// <summary>
/// Accepts a result the opponent didn't answer within the response window, so a team that goes quiet
/// can't stall the bracket. A report with warnings is accepted too; organizers get the warnings
/// afterwards and can still overturn it. Only two different results for the same game wait: that's a
/// real conflict, not a missing answer.
/// </summary>
public sealed class ReportAutoAcceptJob(
    IDbConnectionFactory db, IServiceProvider services, ScoreboardReportVerifier verifier, ILogger<ReportAutoAcceptJob> logger)
{
    public const string Reason = "timeout";
    private const int SweepBatch = 50;

    private sealed record PendingReport(
        Guid Id, Guid MatchId, int GameNumber, Guid ReportedByTeamId, int? Team1Score, int? Team2Score, Guid? WinnerTeamId,
        DateTime? AcceptBy, string Status, string? OcrVerification);

    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(Guid reportId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var report = await conn.QuerySingleOrDefaultAsync<PendingReport>(
            """
            SELECT id AS Id, match_id AS MatchId, game_number AS GameNumber, reported_by_team_id AS ReportedByTeamId,
                   team1_score AS Team1Score, team2_score AS Team2Score, winner_team_id AS WinnerTeamId,
                   accept_by AS AcceptBy, status AS Status, ocr_verification::text AS OcrVerification
            FROM match_result_reports WHERE id = @reportId
            """,
            new { reportId });
        // Answered in time, resubmitted with a new deadline, or never had one: nothing to do.
        if (report is null || report.Status != "pending" || report.AcceptBy is null || report.AcceptBy > DateTime.UtcNow) return;

        var other = await conn.QueryFirstOrDefaultAsync<PendingReport>(
            """
            SELECT id AS Id, match_id AS MatchId, game_number AS GameNumber, reported_by_team_id AS ReportedByTeamId,
                   team1_score AS Team1Score, team2_score AS Team2Score, winner_team_id AS WinnerTeamId,
                   accept_by AS AcceptBy, status AS Status, NULL AS OcrVerification
            FROM match_result_reports
            WHERE match_id = @MatchId AND game_number = @GameNumber AND reported_by_team_id <> @ReportedByTeamId AND status = 'pending'
            """,
            report);
        if (other is not null && (other.Team1Score, other.Team2Score) != (report.Team1Score, report.Team2Score))
        {
            logger.LogInformation("Report {ReportId} not auto-accepted: the opponent reported a different result", reportId);
            return;
        }

        var outcome = await MatchSystemEndpoints.AcceptReportCoreAsync(
            conn, report.MatchId, report.Id, respondedBy: null, Reason, requirePending: true, services, logger, ct);
        if (!outcome.Accepted) return;
        if (other is not null)
        {
            // Both teams reported the same result: the opponent's copy is settled with it.
            await conn.ExecuteAsync(
                "UPDATE match_result_reports SET status = 'accepted', auto_accepted = @Reason, responded_at = NOW() WHERE id = @id AND status = 'pending'",
                new { Reason, id = other.Id });
        }

        await ReportAutoAcceptance.AnnounceAsync(conn, services, report.MatchId, report.Id, Reason, ct);
        await AlertOrganizersIfFlaggedAsync(conn, report, ct);
        logger.LogInformation("Report {ReportId} accepted automatically: no response by {AcceptBy:u}", report.Id, report.AcceptBy);
    }

    /// <summary>Safety net for restarts and lost scheduled jobs: every overdue pending report.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        var overdue = await conn.QueryAsync<Guid>(
            """
            SELECT id FROM match_result_reports
            WHERE status = 'pending' AND accept_by <= NOW()
            ORDER BY accept_by
            LIMIT @SweepBatch
            """,
            new { SweepBatch });
        foreach (var id in overdue)
        {
            try
            {
                await RunAsync(id, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-accept of overdue report {ReportId} failed; the next sweep retries", id);
            }
        }
    }

    private async Task AlertOrganizersIfFlaggedAsync(IDbConnection conn, PendingReport report, CancellationToken ct)
    {
        if (report.OcrVerification is null) return;
        var verification = JsonSerializer.Deserialize<OcrVerification>(report.OcrVerification, OcrVerificationJson.Options);
        if (verification is null || (verification.NeedsReview.Count == 0 && verification.Flags.Count == 0)) return;
        await verifier.NotifyOrganizersAsync(
            conn, report.MatchId, report.Id, report.ReportedByTeamId,
            verification with { Flags = [.. verification.Flags, ScoreboardReportVerifier.FlagAutoAccepted] }, ct);
    }
}

/// <summary>Tells both teams that the system accepted a result, and why.</summary>
public static class ReportAutoAcceptance
{
    public static async Task AnnounceAsync(IDbConnection conn, IServiceProvider services, Guid matchId, Guid reportId, string reason, CancellationToken ct)
    {
        var matchHub = services.GetRequiredService<IHubContext<MatchHub>>();
        await matchHub.Clients.Group(MatchHub.MatchGroup(matchId.ToString()))
            .SendAsync(MatchHubEvents.ReportAccepted, new { matchId, reportId, autoAccepted = reason }, ct);

        var match = await conn.QuerySingleOrDefaultAsync<(Guid? Team1Id, Guid? Team2Id, string? Slug)>(
            """
            SELECT m.team1_id, m.team2_id, t.slug
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            JOIN tournaments t ON t.id = v.tournament_id
            WHERE m.id = @matchId
            """,
            new { matchId });
        var (title, message) = reason == ReportAutoAcceptJob.Reason
            ? ("✅ Result accepted automatically", "The reported result was accepted because there was no response in time.")
            : ("✅ Result confirmed by Riot", "The reported result matches Riot's record of the match and was accepted.");
        var link = match.Slug is null ? "/tournaments" : $"/tournaments/{match.Slug}/captain-match/{matchId}";
        foreach (var competitor in new[] { match.Team1Id, match.Team2Id })
        {
            if (competitor is not { } competitorId) continue;
            var userId = await BracketCompetitorResolver.GetPrimaryUserIdForCompetitorAsync(conn, competitorId);
            if (userId is null) continue;
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'result_accepted', @title, @message, @link, @data::jsonb, FALSE)
                """,
                new
                {
                    userId,
                    title,
                    message,
                    link,
                    data = JsonSerializer.Serialize(new { match_id = matchId, report_id = reportId, auto_accepted = reason }),
                });
        }
    }
}
