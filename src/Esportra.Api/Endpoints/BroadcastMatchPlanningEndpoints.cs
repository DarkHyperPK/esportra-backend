using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Core.Tournaments;
using Esportra.Api.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static partial class BroadcastMatchPlanningEndpoints
{
    public static void MapBroadcastMatchPlanningEndpoints(this WebApplication app)
    {
        app.MapGet("/api/broadcast/shows/{showId:guid}/available-matches", ListAvailable).RequireAuthorization("Authenticated");
        app.MapPatch("/api/broadcast/shows/{showId:guid}/matches/{matchId:guid}", UpdateCode)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
    }

    private static async Task<IResult> ListAvailable(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<ShowScope>("SELECT org_id AS OrgId, tournament_id AS TournamentId, status AS Status FROM broadcast_shows WHERE id = @showId", new { showId });
        if (show is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid)) return Results.StatusCode(403);
        if (show.TournamentId is null) return Results.Ok(Array.Empty<MatchChoice>());
        var matches = await conn.QueryAsync<MatchChoice>($"""
            SELECT bm.id AS Id, COALESCE(t1.name, tp1.team_name, sp1.username, 'TBD') AS TeamA,
                   COALESCE(t2.name, tp2.team_name, sp2.username, 'TBD') AS TeamB,
                   bm.status AS Status, bm.scheduled_time AS ScheduledAt, t.name AS TournamentName
            FROM brkt_matches bm JOIN brkt_versions v ON v.id = bm.version_id
            JOIN tournaments t ON t.id = v.tournament_id
            {BracketTeamResolutionSql.BracketMatchTeam1Joins}
            {BracketTeamResolutionSql.BracketMatchTeam2Joins}
            WHERE v.tournament_id = @tournamentId AND t.organization_id = @orgId AND t.deleted_at IS NULL
              AND v.status IN ('published', 'active')
              AND bm.status IN ('pending', 'scheduled', 'in_progress')
              AND bm.winner_id IS NULL AND bm.team1_id IS NOT NULL AND bm.team2_id IS NOT NULL
              AND NULLIF(BTRIM(COALESCE(t1.name, tp1.team_name, sp1.username)), '') IS NOT NULL
              AND NULLIF(BTRIM(COALESCE(t2.name, tp2.team_name, sp2.username)), '') IS NOT NULL
              AND NOT EXISTS
              (SELECT 1 FROM broadcast_show_matches sm WHERE sm.show_id = @showId AND sm.match_id = bm.id)
            ORDER BY bm.scheduled_time NULLS LAST, bm.id LIMIT 200
            """, new { showId, tournamentId = show.TournamentId, orgId = show.OrgId });
        return Results.Ok(matches);
    }

    private static async Task<IResult> UpdateCode(Guid showId, Guid matchId, [FromBody] CodeChange input, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (input.MatchCode is null || !MatchCodePattern().IsMatch(input.MatchCode)) return Results.BadRequest(new { error = "Use 1–20 letters, digits, underscores or hyphens." });
        var code = input.MatchCode.ToUpperInvariant();
        using var conn = db.CreateConnection();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var show = await conn.QuerySingleOrDefaultAsync<ShowScope>("SELECT org_id AS OrgId, tournament_id AS TournamentId, status AS Status FROM broadcast_shows WHERE id = @showId FOR UPDATE", new { showId }, tx);
        if (show is null) return Results.NotFound();
        if (!await BroadcastHelpers.IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid, tx)) return Results.StatusCode(403);
        if (show.Status is "live" or "ended" or "cancelled") return Results.Conflict(new { error = "Match codes are locked during and after a live show." });
        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM broadcast_show_matches WHERE show_id = @showId AND match_id <> @matchId AND UPPER(match_code) = @code)", new { showId, matchId, code }, tx)) return Results.Conflict(new { error = "Match code is already used in this show." });
        var count = await conn.ExecuteAsync("UPDATE broadcast_show_matches SET match_code = @code WHERE show_id = @showId AND match_id = @matchId", new { showId, matchId, code }, tx);
        tx.Commit();
        return count == 1 ? Results.Ok(new { ok = true }) : Results.NotFound();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex MatchCodePattern();
    public sealed record CodeChange(string? MatchCode);
    private sealed record ShowScope(Guid OrgId, Guid? TournamentId, string Status);
    private sealed record MatchChoice(Guid Id, string TeamA, string TeamB, string Status, DateTime? ScheduledAt, string TournamentName);
}
