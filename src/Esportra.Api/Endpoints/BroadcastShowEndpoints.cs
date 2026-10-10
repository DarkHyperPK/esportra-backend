using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class BroadcastShowEndpoints
{
    private static readonly HashSet<string> ValidShowStatuses =
        ["draft", "scheduled", "live", "ended", "cancelled"];

    public static void MapBroadcastShowEndpoints(this WebApplication app)
    {
        app.MapPost("/api/broadcast/shows", CreateShow)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/broadcast/shows", ListShows)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/broadcast/shows/{showId}", GetShow)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/broadcast/shows/{showId}/push-config", PushShowConfig)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/broadcast/shows/{showId}/matches", AddShowMatch)
            .RequireAuthorization("Authenticated");

        app.MapDelete("/api/broadcast/shows/{showId}/matches/{matchId}", RemoveShowMatch)
            .RequireAuthorization("Authenticated");

        app.MapMethods("/api/broadcast/shows/{showId}/status", ["PATCH"], UpdateShowStatus)
            .RequireAuthorization("Authenticated");
    }

    // ── POST /api/broadcast/shows ─────────────────────────────────────────────

    private static async Task<IResult> CreateShow(
        [FromBody] CreateShowRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.OrgId == Guid.Empty)
            return Results.BadRequest(new { error = "orgId is required" });

        var title = req.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200)
            return Results.BadRequest(new { error = "title must be 1–200 characters" });

        using var conn = db.CreateConnection();
        if (!await IsOrgOwnerAsync(conn, req.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var showId = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO broadcast_shows (org_id, tournament_id, title, status, scheduled_at, created_by)
            VALUES (@orgId, @tournamentId, @title, 'draft', @scheduledAt, @createdBy)
            RETURNING id
            """,
            new
            {
                orgId = req.OrgId,
                tournamentId = req.TournamentId,
                title,
                scheduledAt = req.ScheduledAt,
                createdBy = userCtx.UserIdGuid,
            });

        return Results.Ok(new { showId });
    }

    // ── GET /api/broadcast/shows?orgId= ──────────────────────────────────────

    private static async Task<IResult> ListShows(
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

        var shows = await conn.QueryAsync<ShowListRow>(
            """
            SELECT id AS Id, title AS Title, status AS Status,
                   scheduled_at AS ScheduledAt, started_at AS StartedAt, ended_at AS EndedAt,
                   created_at AS CreatedAt, tournament_id AS TournamentId
            FROM broadcast_shows
            WHERE org_id = @orgId
            ORDER BY created_at DESC
            """,
            new { orgId });

        return Results.Ok(shows);
    }

    // ── GET /api/broadcast/shows/{showId} ─────────────────────────────────────

    private static async Task<IResult> GetShow(
        Guid showId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var show = await FetchShowDetailAsync(conn, showId);

        if (show is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, show.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var matchSlots = await FetchShowMatchSlotsAsync(conn, showId);

        return Results.Ok(new
        {
            show.Id,
            show.OrgId,
            show.Title,
            show.Status,
            show.ScheduledAt,
            show.StartedAt,
            show.EndedAt,
            show.CreatedAt,
            show.UpdatedAt,
            show.TournamentId,
            show.NodeCount,
            show.MatchSlotCount,
            show.MatchSlotsWithCode,
            show.TalentCount,
            show.SponsorCount,
            show.CrewCount,
            MatchSlots = matchSlots.Select(m => new
            {
                Id = m.Id.ToString(),
                MatchId = m.MatchId.ToString(),
                m.MatchCode,
                m.DisplayOrder,
                m.Status,
            }).ToArray(),
        });
    }

    private static async Task<ShowDetailRow?> FetchShowDetailAsync(
        System.Data.IDbConnection conn, Guid showId) =>
        await conn.QuerySingleOrDefaultAsync<ShowDetailRow>(
            """
            SELECT
                s.id               AS Id,
                s.org_id           AS OrgId,
                s.title            AS Title,
                s.status           AS Status,
                s.scheduled_at     AS ScheduledAt,
                s.started_at       AS StartedAt,
                s.ended_at         AS EndedAt,
                s.created_at       AS CreatedAt,
                s.updated_at       AS UpdatedAt,
                s.tournament_id    AS TournamentId,
                (SELECT COUNT(*) FROM broadcast_nodes
                    WHERE org_id = s.org_id AND is_active = true)          AS NodeCount,
                (SELECT COUNT(*) FROM broadcast_show_matches
                    WHERE show_id = s.id)                                   AS MatchSlotCount,
                (SELECT COUNT(*) FROM broadcast_show_matches
                    WHERE show_id = s.id AND match_code IS NOT NULL)        AS MatchSlotsWithCode,
                (SELECT COUNT(*) FROM broadcast_talent
                    WHERE show_id = s.id)                                   AS TalentCount,
                (SELECT COUNT(*) FROM broadcast_show_sponsors
                    WHERE show_id = s.id AND is_active = true)              AS SponsorCount,
                (SELECT COUNT(*) FROM broadcast_crew
                    WHERE show_id = s.id AND is_active = true)              AS CrewCount
            FROM broadcast_shows s
            WHERE s.id = @showId
            """,
            new { showId });

    private static async Task<IEnumerable<ShowDetailMatchSlotRow>> FetchShowMatchSlotsAsync(
        System.Data.IDbConnection conn, Guid showId) =>
        await conn.QueryAsync<ShowDetailMatchSlotRow>(
            """
            SELECT id AS Id, match_id AS MatchId, match_code AS MatchCode,
                   display_order AS DisplayOrder, status AS Status
            FROM broadcast_show_matches
            WHERE show_id = @showId
            ORDER BY display_order
            """,
            new { showId });

    // ── POST /api/broadcast/shows/{showId}/push-config ────────────────────────

    private static async Task<IResult> PushShowConfig(
        Guid showId,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<BroadcastHub> hub,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<ShowRow>(
            "SELECT id AS Id, org_id AS OrgId, title AS Title, status AS Status FROM broadcast_shows WHERE id = @showId",
            new { showId });

        if (show is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, show.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var matches = await conn.QueryAsync<ShowMatchRow>(
            """
            SELECT match_id AS MatchId, match_code AS MatchCode,
                   display_order AS DisplayOrder, status AS Status
            FROM broadcast_show_matches
            WHERE show_id = @showId
            ORDER BY display_order
            """,
            new { showId });

        var talent = await conn.QueryAsync<TalentRow>(
            """
            SELECT name AS Name, handle AS Handle, role AS Role, display_order AS DisplayOrder
            FROM broadcast_talent
            WHERE show_id = @showId
            ORDER BY display_order
            """,
            new { showId });

        var sponsors = await conn.QueryAsync<SponsorRow>(
            """
            SELECT name AS Name, logo_url AS LogoUrl, display_slot AS DisplaySlot,
                   display_order AS DisplayOrder
            FROM broadcast_show_sponsors
            WHERE show_id = @showId AND is_active = true
            ORDER BY display_order
            """,
            new { showId });

        var payload = new ShowConfigPayload(
            ShowId: showId.ToString(),
            Title: show.Title,
            Status: show.Status,
            Matches: matches.Select(m => new ShowMatchSlot(m.MatchId.ToString(), m.MatchCode ?? "MAIN", m.DisplayOrder, m.Status)).ToArray(),
            Casters: talent.Select(t => new ShowTalentSlot(t.Name, t.Handle, t.Role, t.DisplayOrder)).ToArray(),
            Sponsors: sponsors.Select(s => new ShowSponsorSlot(s.Name, s.LogoUrl, s.DisplaySlot, s.DisplayOrder)).ToArray()
        );

        await hub.Clients.Group(BroadcastHub.OrgGroup(show.OrgId)).SendAsync("ShowConfig", payload, ct);
        return Results.Ok(new { success = true });
    }

    // ── POST /api/broadcast/shows/{showId}/matches ────────────────────────────

    private static async Task<IResult> AddShowMatch(
        Guid showId,
        [FromBody] AddShowMatchRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.MatchId == Guid.Empty)
            return Results.BadRequest(new { error = "matchId is required" });

        var matchCode = req.MatchCode?.Trim().ToUpperInvariant() ?? "MAIN";
        if (matchCode.Length > 20)
            return Results.BadRequest(new { error = "matchCode must be 20 characters or fewer" });

        using var conn = db.CreateConnection();
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT org_id FROM broadcast_shows WHERE id = @showId", new { showId });

        if (orgId is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, orgId.Value, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var slotId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            INSERT INTO broadcast_show_matches (show_id, match_id, display_order, match_code)
            VALUES (@showId, @matchId, @displayOrder, @matchCode)
            ON CONFLICT DO NOTHING
            RETURNING id
            """,
            new { showId, matchId = req.MatchId, displayOrder = req.DisplayOrder, matchCode });

        return slotId.HasValue
            ? Results.Ok(new { slotId = slotId.Value })
            : Results.Json(new { error = "match_or_code_already_in_show" }, statusCode: 409);
    }

    // ── DELETE /api/broadcast/shows/{showId}/matches/{matchId} ────────────────

    private static async Task<IResult> RemoveShowMatch(
        Guid showId,
        Guid matchId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT org_id FROM broadcast_shows WHERE id = @showId", new { showId });

        if (orgId is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, orgId.Value, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM broadcast_show_matches WHERE show_id = @showId AND match_id = @matchId",
            new { showId, matchId });

        return affected > 0
            ? Results.Ok(new { success = true })
            : Results.NotFound(new { error = "match_slot_not_found" });
    }

    // ── PATCH /api/broadcast/shows/{showId}/status ────────────────────────────

    private static async Task<IResult> UpdateShowStatus(
        Guid showId,
        [FromBody] UpdateShowStatusRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Status) || !ValidShowStatuses.Contains(req.Status))
            return Results.BadRequest(new { error = "status must be one of: draft, scheduled, live, ended, cancelled" });

        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<ShowRow>(
            "SELECT id AS Id, org_id AS OrgId, title AS Title, status AS Status FROM broadcast_shows WHERE id = @showId",
            new { showId });

        if (show is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, show.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        if (req.Status == "live")
        {
            await conn.ExecuteAsync(
                "UPDATE broadcast_shows SET status = @status, updated_at = NOW(), started_at = NOW() WHERE id = @showId",
                new { status = req.Status, showId });
        }
        else if (req.Status == "ended")
        {
            await conn.ExecuteAsync(
                "UPDATE broadcast_shows SET status = @status, updated_at = NOW(), ended_at = NOW() WHERE id = @showId",
                new { status = req.Status, showId });
        }
        else
        {
            await conn.ExecuteAsync(
                "UPDATE broadcast_shows SET status = @status, updated_at = NOW() WHERE id = @showId",
                new { status = req.Status, showId });
        }

        return Results.Ok(new { success = true, status = req.Status });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<bool> IsOrgOwnerAsync(
        System.Data.IDbConnection conn, Guid orgId, Guid userId) =>
        await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId)",
            new { orgId, userId });

    // ── Private row types ─────────────────────────────────────────────────────

    private sealed record ShowRow(Guid Id, Guid OrgId, string Title, string Status);
    private sealed record ShowListRow(Guid Id, string Title, string Status,
        DateTimeOffset? ScheduledAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
        DateTimeOffset CreatedAt, Guid? TournamentId);
    private sealed record ShowDetailRow(
        Guid Id, Guid OrgId, string Title, string Status,
        DateTimeOffset? ScheduledAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? TournamentId,
        int NodeCount, int MatchSlotCount, int MatchSlotsWithCode,
        int TalentCount, int SponsorCount, int CrewCount);
    private sealed record ShowDetailMatchSlotRow(
        Guid Id, Guid MatchId, string? MatchCode, int DisplayOrder, string Status);
    private sealed record ShowMatchRow(Guid MatchId, string? MatchCode, int DisplayOrder, string Status);
    private sealed record TalentRow(string Name, string? Handle, string Role, int DisplayOrder);
    private sealed record SponsorRow(string Name, string? LogoUrl, string DisplaySlot, int DisplayOrder);
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public sealed record CreateShowRequest(
    [property: JsonPropertyName("orgId")] Guid OrgId,
    string? Title = null,
    [property: JsonPropertyName("tournamentId")] Guid? TournamentId = null,
    [property: JsonPropertyName("scheduledAt")] DateTimeOffset? ScheduledAt = null);

public sealed record AddShowMatchRequest(
    [property: JsonPropertyName("matchId")] Guid MatchId,
    [property: JsonPropertyName("matchCode")] string? MatchCode = null,
    [property: JsonPropertyName("displayOrder")] int DisplayOrder = 0);

public sealed record UpdateShowStatusRequest(string Status);

// ── ShowConfig SignalR payload records ────────────────────────────────────────

public sealed record ShowConfigPayload(
    string ShowId,
    string Title,
    string Status,
    ShowMatchSlot[] Matches,
    ShowTalentSlot[] Casters,
    ShowSponsorSlot[] Sponsors);

public sealed record ShowMatchSlot(
    string MatchId,
    string MatchCode,
    int DisplayOrder,
    string Status);

public sealed record ShowTalentSlot(
    string Name,
    string? Handle,
    string Role,
    int DisplayOrder);

public sealed record ShowSponsorSlot(
    string Name,
    string? LogoUrl,
    string DisplaySlot,
    int DisplayOrder);
