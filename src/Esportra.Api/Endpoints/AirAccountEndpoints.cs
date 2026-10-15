using System.Data;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Air;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Air;

namespace Esportra.Api.Endpoints;

public static class AirAccountEndpoints
{
    public static void MapAirAccountEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/air").RequireAuthorization("Authenticated").WithTags("Esportra Air");
        routes.MapPost("/session", OpenSession);
        routes.MapPost("/access", CheckAccess);
        routes.MapGet("/workspaces", ListWorkspaces);
        routes.MapPost("/workspaces", CreateWorkspace);
        routes.MapPut("/workspaces/{workspaceId:guid}", RenameWorkspace);
        routes.MapDelete("/workspaces/{workspaceId:guid}", DeleteWorkspace);
        routes.MapPost("/devices", RegisterDevice);
        routes.MapPost("/devices/{deviceId:guid}/heartbeat", Heartbeat);
    }
    internal static Guid User(HttpContext context) => context.Items["UserContext"] is UserContext user ? user.UserIdGuid : Guid.Empty;
    private static async Task<IResult> OpenSession(HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        var userId = User(ctx); if (userId == Guid.Empty) return Results.Unauthorized();
        using var connection = db.CreateConnection(); await accounts.EnsureAsync(connection, userId);
        var account = await connection.QuerySingleAsync<AirAccountSummary>("SELECT user_id AS UserId,trial_started_at AS TrialStartedAt,trial_ends_at AS TrialEndsAt,valid_until AS ValidUntil,status AS Status,plan_id AS PlanId FROM air_accounts WHERE user_id=@userId", new { userId });
        return Results.Ok(new { userId, account, workspaces = await connection.QueryAsync<AirWorkspaceView>("SELECT id,name,created_at AS CreatedAt FROM air_workspaces WHERE owner_id=@userId ORDER BY created_at", new { userId }) });
    }
    private static async Task<IResult> ListWorkspaces(HttpContext ctx, IDbConnectionFactory db)
    {
        using var connection = db.CreateConnection(); return Results.Ok(await connection.QueryAsync<AirWorkspaceView>("SELECT id,name,created_at AS CreatedAt FROM air_workspaces WHERE owner_id=@userId ORDER BY created_at", new { userId = User(ctx) }));
    }
    private static async Task<IResult> CreateWorkspace(AirWorkspaceRequest request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        try { AirProjectRules.Text(request.Name, 120, "Workspace name"); } catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        using var connection = db.CreateConnection(); var userId = User(ctx);
        await accounts.EnsureAsync(connection, userId);
        if (!await accounts.HasAccessAsync(connection, userId)) return Results.Json(new { error = "Renew Air access before creating a workspace." }, statusCode: 403);
        var id = await connection.ExecuteScalarAsync<Guid>("INSERT INTO air_workspaces(owner_id,name) SELECT @userId,@name WHERE (SELECT count(*) FROM air_workspaces WHERE owner_id=@userId)<20 RETURNING id", new { userId, name = request.Name.Trim() });
        return id == Guid.Empty ? Results.Conflict(new { error = "A personal account supports up to 20 workspaces." }) : Results.Ok(new { id, name = request.Name.Trim() });
    }
    private static async Task<IResult> RegisterDevice(AirDeviceRequest request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        if (request.DeviceId == Guid.Empty || request.Mode is not ("production" or "capture-only")) return Results.BadRequest(new { error = "Choose a valid device and PC role." });
        try { AirProjectRules.Text(request.Name, 80, "PC name"); } catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        using var connection = db.CreateConnection(); var userId = User(ctx); await accounts.EnsureAsync(connection, userId);
        if (!await accounts.HasAccessAsync(connection, userId)) return Results.Json(new { error = "Air access has expired or was suspended." }, statusCode: 403);
        using var tx = connection.BeginTransaction();
        var account = await connection.QuerySingleAsync<DeviceLimits>("SELECT a.user_id AS UserId,CASE WHEN a.override_device_limits THEN a.production_limit ELSE p.production_limit END AS ProductionLimit,CASE WHEN a.override_device_limits THEN a.capture_limit ELSE p.capture_limit END AS CaptureLimit FROM air_accounts a JOIN air_plans p ON p.id=a.plan_id WHERE a.user_id=@userId FOR UPDATE OF a", new { userId }, tx);
        var existing = await connection.QuerySingleOrDefaultAsync<DeviceOwner>("SELECT user_id AS UserId,status AS Status FROM air_devices WHERE id=@id", new { id = request.DeviceId }, tx);
        if (existing is not null && (existing.UserId != userId || existing.Status != "active")) return Results.Json(new { error = "This device is revoked or belongs to another account." }, statusCode: 403);
        var limit = request.Mode == "production" ? account.ProductionLimit : account.CaptureLimit;
        var used = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM air_devices WHERE user_id=@userId AND mode=@mode AND status='active' AND id<>@id", new { userId, mode = request.Mode, id = request.DeviceId }, tx);
        if (limit.HasValue && used >= limit.Value) return Results.Conflict(new { error = "Your plan's device allowance is full. Ask your admin to update access." });
        await connection.ExecuteAsync("INSERT INTO air_devices(id,user_id,name,mode) VALUES(@id,@userId,@name,@mode) ON CONFLICT(id) DO UPDATE SET name=excluded.name,mode=excluded.mode,last_seen_at=now()", new { id = request.DeviceId, userId, name = request.Name.Trim(), mode = request.Mode }, tx);
        tx.Commit(); return Results.Ok(new { deviceId = request.DeviceId });
    }
    private static async Task<IResult> CheckAccess(AirAccessRequest request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts, AirAccessSigner signer)
    {
        using var connection = db.CreateConnection(); var userId = User(ctx);
        if (!await accounts.HasAccessAsync(connection, userId)) return Results.Json(new { error = "Air access expired or was suspended. Existing production must finish before starting another match." }, statusCode: 403);
        var access = await connection.QuerySingleOrDefaultAsync<AccessState>("SELECT a.valid_until AS ValidUntil,a.revision AS AccountRevision,d.revision AS DeviceRevision FROM air_accounts a JOIN air_devices d ON d.user_id=a.user_id WHERE a.user_id=@userId AND d.id=@id AND d.status='active'", new { userId, id = request.DeviceId });
        if (access is null) return Results.Json(new { error = "Register an active device before requesting access." }, statusCode: 403);
        var lease = signer.Sign(userId, request.DeviceId, access.ValidUntil, access.AccountRevision, access.DeviceRevision);
        return Results.Ok(new { allowed = true, serverTime = DateTimeOffset.UtcNow, validUntil = access.ValidUntil, offlineLease = lease, offlineWarning = lease is null ? "Offline access is unavailable until the server signing key is configured." : null });
    }
    private static async Task<IResult> Heartbeat(Guid deviceId, AirDeviceHealth request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (request.AppVersion?.Length > 40 || request.FeedState is not (null or "waiting" or "live" or "stale" or "offline")) return Results.BadRequest();
        using var connection = db.CreateConnection();
        var updated = await connection.ExecuteAsync("UPDATE air_devices SET last_seen_at=now(),health=CAST(@health AS jsonb) WHERE id=@deviceId AND user_id=@userId AND status='active'", new { deviceId, userId = User(ctx), health = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) });
        return updated == 1 ? Results.Ok(new { acknowledged = true }) : Results.Json(new { error = "Device access was revoked." }, statusCode: 403);
    }
    private static async Task<IResult> RenameWorkspace(Guid workspaceId, AirWorkspaceRequest request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        try { AirProjectRules.Text(request.Name, 120, "Workspace name"); } catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        using var connection = db.CreateConnection(); var userId = User(ctx);
        if (!await accounts.OwnsWorkspaceAsync(connection, workspaceId, userId)) return Results.NotFound();
        if (!await accounts.HasAccessAsync(connection, userId)) return Results.Json(new { error = "Active Air access is required." }, statusCode: 403);
        await connection.ExecuteAsync("UPDATE air_workspaces SET name=@name WHERE id=@workspaceId AND owner_id=@userId", new { name = request.Name.Trim(), workspaceId, userId });
        return Results.Ok(new { id = workspaceId, name = request.Name.Trim() });
    }
    private static async Task<IResult> DeleteWorkspace(Guid workspaceId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var connection = db.CreateConnection(); var userId = User(ctx);
        if (!await accounts.OwnsWorkspaceAsync(connection, workspaceId, userId)) return Results.NotFound();
        if (!await accounts.HasAccessAsync(connection, userId)) return Results.Json(new { error = "Active Air access is required." }, statusCode: 403);
        var changed = await connection.ExecuteAsync("DELETE FROM air_workspaces WHERE id=@workspaceId AND owner_id=@userId AND NOT EXISTS(SELECT 1 FROM air_projects WHERE workspace_id=@workspaceId) AND NOT EXISTS(SELECT 1 FROM air_assets WHERE workspace_id=@workspaceId) AND (SELECT count(*) FROM air_workspaces WHERE owner_id=@userId)>1", new { workspaceId, userId });
        return changed == 1 ? Results.Ok(new { deleted = true }) : Results.Conflict(new { error = "Only empty workspaces can be deleted. Keep at least one workspace; project history and assets are retained." });
    }
    private sealed record DeviceLimits(Guid UserId, int? ProductionLimit, int? CaptureLimit);
    private sealed record DeviceOwner(Guid UserId, string Status);
    private sealed record AccessState(DateTime? ValidUntil, int AccountRevision, int DeviceRevision);
}
public sealed record AirDeviceHealth(string? AppVersion, bool ObsConnected, bool CaptureActive, bool ProductionActive, string? FeedState, Guid? ProjectId);
