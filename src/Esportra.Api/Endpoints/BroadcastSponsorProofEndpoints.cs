using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Api.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Sponsor proof-of-play endpoints for PROJ-059 P4 (Esportra Air: Results + Reports).
///
/// POST /api/broadcast/shows/{showId}/sponsor-impressions
///   Auth: node bearer token (same mechanism as BroadcastDrainEndpoints).
///   IDOR guard: node's org must own the show before any DB write.
///
/// GET /api/broadcast/shows/{showId}/sponsor-proof
///   Auth: Supabase JWT, org owner only.
///   IDOR guard: user must own the show's org before any data fetch.
///   No auth.users join anywhere in this query path (CIO requirement).
/// </summary>
public static class BroadcastSponsorProofEndpoints
{
    private static readonly HashSet<string> ValidSlotTypes =
        ["lower_third", "scorebug", "break_fullscreen", "custom"];

    public static void MapBroadcastSponsorProofEndpoints(this WebApplication app)
    {
        app.MapPost("/api/broadcast/shows/{showId}/sponsor-impressions", PostSponsorImpressions)
            .WithMetadata(new RateLimitPolicyMetadata("strict"));

        app.MapGet("/api/broadcast/shows/{showId}/sponsor-proof", GetSponsorProof)
            .RequireAuthorization("Authenticated");
    }

    // ── POST /api/broadcast/shows/{showId}/sponsor-impressions ────────────────

    private static async Task<IResult> PostSponsorImpressions(
        Guid showId,
        [FromBody] SponsorImpressionBatchRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        // Auth: node bearer token
        var node = await BroadcastHelpers.ValidateNodeBearerAsync(ctx, db);
        if (node is null) return Results.Unauthorized();

        if (req.Impressions is not null && req.Impressions.Count > 50)
            return Results.BadRequest(new { error = "Batch too large. Maximum 50 impressions per call." });

        using var conn = db.CreateConnection();

        // IDOR guard (CIO mandatory): node's org must own the show before any DB write.
        // Must run even on empty batches to prevent org-membership probing.
        var showOrgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT org_id FROM broadcast_shows WHERE id = @showId",
            new { showId });

        if (!showOrgId.HasValue)
            return Results.Json(new { error = "Show not found." }, statusCode: 404);

        if (showOrgId.Value != node.OrgId)
            return Results.Json(new { error = "You do not have access to this show." }, statusCode: 403);

        if (req.Impressions is null || req.Impressions.Count == 0)
            return Results.Ok(new { accepted = 0 });

        // Validate each impression before any insert
        for (var i = 0; i < req.Impressions.Count; i++)
        {
            var imp = req.Impressions[i];
            if (imp.SponsorId == Guid.Empty)
                return Results.BadRequest(new { error = $"impressions[{i}].sponsorId is required." });
            if (imp.StartedAt == default)
                return Results.BadRequest(new { error = $"impressions[{i}].startedAt is required." });
            if (imp.EndedAt < imp.StartedAt)
                return Results.BadRequest(new { error = $"impressions[{i}].endedAt must not precede startedAt." });
            if (!ValidSlotTypes.Contains(imp.SlotType ?? ""))
                return Results.BadRequest(new
                {
                    error = $"impressions[{i}].slotType must be one of: lower_third, scorebug, break_fullscreen, custom."
                });
        }

        var sponsorIds = req.Impressions.Select(imp => imp.SponsorId).Distinct().ToArray();
        if (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM broadcast_show_sponsors WHERE show_id = @showId AND id = ANY(@sponsorIds)", new { showId, sponsorIds }) != sponsorIds.Length)
            return Results.Json(new { error = "Sponsors must belong to this show." }, statusCode: 403);
        var segmentIds = req.Impressions.Where(imp => imp.SegmentId.HasValue).Select(imp => imp.SegmentId!.Value).Distinct().ToArray();
        if (segmentIds.Length > 0 && await conn.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM rundown_segments WHERE show_id = @showId AND id = ANY(@segmentIds)", new { showId, segmentIds }) != segmentIds.Length)
            return Results.Json(new { error = "Segments must belong to this show." }, statusCode: 403);

        // Insert via service_role connection (API connection IS the service_role connection;
        // no authenticated-user INSERT policy exists on broadcast_sponsor_impressions by design).
        var accepted = 0;
        foreach (var imp in req.Impressions)
        {
            var rows = await conn.ExecuteAsync(
                """
                INSERT INTO broadcast_sponsor_impressions
                    (show_id, sponsor_id, segment_id, started_at, ended_at, slot_type)
                VALUES
                    (@showId, @sponsorId, @segmentId, @startedAt, @endedAt, @slotType)
                ON CONFLICT (show_id, sponsor_id, started_at) DO UPDATE
                SET ended_at = EXCLUDED.ended_at
                WHERE broadcast_sponsor_impressions.ended_at IS NULL
                  AND EXCLUDED.ended_at IS NOT NULL
                  AND broadcast_sponsor_impressions.slot_type = EXCLUDED.slot_type
                """,
                new
                {
                    showId,
                    sponsorId = imp.SponsorId,
                    segmentId = imp.SegmentId,
                    startedAt = imp.StartedAt,
                    endedAt = imp.EndedAt,
                    slotType = imp.SlotType,
                });
            accepted += rows;
        }

        return Results.Ok(new { accepted, acknowledged = req.Impressions.Count });
    }

    // ── GET /api/broadcast/shows/{showId}/sponsor-proof ───────────────────────

    private static async Task<IResult> GetSponsorProof(
        Guid showId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();

        // Fetch show; derive org_id for ownership check
        var show = await conn.QuerySingleOrDefaultAsync<ShowBasicRow>(
            "SELECT id AS Id, org_id AS OrgId, title AS Title, ended_at AS EndedAt FROM broadcast_shows WHERE id = @showId",
            new { showId });

        if (show is null) return Results.NotFound(new { error = "show_not_found" });

        // IDOR guard (CIO mandatory): user must own the org; no auth.users join
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        // Aggregation query — COALESCE(bsi.ended_at, bs.ended_at) for open impressions (AC8)
        var rows = await conn.QueryAsync<SponsorProofRow>(
            """
            SELECT
                bsi.sponsor_id          AS SponsorId,
                bss.name                AS SponsorName,
                bsi.slot_type           AS SlotType,
                SUM(EXTRACT(EPOCH FROM (
                    COALESCE(bsi.ended_at, bs.ended_at) - bsi.started_at
                )))::double precision   AS TotalSeconds,
                COUNT(*) FILTER (WHERE bsi.ended_at IS NOT NULL)  AS PlacementCount,
                COUNT(*) FILTER (WHERE bsi.ended_at IS NULL)      AS IncompleteCount
            FROM broadcast_sponsor_impressions bsi
            JOIN broadcast_show_sponsors bss ON bss.id = bsi.sponsor_id
            JOIN broadcast_shows bs ON bs.id = bsi.show_id
            WHERE bsi.show_id = @showId
            GROUP BY bsi.sponsor_id, bss.name, bsi.slot_type
            """,
            new { showId });

        // C# aggregation: group by sponsor_id, build bySlotType dict
        var grouped = rows
            .GroupBy(r => r.SponsorId)
            .Select(g =>
            {
                var totalOnAir = g.Sum(r => r.TotalSeconds ?? 0.0);
                var totalPlacements = g.Sum(r => r.PlacementCount);
                var totalIncomplete = g.Sum(r => r.IncompleteCount);

                var bySlotType = g.ToDictionary(
                    r => r.SlotType,
                    r => new SponsorSlotBreakdown(
                        TotalSeconds: r.TotalSeconds ?? 0.0,
                        Count: r.PlacementCount));

                return new SponsorProofEntry(
                    SponsorId: g.Key,
                    Name: g.First().SponsorName,
                    TotalOnAirSeconds: totalOnAir,
                    PlacementCount: totalPlacements,
                    BySlotType: bySlotType,
                    HasIncomplete: totalIncomplete > 0,
                    IncompleteCount: totalIncomplete);
            })
            .ToList();

        return Results.Ok(new
        {
            showId,
            showTitle = show.Title,
            generatedAt = DateTimeOffset.UtcNow,
            sponsors = grouped,
        });
    }

    // ── Private row types ─────────────────────────────────────────────────────

    private sealed record ShowBasicRow(Guid Id, Guid OrgId, string Title, DateTime? EndedAt);
    private sealed record SponsorProofRow(
        Guid SponsorId,
        string SponsorName,
        string SlotType,
        double? TotalSeconds,
        long PlacementCount,
        long IncompleteCount);
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/broadcast/shows/{showId}/sponsor-impressions.</summary>
public sealed record SponsorImpressionBatchRequest(IReadOnlyList<SponsorImpressionItem> Impressions);

/// <summary>Single impression in a batch.</summary>
public sealed record SponsorImpressionItem(
    Guid SponsorId,
    DateTimeOffset StartedAt,
    string? SlotType,
    Guid? SegmentId = null,
    DateTimeOffset? EndedAt = null);

/// <summary>Per-slot-type aggregation row in the sponsor proof response.</summary>
public sealed record SponsorSlotBreakdown(double TotalSeconds, long Count);

/// <summary>Per-sponsor entry in the sponsor proof response.</summary>
public sealed record SponsorProofEntry(
    Guid SponsorId,
    string Name,
    double TotalOnAirSeconds,
    long PlacementCount,
    IReadOnlyDictionary<string, SponsorSlotBreakdown> BySlotType,
    bool HasIncomplete,
    long IncompleteCount);
