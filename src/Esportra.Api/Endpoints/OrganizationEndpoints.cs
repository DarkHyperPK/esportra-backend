using System.Data;
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
    public static void MapOrganizationEndpoints(this WebApplication app)
    {
        // ── GET /api/organizations/{orgId}/staff ───────────────────────────────
        // N+1 fix: single query with jsonb_agg for profiles + tournament assignments.
        app.MapGet("/api/organizations/{orgId}/staff", async (
            string               orgId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var orgIdGuid = Guid.Parse(orgId);
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
                new { orgId = orgIdGuid, userId = userCtx.UserIdGuid });
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
                new { orgId = orgIdGuid });

            return Results.Ok(staff);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizations/{orgId}/staff/invite ───────────────────────
        // All 5 operations in one server call: resolve + upsert + assign + notify + email + audit.
        app.MapPost("/api/organizations/{orgId}/staff/invite", async (
            string                          orgId,
            [FromBody] InviteStaffRequest   req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            IEmailService                  email,
            IHubContext<NotificationHub>   notifHub,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var orgIdGuid = Guid.Parse(orgId);
            using var conn = db.CreateConnection();

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
                new { orgId = orgIdGuid, profileId = profileIdGuid });

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
                    new { orgId = orgIdGuid, profileId = profileIdGuid, role = req.Role, permissions = req.Permissions, assignedBy = userCtx.UserIdGuid });
                staffIdGuid = (Guid)inserted.id;
            }

            // 3. Assign tournaments (bulk upsert)
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
            await LogAudit(conn, orgIdGuid, userCtx.UserIdGuid, "staff.invite", "staff", staffIdGuid,
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
            string                           orgId,
            string                           staffId,
            [FromBody] UpdateStaffRequest    req,
            HttpContext                      ctx,
            IDbConnectionFactory            db,
            CancellationToken               ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var orgIdGuid = Guid.Parse(orgId);
            var staffIdGuid = Guid.Parse(staffId);
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE organization_staff
                SET role = @role, permissions = @permissions::text[], updated_at = NOW()
                WHERE id = @staffId AND organization_id = @orgId
                """,
                new { staffId = staffIdGuid, orgId = orgIdGuid, role = req.Role, permissions = req.Permissions });

            await LogAudit(conn, orgIdGuid, userCtx.UserIdGuid, "staff.update_permissions", "staff", staffIdGuid,
                new { req.Role, req.Permissions });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId}/staff/{staffId} ─────────────────
        app.MapDelete("/api/organizations/{orgId}/staff/{staffId}", async (
            string               orgId,
            string               staffId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var orgIdGuid = Guid.Parse(orgId);
            var staffIdGuid = Guid.Parse(staffId);
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM organization_staff WHERE id = @staffId AND organization_id = @orgId",
                new { staffId = staffIdGuid, orgId = orgIdGuid });

            await LogAudit(conn, orgIdGuid, userCtx.UserIdGuid, "staff.remove", "staff", staffIdGuid, new { });
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
            string                          inviteId,
            [FromBody] RespondInviteRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var inviteIdGuid = Guid.Parse(inviteId);
            using var conn = db.CreateConnection();

            var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT organization_id FROM organization_staff WHERE id = @inviteId AND user_id = @userId AND status = 'pending'",
                new { inviteId = inviteIdGuid, userId = userCtx.UserIdGuid });
            if (invite is null) return Results.NotFound();

            var now = DateTime.UtcNow;
            await conn.ExecuteAsync(
                """
                UPDATE organization_staff
                SET status = @status, accepted_at = @acceptedAt, responded_at = @now
                WHERE id = @inviteId
                """,
                new
                {
                    inviteId = inviteIdGuid,
                    status     = req.Accept ? "active" : "declined",
                    acceptedAt = req.Accept ? now : (DateTime?)null,
                    now,
                });

            Guid orgIdGuid = (Guid)invite.organization_id;
            await LogAudit(conn, orgIdGuid, userCtx.UserIdGuid,
                req.Accept ? "staff.accept" : "staff.decline", "staff", inviteIdGuid, new { });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/organizations/{orgId}/staff/{staffId}/assign-tournaments ─
        app.MapPost("/api/organizations/{orgId}/staff/{staffId}/assign-tournaments", async (
            string                                    orgId,
            string                                    staffId,
            [FromBody] AssignTournamentsRequest       req,
            HttpContext                               ctx,
            IDbConnectionFactory                     db,
            CancellationToken                        ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var orgIdGuid = Guid.Parse(orgId);
            var staffIdGuid = Guid.Parse(staffId);
            using var conn = db.CreateConnection();
            if (req.TournamentIds.Count > 0)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO staff_tournament_assignments (organization_staff_id, tournament_id, assigned_by)
                    VALUES (@staffId, @tournamentId, @assignedBy)
                    ON CONFLICT (organization_staff_id, tournament_id) DO NOTHING
                    """,
                    req.TournamentIds.Select(tid => new { staffId = staffIdGuid, tournamentId = Guid.Parse(tid), assignedBy = userCtx.UserIdGuid }));
            }

            await LogAudit(conn, orgIdGuid, userCtx.UserIdGuid, "staff.assign_tournament", "staff", staffIdGuid,
                new { tournamentIds = req.TournamentIds });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId}/staff/assignments/{assignmentId} ─
        app.MapDelete("/api/organizations/{orgId}/staff/assignments/{assignmentId}", async (
            string               orgId,
            string               assignmentId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var orgIdGuid = Guid.Parse(orgId);
            var assignmentIdGuid = Guid.Parse(assignmentId);
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM staff_tournament_assignments WHERE id = @assignmentId",
                new { assignmentId = assignmentIdGuid });

            await LogAudit(conn, orgIdGuid, userCtx.UserIdGuid, "staff.unassign_tournament", "assignment", assignmentIdGuid, new { });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/{tournamentId}/assigned-staff ────────────────
        app.MapGet("/api/tournaments/{tournamentId}/assigned-staff", async (
            string               tournamentId,
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
                new { tournamentId = Guid.Parse(tournamentId) });
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
            string               orgId,
            string?              action,
            int                  limit  = 50,
            int                  offset = 0,
            HttpContext          ctx    = null!,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var orgIdGuid = Guid.Parse(orgId);
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
                new { orgId = orgIdGuid, userId = userCtx.UserIdGuid });
            if (!hasAccess && !userCtx.Roles.Contains("admin")) return Results.Forbid();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT sal.*,
                       to_jsonb(p) AS actor
                FROM staff_audit_log sal
                LEFT JOIN profiles p ON p.id = sal.actor_id
                WHERE sal.organization_id = @orgId
                  AND (@action IS NULL OR sal.action ILIKE '%' || @action || '%')
                ORDER BY sal.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { orgId = orgIdGuid, action, limit, offset });

            var total = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM staff_audit_log WHERE organization_id = @orgId",
                new { orgId = orgIdGuid });

            return Results.Ok(new { logs = rows, total });
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
            return row is null ? Results.NotFound() : Results.Ok(row);
        });

        // ── GET /api/organizations/{orgId}/tournaments — full details ───────
        app.MapGet("/api/organizations/{orgId}/tournaments", async (
            string               orgId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT * FROM v_tournament_details
                WHERE organization_id = @orgId AND deleted_at IS NULL
                ORDER BY start_date DESC
                """,
                new { orgId = Guid.Parse(orgId) });
            return Results.Ok(rows);
        });

        // ── GET /api/organizations/{orgId}/albums — with cover URL ──────────
        app.MapGet("/api/organizations/{orgId}/albums", async (
            string               orgId,
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
                new { orgId = Guid.Parse(orgId) });
            return Results.Ok(rows);
        });

        // ── GET /api/organizations/{orgId}/media — all media (public) ───────
        app.MapGet("/api/organizations/{orgId}/media", async (
            string               orgId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT * FROM organization_media WHERE organization_id = @orgId ORDER BY created_at DESC",
                new { orgId = Guid.Parse(orgId) });
            return Results.Ok(rows);
        });

        // ── POST /api/organizations/{orgId}/media — insert media record ──────
        app.MapPost("/api/organizations/{orgId}/media", async (
            string                            orgId,
            [FromBody] InsertOrgMediaRequest  req,
            HttpContext                       ctx,
            IDbConnectionFactory            db,
            CancellationToken               ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                INSERT INTO organization_media (organization_id, url, type, caption, album_id)
                VALUES (@orgId, @url, @type, @caption, @albumId)
                """,
                new { orgId = Guid.Parse(orgId), url = req.Url, type = req.Type, caption = req.Caption, albumId = req.AlbumId is not null ? Guid.Parse(req.AlbumId) : (Guid?)null });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId}/media/{mediaId} ────────────────
        app.MapDelete("/api/organizations/{orgId}/media/{mediaId}", async (
            string               orgId,
            string               mediaId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM organization_media WHERE id = @mediaId AND organization_id = @orgId",
                new { mediaId = Guid.Parse(mediaId), orgId = Guid.Parse(orgId) });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId}/albums/{albumId} ───────────────
        app.MapDelete("/api/organizations/{orgId}/albums/{albumId}", async (
            string               orgId,
            string               albumId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            // CASCADE delete handles media
            await conn.ExecuteAsync(
                "DELETE FROM organization_albums WHERE id = @albumId",
                new { albumId = Guid.Parse(albumId) });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── PUT /api/organizations/{orgId}/logo ──────────────────────────────
        app.MapPut("/api/organizations/{orgId}/logo", async (
            string                          orgId,
            [FromBody] UpdateOrgImageRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE organizations SET logo_url = @url WHERE id = @orgId",
                new { orgId = Guid.Parse(orgId), url = req.Url });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── PUT /api/organizations/{orgId}/banner ────────────────────────────
        app.MapPut("/api/organizations/{orgId}/banner", async (
            string                          orgId,
            [FromBody] UpdateOrgImageRequest req,
            HttpContext                     ctx,
            IDbConnectionFactory           db,
            CancellationToken              ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE organizations SET banner_url = @url WHERE id = @orgId",
                new { orgId = Guid.Parse(orgId), url = req.Url });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        // ── DELETE /api/organizations/{orgId} — safe delete via RPC ──────────
        app.MapDelete("/api/organizations/{orgId}", async (
            string               orgId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var result = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT * FROM delete_organization_safely(@p_org_id)",
                new { p_org_id = Guid.Parse(orgId) });
            return Results.Ok(result);
        }).RequireAuthorization("Organizer");

        // ── GET /api/tournaments/by-slug/{slug} — fetch with org join ────────
        // Replaces ManageBracketPage's supabase query
        app.MapGet("/api/tournaments/by-slug/{slug}", async (
            string               slug,
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
            return row is null ? Results.NotFound() : Results.Ok(row);
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

            return org is null ? Results.NotFound() : Results.Ok(org);
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

            return org is null ? Results.NotFound() : Results.Ok(org);
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

            return Results.Ok(rows);
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
                RETURNING *
                """,
                new
                {
                    ownerId     = userCtx.UserIdGuid,
                    name        = req.Name,
                    slug,
                    description = req.Description,
                    logoUrl     = req.LogoUrl,
                    bannerUrl   = req.BannerUrl,
                    socialLinks = System.Text.Json.JsonSerializer.Serialize(req.SocialLinks ?? new Dictionary<string, string>())
                });

            return Results.Created($"/api/organizations/{((Guid)org.id)}", org);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/organizations/{orgId} ─────────────────────────────────────
        // Update organization details (owner only).
        app.MapPut("/api/organizations/{orgId}", async (
            Guid                                orgId,
            [FromBody] UpdateOrganizationRequest req,
            HttpContext                          ctx,
            IDbConnectionFactory                db,
            CancellationToken                   ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM organizations WHERE id = @orgId AND owner_id = @userId)",
                new { orgId, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE organizations
                SET name         = COALESCE(@name, name),
                    description  = COALESCE(@description, description),
                    social_links = CASE WHEN @socialLinks IS NOT NULL THEN @socialLinks::jsonb ELSE social_links END,
                    updated_at   = NOW()
                WHERE id = @orgId
                RETURNING *
                """,
                new
                {
                    orgId,
                    name        = req.Name,
                    description = req.Description,
                    socialLinks = req.SocialLinks is not null
                        ? System.Text.Json.JsonSerializer.Serialize(req.SocialLinks)
                        : null
                });

            return updated is null ? Results.NotFound() : Results.Ok(updated);
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
                    (SELECT COUNT(*) FROM tournaments WHERE organization_id = @orgId AND status = 'active' AND deleted_at IS NULL) AS active_tournaments,
                    (SELECT COUNT(*) FROM organization_staff WHERE organization_id = @orgId AND status = 'active') AS staff_count,
                    (SELECT COUNT(*) FROM tournament_participants tp
                     JOIN tournaments t ON t.id = tp.tournament_id
                     WHERE t.organization_id = @orgId AND t.deleted_at IS NULL) AS total_participants
                """,
                new { orgId });
            return Results.Ok(stats);
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

public sealed record CreateOrganizationRequest(
    string                      Name,
    string?                     Description = null,
    string?                     LogoUrl     = null,
    string?                     BannerUrl   = null,
    Dictionary<string, string>? SocialLinks = null);

public sealed record UpdateOrganizationRequest(
    string?                     Name        = null,
    string?                     Description = null,
    Dictionary<string, string>? SocialLinks = null);
