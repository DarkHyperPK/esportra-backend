using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Middleware;
using Esportra.Api.Services;
using Esportra.Core.Air;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Endpoints;

public static partial class AirBroadcastEndpoints
{
    private const string ScopeSql = """
        (t.organizer_id=@userId OR o.owner_id=@userId OR EXISTS(SELECT 1 FROM air_match_crew ac WHERE ac.match_id=bm.id AND ac.recipient_id=@userId AND ac.status='accepted' AND ac.role IN ('producer','technical_director')) OR EXISTS (
          SELECT 1 FROM broadcast_show_matches sm
          JOIN broadcast_shows bs ON bs.id=sm.show_id AND bs.org_id=t.organization_id
          JOIN broadcast_crew c ON c.show_id=bs.id
          WHERE sm.match_id=bm.id AND (bs.tournament_id IS NULL OR bs.tournament_id=t.id)
            AND c.user_id=@userId AND c.is_active AND c.joined_at IS NOT NULL
            AND c.role IN ('producer','technical_director') AND bs.status NOT IN ('ended','cancelled')))
        """;
    private const string MatchSql = """
        SELECT bm.id AS Id,t.id AS TournamentId,t.name AS TournamentName,t.game AS Game,
          bm.status,bm.best_of AS BestOf,bm.scheduled_time AS ScheduledAt,
          bm.team1_id AS Team1Id,bm.team2_id AS Team2Id,
          COALESCE(t1.name,tp1.team_name,sp1.username) AS TeamA,
          COALESCE(t2.name,tp2.team_name,sp2.username) AS TeamB,
          COALESCE(t1.tag,'') AS TagA,COALESCE(t2.tag,'') AS TagB,
          COALESCE(t1.logo_url,sp1.avatar_url) AS LogoA,COALESCE(t2.logo_url,sp2.avatar_url) AS LogoB,
          COALESCE(bm.team1_score,0) AS ScoreA,COALESCE(bm.team2_score,0) AS ScoreB
        FROM brkt_matches bm JOIN brkt_versions bv ON bv.id=bm.version_id
        JOIN tournaments t ON t.id=bv.tournament_id
        LEFT JOIN organizations o ON o.id=t.organization_id
        """;
    private const string EligibleSql = """
        t.deleted_at IS NULL AND bv.status='active'
        AND bm.status IN ('pending','scheduled','in_progress') AND bm.winner_id IS NULL
        AND bm.team1_id IS NOT NULL AND bm.team2_id IS NOT NULL
        AND NULLIF(BTRIM(COALESCE(t1.name,tp1.team_name,sp1.username)),'') IS NOT NULL
        AND NULLIF(BTRIM(COALESCE(t2.name,tp2.team_name,sp2.username)),'') IS NOT NULL
        """;
    public static void MapAirBroadcastEndpoints(this WebApplication app)
    {
        app.MapGet("/api/air/broadcast/saved-records", SavedAirRecords).RequireAuthorization("Authenticated");
        var routes = app.MapGroup("/api/air/broadcast").RequireAuthorization("Authenticated")
            .WithMetadata(new RateLimitPolicyMetadata("fixed"));
        routes.MapGet("/matches", List);
        routes.MapGet("/invitations", AirInvitations);
        routes.MapPost("/invitations/{inviteId:guid}/accept", AcceptAirCrew);
        routes.MapGet("/matches/{matchId:guid}/crew", ListAirCrew);
        routes.MapPost("/matches/{matchId:guid}/crew/invitations", InviteAirCrew);
        routes.MapDelete("/matches/{matchId:guid}/crew/{inviteId:guid}", RevokeAirCrew);
        routes.MapPost("/matches/{matchId:guid}/prepare", Prepare);
        routes.MapGet("/matches/{matchId:guid}/draft", GetDraft);
        routes.MapPut("/matches/{matchId:guid}/draft", SaveDraft);
        routes.MapGet("/matches/{matchId:guid}/bracket", GetBracket);
        routes.MapGet("/matches/{matchId:guid}/roster", GetRoster);
        routes.MapPost("/packages/{packageId:guid}/events", SavePackageEvents);
        routes.MapGet("/packages/{packageId:guid}/activity", PackageActivity);
        routes.MapGet("/packages/{packageId:guid}/result-proposals", ListAirResults);
        routes.MapPost("/packages/{packageId:guid}/result-proposals", ProposeAirResult);
        routes.MapPost("/packages/{packageId:guid}/result-proposals/{proposalId:guid}/review", ReviewAirResult);
        routes.MapGet("/packages/{packageId:guid}/versions/{version:int}", ReadVersion);
    }
    private static async Task<bool> AuthorizedDevice(IDbConnection conn, HttpContext ctx, AirAccountService accounts)
    {
        var userId = AirAccountEndpoints.User(ctx);
        return userId != Guid.Empty && Guid.TryParse(ctx.Request.Headers["X-Air-Device-Id"], out var deviceId)
            && await accounts.HasAccessAsync(conn, userId)
            && await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM air_devices WHERE id=@deviceId AND user_id=@userId AND status='active' AND mode='production')", new { deviceId, userId });
    }
    private static async Task<AirMatch[]> Matches(IDbConnection conn, Guid userId, Guid? matchId = null, IDbTransaction? tx = null, bool history = false)
    {
        var sql = MatchSql + "\n" + BracketTeamResolutionSql.BracketMatchTeam1Joins + "\n" + BracketTeamResolutionSql.BracketMatchTeam2Joins
            + $" WHERE {ScopeSql} AND {(history ? "t.deleted_at IS NULL" : EligibleSql)} AND (@matchId IS NULL OR bm.id=@matchId) ORDER BY bm.scheduled_time NULLS LAST,bm.id LIMIT 500";
        return (await conn.QueryAsync<AirMatch>(sql, new { userId, matchId }, tx)).ToArray();
    }
    private static async Task<IResult> List(HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Json(new { error = "An active Air account and registered production PC are required. Check access in Settings & help." }, statusCode: 403);
        var matches = await Matches(conn, AirAccountEndpoints.User(ctx));
        return Results.Ok(new { matches = matches.Select(m => new { m.Id, m.TournamentId, m.TournamentName, m.Game, m.TeamA, m.TeamB, m.TagA, m.TagB, m.LogoA, m.LogoB, m.ScheduledAt, m.Status, m.BestOf, packId = AirGamePacks.Resolve(m.Game), supported = AirGamePacks.Resolve(m.Game) is not null }) });
    }
    private static async Task<IResult> GetRoster(Guid matchId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts)) return Results.Json(new { error = "An active Air account and registered production PC are required." }, statusCode: 403);
        var userId = AirAccountEndpoints.User(ctx);
        var matches = await Matches(conn, userId, matchId);
        if (matches.Length == 0) return Results.NotFound();
        var match = matches[0];

        var players = await conn.QueryAsync<AirRosterPlayer>("""
            SELECT tp.id AS ParticipantId,
                   COALESCE(t.id, tp.id) AS TeamId,
                   p.id AS UserId,
                   COALESCE(p.username, tp.team_name, 'Unknown') AS DisplayName,
                   p.avatar_url AS AvatarUrl,
                   tp.role AS Role
            FROM tournament_participants tp
            LEFT JOIN profiles p ON p.id = tp.user_id
            LEFT JOIN teams t ON t.id = tp.team_id
            WHERE tp.tournament_id = @tournamentId
              AND tp.status IN ('approved', 'confirmed')
              AND (tp.team_id = @team1Id OR tp.team_id = @team2Id
                   OR (tp.team_id IS NULL AND tp.id = @team1Id)
                   OR (tp.team_id IS NULL AND tp.id = @team2Id))
            ORDER BY tp.team_id NULLS LAST, tp.created_at
            LIMIT 50
            """, new { tournamentId = match.TournamentId, team1Id = match.Team1Id, team2Id = match.Team2Id });

        var byTeam = players.GroupBy(p => p.TeamId).ToDictionary(g => g.Key, g => g.ToArray());
        return Results.Ok(new
        {
            matchId,
            team1 = new { id = match.Team1Id, name = match.TeamA, tag = match.TagA, logo = match.LogoA, players = byTeam.GetValueOrDefault(match.Team1Id, []) },
            team2 = new { id = match.Team2Id, name = match.TeamB, tag = match.TagB, logo = match.LogoB, players = byTeam.GetValueOrDefault(match.Team2Id, []) },
        });
    }

    private sealed record AirRosterPlayer(Guid ParticipantId, Guid TeamId, Guid? UserId, string DisplayName, string? AvatarUrl, string? Role);
    private sealed record AirMatch(Guid Id, Guid TournamentId, string TournamentName, string Game, string Status,
        int BestOf, DateTime? ScheduledAt, Guid Team1Id, Guid Team2Id, string TeamA, string TeamB,
        string TagA, string TagB, string? LogoA, string? LogoB, int ScoreA, int ScoreB);
}
