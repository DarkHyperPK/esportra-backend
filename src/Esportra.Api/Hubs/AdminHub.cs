using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// Real-time admin dashboard updates. Connected admins auto-join admin:dashboard group.
/// </summary>
[Authorize(Policy = "Admin")]
public sealed class AdminHub : Hub
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<AdminHub> _logger;

    public AdminHub(IDbConnectionFactory db, ILogger<AdminHub> logger)
    {
        _db = db;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, DashboardGroup);
            _logger.LogDebug("Admin {UserId} connected to dashboard group", userId);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, DashboardGroup);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Request fresh pending counts from the server.</summary>
    public async Task RefreshCounts()
    {
        var userId = Context.UserIdentifier;
        if (userId is null) return;

        try
        {
            using var conn = _db.CreateConnection();
            var counts = await GetPendingCounts(conn);
            await Clients.Caller.SendAsync(AdminHubEvents.PendingCountsUpdated, counts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh admin dashboard counts");
            await Clients.Caller.SendAsync(AdminHubEvents.PendingCountsUpdated, new PendingCounts(0, 0, 0, 0));
        }
    }

    private async Task<PendingCounts> GetPendingCounts(System.Data.IDbConnection conn)
    {
        var verifications = await SafeCount(conn,
            "SELECT COUNT(*) FROM verification_requests WHERE status = 'pending'");

        var disputes = await SafeCount(conn,
            "SELECT COUNT(*) FROM tournament_disputes WHERE status = 'open'");

        var ghostApprovals = await SafeCount(conn,
            "SELECT COUNT(*) FROM ghost_approvals WHERE status = 'pending'");

        var alerts = await SafeCount(conn,
            "SELECT COUNT(*) FROM admin_alerts WHERE resolved_at IS NULL");

        return new PendingCounts(verifications, disputes, ghostApprovals, alerts);
    }

    private async Task<int> SafeCount(System.Data.IDbConnection conn, string sql)
    {
        try
        {
            return await conn.ExecuteScalarAsync<int>(sql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin dashboard count query failed: {Sql}", sql);
            return 0;
        }
    }

    public const string DashboardGroup = "admin:dashboard";
}

/// <summary>Events broadcast to admin dashboard clients.</summary>
public static class AdminHubEvents
{
    /// <summary>Pending counts updated (verifications, disputes, alerts).</summary>
    public const string PendingCountsUpdated = "PendingCountsUpdated";
}

/// <summary>Pending action counts for admin dashboard.</summary>
public sealed record PendingCounts(
    int Verifications,
    int Disputes,
    int GhostApprovals,
    int Alerts);
