using Dapper;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time Battle Royale round/evidence/leaderboard updates.
/// Groups: br:stage:{stageId}, br:group:{groupId}, br:round:{roundId}
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

    public async Task JoinRound(string roundId)
    {
        if (!TryParseGuid(roundId, out var roundGuid))
            throw new HubException("Invalid round id.");

        if (!await CanViewRoundAsync(roundGuid))
            throw new HubException("Not authorized for this BR stream.");

        await Groups.AddToGroupAsync(Context.ConnectionId, RoundGroup(roundId));
        _logger.LogDebug("Client {Conn} joined {Group}", Context.ConnectionId, RoundGroup(roundId));
    }

    public Task LeaveRound(string roundId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RoundGroup(roundId));

    public static string StageGroup(string stageId) => $"br:stage:{stageId}";
    public static string GroupGroup(string groupId) => $"br:group:{groupId}";
    public static string RoundGroup(string roundId) => $"br:round:{roundId}";

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

    private async Task<bool> CanViewRoundAsync(Guid roundId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM br_rounds r
                JOIN br_groups g ON g.id = r.group_id
                JOIN tournament_stages s ON s.id = g.stage_id
                JOIN tournaments t ON t.id = s.tournament_id
                WHERE r.id = @roundId
                  AND t.deleted_at IS NULL
            )
            """,
            new { roundId });
    }
}

public static class BRHubEvents
{
    public const string RoundCreated = "RoundCreated";
    public const string RoundUpdated = "RoundUpdated";
    public const string RoundReset = "RoundReset";
    public const string EvidenceSubmitted = "EvidenceSubmitted";
    public const string EvidenceReviewed = "EvidenceReviewed";
    public const string ResultsUpdated = "ResultsUpdated";
    public const string LeaderboardUpdated = "LeaderboardUpdated";
}
