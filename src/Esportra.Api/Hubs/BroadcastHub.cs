using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// SignalR hub for Esportra Air broadcast nodes (PROJ-057, T3).
/// Authentication: node token (NOT Supabase JWT).
/// Nodes present Authorization: Bearer &lt;token&gt; on the initial HTTP handshake.
/// JWT-based [Authorize] is intentionally absent — auth is validated in OnConnectedAsync.
///
/// Route:  /hubs/broadcast
/// Groups: node:{nodeId} — commands targeting one node
///         org:{orgId}   — org-wide events (e.g. overlay updates)
/// </summary>
public sealed class BroadcastHub : Hub
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<BroadcastHub> _logger;

    public BroadcastHub(IDbConnectionFactory db, ILogger<BroadcastHub> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Connection lifecycle ──────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var httpCtx = Context.GetHttpContext();
        if (httpCtx is null)
        {
            _logger.LogWarning("[BroadcastHub] Rejected — no HTTP context. connId={ConnId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var authHeader = httpCtx.Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!authHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[BroadcastHub] Rejected — missing Bearer token. connId={ConnId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var token = authHeader[bearer.Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            Context.Abort();
            return;
        }

        try
        {
            var tokenHash = HashToken(token);
            using var conn = _db.CreateConnection();

            var node = await conn.QuerySingleOrDefaultAsync<NodeRecord>(
                """
                SELECT id AS Id, org_id AS OrgId
                FROM broadcast_nodes
                WHERE token_hash = @tokenHash AND is_active = true
                """,
                new { tokenHash });

            if (node is null)
            {
                _logger.LogWarning("[BroadcastHub] Rejected — invalid or inactive token. connId={ConnId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            await conn.ExecuteAsync(
                "UPDATE broadcast_nodes SET last_seen_at = NOW() WHERE id = @id",
                new { id = node.Id });

            Context.Items["NodeId"] = node.Id;
            Context.Items["OrgId"] = node.OrgId;

            await Groups.AddToGroupAsync(Context.ConnectionId, NodeGroup(node.Id));
            await Groups.AddToGroupAsync(Context.ConnectionId, OrgGroup(node.OrgId));

            _logger.LogInformation(
                "[BroadcastHub] Node connected — nodeId={NodeId} orgId={OrgId} connId={ConnId}",
                node.Id, node.OrgId, Context.ConnectionId);

            await base.OnConnectedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BroadcastHub] Error during connection validation. connId={ConnId}", Context.ConnectionId);
            Context.Abort();
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (TryGetNodeId(out var nodeId))
        {
            if (Context.Items.TryGetValue("OrgId", out var orgVal) && orgVal is Guid orgId)
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, OrgGroup(orgId));

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, NodeGroup(nodeId));

            _logger.LogInformation(
                "[BroadcastHub] Node disconnected — nodeId={NodeId} reason={Reason}",
                nodeId, exception?.Message ?? "clean close");
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ── Hub methods (called by nodes) ─────────────────────────────────────────

    /// <summary>Periodic heartbeat from a node. Updates last_seen_at.</summary>
    public async Task NodeHeartbeat(HeartbeatPayload payload)
    {
        if (!TryGetNodeId(out var nodeId)) return;

        try
        {
            using var conn = _db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE broadcast_nodes SET last_seen_at = NOW() WHERE id = @id",
                new { id = nodeId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BroadcastHub] Heartbeat update failed. nodeId={NodeId}", nodeId);
        }
    }

    /// <summary>
    /// Event emitted by the node (GEP stats, game state, etc.).
    /// Persisted to broadcast_events in P3 — debug-logged for now.
    /// </summary>
    public Task NodeEvent(BroadcastEventPayload payload)
    {
        if (!TryGetNodeId(out var nodeId)) return Task.CompletedTask;

        _logger.LogDebug(
            "[BroadcastHub] NodeEvent — nodeId={NodeId} type={EventType}",
            nodeId, payload.EventType);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Node acknowledges a command sent by the organizer.
    /// Linked to broadcast_commands in P3 — debug-logged for now.
    /// </summary>
    public Task CommandAck(CommandAckPayload payload)
    {
        if (!TryGetNodeId(out var nodeId)) return Task.CompletedTask;

        _logger.LogDebug(
            "[BroadcastHub] CommandAck — nodeId={NodeId} commandId={CommandId} status={Status}",
            nodeId, payload.CommandId, payload.Status);

        return Task.CompletedTask;
    }

    // ── Group name helpers (used by IHubContext callers) ──────────────────────

    public static string NodeGroup(Guid nodeId) => $"node:{nodeId}";
    public static string OrgGroup(Guid orgId) => $"org:{orgId}";

    // ── Private helpers ───────────────────────────────────────────────────────

    private bool TryGetNodeId(out Guid nodeId)
    {
        if (Context.Items.TryGetValue("NodeId", out var val) && val is Guid id)
        {
            nodeId = id;
            return true;
        }
        nodeId = default;
        return false;
    }

    /// <summary>
    /// SHA-256 of the UTF-8 bytes of the token string. Must match the hash
    /// stored in broadcast_nodes.token_hash during pairing.
    /// </summary>
    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private sealed record NodeRecord(Guid Id, Guid OrgId);
}

// ── Hub payload records ───────────────────────────────────────────────────────

public sealed record HeartbeatPayload(
    DateTimeOffset Timestamp,
    string? Status = null);

public sealed record BroadcastEventPayload(
    string EventType,
    JsonElement? Data = null);

public sealed record CommandAckPayload(
    string CommandId,
    string Status,
    string? Error = null);
