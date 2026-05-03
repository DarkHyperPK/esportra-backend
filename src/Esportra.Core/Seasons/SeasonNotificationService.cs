namespace Esportra.Core.Seasons;

/// <summary>
/// Notification event types for seasons.
/// </summary>
public enum SeasonNotificationEvent
{
    SeasonPublished,
    RegistrationOpened,
    TournamentStarting,
    TeamAdvanced,
    TeamEliminated,
    NextTournamentUnlocked,
    ScheduleChanged,
    RuleChanged,
    TournamentCancelled,
    ManualOverride
}

/// <summary>
/// Notification payload for season events.
/// </summary>
public record SeasonNotificationPayload(
    SeasonNotificationEvent Event,
    Guid SeasonId,
    Guid? TournamentId,
    Guid? TeamId,
    Guid? UserId,
    string Title,
    string Message,
    Dictionary<string, object>? Metadata = null
);

/// <summary>
/// Service for sending season-related notifications.
/// This is a stub that integrates with the existing notification system.
/// </summary>
public class SeasonNotificationService
{
    /// <summary>
    /// Sends a notification to a user or team.
    /// </summary>
    public async Task SendNotificationAsync(
        SeasonNotificationPayload payload,
        CancellationToken ct = default)
    {
        // TODO: Integrate with existing notification service
        // This is a stub implementation that should be connected to the actual notification system
        // The notification system should handle:
        // - In-app notifications
        // - Email notifications
        // - Discord notifications (if connected)
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends a notification to all participants in a tournament.
    /// </summary>
    public async Task NotifyTournamentParticipantsAsync(
        Guid tournamentId,
        SeasonNotificationEvent eventType,
        string title,
        string message,
        CancellationToken ct = default)
    {
        // TODO: Query tournament participants and send notification to each
        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends a notification to a specific team.
    /// </summary>
    public async Task NotifyTeamAsync(
        Guid teamId,
        SeasonNotificationEvent eventType,
        string title,
        string message,
        CancellationToken ct = default)
    {
        // TODO: Query team members and send notification to each
        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends a notification to season staff.
    /// </summary>
    public async Task NotifySeasonStaffAsync(
        Guid seasonId,
        SeasonNotificationEvent eventType,
        string title,
        string message,
        CancellationToken ct = default)
    {
        // TODO: Query season staff and send notification to each
        await Task.CompletedTask;
    }

    /// <summary>
    /// Batch sends notifications to multiple users.
    /// </summary>
    public async Task BatchNotifyUsersAsync(
        List<Guid> userIds,
        SeasonNotificationEvent eventType,
        string title,
        string message,
        CancellationToken ct = default)
    {
        // TODO: Send notification to all specified users
        await Task.CompletedTask;
    }
}
