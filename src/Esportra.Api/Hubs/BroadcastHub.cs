using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Broadcast;
using Esportra.Core.Broadcast;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Hubs;

/// <summary>
/// SignalR hub for Esportra Air broadcast system (PROJ-057, T3/T26; PROJ-062 overlay-source).
/// Four connection types share one hub:
///   node           — Bearer &lt;base64-32-bytes&gt;; validated via SHA-256 hash lookup in broadcast_nodes
///   crew           — Supabase JWT; user has an active row in broadcast_crew for the given ?showId= param
///   org-web        — Supabase JWT; user owns an organization (organizations.owner_id = userId)
///   overlay-source — ?overlay_token=&lt;raw&gt;; read-only; SHA-256 lookup in broadcast_overlay_tokens
///                    (NEVER uses ?access_token= — OBS logs every browser source URL unconditionally)
///
/// Route:  /hubs/broadcast
/// Groups: node:{nodeId}    — commands targeting one node
///         org:{orgId}      — org-wide events (overlay updates, ShowConfig pushes)
///         crew:{showId}    — rundown / show state pushed to crew members
///         overlay:{showId} — revocation push to all browser sources for a show
/// </summary>
public sealed class BroadcastHub : Hub
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<BroadcastHub> _logger;

    // Per-connection invocation counters. Static so the state survives hub instance recreation per method call.
    private static readonly ConcurrentDictionary<string, RateBucket> s_rateLimits = new();

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

        try
        {
            using var conn = _db.CreateConnection();

            // Overlay-source: check ?overlay_token= BEFORE the JWT path.
            // OBS unconditionally logs browser source URLs; a JWT must never appear in a URL.
            var overlayToken = httpCtx.Request.Query["overlay_token"].ToString();
            if (!string.IsNullOrEmpty(overlayToken))
            {
                if (await TryConnectAsOverlaySourceAsync(overlayToken, conn))
                {
                    await base.OnConnectedAsync();
                    return;
                }
                _logger.LogWarning("[BroadcastHub] Rejected — invalid overlay token. connId={ConnId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            var token = ExtractBearerToken(httpCtx);
            if (token is null)
            {
                _logger.LogWarning("[BroadcastHub] Rejected — missing Bearer token. connId={ConnId}", Context.ConnectionId);
                Context.Abort();
                return;
            }

            if (await TryConnectAsNodeAsync(token, conn))
            {
                await base.OnConnectedAsync();
                return;
            }

            if (await TryConnectAsJwtAsync(httpCtx, conn))
            {
                await base.OnConnectedAsync();
                return;
            }

            _logger.LogWarning(
                "[BroadcastHub] Rejected — not a valid node token or authenticated user. connId={ConnId}",
                Context.ConnectionId);
            Context.Abort();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BroadcastHub] Error during connection validation. connId={ConnId}", Context.ConnectionId);
            Context.Abort();
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        s_rateLimits.TryRemove(Context.ConnectionId, out _);

        var connType = Context.Items.TryGetValue("ConnectionType", out var ct) ? ct as string : null;

        switch (connType)
        {
            case "node":
                await DisconnectNodeAsync(exception);
                break;
            case "crew":
                await DisconnectCrewAsync();
                break;
            case "org-web":
                if (Context.Items.TryGetValue("OrgId", out var orgVal) && orgVal is Guid orgId)
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, OrgGroup(orgId));
                _logger.LogInformation("[BroadcastHub] Org-web disconnected — connId={ConnId}", Context.ConnectionId);
                break;
            case "overlay-source":
                if (Context.Items.TryGetValue("OrgId", out var oOrgVal) && oOrgVal is Guid oOrgId)
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, OrgGroup(oOrgId));
                if (Context.Items.TryGetValue("ShowId", out var oShowVal) && oShowVal is Guid oShowId)
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, OverlayGroup(oShowId));
                _logger.LogInformation("[BroadcastHub] Overlay-source disconnected — connId={ConnId}", Context.ConnectionId);
                break;
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ── Hub methods (nodes only) ──────────────────────────────────────────────

    /// <summary>Periodic heartbeat from a node. Updates last_seen_at.</summary>
    public async Task NodeHeartbeat(HeartbeatPayload payload)
    {
        if (!IsNodeConnection(out var nodeId)) return;
        if (IsRateLimited()) return;

        if (payload.Runtime is not null && !NodeRuntimeRules.IsValid(payload.Runtime)) return;
        var runtime = payload.Runtime is null ? null : JsonSerializer.Serialize(payload.Runtime, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        try
        {
            using var conn = _db.CreateConnection();
            if (payload.Runtime?.Plan is { } plan && !await conn.ExecuteScalarAsync<bool>("""
                SELECT EXISTS(SELECT 1 FROM broadcast_shows s JOIN broadcast_nodes n ON n.org_id = s.org_id
                    WHERE n.id = @nodeId AND n.is_active = true AND s.id = @showId
                      AND s.rundown_version >= @version AND s.rundown_version >= COALESCE(@pending, @version))
                """, new { nodeId, showId = plan.ShowId, version = plan.Version, pending = plan.PendingVersion })) return;
            var affected = await conn.ExecuteAsync(
                """
                UPDATE broadcast_nodes
                SET last_seen_at = NOW(),
                    runtime_status = COALESCE(@runtime::jsonb, runtime_status),
                    runtime_seen_at = CASE WHEN @runtime IS NULL THEN runtime_seen_at ELSE NOW() END
                WHERE id = @id AND is_active = true
                """,
                new { id = nodeId, runtime });
            if (affected == 0) Context.Abort();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BroadcastHub] Heartbeat update failed. nodeId={NodeId}", nodeId);
        }
    }

    /// <summary>
    /// Event emitted by the node (GEP stats, game state, etc.).
    /// All four CIO security guards run before any DB write.
    /// </summary>
    public async Task NodeEvent(BroadcastEventPayload payload)
    {
        if (!IsNodeConnection(out var nodeId)) return;
        if (IsRateLimited()) return;

        // Guard (c): event_id must be a valid UUID
        if (!BroadcastEventGuard.IsValidEventId(payload.EventId))
        {
            _logger.LogWarning(
                "[BroadcastHub] NodeEvent rejected — invalid event_id. nodeId={NodeId} eventType={EventType}",
                nodeId, payload.EventType);
            return;
        }

        // Guard (a): event_type server-side allowlist
        if (!BroadcastEventGuard.IsAllowedEventType(payload.EventType) || !BroadcastEventGuard.IsValidTypedMetadata(payload.EventType, payload.Data))
        {
            _logger.LogWarning(
                "[BroadcastHub] NodeEvent rejected — disallowed event_type. nodeId={NodeId} eventType={EventType}",
                nodeId, payload.EventType);
            return;
        }

        // Guard (b): payload size cap — serialize entire payload; log eventType only, never contents
        if (!BroadcastEventGuard.IsPayloadWithinSizeLimit(payload, out var serializedPayload))
        {
            _logger.LogWarning(
                "[BroadcastHub] NodeEvent rejected — payload exceeds {MaxBytes} bytes. nodeId={NodeId} eventType={EventType}",
                BroadcastEventGuard.MaxPayloadBytes, nodeId, payload.EventType);
            return;
        }

        // Guard (d): show_id is server-derived from the node's org — never from the payload
        if (!Context.Items.TryGetValue("OrgId", out var orgVal) || orgVal is not Guid orgId)
        {
            _logger.LogWarning(
                "[BroadcastHub] NodeEvent rejected — missing OrgId in context. nodeId={NodeId}",
                nodeId);
            return;
        }

        try
        {
            using var conn = _db.CreateConnection();

            var showId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT s.id FROM broadcast_shows s JOIN broadcast_nodes n ON n.org_id = s.org_id
                WHERE s.org_id = @orgId AND n.id = @nodeId AND n.is_active = true AND s.status = 'live'
                ORDER BY s.started_at DESC LIMIT 1
                """,
                new { orgId, nodeId });

            if (!showId.HasValue)
            {
                _logger.LogWarning(
                    "[BroadcastHub] NodeEvent dropped — no live show for org. nodeId={NodeId} orgId={OrgId}",
                    nodeId, orgId);
                return;
            }

            var dataJson = payload.Data.HasValue
                ? System.Text.Json.JsonSerializer.Serialize(payload.Data.Value)
                : "{}";

            await conn.ExecuteAsync(
                "INSERT INTO broadcast_events (show_id, node_id, event_id, event_type, payload, source) " +
                "VALUES (@showId, @nodeId, @eventId, @eventType, @payload::jsonb, 'node') " +
                "ON CONFLICT (event_id) DO NOTHING",
                new
                {
                    showId = showId.Value,
                    nodeId,
                    eventId = payload.EventId,
                    eventType = payload.EventType,
                    payload = dataJson,
                });

            _logger.LogDebug(
                "[BroadcastHub] NodeEvent persisted — nodeId={NodeId} showId={ShowId} eventType={EventType}",
                nodeId, showId.Value, payload.EventType);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[BroadcastHub] NodeEvent insert failed. nodeId={NodeId} eventType={EventType}",
                nodeId, payload.EventType);
        }
    }

    /// <summary>
    /// Node acknowledges a command sent by the organizer.
    /// Linked to broadcast_commands in P4 — debug-logged for now.
    /// </summary>
    public Task CommandAck(CommandAckPayload payload)
    {
        if (!IsNodeConnection(out var nodeId)) return Task.CompletedTask;
        if (IsRateLimited()) return Task.CompletedTask;

        _logger.LogDebug(
            "[BroadcastHub] CommandAck — nodeId={NodeId} commandId={CommandId} status={Status}",
            nodeId, payload.CommandId, payload.Status);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by browser source overlays to confirm liveness. Updates last_ping_at.
    /// Only valid for overlay-source connections.
    /// </summary>
    public async Task OverlayPing()
    {
        if (!Context.Items.TryGetValue("ConnectionType", out var ct) || ct as string != "overlay-source") return;
        if (!Context.Items.TryGetValue("OverlayTokenId", out var idVal) || idVal is not Guid tokenId) return;

        try
        {
            using var conn = _db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE broadcast_overlay_tokens SET last_ping_at = NOW() WHERE id = @id AND revoked_at IS NULL",
                new { id = tokenId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BroadcastHub] OverlayPing update failed. tokenId={TokenId}", tokenId);
        }
    }

    // ── Group name helpers (used by IHubContext callers) ──────────────────────

    public static string NodeGroup(Guid nodeId) => $"node:{nodeId}";
    public static string OrgGroup(Guid orgId) => $"org:{orgId}";
    public static string CrewGroup(Guid showId) => $"crew:{showId}";
    public static string OverlayGroup(Guid showId) => $"overlay:{showId}";

    // ── Private connection helpers ────────────────────────────────────────────

    private async Task<bool> TryConnectAsOverlaySourceAsync(string rawToken, System.Data.IDbConnection conn)
    {
        var tokenHash = HashToken(rawToken);
        var overlay = await conn.QuerySingleOrDefaultAsync<OverlayTokenRecord>(
            """
            SELECT id AS Id, show_id AS ShowId, org_id AS OrgId
            FROM broadcast_overlay_tokens
            WHERE token_hash = @tokenHash
              AND revoked_at IS NULL
              AND expires_at > NOW()
            """,
            new { tokenHash });

        if (overlay is null) return false;

        Context.Items["ConnectionType"] = "overlay-source";
        Context.Items["OverlayTokenId"] = overlay.Id;
        Context.Items["ShowId"] = overlay.ShowId;
        Context.Items["OrgId"] = overlay.OrgId;

        await Groups.AddToGroupAsync(Context.ConnectionId, OrgGroup(overlay.OrgId));
        await Groups.AddToGroupAsync(Context.ConnectionId, OverlayGroup(overlay.ShowId));

        _logger.LogInformation(
            "[BroadcastHub] Overlay-source connected — tokenId={TokenId} showId={ShowId} connId={ConnId}",
            overlay.Id, overlay.ShowId, Context.ConnectionId);

        return true;
    }

    private async Task<bool> TryConnectAsNodeAsync(string token, System.Data.IDbConnection conn)
    {
        var tokenHash = HashToken(token);
        var node = await conn.QuerySingleOrDefaultAsync<NodeRecord>(
            """
            SELECT id AS Id, org_id AS OrgId
            FROM broadcast_nodes
            WHERE token_hash = @tokenHash AND is_active = true
            """,
            new { tokenHash });

        if (node is null) return false;

        await conn.ExecuteAsync(
            "UPDATE broadcast_nodes SET last_seen_at = NOW() WHERE id = @id",
            new { id = node.Id });

        Context.Items["ConnectionType"] = "node";
        Context.Items["NodeId"] = node.Id;
        Context.Items["OrgId"] = node.OrgId;

        await Groups.AddToGroupAsync(Context.ConnectionId, NodeGroup(node.Id));
        await Groups.AddToGroupAsync(Context.ConnectionId, OrgGroup(node.OrgId));

        _logger.LogInformation(
            "[BroadcastHub] Node connected — nodeId={NodeId} orgId={OrgId} connId={ConnId}",
            node.Id, node.OrgId, Context.ConnectionId);

        return true;
    }

    private async Task<bool> TryConnectAsJwtAsync(HttpContext httpCtx, System.Data.IDbConnection conn)
    {
        if (httpCtx.User.Identity?.IsAuthenticated != true) return false;

        var userCtx = httpCtx.Items["UserContext"] as UserContext;
        if (userCtx is null) return false;

        var userId = userCtx.UserIdGuid;
        var showIdRaw = httpCtx.Request.Query["showId"].ToString();

        if (Guid.TryParse(showIdRaw, out var showId) && await TryConnectAsCrewAsync(conn, userId, showId))
            return true;

        return await TryConnectAsOrgWebAsync(conn, userId);
    }

    private async Task<bool> TryConnectAsCrewAsync(System.Data.IDbConnection conn, Guid userId, Guid showId)
    {
        var crew = await conn.QuerySingleOrDefaultAsync<CrewRecord>(
            """
            SELECT c.show_id AS ShowId, c.role AS Role, bs.org_id AS OrgId
            FROM broadcast_crew c
            JOIN broadcast_shows bs ON bs.id = c.show_id
            WHERE c.show_id = @showId AND c.user_id = @userId AND c.is_active = true
            """,
            new { showId, userId });

        if (crew is null) return false;

        Context.Items["ConnectionType"] = "crew";
        Context.Items["UserId"] = userId;
        Context.Items["ShowId"] = showId;
        Context.Items["OrgId"] = crew.OrgId;

        await Groups.AddToGroupAsync(Context.ConnectionId, CrewGroup(showId));

        _logger.LogInformation(
            "[BroadcastHub] Crew connected — userId={UserId} showId={ShowId} role={Role} connId={ConnId}",
            userId, showId, crew.Role, Context.ConnectionId);

        return true;
    }

    private async Task<bool> TryConnectAsOrgWebAsync(System.Data.IDbConnection conn, Guid userId)
    {
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT id FROM organizations WHERE owner_id = @userId LIMIT 1",
            new { userId });

        if (!orgId.HasValue) return false;

        Context.Items["ConnectionType"] = "org-web";
        Context.Items["UserId"] = userId;
        Context.Items["OrgId"] = orgId.Value;

        await Groups.AddToGroupAsync(Context.ConnectionId, OrgGroup(orgId.Value));

        _logger.LogInformation(
            "[BroadcastHub] Org-web connected — userId={UserId} orgId={OrgId} connId={ConnId}",
            userId, orgId.Value, Context.ConnectionId);

        return true;
    }

    private async Task DisconnectNodeAsync(Exception? exception)
    {
        if (!TryGetNodeId(out var nodeId)) return;

        if (Context.Items.TryGetValue("OrgId", out var orgVal) && orgVal is Guid orgId)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, OrgGroup(orgId));

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, NodeGroup(nodeId));

        _logger.LogInformation(
            "[BroadcastHub] Node disconnected — nodeId={NodeId} reason={Reason}",
            nodeId, exception?.Message ?? "clean close");
    }

    private async Task DisconnectCrewAsync()
    {
        if (Context.Items.TryGetValue("ShowId", out var showVal) && showVal is Guid showId)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, CrewGroup(showId));

        Context.Items.TryGetValue("UserId", out var userIdVal);
        _logger.LogInformation(
            "[BroadcastHub] Crew disconnected — userId={UserId} connId={ConnId}",
            userIdVal, Context.ConnectionId);
    }

    // ── Private utility helpers ───────────────────────────────────────────────

    private bool IsNodeConnection(out Guid nodeId)
    {
        if (Context.Items.TryGetValue("ConnectionType", out var ct)
            && ct is string connType && connType == "node"
            && TryGetNodeId(out nodeId))
        {
            return true;
        }

        _logger.LogWarning(
            "[BroadcastHub] Non-node connection attempted node-only method. connId={ConnId}",
            Context.ConnectionId);
        nodeId = default;
        return false;
    }

    private bool IsRateLimited()
    {
        var bucket = s_rateLimits.GetOrAdd(Context.ConnectionId, _ => new RateBucket());
        if (bucket.TryConsume()) return false;

        _logger.LogWarning("[BroadcastHub] Rate limit exceeded — aborting connId={ConnId}", Context.ConnectionId);
        Context.Abort();
        return true;
    }

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

    private static string? ExtractBearerToken(HttpContext httpCtx)
    {
        var authHeader = httpCtx.Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (authHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader[bearer.Length..].Trim();
            if (!string.IsNullOrEmpty(token)) return token;
        }

        // SignalR WebSocket upgrades cannot carry custom headers; the web client sends the
        // JWT via ?access_token= which the JWT middleware reads for auth but does not write
        // back to the Authorization header.
        var queryToken = httpCtx.Request.Query["access_token"].ToString();
        return string.IsNullOrEmpty(queryToken) ? null : queryToken;
    }

    /// <summary>SHA-256 hex of the UTF-8 bytes of <paramref name="token"/>.</summary>
    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private sealed record NodeRecord(Guid Id, Guid OrgId);
    private sealed record CrewRecord(Guid ShowId, string Role, Guid OrgId);
    private sealed record OverlayTokenRecord(Guid Id, Guid ShowId, Guid OrgId);

    /// <summary>Sliding-window rate bucket: 60 calls per 60 seconds per connection.</summary>
    private sealed class RateBucket
    {
        private readonly object _lock = new();
        private int _count;
        private DateTime _windowStart = DateTime.UtcNow;

        public bool TryConsume()
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if ((now - _windowStart).TotalSeconds >= 60)
                {
                    _windowStart = now;
                    _count = 0;
                }

                _count++;
                return _count <= 60;
            }
        }
    }
}

// ── Hub payload records ───────────────────────────────────────────────────────

public sealed record HeartbeatPayload(
    DateTimeOffset Timestamp,
    string? Status = null,
    NodeRuntimeStatus? Runtime = null);

public sealed record BroadcastEventPayload(
    string EventType,
    JsonElement? Data = null,
    string EventId = "");

public sealed record CommandAckPayload(
    string CommandId,
    string Status,
    string? Error = null);
