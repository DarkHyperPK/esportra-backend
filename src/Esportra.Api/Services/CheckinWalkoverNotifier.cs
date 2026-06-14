using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using System.Data;
using Esportra.Core.Notifications;
using Esportra.Core.Tournaments;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Services;

/// <summary>
/// SignalR + in-app notifications after automatic check-in walkovers
/// and organizer alerts when neither team checks in.
/// </summary>
public sealed class CheckinWalkoverNotifier(
    IDbConnectionFactory db,
    IHubContext<MatchHub> matchHub,
    IHubContext<BracketHub> bracketHub,
    IHubContext<NotificationHub> notifHub)
{
    public async Task DispatchAsync(
        Guid matchId,
        CheckinWalkoverOutcome outcome,
        CancellationToken ct = default)
    {
        if (outcome.Processed)
            await NotifyWalkoverAsync(matchId, outcome, ct);

        if (outcome.NeedsOrganizerNotification)
            await NotifyOrganizerNeitherCheckedInAsync(matchId, outcome, ct);
    }

    public async Task NotifyWalkoverAsync(
        Guid matchId,
        CheckinWalkoverOutcome outcome,
        CancellationToken ct = default)
    {
        if (!outcome.Processed)
            return;

        using var conn = db.CreateConnection();
        var match = await conn.QuerySingleOrDefaultAsync<MatchRow>(
            """
            SELECT team1_id AS Team1Id, team2_id AS Team2Id, version_id AS VersionId
            FROM public.brkt_matches
            WHERE id = @matchId
            """,
            new { matchId });

        if (match?.Team1Id is null || match.Team2Id is null)
            return;

        var isDoubleForfeit = outcome.Status == CheckinWalkoverStatus.DoubleForfeit;

        await matchHub.Clients
            .Group(MatchHub.MatchGroup(matchId.ToString()))
            .SendAsync(
                MatchHubEvents.StatusChanged,
                new { matchId, status = "completed", reason = isDoubleForfeit ? "double_forfeit" : "walkover" },
                ct);

        if (match.VersionId != Guid.Empty)
        {
            await bracketHub.Clients
                .Group(BracketHub.BracketGroup(match.VersionId.ToString()))
                .SendAsync(
                    BracketHubEvents.MatchUpdated,
                    new { versionId = match.VersionId, matchId },
                    ct);
        }

        var teamIds = new[] { match.Team1Id.Value, match.Team2Id.Value };
        var recipients = (await conn.QueryAsync<RecipientInfo>(
            """
            SELECT DISTINCT user_id AS UserId, competitor_id AS CompetitorId
            FROM (
                SELECT tp.user_id, tp.id AS competitor_id
                FROM public.tournament_participants tp
                WHERE tp.id = ANY(@teamIds) AND tp.user_id IS NOT NULL
                UNION
                SELECT tm.user_id, tm.team_id AS competitor_id
                FROM public.team_members tm
                WHERE tm.team_id = ANY(@teamIds) AND tm.role = 'captain' AND tm.is_active = true
                UNION
                SELECT tp.team_captain_id, tp.team_id AS competitor_id
                FROM public.tournament_participants tp
                WHERE tp.team_id = ANY(@teamIds)
                  AND tp.team_captain_id IS NOT NULL
            ) recipients
            WHERE user_id IS NOT NULL
            """,
            new { teamIds })).AsList();

        var matchContext = await CaptainMatchLinkBuilder.ResolveContextAsync(conn, matchId);
        var matchLink = CaptainMatchLinkBuilder.BuildLink(matchContext.TournamentSlug, matchId);

        foreach (var recipient in recipients)
        {
            var isNoShow = isDoubleForfeit
                        || (recipient.CompetitorId == match.Team1Id && !outcome.Team1CheckedIn)
                        || (recipient.CompetitorId == match.Team2Id && !outcome.Team2CheckedIn);

            var title = isNoShow ? "❌ Match Forfeited" : "✅ Walkover Win";
            var message = isDoubleForfeit
                ? "Neither team checked in before the window closed. Both teams have forfeited the match."
                : isNoShow
                    ? "Your team missed the check-in window. A walkover has been awarded to your opponent."
                    : "Your opponent failed to check in. You've been awarded a walkover victory!";

            await InsertNotificationAsync(
                conn,
                recipient.UserId,
                title,
                message,
                matchLink,
                new
                {
                    match_id = matchId.ToString(),
                    tournament_slug = matchContext.TournamentSlug,
                    audience = "captain",
                    reason = isDoubleForfeit ? "double_forfeit" : "walkover",
                },
                ct);
        }
    }

    public async Task NotifyOrganizerNeitherCheckedInAsync(
        Guid matchId,
        CheckinWalkoverOutcome outcome,
        CancellationToken ct = default)
    {
        if (!outcome.NeedsOrganizerNotification)
            return;

        using var conn = db.CreateConnection();
        var context = await conn.QuerySingleOrDefaultAsync<OrganizerAlertRow>(
            $"""
            SELECT m.id AS MatchId,
                   m.match_number AS MatchNumber,
                   m.round_index AS RoundIndex,
                   v.id AS VersionId,
                   t.id AS TournamentId,
                   t.slug AS TournamentSlug,
                   t.name AS TournamentName,
                   st.name AS StageName,
                   {BracketTeamResolutionSql.Team1Columns},
                   {BracketTeamResolutionSql.Team2Columns}
            FROM public.brkt_matches m
            JOIN public.brkt_versions v ON v.id = m.version_id
            JOIN public.tournaments t ON t.id = v.tournament_id
            LEFT JOIN public.tournament_stages st ON st.id = v.stage_id
            {BracketTeamResolutionSql.Team1Joins}
            {BracketTeamResolutionSql.Team2Joins}
            WHERE m.id = @matchId
            """,
            new { matchId });

        if (context is null)
            return;

        var matchLabel = FormatMatchLabel(context.MatchNumber, context.RoundIndex);
        var matchup = FormatMatchup(context.Team1Name, context.Team2Name);
        var tournamentName = string.IsNullOrWhiteSpace(context.TournamentName)
            ? "Tournament"
            : context.TournamentName.Trim();
        var bracketLink = !string.IsNullOrWhiteSpace(context.TournamentSlug)
            ? $"/organizer/tournament/{context.TournamentSlug}/brackets"
            : "/organizer/tournaments";

        var title = $"{tournamentName} · {matchLabel} — no check-ins";
        var bodyLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.StageName))
            bodyLines.Add(context.StageName.Trim());
        bodyLines.Add(matchup);
        bodyLines.Add("Neither team checked in before the window closed.");
        bodyLines.Add("Review or reset the forfeited match from the bracket.");
        var message = string.Join('\n', bodyLines);

        var organizerIds = (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT r.user_id
            FROM (
                SELECT t.organizer_id AS user_id
                FROM public.tournaments t
                WHERE t.id = @tournamentId
                  AND t.organizer_id IS NOT NULL

                UNION

                SELECT os.user_id
                FROM public.tournaments t
                JOIN public.organizations o ON o.id = t.organization_id
                JOIN public.organization_staff os
                  ON os.organization_id = o.id
                 AND os.status = 'active'
                LEFT JOIN public.staff_tournament_assignments sta
                  ON sta.organization_staff_id = os.id
                 AND sta.tournament_id = t.id
                WHERE t.id = @tournamentId
                  AND (os.role = 'admin' OR sta.id IS NOT NULL)
            ) r
            WHERE r.user_id IS NOT NULL
            """,
            new { tournamentId = context.TournamentId })).AsList();

        var dataPayload = new
        {
            match_id = matchId.ToString(),
            tournament_id = context.TournamentId.ToString(),
            tournament_slug = context.TournamentSlug,
            tournament_name = context.TournamentName,
            stage_name = context.StageName,
            match_number = context.MatchNumber,
            round_index = context.RoundIndex,
            match_label = matchLabel,
            matchup,
            audience = "organizer",
            reason = "neither_checked_in",
        };

        foreach (var userId in organizerIds)
        {
            var alreadySent = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM public.notifications
                    WHERE user_id = @userId
                      AND type = 'match_walkover'
                      AND data->>'match_id' = @matchId::text
                      AND data->>'audience' = 'organizer'
                      AND data->>'reason' = 'neither_checked_in'
                )
                """,
                new { userId, matchId });

            if (alreadySent)
                continue;

            await InsertNotificationAsync(conn, userId, title, message, bracketLink, dataPayload, ct);
        }

        if (context.VersionId != Guid.Empty)
        {
            await bracketHub.Clients
                .Group(BracketHub.BracketGroup(context.VersionId.ToString()))
                .SendAsync(
                    BracketHubEvents.MatchUpdated,
                    new { versionId = context.VersionId, matchId, checkinAlert = "neither_checked_in" },
                    ct);
        }
    }

    private async Task InsertNotificationAsync(
        IDbConnection conn,
        Guid userId,
        string title,
        string message,
        string link,
        object dataPayload,
        CancellationToken ct)
    {
        var dataJson = JsonSerializer.Serialize(dataPayload);
        var notificationId = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
            VALUES (@userId, 'match_walkover', @title, @message, @link, @data::jsonb, FALSE)
            RETURNING id
            """,
            new { userId, title, message, link, data = dataJson });

        await notifHub.Clients
            .Group(NotificationHub.UserGroup(userId.ToString()))
            .SendAsync(
                NotificationHubEvents.NewNotification,
                new
                {
                    id = notificationId.ToString(),
                    type = "match_walkover",
                    title,
                    message,
                    link,
                    data = JsonSerializer.Deserialize<object>(dataJson),
                },
                ct);
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

    private sealed class MatchRow
    {
        public Guid? Team1Id { get; init; }
        public Guid? Team2Id { get; init; }
        public Guid VersionId { get; init; }
    }

    private sealed class OrganizerAlertRow
    {
        public Guid MatchId { get; init; }
        public int MatchNumber { get; init; }
        public int RoundIndex { get; init; }
        public Guid VersionId { get; init; }
        public Guid TournamentId { get; init; }
        public string? TournamentSlug { get; init; }
        public string? TournamentName { get; init; }
        public string? StageName { get; init; }
        public string? Team1Name { get; init; }
        public string? Team2Name { get; init; }
    }

    private sealed class RecipientInfo
    {
        public Guid UserId { get; init; }
        public Guid CompetitorId { get; init; }
    }
}
