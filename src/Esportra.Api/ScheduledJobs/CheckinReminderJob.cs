using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Sends Discord DM reminders to match captains who have not yet checked in,
/// fired 15 minutes before the check-in deadline. DM-only — no in-app row inserted.
/// </summary>
[Queue("notifications")]
public sealed class CheckinReminderJob(
    IDbConnectionFactory db,
    DiscordNotificationService discord,
    IConfiguration config,
    ILogger<CheckinReminderJob> logger)
{
    public async Task ExecuteAsync(Guid matchId, CancellationToken ct)
    {
        if (!discord.IsConfigured)
            return;

        using var conn = db.CreateConnection();

        var matchRow = await conn.QuerySingleOrDefaultAsync<(string Status, DateTime? CheckInDeadline)>(
            """
            SELECT status AS Status, check_in_deadline AS CheckInDeadline
            FROM brkt_matches WHERE id = @matchId
            """,
            new { matchId });

        if (matchRow == default || matchRow.Status != "pending")
            return;

        if (matchRow.CheckInDeadline.HasValue && matchRow.CheckInDeadline.Value.ToUniversalTime() < DateTime.UtcNow)
            return;

        var tournInfo = await conn.QuerySingleOrDefaultAsync<CheckinTournamentInfo>(
            """
            SELECT t.game AS Game, t.id AS TournamentId, t.name AS TournamentName
            FROM brkt_matches bm
            JOIN brkt_rounds r ON r.id = bm.round_id
            JOIN stages s ON s.id = r.stage_id
            JOIN tournaments t ON t.id = s.tournament_id
            WHERE bm.id = @matchId
            """,
            new { matchId });
        var gameSlug = tournInfo?.Game;
        Guid? tournamentId = tournInfo?.TournamentId is Guid tid && tid != Guid.Empty ? tid : null;
        var tournamentName = tournInfo?.TournamentName ?? "";

        var uncheckedUserIds = await QueryUncheckedCaptainsAsync(conn, matchId);

        if (uncheckedUserIds.Count == 0)
            return;

        const string title = "Check-in Reminder";
        const string message = "15 minutes left to check in for your match.";
        var frontendUrl = config["FrontendUrl"] ?? "https://esportra.com";
        var dmMessage = $"{message}\n\n[View →]({frontendUrl}/notifications)";
        var dmTitle = string.IsNullOrWhiteSpace(tournamentName)
            ? title
            : $"[{tournamentName}] {title}";

        foreach (var userId in uncheckedUserIds)
            await discord.TrySendDmAsync(userId, "check_in_reminder", dmTitle, dmMessage, gameSlug, tournamentId);

        logger.LogInformation(
            "[CheckinReminder] Sent DM reminders to {Count} unchecked captain(s) for match {MatchId}.",
            uncheckedUserIds.Count, matchId);
    }

    private static async Task<List<Guid>> QueryUncheckedCaptainsAsync(
        System.Data.IDbConnection conn, Guid matchId)
    {
        var allCaptains = (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT user_id FROM (
                SELECT tp.user_id
                FROM tournament_participants tp
                JOIN brkt_matches bm ON tp.id IN (bm.team1_id, bm.team2_id)
                WHERE bm.id = @matchId AND tp.user_id IS NOT NULL

                UNION

                SELECT tm.user_id
                FROM team_members tm
                JOIN brkt_matches bm ON tm.team_id IN (bm.team1_id, bm.team2_id)
                WHERE bm.id = @matchId AND tm.role = 'captain' AND tm.is_active = TRUE
            ) captains
            WHERE user_id IS NOT NULL
            """,
            new { matchId })).ToList();

        if (allCaptains.Count == 0)
            return allCaptains;

        var checkedInUserIds = (await conn.QueryAsync<Guid>(
            "SELECT user_id FROM match_checkins WHERE match_id = @matchId",
            new { matchId })).ToHashSet();

        return allCaptains.Where(uid => !checkedInUserIds.Contains(uid)).ToList();
    }
}
