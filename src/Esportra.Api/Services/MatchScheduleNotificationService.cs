using System.Data;
using System.Globalization;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Core.Tournaments;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Services;

/// <summary>
/// Broadcasts organizer schedule changes to match/bracket hubs and notifies team captains.
/// </summary>
public sealed class MatchScheduleNotificationService(
    IDbConnectionFactory db,
    IHubContext<MatchHub> matchHub,
    IHubContext<BracketHub> bracketHub,
    IHubContext<NotificationHub> notificationHub,
    ILogger<MatchScheduleNotificationService> logger)
{
    public async Task DispatchScheduleChangedAsync(
        Guid matchId,
        DateTime? scheduledTime,
        CancellationToken ct = default)
    {
        try
        {
            using var conn = db.CreateConnection();

            var match = await conn.QuerySingleOrDefaultAsync<MatchScheduleRow>(
                $"""
                SELECT m.id AS MatchId,
                       m.match_number AS MatchNumber,
                       m.round_index AS RoundIndex,
                       m.team1_id AS Team1Id,
                       m.team2_id AS Team2Id,
                       m.version_id AS VersionId,
                       t.slug AS TournamentSlug,
                       t.name AS TournamentName,
                       st.name AS StageName,
                       {BracketTeamResolutionSql.Team1Columns},
                       {BracketTeamResolutionSql.Team2Columns}
                FROM brkt_matches m
                JOIN brkt_versions v ON v.id = m.version_id
                JOIN tournaments t ON t.id = v.tournament_id
                LEFT JOIN tournament_stages st ON st.id = v.stage_id
                {BracketTeamResolutionSql.Team1Joins}
                {BracketTeamResolutionSql.Team2Joins}
                WHERE m.id = @matchId
                """,
                new { matchId });

            if (match is null)
                return;

            var scheduledUtc = NormalizeUtc(scheduledTime);
            var scheduledIso = scheduledUtc?.ToString("o");
            var hubPayload = new
            {
                matchId = matchId.ToString(),
                scheduledTime = scheduledIso,
                matchNumber = match.MatchNumber,
                roundIndex = match.RoundIndex,
                changedBy = "organizer",
            };

            await matchHub.Clients
                .Group(MatchHub.MatchGroup(matchId.ToString()))
                .SendAsync(MatchHubEvents.ScheduleChanged, hubPayload, ct);

            if (match.VersionId != Guid.Empty)
            {
                await bracketHub.Clients
                    .Group(BracketHub.BracketGroup(match.VersionId.ToString()))
                    .SendAsync(
                        BracketHubEvents.MatchUpdated,
                        new { versionId = match.VersionId, matchId, scheduledTime = scheduledIso },
                        ct);
            }

            var captainUserIds = (await conn.QueryAsync<Guid>(
                """
                SELECT DISTINCT tm.user_id
                FROM team_members tm
                WHERE tm.team_id IN (@team1Id, @team2Id)
                  AND tm.role = 'captain'
                  AND tm.is_active = TRUE
                  AND tm.user_id IS NOT NULL

                UNION

                SELECT DISTINCT tp.user_id
                FROM tournament_participants tp
                WHERE tp.id IN (@team1Id, @team2Id)
                  AND tp.user_id IS NOT NULL
                """,
                new
                {
                    team1Id = match.Team1Id ?? Guid.Empty,
                    team2Id = match.Team2Id ?? Guid.Empty,
                })).ToList();

            if (captainUserIds.Count == 0)
                return;

            var matchLabel = FormatMatchLabel(match.MatchNumber, match.RoundIndex);
            var matchup = FormatMatchup(match.Team1Name, match.Team2Name);
            var tournamentName = string.IsNullOrWhiteSpace(match.TournamentName)
                ? "Tournament"
                : match.TournamentName.Trim();
            var timeLabel = scheduledUtc.HasValue
                ? scheduledUtc.Value.ToString("ddd, MMM d · h:mm tt 'UTC'", CultureInfo.InvariantCulture)
                : "Time TBD";
            var title = scheduledUtc.HasValue
                ? $"{tournamentName} · {matchLabel} scheduled"
                : $"{tournamentName} · {matchLabel} schedule cleared";
            var bodyLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(match.StageName))
                bodyLines.Add(match.StageName.Trim());
            bodyLines.Add(matchup);
            bodyLines.Add(scheduledUtc.HasValue
                ? timeLabel
                : "Organizer removed the scheduled time.");
            var message = string.Join('\n', bodyLines);

            var link = !string.IsNullOrWhiteSpace(match.TournamentSlug)
                ? $"/tournaments/{match.TournamentSlug}/captain-match/{matchId}"
                : "/tournaments";
            var dataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                match_id = matchId.ToString(),
                tournament_slug = match.TournamentSlug,
                tournament_name = match.TournamentName,
                stage_name = match.StageName,
                team1_name = match.Team1Name,
                team2_name = match.Team2Name,
                match_number = match.MatchNumber,
                round_index = match.RoundIndex,
                match_label = matchLabel,
                matchup,
                scheduled_time = scheduledIso,
                time_label = timeLabel,
                schedule_cleared = !scheduledUtc.HasValue,
            });

            foreach (var userId in captainUserIds)
            {
                var notificationId = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO notifications
                      (user_id, type, title, message, link, data, is_read)
                    VALUES
                      (@userId, 'match_schedule_changed', @title, @message, @link, @data::jsonb, FALSE)
                    RETURNING id
                    """,
                    new
                    {
                        userId,
                        title,
                        message,
                        link,
                        data = dataJson,
                    });

                await notificationHub.Clients
                    .Group(NotificationHub.UserGroup(userId.ToString()))
                    .SendAsync(
                        NotificationHubEvents.NewNotification,
                        new
                        {
                            id = notificationId.ToString(),
                            type = "match_schedule_changed",
                            title,
                            message,
                            link,
                            data = System.Text.Json.JsonSerializer.Deserialize<object>(dataJson),
                        },
                        ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Schedule change broadcast failed for match {MatchId}", matchId);
        }
    }

    private static string FormatMatchLabel(int matchNumber, int roundIndex) =>
        roundIndex >= 0
            ? $"Round {roundIndex + 1}, Match {matchNumber}"
            : $"Match {matchNumber}";

    private static string FormatMatchup(string? team1Name, string? team2Name)
    {
        var left = string.IsNullOrWhiteSpace(team1Name) ? "TBD" : team1Name.Trim();
        var right = string.IsNullOrWhiteSpace(team2Name) ? "TBD" : team2Name.Trim();
        return $"{left} vs {right}";
    }

    internal static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        };
    }

    internal static bool ScheduledTimesEqual(DateTime? left, DateTime? right)
    {
        var normalizedLeft = NormalizeUtc(left);
        var normalizedRight = NormalizeUtc(right);
        if (normalizedLeft is null && normalizedRight is null) return true;
        if (normalizedLeft is null || normalizedRight is null) return false;
        return normalizedLeft.Value == normalizedRight.Value;
    }

    private sealed class MatchScheduleRow
    {
        public Guid MatchId { get; init; }
        public int MatchNumber { get; init; }
        public int RoundIndex { get; init; }
        public Guid? Team1Id { get; init; }
        public Guid? Team2Id { get; init; }
        public Guid VersionId { get; init; }
        public string? TournamentSlug { get; init; }
        public string? TournamentName { get; init; }
        public string? StageName { get; init; }
        public string? Team1Name { get; init; }
        public string? Team2Name { get; init; }
    }
}
