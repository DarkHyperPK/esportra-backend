using Dapper;
using Esportra.Api.Helpers;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time Battle Royale round/evidence/leaderboard updates.
/// Groups: br:stage:{stageId}, br:group:{groupId}, br:round:{roundId}
/// </summary>
[Authorize]
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

        if (!await CanViewStageAsync(stageGuid, RequireUserId()))
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

        if (!await CanViewGroupAsync(groupGuid, RequireUserId()))
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

        if (!await CanViewRoundAsync(roundGuid, RequireUserId()))
            throw new HubException("Not authorized for this BR stream.");

        await Groups.AddToGroupAsync(Context.ConnectionId, RoundGroup(roundId));
        _logger.LogDebug("Client {Conn} joined {Group}", Context.ConnectionId, RoundGroup(roundId));
    }

    public Task LeaveRound(string roundId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RoundGroup(roundId));

    public static string StageGroup(string stageId) => $"br:stage:{stageId}";
    public static string GroupGroup(string groupId) => $"br:group:{groupId}";
    public static string RoundGroup(string roundId) => $"br:round:{roundId}";

    private Guid RequireUserId()
    {
        var userId = Context.UserIdentifier;
        if (userId is null || !Guid.TryParse(userId, out var userGuid))
            throw new HubException("Not authenticated.");

        return userGuid;
    }

    private static bool TryParseGuid(string value, out Guid id) =>
        Guid.TryParse(value, out id);

    private async Task<bool> IsPlatformAdminAsync(Guid userId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM public.user_roles
                WHERE user_id = @userId
                  AND is_active = TRUE
                  AND role IN ('admin', 'super_admin')
            )
            """,
            new { userId });
    }

    private async Task<bool> CanViewStageAsync(Guid stageId, Guid userId)
    {
        if (await IsPlatformAdminAsync(userId))
            return true;

        using var conn = _db.CreateConnection();

        var stageMeta = await conn.QuerySingleOrDefaultAsync<(bool isPublic, Guid organizerId)?>(
            """
            SELECT t.is_public, t.organizer_id
            FROM public.tournament_stages s
            JOIN public.tournaments t ON t.id = s.tournament_id
            WHERE s.id = @stageId
              AND t.deleted_at IS NULL
            """,
            new { stageId });

        if (stageMeta is null)
            return false;

        if (stageMeta.Value.isPublic)
            return true;

        if (stageMeta.Value.organizerId == userId)
            return true;

        if (await StaffAuthHelper.CanActOnStageAsync(conn, userId, stageId, StaffAuthHelper.PermBracketEdit))
            return true;

        return await IsParticipantInStageAsync(conn, stageId, userId);
    }

    private async Task<bool> CanViewGroupAsync(Guid groupId, Guid userId)
    {
        if (await IsPlatformAdminAsync(userId))
            return true;

        using var conn = _db.CreateConnection();

        var groupMeta = await conn.QuerySingleOrDefaultAsync<(Guid stageId, bool isPublic, Guid organizerId)?>(
            """
            SELECT g.stage_id, t.is_public, t.organizer_id
            FROM br_groups g
            JOIN tournament_stages s ON s.id = g.stage_id
            JOIN tournaments t ON t.id = s.tournament_id
            WHERE g.id = @groupId
              AND t.deleted_at IS NULL
            """,
            new { groupId });

        if (groupMeta is null)
            return false;

        if (groupMeta.Value.isPublic)
            return true;

        if (groupMeta.Value.organizerId == userId)
            return true;

        if (await StaffAuthHelper.CanActOnStageAsync(
                conn, userId, groupMeta.Value.stageId, StaffAuthHelper.PermBracketEdit))
            return true;

        return await IsParticipantInGroupAsync(conn, groupId, userId);
    }

    private async Task<bool> CanViewRoundAsync(Guid roundId, Guid userId)
    {
        using var conn = _db.CreateConnection();
        var groupId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT group_id FROM br_rounds WHERE id = @roundId",
            new { roundId });

        return groupId is not null && await CanViewGroupAsync(groupId.Value, userId);
    }

    private static async Task<bool> IsParticipantInStageAsync(
        System.Data.IDbConnection conn,
        Guid stageId,
        Guid userId)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM br_groups g
                JOIN br_group_teams bgt ON bgt.group_id = g.id
                LEFT JOIN tournament_participants tp ON tp.id = bgt.participant_id
                LEFT JOIN team_members tm ON tm.team_id = bgt.team_id AND tm.is_active = TRUE
                WHERE g.stage_id = @stageId
                  AND (
                    tp.user_id = @userId
                    OR tm.user_id = @userId
                  )
            )
            """,
            new { stageId, userId });
    }

    private static async Task<bool> IsParticipantInGroupAsync(
        System.Data.IDbConnection conn,
        Guid groupId,
        Guid userId)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM br_group_teams bgt
                LEFT JOIN tournament_participants tp ON tp.id = bgt.participant_id
                LEFT JOIN team_members tm ON tm.team_id = bgt.team_id AND tm.is_active = TRUE
                WHERE bgt.group_id = @groupId
                  AND (
                    tp.user_id = @userId
                    OR tm.user_id = @userId
                  )
            )
            """,
            new { groupId, userId });
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
