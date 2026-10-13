using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// HTTP drain endpoints for the Esportra Air desktop node offline queues (PROJ-059 P4).
///
/// Both endpoints authenticate via node bearer token (same mechanism as the
/// BroadcastHub: SHA-256 hash lookup in broadcast_nodes). No Supabase JWT is
/// involved — these are called by the Desktop node, not by a browser.
///
/// All four CIO security guards from BroadcastEventGuard are applied to every
/// event before any DB write:
///   (a) event_type server-side allowlist
///   (b) payload JSON size cap (8 KB)
///   (c) event_id UUID format validation
///   (d) show_id server-derived from node's org — never from the payload
/// </summary>
public static class BroadcastDrainEndpoints
{
    public static void MapBroadcastDrainEndpoints(this WebApplication app)
    {
        // ── POST /api/broadcast/events/drain ─────────────────────────────────
        // Batch drain for the Desktop node's offline event queue.
        // Auth: node bearer token.  Max 100 events per call.
        // Idempotent: ON CONFLICT (event_id) DO NOTHING.
        // Response: { accepted, rejected } — which event_ids were rejected is
        //           never disclosed (security: prevent oracle attacks on the allowlist).
        app.MapPost("/api/broadcast/events/drain", async (
            [FromBody] DrainEventsRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("BroadcastDrain");
            var node = await BroadcastHelpers.ValidateNodeBearerAsync(ctx, db);
            if (node is null) return Results.Unauthorized();

            if (req.Events is null || req.Events.Count == 0)
                return Results.Ok(new { accepted = 0, rejected = 0 });

            if (req.Events.Count > 100)
                return Results.BadRequest(new { error = "Batch too large. Maximum 100 events per call." });

            using var conn = db.CreateConnection();

            // Guard (d): server-derive show_id from node's org — never from the payload
            var showId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM broadcast_shows WHERE org_id = @orgId AND status = 'live' LIMIT 1",
                new { orgId = node.OrgId });

            if (!showId.HasValue)
            {
                logger.LogWarning(
                    "[BroadcastDrain] No live show for org. nodeId={NodeId} orgId={OrgId} batchSize={BatchSize}",
                    node.NodeId, node.OrgId, req.Events.Count);
                return Results.Ok(new { accepted = 0, rejected = req.Events.Count });
            }

            int accepted = 0, rejected = 0;

            foreach (var evt in req.Events)
            {
                // Guard (c): event_id UUID format
                if (!BroadcastEventGuard.IsValidEventId(evt.EventId))
                {
                    rejected++;
                    continue;
                }

                // Guard (a): event_type allowlist
                if (!BroadcastEventGuard.IsAllowedEventType(evt.EventType))
                {
                    rejected++;
                    continue;
                }

                // Guard (b): payload size cap — log eventType only, never contents
                if (!BroadcastEventGuard.IsDataWithinSizeLimit(evt.Payload, out var payloadJson))
                {
                    logger.LogWarning(
                        "[BroadcastDrain] Event rejected — payload exceeds {MaxBytes} bytes. nodeId={NodeId} eventType={EventType}",
                        BroadcastEventGuard.MaxPayloadBytes, node.NodeId, evt.EventType);
                    rejected++;
                    continue;
                }

                await conn.ExecuteAsync(
                    "INSERT INTO broadcast_events (show_id, node_id, event_id, event_type, payload, source) " +
                    "VALUES (@showId, @nodeId, @eventId, @eventType, @payload::jsonb, 'node') " +
                    "ON CONFLICT (event_id) DO NOTHING",
                    new
                    {
                        showId = showId.Value,
                        nodeId = node.NodeId,
                        eventId = evt.EventId,
                        eventType = evt.EventType,
                        payload = payloadJson,
                    });

                accepted++;
            }

            return Results.Ok(new { accepted, rejected });
        }).WithMetadata(new RateLimitPolicyMetadata("strict"));

        // ── POST /api/broadcast/shows/{showId}/corrections ────────────────────
        // Drain endpoint for the Desktop node's FIX_DATA offline queue.
        // Auth: node bearer token.
        // Ownership guard: node's org must own the show.
        // NOT for gep_conflict — that is handled in T35.
        app.MapPost("/api/broadcast/shows/{showId}/corrections", async (
            Guid showId,
            [FromBody] DrainCorrectionsRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("BroadcastDrain");
            var node = await BroadcastHelpers.ValidateNodeBearerAsync(ctx, db);
            if (node is null) return Results.Unauthorized();

            if (req.Corrections is null || req.Corrections.Count == 0)
                return Results.Ok(new { inserted = 0 });
            if (req.Corrections.Count > 100 || req.Corrections.Any(item => item is null || item.MapIndex is < 1 or > 9 || item.TeamAScore < 0 || item.TeamBScore < 0 || item.Reason?.Length > 500 || string.IsNullOrWhiteSpace(item.MatchCode) || item.MatchCode.Length > 64 || item.MatchCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) || (item.IdempotencyKey is not null && !Guid.TryParse(item.IdempotencyKey, out _))))
                return Results.BadRequest(new { error = "Invalid correction batch. Use up to 100 valid map results." });

            using var conn = db.CreateConnection();

            // Ownership guard: node's org must own the show
            var showOrgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT org_id FROM broadcast_shows WHERE id = @showId",
                new { showId });

            if (!showOrgId.HasValue)
                return Results.Json(new { error = "Show not found." }, statusCode: 404);

            if (showOrgId.Value != node.OrgId)
                return Results.Json(new { error = "You do not have access to this show." }, statusCode: 403);

            int inserted = 0;

            foreach (var correction in req.Corrections)
            {
                var afterState = JsonSerializer.Serialize(new
                {
                    matchCode = correction.MatchCode,
                    mapIndex = correction.MapIndex,
                    teamAScore = correction.TeamAScore,
                    teamBScore = correction.TeamBScore,
                });

                await conn.ExecuteAsync(
                    """
                    INSERT INTO broadcast_corrections
                        (show_id, corrected_by, node_id, correction_type, source, after_state, reason, client_key, created_at)
                    VALUES
                        (@showId, NULL, @nodeId, 'fix_data', 'node', @afterState::jsonb, @reason, @clientKey, COALESCE(@ts, NOW()))
                    ON CONFLICT (client_key) WHERE client_key IS NOT NULL DO NOTHING
                    """,
                    new
                    {
                        showId,
                        nodeId = node.NodeId,
                        afterState,
                        reason = correction.Reason,
                        clientKey = correction.IdempotencyKey,
                        ts = correction.Ts,
                    });

                inserted++;
            }

            logger.LogInformation(
                "[BroadcastDrain] Corrections inserted — nodeId={NodeId} showId={ShowId} count={Count}",
                node.NodeId, showId, inserted);

            return Results.Ok(new { inserted });
        }).WithMetadata(new RateLimitPolicyMetadata("strict"));
    }

}

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/broadcast/events/drain.</summary>
public sealed record DrainEventsRequest(IReadOnlyList<DrainEventItem> Events);

/// <summary>Single event in a drain batch.</summary>
public sealed record DrainEventItem(
    string EventId,
    string EventType,
    JsonElement? Payload = null,
    DateTimeOffset? Ts = null);

/// <summary>Request body for POST /api/broadcast/shows/{showId}/corrections.</summary>
public sealed record DrainCorrectionsRequest(IReadOnlyList<DrainCorrectionItem> Corrections);

/// <summary>Single FIX_DATA correction item.</summary>
public sealed record DrainCorrectionItem(
    string MatchCode,
    int MapIndex,
    int TeamAScore,
    int TeamBScore,
    string? Reason = null,
    DateTimeOffset? Ts = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("idempotencyKey")]
    string? IdempotencyKey = null);
