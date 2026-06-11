using Dapper;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time Battle Royale lobby/evidence/leaderboard updates.
/// Groups: br:stage:{stageId}, br:group:{groupId}, br:lobby:{lobbyId}
/// </summary>
public sealed class BRHub : Hub
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<BRHub> _logger;

    public BRHub(IDbConnectionFactory db, ILogger<BRHub> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task JoinStage(string stageId)
    {
        if (!TryParseGuid(stageId, out var stageGuid))
            throw new HubException("Invalid stage id.");

        if (!await CanViewStageAsync(stageGuid))
            throw new HubException("Not authorized for this BR stream.");

        await Groups.AddToGroupAsync(Context.ConnectionId, StageGroup(stageId));
        _logger.LogDebug("Client {Conn} joined {Group}", Context.ConnectionId, StageGroup(stageId));
    }

    public Task LeaveStage(string stageId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, StageGroup(stageId));

    public async Task JoinGroup(string groupId)
    {
        if (!TryParseGuid(groupId, out var groupGuid))
            throw new HubException("Invalid group id.");

        if (!await CanViewGroupAsync(groupGuid))
            throw new HubException("Not authorized for this BR stream.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupGroup(groupId));
        _logger.LogDebug("Client {Conn} joined {Group}", Context.ConnectionId, GroupGroup(groupId));
    }

    public Task LeaveGroup(string groupId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupGroup(groupId));

    public async Task JoinLobby(string lobbyId)
    {
        if (!TryParseGuid(lobbyId, out var lobbyGuid))
            throw new HubException("Invalid lobby id.");

        if (!await CanViewLobbyAsync(lobbyGuid))
            throw new HubException("Not authorized for this BR stream.");

        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup(lobbyId));
        _logger.LogDebug("Client {Conn} joined {Group}", Context.ConnectionId, LobbyGroup(lobbyId));
    }

    public Task LeaveLobby(string lobbyId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, LobbyGroup(lobbyId));

    public static string StageGroup(string stageId) => $"br:stage:{stageId}";
    public static string GroupGroup(string groupId) => $"br:group:{groupId}";
    public static string LobbyGroup(string lobbyId) => $"br:lobby:{lobbyId}";

    private static bool TryParseGuid(string value, out Guid id) =>
        Guid.TryParse(value, out id);

    private async Task<bool> CanViewStageAsync(Guid stageId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM public.tournament_stages s
                JOIN public.tournaments t ON t.id = s.tournament_id
                WHERE s.id = @stageId
                  AND t.deleted_at IS NULL
            )
            """,
            new { stageId });
    }

    private async Task<bool> CanViewGroupAsync(Guid groupId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM br_groups g
                JOIN tournament_stages s ON s.id = g.stage_id
                JOIN tournaments t ON t.id = s.tournament_id
                WHERE g.id = @groupId
                  AND t.deleted_at IS NULL
            )
            """,
            new { groupId });
    }

    private async Task<bool> CanViewLobbyAsync(Guid lobbyId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM br_lobbies l
                JOIN br_lobby_groups lg ON lg.lobby_id = l.id
                JOIN br_groups g ON g.id = lg.group_id
                JOIN tournament_stages s ON s.id = g.stage_id
                JOIN tournaments t ON t.id = s.tournament_id
                WHERE l.id = @lobbyId
                  AND t.deleted_at IS NULL
            )
            """,
            new { lobbyId });
    }
}

public static class BRHubEvents
{
    public const string LobbyCreated = "LobbyCreated";
    public const string LobbyUpdated = "LobbyUpdated";
    public const string LobbyCompleted = "LobbyCompleted";
    public const string LobbyReset = "LobbyReset";
    public const string EvidenceSubmitted = "EvidenceSubmitted";
    public const string EvidenceReviewed = "EvidenceReviewed";
    public const string ResultsUpdated = "ResultsUpdated";
    public const string LeaderboardUpdated = "LeaderboardUpdated";
}
