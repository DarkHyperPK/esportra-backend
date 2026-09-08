using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Esportra.Api.Services;
using Esportra.Contracts.Database;
using Hangfire;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

/// <summary>
/// Runs every 15 minutes. For every self-play match whose round scheduling deadline
/// has passed with no accepted proposal, notifies the organizer and both captains
/// once (idempotent via scheduling_escalated_at column).
/// </summary>
[Queue("default")]
[DisableConcurrentExecution(timeoutInSeconds: 120)]
public sealed class RoundDeadlineEscalationJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> notifHub,
    DiscordNotificationService discord,
    ILogger<RoundDeadlineEscalationJob> logger)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        // Find matches in stages with self_play_enabled where:
        // - status is pending (not started)
        // - no accepted time proposal
        // - the relevant round deadline has passed
        // - not already escalated
        var candidates = (await conn.QueryAsync<MatchEscalationRow>(
            """
            SELECT
                m.id AS MatchId,
                m.round_index AS RoundIndex,
                v.tournament_id AS TournamentId,
                t.organizer_id AS OrganizerId,
                t.name AS TournamentName
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            JOIN tournament_stages ts ON ts.id = v.stage_id
            JOIN tournaments t ON t.id = v.tournament_id
            WHERE m.status = 'pending'
              AND m.scheduling_escalated_at IS NULL
              AND m.scheduled_time IS NULL
              AND (ts.scheduling_config->>'selfPlayEnabled')::boolean = TRUE
              AND ts.scheduling_config->'roundDeadlines' IS NOT NULL
              AND (
                  ts.scheduling_config->'roundDeadlines'->>(m.round_index::text)
              )::timestamptz < NOW()
            """)).AsList();

        if (candidates.Count == 0) return;

        logger.LogInformation(
            "[RoundDeadlineEscalation] Processing {Count} overdue match(es).", candidates.Count);

        foreach (var match in candidates)
        {
            try
            {
                await EscalateMatchAsync(conn, match, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[RoundDeadlineEscalation] Failed to escalate match {MatchId}.", match.MatchId);
            }
        }
    }

    private async Task EscalateMatchAsync(
        System.Data.IDbConnection conn,
        MatchEscalationRow match,
        CancellationToken ct)
    {
        // Mark escalated atomically — skip if already done (race between job instances)
        var stamped = await conn.ExecuteAsync(
            """
            UPDATE brkt_matches
            SET scheduling_escalated_at = NOW()
            WHERE id = @matchId AND scheduling_escalated_at IS NULL
            """,
            new { matchId = match.MatchId });

        if (stamped == 0) return;

        var captains = (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT tm.user_id
            FROM team_members tm
            WHERE tm.team_id IN (
                SELECT team1_id FROM brkt_matches WHERE id = @matchId
                UNION
                SELECT team2_id FROM brkt_matches WHERE id = @matchId
            )
              AND tm.role = 'captain' AND tm.is_active = TRUE
            UNION
            SELECT tp.user_id
            FROM tournament_participants tp
            WHERE tp.id IN (
                SELECT team1_id FROM brkt_matches WHERE id = @matchId
                UNION
                SELECT team2_id FROM brkt_matches WHERE id = @matchId
            ) AND tp.user_id IS NOT NULL
            """,
            new { matchId = match.MatchId })).ToList();

        var organizerTitle = $"Match needs scheduling — deadline passed [{match.TournamentName}]";
        var organizerMsg = $"A match in {match.TournamentName} has not been scheduled and the round deadline has passed. Please force a time or award a walkover.";

        // Notify organizer
        await conn.ExecuteAsync(
            """
            INSERT INTO notifications (user_id, type, title, message, link, is_read)
            VALUES (@userId, 'scheduling_escalation'::notification_type, @title, @message,
                    '/organizer/tournaments/' || @tournamentId::text || '/matches', FALSE)
            ON CONFLICT DO NOTHING
            """,
            new { userId = match.OrganizerId, title = organizerTitle, message = organizerMsg, tournamentId = match.TournamentId });

        try
        {
            await notifHub.Clients.Group(NotificationHub.UserGroup(match.OrganizerId.ToString()))
                .SendAsync(NotificationHubEvents.NewNotification,
                    new { type = "scheduling_escalation", title = organizerTitle, message = organizerMsg },
                    ct);
        }
        catch { /* non-critical */ }

        await discord.TrySendDmAsync(match.OrganizerId, "scheduling_escalation", organizerTitle, organizerMsg);

        // Notify captains
        if (captains.Count > 0)
        {
            var captainTitle = "Match scheduling deadline passed";
            var captainMsg = $"The scheduling deadline for your match in {match.TournamentName} has passed without an agreed time. The organizer has been notified.";

            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, is_read)
                SELECT uid, 'scheduling_escalation'::notification_type, @title, @message, FALSE
                FROM UNNEST(@userIds::uuid[]) AS uid
                ON CONFLICT DO NOTHING
                """,
                new { userIds = captains.ToArray(), title = captainTitle, message = captainMsg });

            await TeamNotifications.PushAsync(notifHub, captains, "scheduling_escalation", captainTitle, captainMsg);

            foreach (var captainId in captains)
                await discord.TrySendDmAsync(captainId, "scheduling_escalation", captainTitle, captainMsg);
        }

        logger.LogInformation(
            "[RoundDeadlineEscalation] Escalated match {MatchId} (tournament={TournamentId}).",
            match.MatchId, match.TournamentId);
    }

    private sealed record MatchEscalationRow(
        Guid MatchId,
        int RoundIndex,
        Guid TournamentId,
        Guid OrganizerId,
        string TournamentName);
}
