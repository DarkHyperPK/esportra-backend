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

            using var conn = db.CreateConnection();
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

            using var conn = db.CreateConnection();

            // 1. Resolve user by email
            var profile = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id, email FROM profiles WHERE email ILIKE @email",
                new { email = req.UserEmail });
            if (profile is null)
                return Results.BadRequest(new { error = "User not found. They must have an Esportra account first." });

            string profileId = profile.id;

            // 2. Upsert staff record
            var existing = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT id FROM organization_staff WHERE organization_id = @orgId AND user_id = @profileId",
                new { orgId, profileId });

            string staffId;
            if (existing is not null)
            {
                staffId = existing.id;
                await conn.ExecuteAsync(
                    """
                    UPDATE organization_staff
                    SET role = @role, permissions = @permissions::text[], assigned_by = @assignedBy,
                        status = 'pending', accepted_at = NULL, responded_at = NULL, updated_at = NOW()
                    WHERE id = @staffId
                    """,
                    new { staffId, role = req.Role, permissions = req.Permissions, assignedBy = userCtx.UserId });
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
                    new { orgId, profileId, role = req.Role, permissions = req.Permissions, assignedBy = userCtx.UserId });
                staffId = inserted.id;
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
                    req.TournamentIds.Select(tid => new { staffId, tournamentId = tid, assignedBy = userCtx.UserId }));
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
                    userId  = profileId,
                    message = $"{req.InviterName ?? "An organizer"} invited you to staff {req.OrgName ?? "an organization"} as {FriendlyRole(req.Role)}.",
                    data    = $"{{\"link\":\"/staff/dashboard\",\"organization_staff_id\":\"{staffId}\",\"organization_id\":\"{orgId}\",\"role\":\"{req.Role}\"}}",
                });

            // Broadcast to user's SignalR session (if connected)
            await notifHub.Clients.Group($"user:{profileId}")
                .SendAsync("NewNotification", new { type = "staff_invite" }, ct);

            // 5. Audit log
            await LogAudit(conn, orgId, userCtx.UserId, "staff.invite", "staff", staffId,
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

            return Results.Ok(new { staffId });
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

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                """
                UPDATE organization_staff
                SET role = @role, permissions = @permissions::text[], updated_at = NOW()
                WHERE id = @staffId AND organization_id = @orgId
                """,
                new { staffId, orgId, role = req.Role, permissions = req.Permissions });

            await LogAudit(conn, orgId, userCtx.UserId, "staff.update_permissions", "staff", staffId,
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

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM organization_staff WHERE id = @staffId AND organization_id = @orgId",
                new { staffId, orgId });

            await LogAudit(conn, orgId, userCtx.UserId, "staff.remove", "staff", staffId, new { });
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
                new { userId = userCtx.UserId });
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
                new { userId = userCtx.UserId });
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

            using var conn = db.CreateConnection();

            var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT organization_id FROM organization_staff WHERE id = @inviteId AND user_id = @userId AND status = 'pending'",
                new { inviteId, userId = userCtx.UserId });
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
                    inviteId,
                    status     = req.Accept ? "active" : "declined",
                    acceptedAt = req.Accept ? now : (DateTime?)null,
                    now,
                });

            string orgId = invite.organization_id;
            await LogAudit(conn, orgId, userCtx.UserId,
                req.Accept ? "staff.accept" : "staff.decline", "staff", inviteId, new { });

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

            using var conn = db.CreateConnection();
            if (req.TournamentIds.Count > 0)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO staff_tournament_assignments (organization_staff_id, tournament_id, assigned_by)
                    VALUES (@staffId, @tournamentId, @assignedBy)
                    ON CONFLICT (organization_staff_id, tournament_id) DO NOTHING
                    """,
                    req.TournamentIds.Select(tid => new { staffId, tournamentId = tid, assignedBy = userCtx.UserId }));
            }

            await LogAudit(conn, orgId, userCtx.UserId, "staff.assign_tournament", "staff", staffId,
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

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "DELETE FROM staff_tournament_assignments WHERE id = @assignmentId",
                new { assignmentId });

            await LogAudit(conn, orgId, userCtx.UserId, "staff.unassign_tournament", "assignment", assignmentId, new { });
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
                new { orgId = organizationId, userId = userCtx.UserId });

            if (staff is null) return Results.Ok(Array.Empty<string>());

            // Admins get all permissions without a tournament assignment check
            if ((string)staff.role == "admin")
                return Results.Ok(staff.permissions ?? Array.Empty<string>());

            // Non-admins need an explicit tournament assignment
            if (string.IsNullOrEmpty(tournamentId)) return Results.Ok(Array.Empty<string>());

            var assignment = await conn.QuerySingleOrDefaultAsync<string>(
                """
                SELECT id FROM staff_tournament_assignments
                WHERE organization_staff_id = @staffId AND tournament_id = @tournamentId
                """,
                new { staffId = (string)staff.id, tournamentId });

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

            using var conn = db.CreateConnection();
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
                new { orgId, action, limit, offset });

            var total = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM staff_audit_log WHERE organization_id = @orgId",
                new { orgId });

            return Results.Ok(new { logs = rows, total });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/organizations/{orgId}/tournaments ────────────────────────
        app.MapGet("/api/organizations/{orgId}/tournaments", async (
            string               orgId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                "SELECT id, name, status FROM tournaments WHERE organization_id = @orgId ORDER BY created_at DESC",
                new { orgId });
            return Results.Ok(rows);
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FriendlyRole(string role) => role switch
    {
        "admin" => "an Administrator",
        "mod"   => "a Moderator",
        _       => "a Co-Host",
    };

    private static Task LogAudit(IDbConnection conn, string orgId, string actorId,
        string action, string? targetType, string? targetId, object details) =>
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
