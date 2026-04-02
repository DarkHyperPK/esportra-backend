using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class TournamentSponsorEndpoints
{
    public static void MapTournamentSponsorEndpoints(this WebApplication app)
    {
        // ── GET /api/tournaments/{tournamentId}/sponsors ─────────────────────
        // Public — returns active sponsors linked to a tournament
        app.MapGet("/api/tournaments/{tournamentId}/sponsors", async (
            Guid                 tournamentId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT ts.id, ts.sponsor_type, ts.placement_zones, ts.media_overrides, ts.priority,
                       s.id AS sponsor_id, s.name, s.tagline, s.logo_url, s.banner_image_url,
                       s.accent_color, s.tier, s.cta_text, s.website_url, s.gallery_images
                FROM tournament_sponsors ts
                JOIN sponsors s ON s.id = ts.sponsor_id
                WHERE ts.tournament_id = @tournamentId
                  AND ts.is_active = true
                  AND s.is_active = true
                ORDER BY ts.priority DESC, s.name ASC
                LIMIT 50
                """,
                new { tournamentId });

            var sponsors = rows.Select(r => new
            {
                id = r.id.ToString(),
                sponsor_id = r.sponsor_id.ToString(),
                sponsor_type = (string)r.sponsor_type,
                placement_zones = r.placement_zones as string[] ?? Array.Empty<string>(),
                media_overrides = r.media_overrides is string json
                    ? JsonSerializer.Deserialize<object>(json)
                    : r.media_overrides,
                priority = (int)r.priority,
                sponsor = new
                {
                    id = r.sponsor_id.ToString(),
                    name = (string?)r.name ?? "",
                    tagline = (string?)r.tagline,
                    logo_url = (string?)r.logo_url,
                    banner_image_url = (string?)r.banner_image_url,
                    accent_color = (string?)r.accent_color ?? "#8b5cf6",
                    tier = (string?)r.tier ?? "standard",
                    cta_text = (string?)r.cta_text ?? "Learn More",
                    website_url = (string?)r.website_url ?? "",
                    gallery_images = r.gallery_images as string[] ?? Array.Empty<string>(),
                },
            }).ToList();

            return Results.Ok(sponsors);
        });

        // ── POST /api/tournaments/{tournamentId}/sponsors ────────────────────
        // Admin/Organizer — assign a sponsor to a tournament
        app.MapPost("/api/tournaments/{tournamentId}/sponsors", async (
            Guid                            tournamentId,
            [FromBody] AssignTournamentSponsorRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory            db,
            CancellationToken               ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify user is admin or tournament organizer
            var isAuthorized = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM admin_user_roles WHERE user_id = @userId
                    UNION ALL
                    SELECT 1 FROM tournaments WHERE id = @tournamentId AND organizer_id = @userId
                )
                """,
                new { userId = userCtx.UserIdGuid, tournamentId });

            if (!isAuthorized) return Results.Forbid();

            if (!Guid.TryParse(req.SponsorId, out var sponsorId))
                return Results.BadRequest(new { error = "Invalid sponsor_id" });

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                INSERT INTO tournament_sponsors (tournament_id, sponsor_id, sponsor_type, placement_zones, media_overrides, priority, assigned_by)
                VALUES (@tournamentId, @sponsorId, @sponsorType, @placementZones::text[], @mediaOverrides::jsonb, @priority, @assignedBy)
                ON CONFLICT (tournament_id, sponsor_id) DO UPDATE SET
                    sponsor_type    = EXCLUDED.sponsor_type,
                    placement_zones = EXCLUDED.placement_zones,
                    media_overrides = EXCLUDED.media_overrides,
                    priority        = EXCLUDED.priority,
                    is_active       = true
                RETURNING id, tournament_id, sponsor_id, sponsor_type, placement_zones, media_overrides, priority, is_active, created_at
                """,
                new
                {
                    tournamentId,
                    sponsorId,
                    sponsorType = req.SponsorType ?? "event_sponsor",
                    placementZones = req.PlacementZones ?? Array.Empty<string>(),
                    mediaOverrides = JsonSerializer.Serialize(req.MediaOverrides ?? new Dictionary<string, string>()),
                    priority = req.Priority ?? 0,
                    assignedBy = userCtx.UserIdGuid,
                });

            if (row is null)
                return Results.Json(new { error = "We couldn't assign the sponsor. Please try again." }, statusCode: 500);

            return Results.Ok(new
            {
                id = row.id.ToString(),
                tournament_id = row.tournament_id.ToString(),
                sponsor_id = row.sponsor_id.ToString(),
                sponsor_type = (string)row.sponsor_type,
                placement_zones = row.placement_zones as string[] ?? Array.Empty<string>(),
                priority = (int)row.priority,
                is_active = (bool)row.is_active,
            });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/tournaments/{tournamentId}/sponsors/{sponsorId} ─────────
        // Admin/Organizer — update placement, type, media overrides
        app.MapPut("/api/tournaments/{tournamentId}/sponsors/{sponsorId}", async (
            Guid                            tournamentId,
            Guid                            sponsorId,
            [FromBody] UpdateTournamentSponsorRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory            db,
            CancellationToken               ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isAuthorized = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM admin_user_roles WHERE user_id = @userId
                    UNION ALL
                    SELECT 1 FROM tournaments WHERE id = @tournamentId AND organizer_id = @userId
                )
                """,
                new { userId = userCtx.UserIdGuid, tournamentId });

            if (!isAuthorized) return Results.Forbid();

            var sets = new List<string>();
            var p = new DynamicParameters();
            p.Add("tournamentId", tournamentId);
            p.Add("sponsorId", sponsorId);

            if (req.SponsorType is not null)      { sets.Add("sponsor_type = @sponsorType");           p.Add("sponsorType", req.SponsorType); }
            if (req.PlacementZones is not null)    { sets.Add("placement_zones = @zones::text[]");      p.Add("zones", req.PlacementZones); }
            if (req.MediaOverrides is not null)    { sets.Add("media_overrides = @overrides::jsonb");   p.Add("overrides", JsonSerializer.Serialize(req.MediaOverrides)); }
            if (req.Priority.HasValue)             { sets.Add("priority = @priority");                  p.Add("priority", req.Priority.Value); }
            if (req.IsActive.HasValue)             { sets.Add("is_active = @isActive");                 p.Add("isActive", req.IsActive.Value); }

            if (sets.Count == 0)
                return Results.BadRequest(new { error = "No fields to update." });

            var sql = $"""
                UPDATE tournament_sponsors SET {string.Join(", ", sets)}
                WHERE tournament_id = @tournamentId AND sponsor_id = @sponsorId
                RETURNING id, sponsor_type, placement_zones, priority, is_active
                """;

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(sql, p);
            if (row is null) return Results.NotFound(new { error = "Sponsor assignment not found." });

            return Results.Ok(new
            {
                id = row.id.ToString(),
                sponsor_type = (string)row.sponsor_type,
                placement_zones = row.placement_zones as string[] ?? Array.Empty<string>(),
                priority = (int)row.priority,
                is_active = (bool)row.is_active,
            });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/tournaments/{tournamentId}/sponsors/{sponsorId} ──────
        // Admin/Organizer — remove a sponsor from a tournament
        app.MapDelete("/api/tournaments/{tournamentId}/sponsors/{sponsorId}", async (
            Guid                 tournamentId,
            Guid                 sponsorId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isAuthorized = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM admin_user_roles WHERE user_id = @userId
                    UNION ALL
                    SELECT 1 FROM tournaments WHERE id = @tournamentId AND organizer_id = @userId
                )
                """,
                new { userId = userCtx.UserIdGuid, tournamentId });

            if (!isAuthorized) return Results.Forbid();

            var deleted = await conn.ExecuteAsync(
                "DELETE FROM tournament_sponsors WHERE tournament_id = @tournamentId AND sponsor_id = @sponsorId",
                new { tournamentId, sponsorId });

            return deleted > 0
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { error = "Sponsor assignment not found." });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/sponsors/{sponsorId}/tournaments ────────────────────────
        // Partner portal — shows which tournaments a sponsor is linked to
        app.MapGet("/api/sponsors/{sponsorId}/tournaments", async (
            Guid                 sponsorId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify user owns this sponsor account
            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM sponsor_accounts WHERE sponsor_id = @sponsorId AND user_id = @userId)",
                new { sponsorId, userId = userCtx.UserIdGuid });

            // Also allow admins
            var isAdmin = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM admin_user_roles WHERE user_id = @userId)",
                new { userId = userCtx.UserIdGuid });

            if (!isOwner && !isAdmin) return Results.Forbid();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT ts.sponsor_type, ts.placement_zones, ts.priority, ts.is_active, ts.created_at,
                       t.id AS tournament_id, t.title, t.slug, t.status, t.start_date,
                       t.banner_image_url AS tournament_banner
                FROM tournament_sponsors ts
                JOIN tournaments t ON t.id = ts.tournament_id
                WHERE ts.sponsor_id = @sponsorId
                ORDER BY t.start_date DESC
                LIMIT 100
                """,
                new { sponsorId });

            var tournaments = rows.Select(r => new
            {
                tournament_id = r.tournament_id.ToString(),
                title = (string?)r.title ?? "",
                slug = (string?)r.slug,
                status = (string?)r.status,
                start_date = r.start_date?.ToString("o"),
                tournament_banner = (string?)r.tournament_banner,
                sponsor_type = (string)r.sponsor_type,
                placement_zones = r.placement_zones as string[] ?? Array.Empty<string>(),
                priority = (int)r.priority,
                is_active = (bool)r.is_active,
                linked_at = r.created_at?.ToString("o") ?? "",
            }).ToList();

            return Results.Ok(tournaments);
        }).RequireAuthorization("Authenticated");
    }
}

// Request DTOs
public sealed record AssignTournamentSponsorRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("sponsor_id")]       string  SponsorId,
    [property: System.Text.Json.Serialization.JsonPropertyName("sponsor_type")]     string? SponsorType      = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("placement_zones")]  string[]? PlacementZones = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("media_overrides")]  Dictionary<string, string>? MediaOverrides = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("priority")]         int?    Priority         = null);

public sealed record UpdateTournamentSponsorRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("sponsor_type")]     string?  SponsorType      = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("placement_zones")]  string[]? PlacementZones  = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("media_overrides")]  Dictionary<string, string>? MediaOverrides = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("priority")]         int?     Priority         = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("is_active")]        bool?    IsActive         = null);
