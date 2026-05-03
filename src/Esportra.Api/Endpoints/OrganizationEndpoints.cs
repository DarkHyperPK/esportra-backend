using System.Data;
using System.Dynamic;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Infrastructure.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Esportra.Api.Hubs;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain 9: Organizations &amp; Staff Management
///
/// Key perf fix:
///   fetchOrganizationStaff had N+1: separate query for tournament_assignments per staff member.
///   Now a single query with jsonb_agg for assignments + profiles.
///
///   inviteOrganizationStaff was 5 sequential Supabase round-trips.
///   Now a single server-side method: resolve email → upsert staff → assign tournaments
///   → insert notification → send email → audit log.
/// </summary>
public static class OrganizationEndpoints
{
    /// <summary>Converts a Dapper dynamic org row's social_links from raw jsonb string to a parsed object.</summary>
    private static object ParseOrgSocialLinks(dynamic org)
    {
        var dict = (IDictionary<string, object?>)org;
        object? socialLinks = null;
        if (dict.TryGetValue("social_links", out var raw) && raw is string rawStr && !string.IsNullOrEmpty(rawStr))
        {
            try { socialLinks = System.Text.Json.JsonSerializer.Deserialize<object>(rawStr); }
            catch { socialLinks = null; }
        }
        dict["social_links"] = socialLinks;
        return org;
    }

    /// <summary>Returns true if the user is the org owner or an active staff member.</summary>
    private static async Task<bool> IsOrgMember(IDbConnection conn, Guid orgId, Guid userId)
    {
        return await conn.ExecuteScalarAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId
                UNION ALL
                SELECT 1 FROM organization_staff WHERE organization_id = @orgId AND user_id = @userId AND status = 'active'
            )
            """,
            new { orgId, userId });
    }

    public static void MapOrganizationEndpoints(this WebApplication app)
    {
        // ── GET /api/organizations/{orgId}/staff ───────────────────────────────
        // N+1 fix: single query with jsonb_agg for profiles + tournament assignments.
        app.MapGet("/api/organizations/{orgId}/staff", async (
            Guid                 orgId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is org owner or active staff member
            var hasAccess = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId
                    UNION ALL
                    SELECT 1 FROM organization_staff WHERE organization_id = @orgId AND user_id = @userId AND status = 'active'
                )
                """,
                new { orgId, userId = userCtx.UserIdGuid });
            if (!hasAccess && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            var staff = await conn.QueryAsync<dynamic>(
                """
                SELECT
                    os.*,
                    to_jsonb(p) AS profiles,
                    COALESCE(
                        jsonb_agg(
                            jsonb_build_object(
                                'id',                    sta.id,
                                'organization_staff_id', sta.organization_staff_id,
                                'tournament_id',         sta.tournament_id,
                                'assigned_by',           sta.assigned_by,
                                'created_at',            sta.created_at,
                                'tournament',            to_jsonb(t)
                            ) ORDER BY sta.created_at
                        ) FILTER (WHERE sta.id IS NOT NULL),
                        '[]'::jsonb
                    ) AS tournament_assignments
                FROM organization_staff os
                LEFT JOIN profiles p ON p.id = os.user_id
                LEFT JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                LEFT JOIN tournaments t ON t.id = sta.tournament_id
                WHERE os.organization_id = @orgId
                GROUP BY os.id, p.id
                ORDER BY os.created_at ASC
                """,
                new { orgId });

            foreach (var s in staff)
            {
                if (s is IDictionary<string, object?> d)
                {
                    foreach (var key in new[] { "profiles", "tournament_assignments" })
                        if (d.TryGetValue(key, out var v) && v is string str)
                            try { d[key] = JsonSerializer.Deserialize<JsonElement>(str); } catch { }
                }
            }
            return Results.Ok(staff);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizations/{orgId}/staff/invite ───────────────────────
        // All 5 operations in one server call: resolve + upsert + assign + notify + email + audit.
        app.MapPost("/api/organizations/{orgId}/staff/invite", async (
            Guid                            orgId,
            [FromBody] InviteStaffRequest   req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            IEmailService                  email,
            IHubContext<NotificationHub>   notifHub,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            // 1. Resolve user by email
            var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, email FROM profiles WHERE email ILIKE @email",
                new { email = req.UserEmail });
            if (profile is null)
                return Results.BadRequest(new { error = "User not found. They must have an Esportra account first." });

            Guid profileIdGuid = (Guid)profile.id;

            // 2. Upsert staff record
            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id FROM organization_staff WHERE organization_id = @orgId AND user_id = @profileId",
                new { orgId, profileId = profileIdGuid });

            Guid staffIdGuid;
            if (existing is not null)
            {
                staffIdGuid = (Guid)existing.id;
                await conn.ExecuteAsync(
                    """
                    UPDATE organization_staff
                    SET role = @role, permissions = @permissions::text[], assigned_by = @assignedBy,
                        status = 'pending', accepted_at = NULL, responded_at = NULL, updated_at = NOW()
                    WHERE id = @staffId
                    """,
                    new { staffId = staffIdGuid, role = req.Role, permissions = req.Permissions, assignedBy = userCtx.UserIdGuid });
            }
            else
            {
                var inserted = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO organization_staff
                        (organization_id, user_id, role, permissions, assigned_by, status)
                    VALUES (@orgId, @profileId, @role, @permissions::text[], @assignedBy, 'pending')
                    RETURNING id
                    """,
                    new { orgId, profileId = profileIdGuid, role = req.Role, permissions = req.Permissions, assignedBy = userCtx.UserIdGuid });
                staffIdGuid = (Guid)inserted.id;
            }

            // 3. Assign tournaments (bulk upsert) — staff still needs to accept before active
            if (req.TournamentIds is { Count: > 0 })
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO staff_tournament_assignments (organization_staff_id, tournament_id, assigned_by)
                    VALUES (@staffId, @tournamentId, @assignedBy)
                    ON CONFLICT (organization_staff_id, tournament_id) DO NOTHING
                    """,
                    req.TournamentIds.Select(tid => new { staffId = staffIdGuid, tournamentId = Guid.Parse(tid), assignedBy = userCtx.UserIdGuid }));
            }

            // 4. Insert in-app notification
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, data, is_read)
                VALUES (@userId, 'staff_invite', 'Staff Invitation',
                        @message,
                        @data::jsonb, FALSE)
                """,
                new
                {
                    userId  = profileIdGuid,
                    message = $"{req.InviterName ?? "An organizer"} invited you to staff {req.OrgName ?? "an organization"} as {FriendlyRole(req.Role)}.",
                    data    = System.Text.Json.JsonSerializer.Serialize(new { link = "/staff/dashboard", organization_staff_id = staffIdGuid, organization_id = orgId, role = req.Role }),
                });

            // Broadcast to user's SignalR session (if connected)
            await notifHub.Clients.Group($"user:{profileIdGuid}")
                .SendAsync("NewNotification", new { type = "staff_invite" }, ct);

            // 5. Audit log
            await LogAudit(conn, orgId, userCtx.UserIdGuid, "staff.invite", "staff", staffIdGuid,
                new { invitedEmail = req.UserEmail, req.Role, req.Permissions });

            // 6. Send email (best-effort)
            try
            {
                await email.SendAsync(
                    (string)profile.email,
                    EmailType.StaffInvite,
                    new
                    {
                        OrgName     = req.OrgName ?? "Organization",
                        OrgLogo     = req.OrgLogo,
                        Role        = FriendlyRole(req.Role),
                        Permissions = req.Permissions,
                        InvitedBy   = req.InviterName ?? "An organizer",
                    },
                    ct);
            }
            catch { /* Email failure must not block the API response */ }

            return Results.Ok(new { staffId = staffIdGuid });
        }).RequireAuthorization("Organizer");

        // ── PUT /api/organizations/{orgId}/staff/{staffId} ────────────────────
        app.MapPut("/api/organizations/{orgId}/staff/{staffId}", async (
            Guid                             orgId,
            Guid                             staffId,
            [FromBody] UpdateStaffRequest    req,
            HttpContext                      ctx,
            IDbConnectionFactory            db,
            CancellationToken               ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            await conn.ExecuteAsync(
                """
                UPDATE organization_staff
                SET role = @role, permissions = @permissions::text[], updated_at = NOW()
                WHERE id = @staffId AND organization_id = @orgId
                """,
                new { staffId, orgId, role = req.Role, permissions = req.Permissions });

            await LogAudit(conn, orgId, userCtx.UserIdGuid, "staff.update_permissions", "staff", staffId,
                new { req.Role, req.Permissions });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId}/staff/{staffId} ─────────────────
        app.MapDelete("/api/organizations/{orgId}/staff/{staffId}", async (
            Guid                 orgId,
            Guid                 staffId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            await conn.ExecuteAsync(
                "DELETE FROM organization_staff WHERE id = @staffId AND organization_id = @orgId",
                new { staffId, orgId });

            await LogAudit(conn, orgId, userCtx.UserIdGuid, "staff.remove", "staff", staffId, new { });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── GET /api/organizations/staff/invites — pending for current user ───
        app.MapGet("/api/organizations/staff/invites", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT os.*,
                       to_jsonb(o)  AS organization,
                       to_jsonb(ap) AS assigner_profile
                FROM organization_staff os
                LEFT JOIN organizations o ON o.id = os.organization_id
                LEFT JOIN profiles     ap ON ap.id = os.assigned_by
                WHERE os.user_id = @userId AND os.status = 'pending'
                ORDER BY os.created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizations/staff/assignments — active for current user ─
        app.MapGet("/api/organizations/staff/assignments", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT os.*,
                       to_jsonb(o)  AS organization,
                       to_jsonb(ap) AS assigner_profile
                FROM organization_staff os
                LEFT JOIN organizations o ON o.id = os.organization_id
                LEFT JOIN profiles     ap ON ap.id = os.assigned_by
                WHERE os.user_id = @userId AND os.status = 'active'
                ORDER BY os.updated_at DESC
                """,
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizations/staff/{inviteId}/respond ──────────────────
        app.MapPost("/api/organizations/staff/{inviteId}/respond", async (
            Guid                            inviteId,
            [FromBody] RespondInviteRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT organization_id FROM organization_staff WHERE id = @inviteId AND user_id = @userId AND status = 'pending'",
                new { inviteId, userId = userCtx.UserIdGuid });
            if (invite is null) return Results.NotFound();

            var now = DateTime.UtcNow;
            await conn.ExecuteAsync(
                """
                UPDATE organization_staff
                SET status = @status::text, accepted_at = @acceptedAt, responded_at = @now
                WHERE id = @inviteId AND status = 'pending'
                """,
                new
                {
                    inviteId,
                    status     = req.Accept ? "active" : "declined",
                    acceptedAt = req.Accept ? now : (DateTime?)null,
                    now,
                });

            Guid orgIdGuid = (Guid)invite.organization_id;
            await LogAudit(conn, orgIdGuid, userCtx.UserIdGuid,
                req.Accept ? "staff.accept" : "staff.decline", "staff", inviteId, new { });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizations/{orgId}/staff/{staffId}/assign-tournaments ─
        app.MapPost("/api/organizations/{orgId}/staff/{staffId}/assign-tournaments", async (
            Guid                                      orgId,
            Guid                                      staffId,
            [FromBody] AssignTournamentsRequest       req,
            HttpContext                               ctx,
            IDbConnectionFactory                     db,
            CancellationToken                        ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            if (req.TournamentIds.Count > 0)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO staff_tournament_assignments (organization_staff_id, tournament_id, assigned_by)
                    VALUES (@staffId, @tournamentId, @assignedBy)
                    ON CONFLICT (organization_staff_id, tournament_id) DO NOTHING
                    """,
                    req.TournamentIds.Select(tid => new { staffId, tournamentId = Guid.Parse(tid), assignedBy = userCtx.UserIdGuid }));
            }

            await LogAudit(conn, orgId, userCtx.UserIdGuid, "staff.assign_tournament", "staff", staffId,
                new { tournamentIds = req.TournamentIds });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId}/staff/assignments/{assignmentId} ─
        app.MapDelete("/api/organizations/{orgId}/staff/assignments/{assignmentId}", async (
            Guid                 orgId,
            Guid                 assignmentId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            await conn.ExecuteAsync(
                "DELETE FROM staff_tournament_assignments WHERE id = @assignmentId",
                new { assignmentId });

            await LogAudit(conn, orgId, userCtx.UserIdGuid, "staff.unassign_tournament", "assignment", assignmentId, new { });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/{tournamentId}/assigned-staff ────────────────
        app.MapGet("/api/tournaments/{tournamentId}/assigned-staff", async (
            Guid                 tournamentId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT sta.*,
                       to_jsonb(os) AS organization_staff
                FROM staff_tournament_assignments sta
                LEFT JOIN organization_staff os ON os.id = sta.organization_staff_id
                WHERE sta.tournament_id = @tournamentId
                """,
                new { tournamentId });
            return Results.Ok(rows);
        });

        // ── GET /api/organizations/staff/permissions ──────────────────────────
        // Replaces getOrgStaffPermissionsForTournament (2 Supabase calls → 1 query).
        app.MapGet("/api/organizations/staff/permissions", async (
            string?              organizationId,
            string?              tournamentId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (string.IsNullOrEmpty(organizationId)) return Results.Ok(Array.Empty<string>());

            using var conn = db.CreateConnection();

            var staff = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT id, role, permissions FROM organization_staff
                WHERE organization_id = @orgId AND user_id = @userId AND status = 'active'
                """,
                new { orgId = Guid.Parse(organizationId), userId = userCtx.UserIdGuid });

            if (staff is null) return Results.Ok(Array.Empty<string>());

            // Admins get all permissions without a tournament assignment check
            if ((string)staff.role == "admin")
                return Results.Ok(staff.permissions ?? Array.Empty<string>());

            // Non-admins need an explicit tournament assignment
            if (string.IsNullOrEmpty(tournamentId)) return Results.Ok(Array.Empty<string>());

            var assignment = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT id FROM staff_tournament_assignments
                WHERE organization_staff_id = @staffId AND tournament_id = @tournamentId
                """,
                new { staffId = (Guid)staff.id, tournamentId = Guid.Parse(tournamentId) });

            return assignment is not null
                ? Results.Ok(staff.permissions ?? Array.Empty<string>())
                : Results.Ok(Array.Empty<string>());
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizations/{orgId}/audit-logs ─────────────────────────
        app.MapGet("/api/organizations/{orgId}/audit-logs", async (
            Guid                 orgId,
            string?              action,
            int                  limit  = 50,
            int                  offset = 0,
            HttpContext          ctx    = null!,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify caller is org owner, active staff, or platform admin
            var hasAccess = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId
                    UNION ALL
                    SELECT 1 FROM organization_staff WHERE organization_id = @orgId AND user_id = @userId AND status = 'active'
                )
                """,
                new { orgId, userId = userCtx.UserIdGuid });
            if (!hasAccess && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT sal.*,
                       json_build_object(
                           'full_name', p.full_name,
                           'username',  p.username,
                           'avatar_url', p.avatar_url
                       )::text AS actor_json
                FROM staff_audit_log sal
                LEFT JOIN profiles p ON p.id = sal.actor_id
                WHERE sal.organization_id = @orgId
                  AND (@action IS NULL OR sal.action ILIKE '%' || @action || '%')
                ORDER BY sal.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { orgId, action, limit, offset });

            // Parse actor_json string into object for proper JSON serialization
            var mapped = rows.Select(r =>
            {
                var dict = (IDictionary<string, object?>)r;
                var actorJson = dict.ContainsKey("actor_json") ? dict["actor_json"] as string : null;
                dict.Remove("actor_json");
                if (actorJson is not null)
                    dict["actor"] = System.Text.Json.JsonSerializer.Deserialize<object>(actorJson);
                else
                    dict["actor"] = null;
                return r;
            }).ToList();

            var total = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM staff_audit_log WHERE organization_id = @orgId",
                new { orgId });

            return Results.Ok(new { logs = mapped, total });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizations/by-slug/{slug} — public org lookup ────────
        app.MapGet("/api/organizations/by-slug/{slug}", async (
            string               slug,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM organizations WHERE slug = @slug",
                new { slug });
            if (row is null) return Results.NotFound();
            return Results.Ok(ParseOrgSocialLinks(row));
        });

        // ── GET /api/organizations/{orgId}/tournaments — full details ───────
        app.MapGet("/api/organizations/{orgId}/tournaments", async (
            Guid                 orgId,
            IDbConnectionFactory db,
            CancellationToken    ct,
            bool?                deleted = null,
            bool?                exclude_completed = null) =>
        {
            using var conn = db.CreateConnection();

            string filter;
            if (deleted == true)
                filter = "deleted_at IS NOT NULL";
            else
            {
                filter = "deleted_at IS NULL";
                if (exclude_completed == true)
                    filter += " AND status != 'completed'";
            }

            var rows = await conn.QueryAsync<dynamic>(
                $"""
                SELECT * FROM v_tournament_details
                WHERE organization_id = @orgId AND {filter}
                ORDER BY start_date DESC
                """,
                new { orgId });
            return Results.Ok(rows);
        });

        // ── GET /api/organizations/{orgId}/albums — with cover URL ──────────
        app.MapGet("/api/organizations/{orgId}/albums", async (
            Guid                 orgId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT a.*,
                       COALESCE(
                           jsonb_agg(jsonb_build_object('url', m.url))
                           FILTER (WHERE m.id IS NOT NULL),
                           '[]'::jsonb
                       ) AS media
                FROM organization_albums a
                LEFT JOIN organization_media m ON m.album_id = a.id
                WHERE a.organization_id = @orgId
                GROUP BY a.id
                ORDER BY a.created_at DESC
                """,
                new { orgId });
            foreach (var r in rows)
                if (r is IDictionary<string, object?> d && d.TryGetValue("media", out var v) && v is string str)
                    try { d["media"] = JsonSerializer.Deserialize<JsonElement>(str); } catch { }
            return Results.Ok(rows);
        });

        // ── GET /api/organizations/{orgId}/media — all media (public) ───────
        app.MapGet("/api/organizations/{orgId}/media", async (
            Guid                 orgId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT * FROM organization_media WHERE organization_id = @orgId ORDER BY created_at DESC",
                new { orgId });
            return Results.Ok(rows);
        });

        // ── POST /api/organizations/{orgId}/media — insert media record ──────
        app.MapPost("/api/organizations/{orgId}/media", async (
            Guid                              orgId,
            [FromBody] InsertOrgMediaRequest  req,
            HttpContext                       ctx,
            IDbConnectionFactory            db,
            CancellationToken               ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            await conn.ExecuteAsync(
                """
                INSERT INTO organization_media (organization_id, url, type, caption, album_id)
                VALUES (@orgId, @url, @type, @caption, @albumId)
                """,
                new { orgId, url = req.Url, type = req.Type, caption = req.Caption, albumId = req.AlbumId is not null ? Guid.Parse(req.AlbumId) : (Guid?)null });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId}/media/{mediaId} ────────────────
        app.MapDelete("/api/organizations/{orgId}/media/{mediaId}", async (
            Guid                 orgId,
            Guid                 mediaId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            await conn.ExecuteAsync(
                "DELETE FROM organization_media WHERE id = @mediaId AND organization_id = @orgId",
                new { mediaId, orgId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── POST /api/organizations/{orgId}/albums — create album ────────────
        app.MapPost("/api/organizations/{orgId}/albums", async (
            Guid                             orgId,
            [FromBody] CreateOrgAlbumRequest  req,
            HttpContext                       ctx,
            IDbConnectionFactory             db,
            CancellationToken                ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            var album = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                INSERT INTO organization_albums (organization_id, title, description)
                VALUES (@orgId, @title, @description)
                RETURNING id, organization_id, title, description, created_at
                """,
                new { orgId, title = req.Title, description = req.Description });
            return Results.Ok(album);
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId}/albums/{albumId} ───────────────
        app.MapDelete("/api/organizations/{orgId}/albums/{albumId}", async (
            Guid                 orgId,
            Guid                 albumId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            // CASCADE delete handles media
            await conn.ExecuteAsync(
                "DELETE FROM organization_albums WHERE id = @albumId",
                new { albumId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── PUT /api/organizations/{orgId}/logo ──────────────────────────────
        app.MapPut("/api/organizations/{orgId}/logo", async (
            Guid                            orgId,
            [FromBody] UpdateOrgImageRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            await conn.ExecuteAsync(
                "UPDATE organizations SET logo_url = @url WHERE id = @orgId",
                new { orgId, url = req.Url });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── PUT /api/organizations/{orgId}/banner ────────────────────────────
        app.MapPut("/api/organizations/{orgId}/banner", async (
            Guid                            orgId,
            [FromBody] UpdateOrgImageRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            if (!await IsOrgMember(conn, orgId, userCtx.UserIdGuid))
                return Results.Forbid();

            await conn.ExecuteAsync(
                "UPDATE organizations SET banner_url = @url WHERE id = @orgId",
                new { orgId, url = req.Url });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId} — safe delete via RPC ──────────
        app.MapDelete("/api/organizations/{orgId}", async (
            Guid                 orgId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Only the owner can delete the organization
            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId)",
                new { orgId, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            var result = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM delete_organization_safely(@p_org_id)",
                new { p_org_id = orgId });
            return Results.Ok(result);
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/by-slug/{slug} — fetch with org join ────────
        // Replaces ManageBracketPage's supabase query
        app.MapGet("/api/tournaments/by-slug/{slug}", async (
            string               slug,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var isUuid = Guid.TryParse(slug, out var slugGuid);
            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                isUuid
                    ? """
                      SELECT t.*, jsonb_build_object('owner_id', o.owner_id) AS organization
                      FROM tournaments t
                      LEFT JOIN organizations o ON o.id = t.organization_id
                      WHERE t.slug = @slug OR t.id = @slugId
                      LIMIT 1
                      """
                    : """
                      SELECT t.*, jsonb_build_object('owner_id', o.owner_id) AS organization
                      FROM tournaments t
                      LEFT JOIN organizations o ON o.id = t.organization_id
                      WHERE t.slug = @slug
                      LIMIT 1
                      """,
                new { slug, slugId = isUuid ? slugGuid : (Guid?)null });
            if (row is null) return Results.NotFound();
            // Dapper maps jsonb to string — parse so it serializes as a proper object
            if (row is IDictionary<string, object?> dict
                && dict.TryGetValue("organization", out var val) && val is string s)
            {
                try { dict["organization"] = JsonSerializer.Deserialize<JsonElement>(s); }
                catch { /* leave as-is */ }
            }

            // Resolve staff permissions for the calling user
            var userCtx = ctx.Items["UserContext"] as UserContext;
            string[]? staffPermissions = null;
            if (userCtx is not null && row is IDictionary<string, object?> d)
            {
                var tournamentId = (Guid)d["id"];
                staffPermissions = (await conn.QueryAsync<string>(
                    """
                    SELECT DISTINCT unnest(os.permissions)
                    FROM organization_staff os
                    JOIN staff_tournament_assignments sta ON sta.organization_staff_id = os.id
                    WHERE sta.tournament_id = @tid
                      AND os.user_id = @userId AND os.status = 'active'
                    """,
                    new { tid = tournamentId, userId = userCtx.UserIdGuid })).ToArray();

                if (staffPermissions.Length > 0)
                    d["staffPermissions"] = staffPermissions;
            }

            return Results.Ok(row);
        });

        // ── GET /api/organizations/me ──────────────────────────────────────────
        // Returns the organization owned by the current user.
        // Also aliased as /api/organizations/mine for compat.
        app.MapGet("/api/organizations/me", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var org = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM organizations WHERE owner_id = @userId LIMIT 1",
                new { userId = userCtx.UserIdGuid });

            return org is null ? Results.NotFound() : Results.Ok(ParseOrgSocialLinks(org));
        }).RequireAuthorization("Authenticated");

        app.MapGet("/api/organizations/mine", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var org = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM organizations WHERE owner_id = @userId LIMIT 1",
                new { userId = userCtx.UserIdGuid });

            return org is null ? Results.NotFound() : Results.Ok(ParseOrgSocialLinks(org));
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizations/my-staff ────────────────────────────────────
        // Returns the organization where current user is a staff member (not owner).
        app.MapGet("/api/organizations/my-staff", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT o.*
                FROM organizations o
                JOIN organization_staff os ON os.organization_id = o.id
                WHERE os.user_id = @userId AND os.status = 'active'
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(new { organizations = rows });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizations ────────────────────────────────────────────
        // Create a new organization.
        app.MapPost("/api/organizations", async (
            [FromBody] CreateOrganizationRequest req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id FROM organizations WHERE owner_id = @userId LIMIT 1",
                new { userId = userCtx.UserIdGuid });
            if (existing is not null)
                return Results.Conflict(new { error = "You already own an organization" });

            var slug = req.Name.ToLowerInvariant()
                .Replace(" ", "-")
                .Replace("'", "")
                .Replace("\"", "");
            slug = System.Text.RegularExpressions.Regex.Replace(slug, "[^a-z0-9-]", "");

            var org = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO organizations (owner_id, name, slug, description, logo_url, banner_url, social_links)
                VALUES (@ownerId, @name, @slug, @description, @logoUrl, @bannerUrl, @socialLinks::jsonb)
                RETURNING id, owner_id, name, slug, description, logo_url, banner_url, social_links, created_at
                """,
                new
                {
                    ownerId     = userCtx.UserIdGuid,
                    name        = req.Name,
                    slug,
                    description = req.Description,
                    logoUrl     = req.LogoUrl,
                    bannerUrl   = req.BannerUrl,
                    socialLinks = req.SocialLinks.HasValue
                        ? req.SocialLinks.Value.GetRawText()
                        : "{}"
                });

            return Results.Created($"/api/organizations/{((Guid)org.id)}", org);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/organizations/{orgId} ─────────────────────────────────────
        // Update organization details (owner only).
        app.MapPut("/api/organizations/{orgId}", async (
            Guid                                orgId,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            ILoggerFactory                      loggerFactory,
            CancellationToken                   ct) =>
        {
            var logger = loggerFactory.CreateLogger("OrganizationUpdate");
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            UpdateOrganizationRequest? req;
            try
            {
                req = await System.Text.Json.JsonSerializer.DeserializeAsync<UpdateOrganizationRequest>(
                    ctx.Request.Body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize organization update body");
                return Results.BadRequest(new { error = "Invalid request body." });
            }
            if (req is null) return Results.BadRequest(new { error = "Empty request body" });

            try
            {
            using var conn = db.CreateConnection();

            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId)",
                new { orgId, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE organizations
                SET name         = COALESCE(@name, name),
                    slug         = COALESCE(@slug, slug),
                    description  = COALESCE(@description, description),
                    logo_url     = COALESCE(@logoUrl, logo_url),
                    banner_url   = COALESCE(@bannerUrl, banner_url),
                    social_links = CASE WHEN @socialLinks IS NOT NULL THEN @socialLinks::jsonb ELSE social_links END,
                    updated_at   = NOW()
                WHERE id = @orgId
                RETURNING id, owner_id, name, slug, description, logo_url, banner_url, social_links, created_at, updated_at
                """,
                new
                {
                    orgId,
                    name        = req.Name,
                    slug        = req.Slug,
                    description = req.Description,
                    logoUrl     = req.LogoUrl,
                    bannerUrl   = req.BannerUrl,
                    socialLinks = req.SocialLinks.HasValue
                        ? req.SocialLinks.Value.GetRawText()
                        : null
                });

            return updated is null ? Results.NotFound() : Results.Ok(ParseOrgSocialLinks(updated));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update organization {OrgId}", orgId);
                return Results.Problem("Failed to update organization. Please try again.", statusCode: 500);
            }
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizations/{orgId}/stats ───────────────────────────────
        app.MapGet("/api/organizations/{orgId}/stats", async (
            Guid                 orgId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var stats = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT
                    (SELECT COUNT(*) FROM tournaments WHERE organization_id = @orgId AND deleted_at IS NULL) AS total_tournaments,
                    (SELECT COUNT(*) FROM tournaments WHERE organization_id = @orgId AND status::text IN ('open', 'check_in', 'ongoing') AND deleted_at IS NULL) AS active_tournaments,
                    (SELECT COUNT(*) FROM organization_staff WHERE organization_id = @orgId AND status = 'active') AS staff_count,
                    (SELECT COUNT(*) FROM tournament_participants tp
                     JOIN tournaments t ON t.id = tp.tournament_id
                     WHERE t.organization_id = @orgId AND t.deleted_at IS NULL) AS total_participants
                """,
                new { orgId });

            if (stats is null) return Results.Ok(new { totalTournaments = 0, activeTournaments = 0, staffCount = 0, totalParticipants = 0 });

            return Results.Ok(new
            {
                totalTournaments  = (long)stats.total_tournaments,
                activeTournaments = (long)stats.active_tournaments,
                staffCount        = (long)stats.staff_count,
                totalParticipants = (long)stats.total_participants
            });
        }).RequireAuthorization("Authenticated");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FriendlyRole(string role) => role switch
    {
        "admin" => "an Administrator",
        "mod"   => "a Moderator",
        _       => "a Co-Host",
    };

    private static Task LogAudit(IDbConnection conn, Guid orgId, Guid actorId,
        string action, string? targetType, Guid? targetId, object details) =>
        conn.ExecuteAsync(
            """
            INSERT INTO staff_audit_log (organization_id, actor_id, action, target_type, target_id, details)
            VALUES (@orgId, @actorId, @action, @targetType, @targetId, @details::jsonb)
            """,
            new
            {
                orgId,
                actorId,
                action,
                targetType,
                targetId,
                details = System.Text.Json.JsonSerializer.Serialize(details),
            });
}

// ── Request records ────────────────────────────────────────────────────────────

public sealed record InviteStaffRequest(
    string       UserEmail,
    string       Role,
    List<string> Permissions,
    string?      OrgName       = null,
    string?      OrgLogo       = null,
    string?      InviterName   = null,
    List<string>? TournamentIds = null);

public sealed record UpdateStaffRequest(string Role, List<string> Permissions);

public sealed record RespondInviteRequest(bool Accept);

public sealed record AssignTournamentsRequest(List<string> TournamentIds);

public sealed record InsertOrgMediaRequest(string Url, string Type, string? Caption = null, string? AlbumId = null);
public sealed record UpdateOrgImageRequest(string Url);
public sealed record CreateOrgAlbumRequest(string Title, string? Description = null);

public sealed record CreateOrganizationRequest(
    string                               Name,
    string?                              Description = null,
    string?                              LogoUrl     = null,
    string?                              BannerUrl   = null,
    System.Text.Json.JsonElement?        SocialLinks = null);

public sealed record UpdateOrganizationRequest(
    string?                              Name        = null,
    string?                              Slug        = null,
    string?                              Description = null,
    string?                              LogoUrl     = null,
    string?                              BannerUrl   = null,
    System.Text.Json.JsonElement?        SocialLinks = null);
