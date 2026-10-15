using System.Data;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Air;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Air;

namespace Esportra.Api.Endpoints;

public static class AirAdminEndpoints
{
    public static void MapAirAdminEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/admin/air").RequireAuthorization("Authenticated").WithTags("Air super admin");
        routes.MapGet("/", Overview);
        routes.MapGet("/{section}", List);
        routes.MapPost("/plans", CreatePlan);
        routes.MapPut("/plans/{planId:guid}", UpdatePlan);
        routes.MapPut("/customers/{userId:guid}/access", Grant);
        routes.MapPost("/devices/{deviceId:guid}/revoke", RevokeDevice);
        routes.MapPost("/releases", CreateRelease);
        routes.MapPut("/releases/{releaseId:guid}", UpdateRelease);
        app.MapGet("/api/air/releases", async (HttpContext ctx, IDbConnectionFactory db) =>
        {
            using var connection = db.CreateConnection();
            return Results.Ok(await connection.QueryAsync<AirReleaseView>("SELECT id,version,channel,download_url AS DownloadUrl,notes,published,created_at AS CreatedAt FROM air_releases WHERE published ORDER BY created_at DESC LIMIT 20"));
        }).RequireAuthorization("Authenticated");
    }
    private static bool Admin(HttpContext context) => context.Items["UserContext"] is UserContext { IsSuperAdmin: true };
    private static IResult Forbidden() => Results.Json(new { error = "Esportra Air super-admin access is required." }, statusCode: 403);
    private static async Task<IResult> Overview(HttpContext ctx, IDbConnectionFactory db, IConfiguration configuration)
    {
        if (!Admin(ctx)) return Forbidden(); using var connection = db.CreateConnection();
        var counts = await connection.QuerySingleAsync<AirAdminCounts>("SELECT (SELECT count(*) FROM air_accounts) AS Customers,(SELECT count(*) FROM air_workspaces) AS Workspaces,(SELECT count(*) FROM air_devices WHERE status='active') AS ActiveDevices,(SELECT count(*) FROM air_devices WHERE status='active' AND last_seen_at>now()-interval '90 seconds') AS OnlineDevices,(SELECT count(*) FROM air_accounts WHERE status='active' AND valid_until<=now()) AS ExpiredAccess");
        return Results.Ok(new { counts, offlineSigningConfigured = !string.IsNullOrWhiteSpace(configuration["AirAccess:SigningPrivateKey"]), paymentsEnabled = false });
    }
    private static async Task<IResult> List(string section, int? page, HttpContext ctx, IDbConnectionFactory db)
    {
        if (!Admin(ctx)) return Forbidden(); var number = page ?? 1;
        if (number is < 1 or > 10000) return Results.BadRequest();
        var sql = section switch
        {
            "customers" => "SELECT a.user_id AS UserId,a.status,a.trial_started_at AS TrialStartedAt,a.trial_ends_at AS TrialEndsAt,a.valid_until AS ValidUntil,a.plan_id AS PlanId,p.name AS Plan,l.license_id AS LicenseCode,a.revision,a.override_device_limits AS OverrideDeviceLimits,a.production_limit AS ProductionLimit,a.capture_limit AS CaptureLimit FROM air_accounts a JOIN air_plans p ON p.id=a.plan_id JOIN licenses l ON l.id=a.license_id ORDER BY a.trial_started_at DESC LIMIT 50 OFFSET @offset",
            "workspaces" => "SELECT w.id,w.name,w.owner_id AS OwnerId,w.created_at AS CreatedAt,(SELECT count(*) FROM air_projects p WHERE p.workspace_id=w.id AND p.archived_at IS NULL) AS Projects FROM air_workspaces w ORDER BY created_at DESC LIMIT 50 OFFSET @offset",
            "plans" => "SELECT id,name,production_limit AS ProductionLimit,capture_limit AS CaptureLimit,enabled FROM air_plans ORDER BY name LIMIT 50 OFFSET @offset",
            "devices" => "SELECT id,user_id AS UserId,name,mode,status,last_seen_at AS LastSeenAt,health,revision FROM air_devices ORDER BY last_seen_at DESC LIMIT 50 OFFSET @offset",
            "activity" => "SELECT id,actor_id AS ActorId,action,subject_id AS SubjectId,reason,created_at AS CreatedAt FROM air_admin_audit ORDER BY id DESC LIMIT 50 OFFSET @offset",
            "releases" => "SELECT id,version,channel,download_url AS DownloadUrl,notes,published,created_at AS CreatedAt FROM air_releases ORDER BY created_at DESC LIMIT 50 OFFSET @offset",
            _ => null
        };
        if (sql is null) return Results.NotFound(); using var connection = db.CreateConnection();
        var type = section switch { "customers" => typeof(AirAdminCustomer), "workspaces" => typeof(AirAdminWorkspace), "plans" => typeof(AirPlanView), "devices" => typeof(AirDeviceView), "activity" => typeof(AirAdminActivity), _ => typeof(AirReleaseView) };
        var items = (await connection.QueryAsync(type, sql, new { offset = (number - 1) * 50 })).ToArray();
        return Results.Ok(new { items, page = number, hasMore = items.Length == 50 });
    }
    private static async Task<IResult> CreatePlan(AirPlanRequest request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (!Admin(ctx)) return Forbidden(); var error = PlanError(request); if (error is not null) return error;
        using var connection = db.CreateConnection(); if (connection.State != System.Data.ConnectionState.Open) connection.Open(); using var tx = connection.BeginTransaction();
        var id = await connection.ExecuteScalarAsync<Guid>("INSERT INTO air_plans(name,production_limit,capture_limit,enabled) VALUES(@Name,@ProductionLimit,@CaptureLimit,@Enabled) RETURNING id", request, tx);
        await Audit(connection, tx, ctx, "plan_created", id, request.Name); tx.Commit(); return Results.Ok(new { id });
    }
    private static async Task<IResult> UpdatePlan(Guid planId, AirPlanRequest request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (!Admin(ctx)) return Forbidden(); var error = PlanError(request); if (error is not null) return error;
        using var connection = db.CreateConnection(); if (connection.State != System.Data.ConnectionState.Open) connection.Open(); using var tx = connection.BeginTransaction();
        var changed = await connection.ExecuteAsync("UPDATE air_plans SET name=@Name,production_limit=@ProductionLimit,capture_limit=@CaptureLimit,enabled=@Enabled WHERE id=@id", new { request.Name, request.ProductionLimit, request.CaptureLimit, request.Enabled, id = planId }, tx);
        if (changed == 0) return Results.NotFound();
        await connection.ExecuteAsync("UPDATE air_accounts SET revision=revision+1 WHERE plan_id=@planId", new { planId }, tx);
        await Audit(connection, tx, ctx, "plan_updated", planId, request.Name); tx.Commit(); return Results.Ok(new { updated = true });
    }
    private static async Task<IResult> Grant(Guid userId, AirGrantRequest request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (!Admin(ctx)) return Forbidden();
        try { AirProjectRules.Text(request.Reason, 500, "Reason"); } catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        if (request.ProductionLimit is < 0 or > 1000 || request.CaptureLimit is < 0 or > 1000) return Results.BadRequest(new { error = "Use device limits from 0 to 1000, or empty for unrestricted." });
        if (request.Status is not ("active" or "suspended" or "revoked") || request.Status == "active" && request.ValidUntil <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "Choose an access status and future expiry, or leave expiry empty." });
        using var connection = db.CreateConnection(); if (connection.State != System.Data.ConnectionState.Open) connection.Open(); using var tx = connection.BeginTransaction();
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM air_plans WHERE id=@id AND enabled)", new { id = request.PlanId }, tx)) return Results.BadRequest(new { error = "Choose an enabled Air plan." });
        var licenseId = await connection.ExecuteScalarAsync<Guid?>("UPDATE air_accounts SET plan_id=@PlanId,valid_until=@ValidUntil,status=@Status,override_device_limits=@OverrideDeviceLimits,production_limit=@ProductionLimit,capture_limit=@CaptureLimit,revision=revision+1 WHERE user_id=@userId RETURNING license_id", new { userId, request.PlanId, request.ValidUntil, request.Status, request.OverrideDeviceLimits, request.ProductionLimit, request.CaptureLimit }, tx);
        if (licenseId is null) return Results.NotFound();
        await connection.ExecuteAsync("UPDATE licenses SET status=@Status,expires_at=@ValidUntil WHERE id=@id", new { id = licenseId.Value, request.Status, request.ValidUntil }, tx);
        await Audit(connection, tx, ctx, "access_updated", userId, request.Reason); tx.Commit(); return Results.Ok(new { updated = true });
    }
    private static async Task<IResult> RevokeDevice(Guid deviceId, AirReasonRequest request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (!Admin(ctx)) return Forbidden();
        try { AirProjectRules.Text(request.Reason, 500, "Reason"); } catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        using var connection = db.CreateConnection(); if (connection.State != System.Data.ConnectionState.Open) connection.Open(); using var tx = connection.BeginTransaction();
        if (await connection.ExecuteAsync("UPDATE air_devices SET status='revoked',revision=revision+1 WHERE id=@deviceId", new { deviceId }, tx) == 0) return Results.NotFound();
        await Audit(connection, tx, ctx, "device_revoked", deviceId, request.Reason); tx.Commit(); return Results.Ok(new { revoked = true });
    }
    private static async Task<IResult> CreateRelease(AirReleaseRequest request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (!Admin(ctx)) return Forbidden(); var error = ReleaseError(request); if (error is not null) return error;
        using var connection = db.CreateConnection(); if (connection.State != System.Data.ConnectionState.Open) connection.Open(); using var tx = connection.BeginTransaction();
        var id = await connection.ExecuteScalarAsync<Guid>("INSERT INTO air_releases(version,channel,download_url,notes,published) VALUES(@Version,@Channel,@DownloadUrl,@Notes,@Published) RETURNING id", request, tx);
        await Audit(connection, tx, ctx, "release_created", id, request.Version); tx.Commit(); return Results.Ok(new { id });
    }
    private static async Task<IResult> UpdateRelease(Guid releaseId, AirReleaseRequest request, HttpContext ctx, IDbConnectionFactory db)
    {
        if (!Admin(ctx)) return Forbidden(); var error = ReleaseError(request); if (error is not null) return error;
        using var connection = db.CreateConnection(); if (connection.State != System.Data.ConnectionState.Open) connection.Open(); using var tx = connection.BeginTransaction();
        if (await connection.ExecuteAsync("UPDATE air_releases SET version=@Version,channel=@Channel,download_url=@DownloadUrl,notes=@Notes,published=@Published WHERE id=@id", new { id = releaseId, request.Version, request.Channel, request.DownloadUrl, request.Notes, request.Published }, tx) == 0) return Results.NotFound();
        await Audit(connection, tx, ctx, "release_updated", releaseId, request.Version); tx.Commit(); return Results.Ok(new { updated = true });
    }
    private static IResult? PlanError(AirPlanRequest request)
    {
        try { AirProjectRules.Text(request.Name, 80, "Plan name"); } catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        return request.ProductionLimit is < 0 or > 1000 || request.CaptureLimit is < 0 or > 1000 ? Results.BadRequest(new { error = "Device limits must be 0–1000 or empty for unrestricted." }) : null;
    }
    private static IResult? ReleaseError(AirReleaseRequest request)
    {
        if (request.Version is null || !System.Text.RegularExpressions.Regex.IsMatch(request.Version, "^[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,4}(-[a-zA-Z0-9.-]{1,20})?$") || request.Channel is not ("stable" or "preview") || !Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host is not ("esportra.gg" or "staging.esportra.gg") || !string.IsNullOrEmpty(uri.UserInfo) || request.Notes is null || request.Notes.Length > 10000) return Results.BadRequest(new { error = "Use a valid version, channel and official HTTPS download URL." });
        return null;
    }
    private static Task Audit(IDbConnection connection, IDbTransaction tx, HttpContext ctx, string action, Guid subject, string reason) => connection.ExecuteAsync("INSERT INTO air_admin_audit(actor_id,action,subject_id,reason) VALUES(@actor,@action,@subject,@reason)", new { actor = AirAccountEndpoints.User(ctx), action, subject = subject.ToString(), reason }, tx);
}
public sealed record AirReasonRequest(string Reason);
