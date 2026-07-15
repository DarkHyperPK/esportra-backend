using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Core.Br;

namespace Esportra.Api.Helpers;

public static class TournamentTimelineValidator
{
    public static string? ValidateDateOrder(DateTimeOffset? startDate, DateTimeOffset? endDate)
    {
        if (startDate is null || endDate is null)
            return null;

        return endDate < startDate
            ? "Tournament end date must be on or after the start date."
            : null;
    }

    public static string? ValidateRegistrationDeadline(DateTimeOffset? registrationDeadline, DateTimeOffset? startDate)
    {
        if (registrationDeadline is null || startDate is null)
            return null;

        return registrationDeadline > startDate
            ? "Registration deadline must be on or before the tournament start date."
            : null;
    }

    /// <summary>
    /// Registration is open through the deadline instant (UTC). Midnight UTC deadlines
    /// are treated as inclusive through 23:59:59.999 on that calendar day (UTC).
    /// </summary>
    public static bool IsRegistrationDeadlineOpen(DateTimeOffset? registrationDeadline, DateTimeOffset? now = null)
    {
        if (registrationDeadline is null)
            return true;

        var current = now ?? DateTimeOffset.UtcNow;
        var end = registrationDeadline.Value;

        if (end is { Hour: 0, Minute: 0, Second: 0 })
        {
            end = new DateTimeOffset(end.Year, end.Month, end.Day, 23, 59, 59, 999, end.Offset);
        }

        return current <= end;
    }

    public static bool IsRegistrationStatusOpen(string? status)
    {
        return string.Equals(status, "open", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "published", StringComparison.OrdinalIgnoreCase);
    }

    public static DateTimeOffset? ParseRegistrationOpensAt(object? settings)
    {
        if (settings is null)
            return null;

        try
        {
            var json = settings switch
            {
                string s => s,
                _ => JsonSerializer.Serialize(settings),
            };

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("registrationOpensAt", out var prop))
                return null;

            if (prop.ValueKind != JsonValueKind.String)
                return null;

            return DateTimeOffset.TryParse(prop.GetString(), out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? ValidateRegistrationWindow(
        string? status,
        DateTimeOffset? registrationDeadline,
        DateTimeOffset? startDate,
        DateTimeOffset? registrationOpens = null,
        DateTimeOffset? now = null)
    {
        if (!IsRegistrationStatusOpen(status))
            return "Tournament is not accepting registrations.";

        var current = now ?? DateTimeOffset.UtcNow;
        if (registrationOpens is not null && current < registrationOpens.Value)
            return "Registration has not opened yet.";

        if (!IsRegistrationDeadlineOpen(registrationDeadline, now))
            return "Registration deadline has passed.";

        if (startDate is not null && current >= startDate.Value)
            return "Tournament has already started.";

        return null;
    }

    public static string FormatWindow(DateTimeOffset? startDate, DateTimeOffset? endDate)
    {
        if (startDate is null && endDate is null)
            return "the tournament schedule";

        if (startDate is not null && endDate is not null)
            return $"{startDate.Value:MMM d, yyyy h:mm tt} – {endDate.Value:MMM d, yyyy h:mm tt}";

        if (startDate is not null)
            return $"on or after {startDate.Value:MMM d, yyyy h:mm tt}";

        return $"on or before {endDate!.Value:MMM d, yyyy h:mm tt}";
    }

    public static string? ValidateTimestampWithinWindow(
        DateTimeOffset? timestamp,
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        string actionLabel)
    {
        if (timestamp is null)
            return null;

        if (startDate is not null && timestamp < startDate)
        {
            return $"{actionLabel} must be within the tournament window ({FormatWindow(startDate, endDate)}).";
        }

        if (endDate is not null && timestamp > endDate)
        {
            return $"{actionLabel} must be within the tournament window ({FormatWindow(startDate, endDate)}).";
        }

        return null;
    }

    public static string? ValidateTournamentIsOngoing(string? tournamentStatus)
    {
        if (string.Equals(tournamentStatus, "ongoing", StringComparison.OrdinalIgnoreCase))
            return null;

        return "The tournament must be marked as ongoing before starting a live round.";
    }

    private static readonly HashSet<string> PromotableToOngoing = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "check_in", "published",
    };

    private static readonly HashSet<string> BlockedForLiveRounds = new(StringComparer.OrdinalIgnoreCase)
    {
        "draft", "completed", "cancelled", "closed",
    };

    /// <summary>
    /// Promotes open/check_in/published tournaments to ongoing on first live lobby start.
    /// Returns an error for terminal/invalid states.
    /// </summary>
    public static async Task<string?> EnsureTournamentLiveAsync(
        IDbConnection conn,
        Guid stageId,
        IDbTransaction? tx = null)
    {
        var (_, _, status) = await StageCompletionHelper.GetTournamentWindowForStageAsync(conn, stageId, tx);
        var normalized = (status ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(normalized))
            return "Tournament not found.";

        if (BlockedForLiveRounds.Contains(normalized))
        {
            return normalized.Equals("draft", StringComparison.OrdinalIgnoreCase)
                ? "Publish the tournament before starting a live lobby."
                : $"Cannot start a live lobby while the tournament status is '{normalized}'.";
        }

        if (string.Equals(normalized, "ongoing", StringComparison.OrdinalIgnoreCase))
            return null;

        if (PromotableToOngoing.Contains(normalized))
        {
            await conn.ExecuteAsync(
                """
                UPDATE tournaments t
                SET status = 'ongoing',
                    updated_at = NOW()
                FROM tournament_stages ts
                WHERE ts.tournament_id = t.id
                  AND ts.id = @stageId
                """,
                new { stageId },
                tx);
            return null;
        }

        return ValidateTournamentIsOngoing(normalized);
    }

    /// <summary>
    /// Validates BR game schedule ordering within a lobby (sequential games + lobby floor).
    /// Tournament window checks use <see cref="ValidateTimestampWithinWindow"/>.
    /// </summary>
    public static async Task<string?> ValidateBrGameScheduleOrderAsync(
        IDbConnection conn,
        Guid lobbyId,
        Guid gameId,
        int gameNumber,
        DateTimeOffset? scheduledAt,
        IDbTransaction tx)
    {
        if (scheduledAt is null)
            return null;

        var prior = await BrGameRepository.QuerySingleTimestampOrDefaultAsync(
            conn,
            """
            SELECT scheduled_at
            FROM br_games
            WHERE lobby_id = @lobbyId
              AND game_number < @gameNumber
              AND scheduled_at IS NOT NULL
            ORDER BY game_number DESC
            LIMIT 1
            """,
            new { lobbyId, gameNumber },
            tx);

        if (prior is not null && scheduledAt <= prior)
        {
            return $"Game {gameNumber} must be scheduled after Game {gameNumber - 1}.";
        }

        var next = await BrGameRepository.QuerySingleTimestampOrDefaultAsync(
            conn,
            """
            SELECT scheduled_at
            FROM br_games
            WHERE lobby_id = @lobbyId
              AND game_number > @gameNumber
              AND scheduled_at IS NOT NULL
              AND id <> @gameId
            ORDER BY game_number ASC
            LIMIT 1
            """,
            new { lobbyId, gameNumber, gameId },
            tx);

        if (next is not null && scheduledAt >= next)
        {
            return $"Game {gameNumber} must be scheduled before the next game in this lobby.";
        }

        if (!await BrSchemaRepository.BrGamesModelReadyAsync(conn, tx))
        {
            var lobbySchedule = await BrGameRepository.QuerySingleTimestampOrDefaultAsync(
                conn,
                """
                SELECT scheduled_at
                FROM br_lobbies
                WHERE id = @lobbyId
                """,
                new { lobbyId },
                tx);

            if (lobbySchedule is not null && scheduledAt < lobbySchedule)
            {
                return $"Game {gameNumber} cannot be scheduled before its lobby start time.";
            }
        }

        return null;
    }
}
