using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Services;

/// <summary>
/// Notifies BR participants when lobby or game schedules change.
/// </summary>
public sealed class BrScheduleNotificationService(
    IDbConnectionFactory db,
    IHubContext<NotificationHub> notificationHub,
    ILogger<BrScheduleNotificationService> logger)
{
    public async Task DispatchGameScheduleChangedAsync(
        Guid gameId,
        DateTimeOffset? previousScheduledAt,
        DateTimeOffset? scheduledAt,
        CancellationToken ct = default)
    {
        if (ScheduledTimesEqual(previousScheduledAt, scheduledAt))
            return;

        try
        {
            using var conn = db.CreateConnection();
            var meta = await conn.QuerySingleOrDefaultAsync<GameScheduleRow>(
                """
                SELECT g.id AS GameId,
                       g.game_number AS GameNumber,
                       g.lobby_id AS LobbyId,
                       l.wave_number AS WaveNumber,
                       l.lobby_code AS LobbyCode,
                       ts.name AS StageName,
                       ts.stage_order AS StageOrder,
                       t.name AS TournamentName,
                       t.slug AS TournamentSlug,
                       (
                           SELECT string_agg(g2.name, ' + ' ORDER BY g2.group_order)
                           FROM br_lobby_groups lg
                           JOIN br_groups g2 ON g2.id = lg.group_id
                           WHERE lg.lobby_id = l.id
                       ) AS GroupLabel
                FROM br_games g
                JOIN br_lobbies l ON l.id = g.lobby_id
                JOIN tournament_stages ts ON ts.id = l.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE g.id = @gameId
                """,
                new { gameId });

            if (meta is null)
                return;

            var userIds = await LoadLobbyParticipantUserIdsAsync(conn, meta.LobbyId);
            if (userIds.Count == 0)
                return;

            var contextLabel = BuildContextLabel(meta.StageName, meta.StageOrder, meta.GroupLabel, meta.WaveNumber);
            var title = scheduledAt is null
                ? $"Game {meta.GameNumber} schedule cleared"
                : $"Game {meta.GameNumber} rescheduled";
            var lobbyCodeSuffix = !string.IsNullOrWhiteSpace(meta.LobbyCode)
                ? $" Lobby code: {meta.LobbyCode.Trim()}."
                : string.Empty;
            Func<string, string> buildMessage = timeLabel => scheduledAt is null
                ? $"{contextLabel} — Game {meta.GameNumber} no longer has a scheduled start time.{lobbyCodeSuffix}"
                : $"{contextLabel} — Game {meta.GameNumber} is scheduled for {timeLabel}.{lobbyCodeSuffix}";

            var link = BuildBrRoomLink(meta.TournamentSlug);
            await InsertAndPushAsync(conn, userIds, "br_game_schedule_changed", title, scheduledAt, buildMessage, link, new
            {
                game_id = meta.GameId.ToString(),
                lobby_id = meta.LobbyId.ToString(),
                game_number = meta.GameNumber,
                wave_number = meta.WaveNumber,
                lobby_code = meta.LobbyCode,
                scheduled_at = scheduledAt?.ToString("o"),
                tournament_slug = meta.TournamentSlug,
                tournament_name = meta.TournamentName,
                stage_name = meta.StageName,
                group_label = meta.GroupLabel,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BR game schedule notification failed for game {GameId}", gameId);
        }
    }

    public async Task DispatchLobbyScheduleChangedAsync(
        Guid lobbyId,
        DateTimeOffset? previousScheduledAt,
        DateTimeOffset? scheduledAt,
        CancellationToken ct = default)
    {
        if (ScheduledTimesEqual(previousScheduledAt, scheduledAt))
            return;

        try
        {
            using var conn = db.CreateConnection();
            var meta = await conn.QuerySingleOrDefaultAsync<LobbyScheduleRow>(
                """
                SELECT l.id AS LobbyId,
                       l.wave_number AS WaveNumber,
                       l.lobby_code AS LobbyCode,
                       ts.name AS StageName,
                       ts.stage_order AS StageOrder,
                       t.name AS TournamentName,
                       t.slug AS TournamentSlug,
                       (
                           SELECT string_agg(g2.name, ' + ' ORDER BY g2.group_order)
                           FROM br_lobby_groups lg
                           JOIN br_groups g2 ON g2.id = lg.group_id
                           WHERE lg.lobby_id = l.id
                       ) AS GroupLabel
                FROM br_lobbies l
                JOIN tournament_stages ts ON ts.id = l.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE l.id = @lobbyId
                """,
                new { lobbyId });

            if (meta is null)
                return;

            var userIds = await LoadLobbyParticipantUserIdsAsync(conn, lobbyId);
            if (userIds.Count == 0)
                return;

            var contextLabel = BuildContextLabel(meta.StageName, meta.StageOrder, meta.GroupLabel, meta.WaveNumber);
            var roundLabel = meta.WaveNumber > 0 ? $"Round {meta.WaveNumber}" : "Lobby";
            var title = scheduledAt is null
                ? $"{roundLabel} schedule cleared"
                : $"{roundLabel} rescheduled";
            var lobbyCodeSuffix = !string.IsNullOrWhiteSpace(meta.LobbyCode)
                ? $" Lobby code: {meta.LobbyCode.Trim()}."
                : string.Empty;
            Func<string, string> buildMessage = timeLabel => scheduledAt is null
                ? $"{contextLabel} — {roundLabel} no longer has a scheduled start time.{lobbyCodeSuffix}"
                : $"{contextLabel} — {roundLabel} is scheduled for {timeLabel}.{lobbyCodeSuffix}";

            var link = BuildBrRoomLink(meta.TournamentSlug);
            await InsertAndPushAsync(conn, userIds, "br_lobby_schedule_changed", title, scheduledAt, buildMessage, link, new
            {
                lobby_id = meta.LobbyId.ToString(),
                wave_number = meta.WaveNumber,
                lobby_code = meta.LobbyCode,
                scheduled_at = scheduledAt?.ToString("o"),
                tournament_slug = meta.TournamentSlug,
                tournament_name = meta.TournamentName,
                stage_name = meta.StageName,
                group_label = meta.GroupLabel,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BR lobby schedule notification failed for lobby {LobbyId}", lobbyId);
        }
    }

    private static async Task<List<Guid>> LoadLobbyParticipantUserIdsAsync(IDbConnection conn, Guid lobbyId)
    {
        return (await conn.QueryAsync<Guid>(
            """
            SELECT DISTINCT recipients.user_id
            FROM (
                SELECT tp.user_id
                FROM br_lobby_groups lg
                JOIN br_group_teams bgt ON bgt.group_id = lg.group_id
                JOIN tournament_participants tp ON tp.id = bgt.participant_id
                WHERE lg.lobby_id = @lobbyId
                  AND tp.user_id IS NOT NULL
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')

                UNION

                SELECT tm.user_id
                FROM br_lobby_groups lg
                JOIN br_group_teams bgt ON bgt.group_id = lg.group_id
                JOIN tournament_participants tp ON tp.team_id = bgt.team_id
                JOIN team_members tm ON tm.team_id = bgt.team_id
                WHERE lg.lobby_id = @lobbyId
                  AND tm.user_id IS NOT NULL
                  AND tm.is_active = TRUE
                  AND tp.status NOT IN ('cancelled', 'rejected', 'disqualified')
            ) recipients
            """,
            new { lobbyId })).ToList();
    }

    private async Task InsertAndPushAsync(
        IDbConnection conn,
        IReadOnlyList<Guid> userIds,
        string type,
        string title,
        DateTimeOffset? scheduledAt,
        Func<string, string> buildMessage,
        string link,
        object data,
        CancellationToken ct)
    {
        var dataJson = JsonSerializer.Serialize(data);
        foreach (var userId in userIds)
        {
            var tzIana = await FetchUserTimezoneAsync(conn, userId, ct);
            var timeLabel = FormatScheduleTimeForUser(scheduledAt, tzIana);
            var message = buildMessage(timeLabel);

            var notificationId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO notifications
                  (user_id, type, title, message, link, data, is_read)
                VALUES
                  (@userId, @type, @title, @message, @link, @data::jsonb, FALSE)
                RETURNING id
                """,
                new { userId, type, title, message, link, data = dataJson });

            await notificationHub.Clients
                .Group(NotificationHub.UserGroup(userId.ToString()))
                .SendAsync(
                    NotificationHubEvents.NewNotification,
                    new
                    {
                        id = notificationId.ToString(),
                        type,
                        title,
                        message,
                        link,
                        data = JsonSerializer.Deserialize<object>(dataJson),
                    },
                    ct);
        }
    }

    private static string BuildContextLabel(string? stageName, int stageOrder, string? groupLabel, int waveNumber)
    {
        var stage = !string.IsNullOrWhiteSpace(stageName) ? stageName.Trim() : $"Stage {stageOrder}";
        var group = !string.IsNullOrWhiteSpace(groupLabel) ? groupLabel.Trim() : "your group";
        return waveNumber > 0
            ? $"{stage} · {group} · Matchday {waveNumber}"
            : $"{stage} · {group}";
    }

    private static async Task<string?> FetchUserTimezoneAsync(
        IDbConnection conn, Guid userId, CancellationToken ct)
    {
        return await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT settings->>'timezone_iana' FROM profiles WHERE id = @userId",
            new { userId });
    }

    internal static string FormatScheduleTimeForUser(DateTimeOffset? scheduledAt, string? tzIana)
    {
        if (!scheduledAt.HasValue)
            return "TBD";

        var utc = scheduledAt.Value.ToUniversalTime();

        if (!string.IsNullOrWhiteSpace(tzIana)
            && TimeZoneInfo.TryFindSystemTimeZoneById(tzIana, out var tz))
        {
            var local = TimeZoneInfo.ConvertTime(utc, tz);
            return local.ToString("MMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture);
        }

        return utc.ToString("MMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture);
    }

    private static string BuildBrRoomLink(string? tournamentSlug) =>
        !string.IsNullOrWhiteSpace(tournamentSlug)
            ? $"/tournaments/{tournamentSlug}/br-game-room"
            : "/tournaments";

    internal static bool ScheduledTimesEqual(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return left.Value.ToUniversalTime() == right.Value.ToUniversalTime();
    }

    private sealed class GameScheduleRow
    {
        public Guid GameId { get; init; }
        public int GameNumber { get; init; }
        public Guid LobbyId { get; init; }
        public int WaveNumber { get; init; }
        public string? LobbyCode { get; init; }
        public string? StageName { get; init; }
        public int StageOrder { get; init; }
        public string? TournamentName { get; init; }
        public string? TournamentSlug { get; init; }
        public string? GroupLabel { get; init; }
    }

    private sealed class LobbyScheduleRow
    {
        public Guid LobbyId { get; init; }
        public int WaveNumber { get; init; }
        public string? LobbyCode { get; init; }
        public string? StageName { get; init; }
        public int StageOrder { get; init; }
        public string? TournamentName { get; init; }
        public string? TournamentSlug { get; init; }
        public string? GroupLabel { get; init; }
    }
}
