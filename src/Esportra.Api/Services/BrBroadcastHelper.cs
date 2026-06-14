using Esportra.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Services;

public static class BrBroadcastHelper
{
    public static async Task BroadcastAsync(
        IHubContext<BRHub> hub,
        string eventName,
        Guid stageId,
        Guid? groupId,
        Guid? lobbyId,
        Guid? gameId,
        object payload,
        CancellationToken ct = default)
    {
        _ = ct;
        var tasks = new List<Task>
        {
            hub.Clients.Group(BRHub.StageGroup(stageId.ToString())).SendAsync(eventName, payload, CancellationToken.None),
        };

        if (groupId is not null)
        {
            tasks.Add(hub.Clients.Group(BRHub.GroupGroup(groupId.Value.ToString()))
                .SendAsync(eventName, payload, CancellationToken.None));
        }

        if (lobbyId is not null)
        {
            tasks.Add(hub.Clients.Group(BRHub.LobbyGroup(lobbyId.Value.ToString()))
                .SendAsync(eventName, payload, CancellationToken.None));
        }

        if (gameId is not null)
        {
            tasks.Add(hub.Clients.Group(BRHub.GameGroup(gameId.Value.ToString()))
                .SendAsync(eventName, payload, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[BrBroadcastHelper] Failed to broadcast {eventName} for stage {stageId}: {ex.Message}");
        }
    }

    public static async Task BroadcastToLobbyGroupsAsync(
        IHubContext<BRHub> hub,
        string eventName,
        Guid stageId,
        IReadOnlyList<Guid> groupIds,
        Guid? lobbyId,
        Guid? gameId,
        object payload,
        CancellationToken ct = default)
    {
        if (groupIds.Count == 0)
        {
            await BroadcastAsync(hub, eventName, stageId, null, lobbyId, gameId, payload, ct);
            return;
        }

        foreach (var groupId in groupIds)
        {
            await BroadcastAsync(hub, eventName, stageId, groupId, lobbyId, gameId, payload, ct);
        }
    }
}
