using System.Data;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Air;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Air access requests: a person whose Air access ended asks for it from inside Air (one open request
/// each), and a super admin grants (through the access grant) or declines it on the admin Air page.
/// Super admins can also open an account by email for someone who has never used Air.
/// </summary>
public static class AirAccessRequestEndpoints
{
    private const int NoteMax = 500;

    public static void MapAirAccessRequestEndpoints(this WebApplication app)
    {
        var mine = app.MapGroup("/api/air/access-request").RequireAuthorization("Authenticated").WithTags("Esportra Air");
        mine.MapGet("", Mine);
        mine.MapPost("", Ask);
        mine.MapDelete("", Withdraw);
        var admin = app.MapGroup("/api/admin/air").RequireAuthorization("Authenticated").WithTags("Air super admin");
        admin.MapPost("/requests/{requestId:guid}/decline", Decline);
        admin.MapPost("/customers/by-email", ByEmail);
    }

    private static bool Admin(HttpContext context) => context.Items["UserContext"] is UserContext { IsSuperAdmin: true };
    private static IResult Forbidden() => Results.Json(new { error = "Esportra Air super-admin access is required." }, statusCode: 403);

    private static Task<AirAccessRequestView?> Latest(IDbConnection connection, Guid userId) =>
        connection.QuerySingleOrDefaultAsync<AirAccessRequestView>(
            "SELECT id,note,status,reason,created_at AS CreatedAt,decided_at AS DecidedAt FROM air_access_requests WHERE user_id=@userId ORDER BY created_at DESC LIMIT 1",
            new { userId });

    private static async Task<IResult> Mine(HttpContext ctx, IDbConnectionFactory db)
    {
        var userId = AirAccountEndpoints.User(ctx);
        if (userId == Guid.Empty) return Results.Unauthorized();
        using var connection = db.CreateConnection();
        return Results.Ok(new { request = await Latest(connection, userId) });
    }

    private static async Task<IResult> Ask(AirAccessRequestInput input, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        var userId = AirAccountEndpoints.User(ctx);
        if (userId == Guid.Empty) return Results.Unauthorized();
        var note = (input?.Note ?? "").Trim();
        if (note.Length > NoteMax) return Results.BadRequest(new { error = $"Keep the note under {NoteMax} characters." });
        using var connection = db.CreateConnection();
        await accounts.EnsureAsync(connection, userId);
        if (await accounts.HasAccessAsync(connection, userId)) return Results.Conflict(new { error = "You already have Air access. Press Check again in Air." });
        var last = await Latest(connection, userId);
        if (last is { Status: "declined" } && last.DecidedAt > DateTime.UtcNow.AddHours(-24))
            return Results.Json(new { error = "Your last request was declined. You can ask again 24 hours after it." }, statusCode: 429);
        var id = await connection.ExecuteScalarAsync<Guid>("""
            INSERT INTO air_access_requests(user_id,note) VALUES(@userId,@note)
            ON CONFLICT (user_id) WHERE status='pending' DO UPDATE SET note=excluded.note,updated_at=now()
            RETURNING id
            """, new { userId, note });
        return Results.Ok(new { request = await Latest(connection, userId), id });
    }

    private static async Task<IResult> Withdraw(HttpContext ctx, IDbConnectionFactory db)
    {
        var userId = AirAccountEndpoints.User(ctx);
        if (userId == Guid.Empty) return Results.Unauthorized();
        using var connection = db.CreateConnection();
        var changed = await connection.ExecuteAsync("UPDATE air_access_requests SET status='withdrawn',updated_at=now() WHERE user_id=@userId AND status='pending'", new { userId });
        return changed == 1 ? Results.Ok(new { withdrawn = true }) : Results.NotFound(new { error = "There is no open request." });
    }

    private static async Task<IResult> Decline(Guid requestId, AirReasonRequest request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (!Admin(ctx)) return Forbidden();
        var reason = (request?.Reason ?? "").Trim();
        if (reason.Length is < 1 or > NoteMax) return Results.BadRequest(new { error = "Give a reason the person will see (up to 500 characters)." });
        using var connection = db.CreateConnection();
        if (connection.State != ConnectionState.Open) connection.Open();
        using var tx = connection.BeginTransaction();
        var actor = AirAccountEndpoints.User(ctx);
        var changed = await connection.ExecuteAsync(
            "UPDATE air_access_requests SET status='declined',reason=@reason,decided_by=@actor,decided_at=now(),updated_at=now() WHERE id=@requestId AND status='pending'",
            new { requestId, reason, actor }, tx);
        if (changed == 0) return Results.NotFound(new { error = "That request is no longer open." });
        await connection.ExecuteAsync("INSERT INTO air_admin_audit(actor_id,action,subject_id,reason) VALUES(@actor,'request_declined',@subject,@reason)", new { actor, subject = requestId.ToString(), reason }, tx);
        tx.Commit();
        return Results.Ok(new { declined = true });
    }

    /// <summary>Finds a person by their account email and makes sure they have an Air account, so access can be granted before they first open Air.</summary>
    private static async Task<IResult> ByEmail(AirCustomerByEmail input, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        if (!Admin(ctx)) return Forbidden();
        var email = (input?.Email ?? "").Trim().ToLowerInvariant();
        if (email.Length is < 3 or > 254 || !email.Contains('@')) return Results.BadRequest(new { error = "Enter the email the person signs in to Esportra with." });
        using var connection = db.CreateConnection();
        var userId = await connection.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT u.id FROM auth.users u JOIN profiles p ON p.id=u.id WHERE lower(u.email)=@email LIMIT 1", new { email });
        if (userId is null) return Results.NotFound(new { error = "No Esportra account uses that email. They need to sign up first." });
        await accounts.EnsureAsync(connection, userId.Value);
        return Results.Ok(new { userId });
    }

    /// <summary>When access is granted, the person's open request is marked granted.</summary>
    internal static Task MarkGranted(IDbConnection connection, IDbTransaction tx, Guid userId, Guid actor) =>
        connection.ExecuteAsync(
            "UPDATE air_access_requests SET status='granted',decided_by=@actor,decided_at=now(),updated_at=now() WHERE user_id=@userId AND status='pending'",
            new { userId, actor }, tx);
}
