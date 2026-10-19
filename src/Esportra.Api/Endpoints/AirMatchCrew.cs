using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

public static partial class AirBroadcastEndpoints
{
    private static Task<bool> ManagesAirMatch(System.Data.IDbConnection conn, Guid matchId, Guid userId) =>
        conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM brkt_matches m JOIN brkt_versions v ON v.id=m.version_id JOIN tournaments t ON t.id=v.tournament_id LEFT JOIN organizations o ON o.id=t.organization_id WHERE m.id=@matchId AND t.deleted_at IS NULL AND (t.organizer_id=@userId OR o.owner_id=@userId))", new { matchId, userId });

    private static async Task<IResult> AirInvitations(HttpContext ctx, IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        return Results.Ok(await conn.QueryAsync<AirCrewInvitation>("SELECT c.id,c.match_id AS matchId,c.role,t.name AS showTitle,c.expires_at AS expiresAt FROM air_match_crew c JOIN brkt_matches m ON m.id=c.match_id JOIN brkt_versions v ON v.id=m.version_id JOIN tournaments t ON t.id=v.tournament_id WHERE c.recipient_id=@userId AND c.status='pending' AND c.expires_at>now() AND t.deleted_at IS NULL ORDER BY c.created_at DESC LIMIT 100", new { userId = AirAccountEndpoints.User(ctx) }));
    }

    private static async Task<IResult> InviteAirCrew(Guid matchId, AirCrewInvite request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        if (request.Role is not ("producer" or "technical_director" or "observer" or "monitor" or "replay_operator") || string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254)
            return Results.BadRequest(new { error = "Choose an account email and production responsibility." });
        using var conn = db.CreateConnection();
        if (!await AuthorizedDevice(conn, ctx, accounts) || !await ManagesAirMatch(conn, matchId, AirAccountEndpoints.User(ctx))) return Results.Forbid();
        var recipient = await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM auth.users WHERE lower(email)=@email AND email_confirmed_at IS NOT NULL LIMIT 1", new { email = request.Email.Trim().ToLowerInvariant() });
        if (!recipient.HasValue) return Results.BadRequest(new { error = "Ask this teammate to create and verify their Esportra account first." });
        var id = await conn.ExecuteScalarAsync<Guid?>("INSERT INTO air_match_crew(match_id,invited_by,recipient_id,role,can_submit_result) VALUES(@matchId,@userId,@recipient,@role,@canReview) ON CONFLICT(match_id,recipient_id) DO UPDATE SET role=excluded.role,can_submit_result=excluded.can_submit_result,status='pending',expires_at=now()+interval '72 hours' WHERE air_match_crew.status='revoked' OR (air_match_crew.status='pending' AND air_match_crew.expires_at<now()) RETURNING id", new { matchId, userId = AirAccountEndpoints.User(ctx), recipient, request.Role, canReview = request.CanReviewResults });
        return id.HasValue ? Results.Ok(new { id, status = "pending" }) : Results.Conflict(new { error = "This teammate already has an active invitation or assignment. Revoke it before changing access." });
    }

    private static async Task<IResult> AcceptAirCrew(Guid inviteId, HttpContext ctx, IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        var match = await conn.ExecuteScalarAsync<Guid?>("UPDATE air_match_crew SET status='accepted' WHERE id=@inviteId AND recipient_id=@userId AND status='pending' AND expires_at>now() AND EXISTS(SELECT 1 FROM auth.users WHERE id=@userId AND email_confirmed_at IS NOT NULL) RETURNING match_id", new { inviteId, userId = AirAccountEndpoints.User(ctx) });
        return match.HasValue ? Results.Ok(new { matchId = match.Value, status = "accepted" }) : Results.NotFound(new { error = "This invitation expired, was revoked or belongs to another account." });
    }

    private static async Task<IResult> ListAirCrew(Guid matchId, HttpContext ctx, IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        if (!await ManagesAirMatch(conn, matchId, AirAccountEndpoints.User(ctx))) return Results.Forbid();
        return Results.Ok(await conn.QueryAsync("SELECT id,recipient_id AS recipientId,role,status,can_submit_result AS canReviewResults,expires_at AS expiresAt FROM air_match_crew WHERE match_id=@matchId ORDER BY created_at DESC LIMIT 100", new { matchId }));
    }

    private static async Task<IResult> RevokeAirCrew(Guid matchId, Guid inviteId, HttpContext ctx, IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        if (!await ManagesAirMatch(conn, matchId, AirAccountEndpoints.User(ctx))) return Results.Forbid();
        await conn.ExecuteAsync("UPDATE air_match_crew SET status='revoked' WHERE id=@inviteId AND match_id=@matchId", new { inviteId, matchId });
        return Results.Ok(new { revoked = true });
    }
}

public sealed record AirCrewInvite(string Email, string Role, bool CanReviewResults = false);

public sealed record AirCrewInvitation(Guid Id, Guid MatchId, string Role, string ShowTitle, DateTime ExpiresAt);
