using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.ScheduledJobs;

public sealed class TournamentCheckinDeadlineJob(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> notifHub,
    ILogger<TournamentCheckinDeadlineJob> logger)
{
    public async Task ExecuteAsync(Guid tournamentId, CancellationToken ct)
    {
        using var conn = db.CreateConnection();

        var tournament = await conn.QuerySingleOrDefaultAsync<TournamentInfo>(
            """
            SELECT id AS TournamentId, name AS TournamentName, organizer_id AS OrganizerId,
                   auto_remove_unchecked AS AutoRemove
            FROM tournaments
            WHERE id = @tournamentId
              AND check_in_required = true
            """,
            new { tournamentId });

        if (tournament is null || !tournament.AutoRemove)
        {
            logger.LogDebug("[TournamentCheckinDeadline] Tournament {Id} not eligible (missing or auto_remove disabled).", tournamentId);
            return;
        }

        var affectedUsers = (await conn.QueryAsync<AffectedParticipant>(
            """
            SELECT
                COALESCE(tp.team_captain_id, tp.user_id) AS UserId
            FROM tournament_participants tp
            WHERE tp.tournament_id = @tournamentId
              AND tp.checked_in_at IS NULL
              AND tp.status IN ('pending', 'approved')
            """,
            new { tournamentId })).AsList();

        var removedCount = await conn.ExecuteAsync(
            """
            UPDATE tournament_participants
            SET status = 'cancelled'
            WHERE tournament_id = @tournamentId
              AND checked_in_at IS NULL
              AND status IN ('pending', 'approved')
            """,
            new { tournamentId });

        if (removedCount == 0)
        {
            logger.LogDebug("[TournamentCheckinDeadline] Tournament {Id}: all participants checked in.", tournamentId);
            return;
        }

        logger.LogInformation(
            "[TournamentCheckinDeadline] Tournament {Id} ({Name}): removed {Count} unchecked participant(s).",
            tournamentId, tournament.TournamentName, removedCount);

        // Notify affected participants
        foreach (var p in affectedUsers.Where(u => u.UserId != Guid.Empty))
        {
            var dataJson = JsonSerializer.Serialize(new { tournament_id = tournamentId });
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, data, is_read)
                VALUES (@userId, 'tournament_announcement', @title, @message, @data::jsonb, false)
                """,
                new
                {
                    userId = p.UserId,
                    title = "Removed from Tournament",
                    message = $"You were removed from {tournament.TournamentName} because you didn't check in before the deadline.",
                    data = dataJson
                });

            await notifHub.Clients
                .Group(NotificationHub.UserGroup(p.UserId.ToString()))
                .SendAsync(NotificationHubEvents.NewNotification, new
                {
                    type = "tournament_announcement",
                    title = "Removed from Tournament",
                    message = $"You were removed from {tournament.TournamentName} because you didn't check in before the deadline.",
                    data = new { tournament_id = tournamentId },
                }, ct);
        }

        // Notify organizer
        var orgData = JsonSerializer.Serialize(new { tournament_id = tournamentId, removed_count = removedCount });
        await conn.ExecuteAsync(
            """
            INSERT INTO notifications (user_id, type, title, message, data, is_read)
            VALUES (@userId, 'tournament_announcement', @title, @message, @data::jsonb, false)
            """,
            new
            {
                userId = tournament.OrganizerId,
                title = "Participants Auto-Removed",
                message = $"{removedCount} participant(s) were removed from {tournament.TournamentName} for not checking in before the deadline.",
                data = orgData
            });

        await notifHub.Clients
            .Group(NotificationHub.UserGroup(tournament.OrganizerId.ToString()))
            .SendAsync(NotificationHubEvents.NewNotification, new
            {
                type = "tournament_announcement",
                title = "Participants Auto-Removed",
                message = $"{removedCount} participant(s) were removed from {tournament.TournamentName} for not checking in before the deadline.",
                data = new { tournament_id = tournamentId, removed_count = removedCount },
            }, ct);
    }

    private sealed record TournamentInfo(Guid TournamentId, string TournamentName, Guid OrganizerId, bool AutoRemove);
    private sealed record AffectedParticipant(Guid UserId);
}
