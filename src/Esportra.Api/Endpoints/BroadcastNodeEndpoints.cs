using Dapper;
using Esportra.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastNodeEndpoints
{
    // Pairing codes were retired on 18 October 2026: production workstations sign
    // in with a registered account in Esportra Air instead. Legacy paired nodes
    // keep their session until the organization owner revokes them here.
    public static void MapBroadcastNodeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/broadcast/nodes", ListNodes)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/broadcast/nodes/session", async (HttpContext ctx, IDbConnectionFactory db) =>
        {
            var node = await BroadcastHelpers.ValidateNodeBearerAsync(ctx, db);
            return node is null
                ? Results.Json(new { error = "node_not_active" }, statusCode: 401)
                : Results.Ok(new { nodeId = node.NodeId, active = true });
        }).AllowAnonymous().WithMetadata(new RateLimitPolicyMetadata("fixed"));

        app.MapDelete("/api/broadcast/nodes/{nodeId:guid}", DeactivateNode)
            .RequireAuthorization("Authenticated")
            .WithMetadata(new RateLimitPolicyMetadata("fixed"));
    }

    // ── GET /api/broadcast/nodes?orgId={orgId} ────────────────────────────────

    private static async Task<IResult> ListNodes(
        [FromQuery] Guid orgId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (orgId == Guid.Empty)
            return Results.BadRequest(new { error = "orgId is required" });

        using var conn = db.CreateConnection();

        if (!await IsOrgOwnerAsync(conn, orgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var nodes = await conn.QueryAsync<NodeListRow>(
            """
            SELECT id AS Id, name AS Name, capabilities AS Capabilities,
                   last_seen_at AS LastSeenAt, is_active AS IsActive,
                   runtime_status::text AS RuntimeStatus, runtime_seen_at AS RuntimeSeenAt
            FROM broadcast_nodes
            WHERE org_id = @orgId AND is_active = true
            ORDER BY created_at DESC LIMIT 100
            """,
            new { orgId });

        return Results.Ok(nodes);
    }

    // ── DELETE /api/broadcast/nodes/{nodeId} ──────────────────────────────────

    private static async Task<IResult> DeactivateNode(
        Guid nodeId,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<BroadcastHub> hub,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        var affected = await conn.ExecuteAsync(
            """
            UPDATE broadcast_nodes
            SET is_active = false, updated_at = NOW()
            WHERE id = @nodeId
              AND org_id IN (SELECT id FROM organizations WHERE owner_id = @userId)
            """,
            new { nodeId, userId = userCtx.UserIdGuid });

        if (affected == 0) return Results.NotFound(new { error = "node_not_found" });
        await hub.Clients.Group($"node:{nodeId}").SendAsync("NodeRevoked", cancellationToken: ct);
        return Results.Ok(new { success = true });
    }

    // ── Private helpers (delegates to BroadcastHelpers) ──────────────────────

    private static Task<bool> IsOrgOwnerAsync(
        System.Data.IDbConnection conn, Guid orgId, Guid userId,
        System.Data.IDbTransaction? tx = null) =>
        BroadcastHelpers.IsOrgOwnerAsync(conn, orgId, userId, tx);

    private sealed record NodeListRow(Guid Id, string Name, string Capabilities, DateTime? LastSeenAt, bool IsActive, string? RuntimeStatus, DateTime? RuntimeSeenAt);
}

