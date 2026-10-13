using System.Data;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class TournamentBroadcastEndpoints
{
    public static void MapTournamentBroadcastEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tournaments/{tournamentId:guid}/broadcast", GetWorkspace).RequireAuthorization("Authenticated");
        app.MapPost("/api/tournaments/{tournamentId:guid}/broadcast/shows", CreateShow)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
    }

    private static async Task<IResult> GetWorkspace(Guid tournamentId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var tournament = await conn.QuerySingleOrDefaultAsync<TournamentScope>("""
            SELECT t.id AS Id, t.name AS Name, t.slug AS Slug, t.game AS Game,
                   t.organizer_id AS OrganizerId, t.organization_id AS OrganizationId, o.owner_id AS OrganizationOwnerId
            FROM tournaments t LEFT JOIN organizations o ON o.id = t.organization_id
            WHERE t.id = @tournamentId AND t.deleted_at IS NULL
            """, new { tournamentId });
        if (tournament is null) return Results.NotFound();
        if (tournament.OrganizerId != user.UserIdGuid && tournament.OrganizationOwnerId != user.UserIdGuid) return Results.StatusCode(403);
        var canManage = tournament.OrganizationId.HasValue && tournament.OrganizationOwnerId == user.UserIdGuid;
        var organizations = tournament.OrganizationId is null
            ? (await conn.QueryAsync<OrganizationChoice>("SELECT id AS Id, name AS Name FROM organizations WHERE owner_id = @userId ORDER BY name LIMIT 100", new { userId = user.UserIdGuid })).ToArray()
            : [];
        // Eligibility can be shown to the tournament organizer. Organization-wide
        // node/crew controls retain the existing organization-owner authorization.
        var shows = canManage ? (await conn.QueryAsync<TournamentShow>("""
            SELECT s.id AS Id, s.title AS Title, s.status AS Status, s.scheduled_at AS ScheduledAt,
                   s.last_synced_at AS LastSyncedAt,
                   (SELECT COUNT(*) FROM broadcast_show_matches m WHERE m.show_id = s.id) AS MatchCount
            FROM broadcast_shows s WHERE s.tournament_id = @tournamentId AND s.org_id = @orgId
            ORDER BY s.created_at DESC LIMIT 100
            """, new { tournamentId, orgId = tournament.OrganizationId })).ToArray() : [];
        return Results.Ok(new
        {
            tournament = new { tournament.Id, tournament.Name, tournament.Slug, tournament.Game, tournament.OrganizationId },
            canManage,
            access = canManage ? "ready" : tournament.OrganizationId is null ? "organization_required" : "organization_owner_required",
            organizations,
            shows
        });
    }

    private static async Task<IResult> CreateShow(Guid tournamentId, [FromBody] TournamentShowRequest request,
        HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200 || request.OrgId == Guid.Empty)
            return Results.BadRequest(new { error = "Provide a title of 1–200 characters and a valid organization." });
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var tournament = await conn.QuerySingleOrDefaultAsync<LockedTournament>("""
            SELECT id AS Id, organizer_id AS OrganizerId, organization_id AS OrganizationId
            FROM tournaments WHERE id = @tournamentId AND deleted_at IS NULL FOR UPDATE
            """, new { tournamentId }, tx);
        if (tournament is null) return Results.NotFound();
        var orgId = tournament.OrganizationId ?? request.OrgId;
        if (orgId is null) return Results.BadRequest(new { error = "Choose an organization you own to link this tournament." });
        if (tournament.OrganizationId.HasValue && request.OrgId.HasValue && request.OrgId != tournament.OrganizationId)
            return Results.Conflict(new { error = "This tournament is linked to a different organization." });
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, orgId.Value, user.UserIdGuid, tx)) return Results.StatusCode(403);
        if (tournament.OrganizationId is null)
        {
            if (tournament.OrganizerId != user.UserIdGuid) return Results.StatusCode(403);
            if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM broadcast_shows WHERE tournament_id = @tournamentId AND org_id <> @orgId)", new { tournamentId, orgId }, tx))
                return Results.Conflict(new { error = "Existing broadcasts belong to a different organization." });
            await conn.ExecuteAsync("UPDATE tournaments SET organization_id = @orgId, updated_at = NOW() WHERE id = @tournamentId", new { tournamentId, orgId }, tx);
        }
        var showId = await conn.QuerySingleAsync<Guid>("""
            INSERT INTO broadcast_shows (org_id, tournament_id, title, created_by)
            VALUES (@orgId, @tournamentId, @title, @userId) RETURNING id
            """, new { orgId, tournamentId, title = request.Title.Trim(), userId = user.UserIdGuid }, tx);
        tx.Commit();
        return Results.Ok(new { showId, orgId, tournamentId });
    }

    private sealed record TournamentScope(Guid Id, string Name, string Slug, string Game, Guid OrganizerId, Guid? OrganizationId, Guid? OrganizationOwnerId);
    private sealed record LockedTournament(Guid Id, Guid OrganizerId, Guid? OrganizationId);
    private sealed record OrganizationChoice(Guid Id, string Name);
    private sealed record TournamentShow(Guid Id, string Title, string Status, DateTime? ScheduledAt, DateTime? LastSyncedAt, long MatchCount);
}

public sealed record TournamentShowRequest(string? Title, Guid? OrgId = null);
