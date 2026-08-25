using System.Data;
using Dapper;
using Esportra.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Helpers;

/// <summary>
/// Roster-change notifications for teams.
///
/// InsertAsync writes notification rows — pass the ambient transaction so they
/// commit atomically with the roster mutation (a rollback leaves zero rows).
/// PushAsync fans out real-time SignalR events AFTER commit; failures are
/// swallowed because a committed roster change must never surface as a failed
/// request — clients reconcile via the persisted rows on next fetch.
/// </summary>
internal static class TeamNotifications
{
    /// <summary>The recipient was removed from a team's roster.</summary>
    public const string MemberRemoved = "team_member_removed";

    /// <summary>A team's roster changed (someone was removed, left, or captaincy moved).</summary>
    public const string RosterUpdated = "team_roster_updated";

    /// <summary>The recipient was promoted to team captain.</summary>
    public const string CaptainChanged = "team_captain_changed";

    /// <summary>Insert one notification row per recipient.</summary>
    public static async Task InsertAsync(
        IDbConnection conn,
        IReadOnlyCollection<Guid> userIds,
        string type,
        string title,
        string message,
        Guid teamId,
        IDbTransaction? tx = null)
    {
        if (userIds.Count == 0) return;

        await conn.ExecuteAsync(
            """
            INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
            SELECT uid, @Type, @Title, @Message, '/player/teams',
                   jsonb_build_object('team_id', @TeamId::uuid), FALSE
            FROM UNNEST(@UserIds::uuid[]) AS uid
            """,
            new { UserIds = userIds.ToArray(), Type = type, Title = title, Message = message, TeamId = teamId },
            tx);
    }

    /// <summary>
    /// Real-time fan-out of a NewNotification event. Call only after the
    /// transaction has committed; delivery failures are non-critical.
    /// </summary>
    public static async Task PushAsync(
        IHubContext<NotificationHub> hub,
        IReadOnlyCollection<Guid> userIds,
        string type,
        string title,
        string message)
    {
        foreach (var uid in userIds)
        {
            try
            {
                await hub.Clients.Group(NotificationHub.UserGroup(uid.ToString()))
                    .SendAsync(NotificationHubEvents.NewNotification, new { type, title, message });
            }
            catch
            {
                // Non-critical: the persisted notification row delivers on next fetch.
            }
        }
    }
}
