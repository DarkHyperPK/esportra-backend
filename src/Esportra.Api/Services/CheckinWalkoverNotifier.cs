using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using Esportra.Core.Notifications;
using Esportra.Core.Tournaments;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Services;

/// <summary>
/// SignalR + in-app notifications after an automatic check-in walkover.
/// </summary>
public sealed class CheckinWalkoverNotifier(
    IDbConnectionFactory db,
    IHubContext<MatchHub> matchHub,
    IHubContext<BracketHub> bracketHub,
    IHubContext<NotificationHub> notifHub)
{
    public async Task NotifyAsync(
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

        await matchHub.Clients
            .Group(MatchHub.MatchGroup(matchId.ToString()))
            .SendAsync(
                MatchHubEvents.StatusChanged,
                new { matchId, status = "completed", reason = "walkover" },
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
                WHERE tp.id = ANY(@teamIds) AND tp.participant_type = 'solo'
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
            var isNoShow = (recipient.CompetitorId == match.Team1Id && !outcome.Team1CheckedIn)
                        || (recipient.CompetitorId == match.Team2Id && !outcome.Team2CheckedIn);

            string title;
            string message;
            if (!outcome.Team1CheckedIn && !outcome.Team2CheckedIn)
            {
                title = "⚠️ Double Forfeit";
                message = "Neither team checked in. Both teams have been eliminated from this match.";
            }
            else if (isNoShow)
            {
                title = "❌ Match Forfeited";
                message = "Your team missed the check-in window. A walkover has been awarded to your opponent.";
            }
            else
            {
                title = "✅ Walkover Win";
                message = "Your opponent failed to check in. You've been awarded a walkover victory!";
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'match_walkover', @title, @message, @link,
                        jsonb_build_object(
                            'match_id', @matchId::text,
                            'tournament_slug', @tournamentSlug
                        )::jsonb,
                        false)
                """,
                new
                {
                    userId = recipient.UserId,
                    title,
                    message,
                    link = matchLink,
                    matchId,
                    tournamentSlug = matchContext.TournamentSlug,
                });

            await notifHub.Clients
                .Group(NotificationHub.UserGroup(recipient.UserId.ToString()))
                .SendAsync(
                    NotificationHubEvents.NewNotification,
                    new { type = "match_walkover", title, message },
                    ct);
        }
    }

    private sealed class MatchRow
    {
        public Guid? Team1Id { get; init; }
        public Guid? Team2Id { get; init; }
        public Guid VersionId { get; init; }
    }

    private sealed class RecipientInfo
    {
        public Guid UserId { get; init; }
        public Guid CompetitorId { get; init; }
    }
}
