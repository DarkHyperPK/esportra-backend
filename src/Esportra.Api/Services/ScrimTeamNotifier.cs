using System.Data;
using Dapper;
using Esportra.Api.Helpers;
using Esportra.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Services;

/// <summary>
/// Notifies a team's captains and managers about a scrim: stored with a link to the room, then pushed
/// live. Best-effort: a failure here must never undo the action that triggered it.
/// </summary>
public static class ScrimTeamNotifier
{
    public static async Task SendAsync(
        IDbConnection conn, IHubContext<NotificationHub> hub, Guid teamId, Guid sessionId,
        string type, string title, string message)
    {
        try
        {
            var staffIds = (await conn.QueryAsync<Guid>(
                """
                SELECT user_id FROM public.team_members
                WHERE team_id = @teamId AND role IN ('captain', 'manager') AND is_active = TRUE
                """,
                new { teamId })).ToList();
            if (staffIds.Count == 0) return;

            await conn.ExecuteAsync(
                """
                INSERT INTO public.notifications (user_id, type, title, message, link, data, is_read)
                SELECT uid, @Type::notification_type, @Title, @Message,
                       '/competitive/scrims/sessions/' || @SessionId::text,
                       jsonb_build_object('session_id', @SessionId::uuid), FALSE
                FROM UNNEST(@UserIds::uuid[]) AS uid
                """,
                new { Type = type, Title = title, Message = message, SessionId = sessionId, UserIds = staffIds.ToArray() });

            await TeamNotifications.PushAsync(hub, staffIds, type, title, message);
        }
        catch
        {
            // Non-critical: the action already committed.
        }
    }
}
