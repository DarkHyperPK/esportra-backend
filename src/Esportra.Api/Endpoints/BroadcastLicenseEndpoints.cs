using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Contracts.Requests;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text.Json;

namespace Esportra.Api.Endpoints;

public static class BroadcastLicenseEndpoints
{
    public static void MapBroadcastLicenseEndpoints(this WebApplication app)
    {
        // ── GET /api/broadcast/license — current user's license status ──────
        app.MapGet("/api/broadcast/license", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var license = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, license_key, license_type, status, plan,
                       activated_at, expires_at, last_validated_at,
                       device_fingerprints, max_devices, metadata,
                       created_at, updated_at
                FROM broadcast_licenses
                WHERE user_id = @UserId
                  AND status = 'active'
                ORDER BY created_at DESC
                LIMIT 1
                """,
                new { UserId = userCtx.UserIdGuid });

            if (license is null)
                return Results.Ok(new { licensed = false });

            return Results.Ok(new
            {
                licensed = true,
                license
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/broadcast/license/validate — validate on app startup ──
        app.MapPost("/api/broadcast/license/validate", async (
            [FromBody] ValidateLicenseRequest req,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var license = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, license_type, status, plan, expires_at,
                       device_fingerprints, max_devices
                FROM broadcast_licenses
                WHERE user_id = @UserId
                  AND status = 'active'
                ORDER BY created_at DESC
                LIMIT 1
                """,
                new { UserId = userCtx.UserIdGuid });

            if (license is null)
                return Results.Ok(new { valid = false, reason = "no_license" });

            // Check expiry for subscription licenses
            if (license.license_type == "subscription" && license.expires_at != null)
            {
                if ((DateTimeOffset)license.expires_at < DateTimeOffset.UtcNow)
                {
                    await conn.ExecuteAsync(
                        "UPDATE broadcast_licenses SET status = 'expired', updated_at = now() WHERE id = @Id",
                        new { Id = (Guid)license.id });

                    return Results.Ok(new { valid = false, reason = "expired" });
                }
            }

            // Check device fingerprint
            var fingerprints = JsonSerializer.Deserialize<List<string>>(
                ((JsonElement)license.device_fingerprints).GetRawText()) ?? [];

            if (!fingerprints.Contains(req.DeviceFingerprint))
            {
                if (fingerprints.Count >= (int)license.max_devices)
                    return Results.Ok(new { valid = false, reason = "device_limit_reached" });

                fingerprints.Add(req.DeviceFingerprint);
                await conn.ExecuteAsync(
                    """
                    UPDATE broadcast_licenses
                    SET device_fingerprints = @Fingerprints::jsonb,
                        last_validated_at = now(),
                        updated_at = now()
                    WHERE id = @Id
                    """,
                    new { Id = (Guid)license.id, Fingerprints = JsonSerializer.Serialize(fingerprints) });
            }
            else
            {
                await conn.ExecuteAsync(
                    "UPDATE broadcast_licenses SET last_validated_at = now(), updated_at = now() WHERE id = @Id",
                    new { Id = (Guid)license.id });
            }

            return Results.Ok(new
            {
                valid = true,
                plan = (string)license.plan,
                expiresAt = license.expires_at,
                features = GetFeaturesForPlan((string)license.plan)
            });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/broadcast/license/activate — activate a license key ───
        app.MapPost("/api/broadcast/license/activate", async (
            [FromBody] ActivateLicenseRequest req,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Check if key exists and is unclaimed
            var existing = await conn.QueryFirstOrDefaultAsync<dynamic>(
                """
                SELECT id, user_id, status
                FROM broadcast_licenses
                WHERE license_key = @Key
                """,
                new { Key = req.LicenseKey });

            if (existing is null)
                return Results.BadRequest(new { error = "Invalid license key." });

            if ((Guid)existing.user_id != Guid.Empty && (Guid)existing.user_id != userCtx.UserIdGuid)
                return Results.BadRequest(new { error = "License key already claimed." });

            if ((string)existing.status != "active")
                return Results.BadRequest(new { error = $"License is {existing.status}." });

            // Activate for this user and device
            var fingerprints = JsonSerializer.Serialize(new[] { req.DeviceFingerprint });

            await conn.ExecuteAsync(
                """
                UPDATE broadcast_licenses
                SET user_id             = @UserId,
                    device_fingerprints = @Fingerprints::jsonb,
                    activated_at        = now(),
                    last_validated_at   = now(),
                    updated_at          = now()
                WHERE id = @Id
                """,
                new
                {
                    UserId = userCtx.UserIdGuid,
                    Fingerprints = fingerprints,
                    Id = (Guid)existing.id
                });

            return Results.Ok(new { activated = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/broadcast/license/deactivate — release license ────────
        app.MapPost("/api/broadcast/license/deactivate", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.ExecuteAsync(
                """
                UPDATE broadcast_licenses
                SET device_fingerprints = '[]'::jsonb,
                    updated_at = now()
                WHERE user_id = @UserId AND status = 'active'
                """,
                new { UserId = userCtx.UserIdGuid });

            return Results.Ok(new { deactivated = rows > 0 });
        }).RequireAuthorization("Authenticated");
    }

    private static string[] GetFeaturesForPlan(string plan) => plan switch
    {
        "enterprise" => ["gep", "overlays", "custom_widgets", "cloud_sync", "priority_support", "multi_stream", "api_access"],
        "pro"        => ["gep", "overlays", "custom_widgets", "cloud_sync", "priority_support"],
        _            => ["gep", "overlays", "cloud_sync"]
    };
}
