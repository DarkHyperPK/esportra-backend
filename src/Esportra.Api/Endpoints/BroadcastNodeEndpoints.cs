using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class BroadcastNodeEndpoints
{
    // Uppercase alphanumeric, excluding confusable chars O, 0, I, 1 (32 chars = no modulo bias)
    private static readonly char[] CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    public static void MapBroadcastNodeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/broadcast/nodes/codes", GeneratePairingCode)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/broadcast/nodes/pair", PairNode)
            .WithMetadata(new RateLimitPolicyMetadata("strict"));

        app.MapGet("/api/broadcast/nodes", ListNodes)
            .RequireAuthorization("Authenticated");

        app.MapDelete("/api/broadcast/nodes/{nodeId}", DeactivateNode)
            .RequireAuthorization("Authenticated");
    }

    // ── POST /api/broadcast/nodes/codes ──────────────────────────────────────

    private static async Task<IResult> GeneratePairingCode(
        [FromBody] GeneratePairingCodeRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.OrgId == Guid.Empty)
            return Results.BadRequest(new { error = "orgId is required" });

        using var conn = db.CreateConnection();

        if (!await IsOrgOwnerAsync(conn, req.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var code = GenerateCode();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        await conn.ExecuteAsync(
            """
            INSERT INTO broadcast_pairing_codes (org_id, code_hash, created_by, expires_at)
            VALUES (@orgId, @codeHash, @createdBy, @expiresAt)
            """,
            new { orgId = req.OrgId, codeHash = HashString(code), createdBy = userCtx.UserIdGuid, expiresAt });

        return Results.Ok(new { code, expiresAt });
    }

    // ── POST /api/broadcast/nodes/pair ────────────────────────────────────────

    private static async Task<IResult> PairNode(
        [FromBody] PairNodeRequest req,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            return Results.BadRequest(new { error = "code is required" });

        var trimmedName = req.Name?.Trim() ?? string.Empty;
        if (trimmedName.Length == 0 || trimmedName.Length > 100)
            return Results.BadRequest(new { error = "name must be 1–100 characters" });

        var codeHash = HashString(req.Code.ToUpperInvariant());
        using var conn = db.CreateConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();

        // Atomically lock and read the code row — FOR UPDATE prevents concurrent claims.
        var codeRow = await conn.QuerySingleOrDefaultAsync<PairingCodeRow>(
            """
            SELECT id AS Id, org_id AS OrgId, claimed_at AS ClaimedAt
            FROM broadcast_pairing_codes
            WHERE code_hash = @codeHash AND expires_at > NOW()
            FOR UPDATE
            """,
            new { codeHash }, tx);

        if (codeRow is null)
        {
            tx.Rollback();
            return Results.Json(new { error = "invalid_code" }, statusCode: 400);
        }

        if (codeRow.ClaimedAt is not null)
        {
            tx.Rollback();
            return Results.Json(new { error = "code_claimed" }, statusCode: 409);
        }

        // Generate token — shown once, never stored in plaintext
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashString(token);
        var capabilitiesJson = req.Capabilities.HasValue
            ? JsonSerializer.Serialize(req.Capabilities.Value)
            : "{}";

        var nodeId = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO broadcast_nodes (org_id, name, token_hash, capabilities, is_active)
            VALUES (@orgId, @name, @tokenHash, @capabilities::jsonb, true)
            RETURNING id
            """,
            new { orgId = codeRow.OrgId, name = trimmedName, tokenHash, capabilities = capabilitiesJson },
            tx);

        await conn.ExecuteAsync(
            """
            UPDATE broadcast_pairing_codes
            SET claimed_at = NOW(), claimed_by_node_id = @nodeId
            WHERE id = @codeId
            """,
            new { nodeId, codeId = codeRow.Id },
            tx);

        tx.Commit();
        return Results.Ok(new { nodeId, token });
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
                   last_seen_at AS LastSeenAt, is_active AS IsActive
            FROM broadcast_nodes
            WHERE org_id = @orgId AND is_active = true
            ORDER BY created_at DESC
            """,
            new { orgId });

        return Results.Ok(nodes);
    }

    // ── DELETE /api/broadcast/nodes/{nodeId} ──────────────────────────────────

    private static async Task<IResult> DeactivateNode(
        Guid nodeId,
        HttpContext ctx,
        IDbConnectionFactory db,
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

        return affected > 0
            ? Results.Ok(new { success = true })
            : Results.NotFound(new { error = "node_not_found" });
    }

    // ── Private helpers (delegates to BroadcastHelpers) ──────────────────────

    private static string HashString(string value) =>
        BroadcastHelpers.HashString(value);

    private static Task<bool> IsOrgOwnerAsync(
        System.Data.IDbConnection conn, Guid orgId, Guid userId,
        System.Data.IDbTransaction? tx = null) =>
        BroadcastHelpers.IsOrgOwnerAsync(conn, orgId, userId, tx);

    private static string GenerateCode()
    {
        // 8 chars from 32-char alphabet — 32 divides 256 so no modulo bias per byte
        var chars = new char[8];
        var bytes = RandomNumberGenerator.GetBytes(8);
        for (var i = 0; i < 8; i++)
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        return new string(chars);
    }

    private sealed record PairingCodeRow(Guid Id, Guid OrgId, DateTimeOffset? ClaimedAt);
    private sealed record NodeListRow(Guid Id, string Name, string Capabilities, DateTimeOffset? LastSeenAt, bool IsActive);
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public sealed record GeneratePairingCodeRequest(
    [property: JsonPropertyName("orgId")] Guid OrgId);

public sealed record PairNodeRequest(
    string Code,
    string? Name,
    JsonElement? Capabilities = null);
