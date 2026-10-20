using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class BroadcastOverlayTokenEndpoints
{
    public static void MapBroadcastOverlayTokenEndpoints(this WebApplication app)
    {
        // Generate a new overlay token for a show.
        // Only the show's organiser (org owner) may generate tokens.
        app.MapPost("/api/broadcast/shows/{showId:guid}/overlay-tokens",
            async (
                Guid showId,
                [FromBody] CreateOverlayTokenRequest req,
                HttpContext httpCtx,
                IDbConnectionFactory db) =>
            {
                var userCtx = httpCtx.Items["UserContext"] as UserContext;
                if (userCtx is null) return Results.Unauthorized();

                using var conn = db.CreateConnection();

                var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    SELECT s.org_id FROM broadcast_shows s
                    JOIN organizations o ON o.id = s.org_id
                    WHERE s.id = @showId AND o.owner_id = @userId
                    """,
                    new { showId, userId = userCtx.UserIdGuid });

                if (!orgId.HasValue) return Results.NotFound();

                var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var tokenHash = Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

                // Show expires + 15-minute buffer so the token outlives the show slightly.
                var expiresAt = await conn.QuerySingleOrDefaultAsync<DateTimeOffset?>(
                    "SELECT COALESCE(ended_at, NOW()) + INTERVAL '24 hours' FROM broadcast_shows WHERE id = @showId",
                    new { showId }) ?? DateTimeOffset.UtcNow.AddHours(24);

                var tokenId = await conn.QuerySingleAsync<Guid>(
                    """
                    INSERT INTO broadcast_overlay_tokens (show_id, org_id, token_hash, label, expires_at)
                    VALUES (@showId, @orgId, @tokenHash, @label, @expiresAt)
                    RETURNING id
                    """,
                    new { showId, orgId = orgId.Value, tokenHash, label = req.Label, expiresAt });

                return Results.Ok(new CreateOverlayTokenResponse(tokenId, rawToken, expiresAt));
            })
            .RequireAuthorization()
            .WithName("CreateOverlayToken");

        // List active overlay tokens for a show.
        app.MapGet("/api/broadcast/shows/{showId:guid}/overlay-tokens",
            async (
                Guid showId,
                HttpContext httpCtx,
                IDbConnectionFactory db) =>
            {
                var userCtx = httpCtx.Items["UserContext"] as UserContext;
                if (userCtx is null) return Results.Unauthorized();

                using var conn = db.CreateConnection();

                var owned = await conn.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM broadcast_shows s
                        JOIN organizations o ON o.id = s.org_id
                        WHERE s.id = @showId AND o.owner_id = @userId
                    )
                    """,
                    new { showId, userId = userCtx.UserIdGuid });

                if (!owned) return Results.NotFound();

                var tokens = await conn.QueryAsync<OverlayTokenRow>(
                    """
                    SELECT id AS Id, label AS Label, created_at AS CreatedAt,
                           expires_at AS ExpiresAt, revoked_at AS RevokedAt,
                           last_ping_at AS LastPingAt
                    FROM broadcast_overlay_tokens
                    WHERE show_id = @showId
                    ORDER BY created_at DESC
                    """,
                    new { showId });

                return Results.Ok(tokens);
            })
            .RequireAuthorization()
            .WithName("ListOverlayTokens");

        // Revoke an overlay token. Pushes BroadcastOverlayRevoked to the overlay group
        // so connected browser sources are notified immediately.
        app.MapDelete("/api/broadcast/shows/{showId:guid}/overlay-tokens/{tokenId:guid}",
            async (
                Guid showId,
                Guid tokenId,
                HttpContext httpCtx,
                IDbConnectionFactory db,
                IHubContext<BroadcastHub> hub) =>
            {
                var userCtx = httpCtx.Items["UserContext"] as UserContext;
                if (userCtx is null) return Results.Unauthorized();

                using var conn = db.CreateConnection();

                var affected = await conn.ExecuteAsync(
                    """
                    UPDATE broadcast_overlay_tokens
                    SET revoked_at = NOW()
                    WHERE id = @tokenId
                      AND show_id = @showId
                      AND revoked_at IS NULL
                      AND EXISTS (
                          SELECT 1 FROM broadcast_shows s
                          JOIN organizations o ON o.id = s.org_id
                          WHERE s.id = @showId AND o.owner_id = @userId
                      )
                    """,
                    new { tokenId, showId, userId = userCtx.UserIdGuid });

                if (affected == 0) return Results.NotFound();

                // Push revocation to all browser sources for this show.
                await hub.Clients
                    .Group(BroadcastHub.OverlayGroup(showId))
                    .SendAsync("BroadcastOverlayRevoked", new { TokenId = tokenId });

                return Results.NoContent();
            })
            .RequireAuthorization()
            .WithName("RevokeOverlayToken");

        // Lightweight node health: returns tokens with recent pings.
        app.MapGet("/api/broadcast/shows/{showId:guid}/overlay-nodes",
            async (
                Guid showId,
                HttpContext httpCtx,
                IDbConnectionFactory db) =>
            {
                var userCtx = httpCtx.Items["UserContext"] as UserContext;
                if (userCtx is null) return Results.Unauthorized();

                using var conn = db.CreateConnection();

                var owned = await conn.ExecuteScalarAsync<bool>(
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM broadcast_shows s
                        JOIN organizations o ON o.id = s.org_id
                        WHERE s.id = @showId AND o.owner_id = @userId
                    )
                    """,
                    new { showId, userId = userCtx.UserIdGuid });

                if (!owned) return Results.NotFound();

                // "alive" = pinged within the last 15 seconds
                var nodes = await conn.QueryAsync<OverlayNodeStatus>(
                    """
                    SELECT id AS Id, label AS Label,
                           last_ping_at AS LastPingAt,
                           last_ping_at > NOW() - INTERVAL '15 seconds' AS IsAlive
                    FROM broadcast_overlay_tokens
                    WHERE show_id = @showId AND revoked_at IS NULL AND expires_at > NOW()
                    ORDER BY created_at
                    """,
                    new { showId });

                return Results.Ok(nodes);
            })
            .RequireAuthorization()
            .WithName("GetOverlayNodes");
    }
}

public sealed record CreateOverlayTokenRequest([property: JsonPropertyName("label")] string? Label);

public sealed record CreateOverlayTokenResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

internal sealed record OverlayTokenRow(
    Guid Id,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastPingAt);

internal sealed record OverlayNodeStatus(
    Guid Id,
    string? Label,
    DateTimeOffset? LastPingAt,
    bool IsAlive);
