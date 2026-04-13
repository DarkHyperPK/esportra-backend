using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Infrastructure.Email;
using Esportra.Infrastructure.Supabase;
using Microsoft.AspNetCore.Mvc;

namespace Esportra.Api.Endpoints;

public static class VenueStaffEndpoints
{
    public static void MapVenueStaffEndpoints(this WebApplication app)
    {
        // ── POST /api/venues/{id}/staff/invite ─────────────────────────────────
        // Owner invites staff: creates Supabase account if needed, sends magic link.
        app.MapPost("/api/venues/{id}/staff/invite", async (
            Guid                    id,
            [FromBody] InviteStaffRequest req,
            HttpContext              ctx,
            IDbConnectionFactory    db,
            ISupabaseAdminClient    supabase,
            IEmailService           email,
            ILogger<Program>        logger,
            CancellationToken       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // 1. Verify caller is venue owner
            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM venue_staff WHERE venue_id = @id AND user_id = @userId AND role = 'owner' AND accepted_at IS NOT NULL)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            // 2. Validate input
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Role))
                return Results.BadRequest(new { error = "Email and role are required." });

            var normalizedEmail = req.Email.Trim().ToLowerInvariant();
            if (req.Role is not ("manager" or "cashier"))
                return Results.BadRequest(new { error = "Role must be 'manager' or 'cashier'." });

            // 3. Check for existing invite or staff record
            var existingStaff = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id FROM venue_staff WHERE venue_id = @id AND user_id IN (SELECT id FROM auth.users WHERE email = @email) LIMIT 1",
                new { id, email = normalizedEmail });
            if (existingStaff is not null)
                return Results.Conflict(new { error = "This person is already staff at this venue." });

            var pendingInvite = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id FROM venue_staff_invites WHERE venue_id = @id AND email = @email AND used_at IS NULL AND expires_at > NOW() LIMIT 1",
                new { id, email = normalizedEmail });
            if (pendingInvite is not null)
                return Results.Conflict(new { error = "An active invite already exists for this email." });

            // 4. Find or create Supabase account
            Guid staffUserId;
            bool isNewAccount = false;
            var existingUser = await supabase.GetUserByEmailAsync(normalizedEmail, ct);

            if (existingUser is not null)
            {
                staffUserId = Guid.Parse(existingUser.Id);
            }
            else
            {
                var newUser = await supabase.CreateUserAsync(normalizedEmail, new
                {
                    display_name = req.Name?.Trim(),
                    phone = req.Phone?.Trim(),
                    venue_staff = true,
                }, ct);
                staffUserId = Guid.Parse(newUser.Id);
                isNewAccount = true;
            }

            // 5. Get venue name for the email
            var venueName = await conn.ExecuteScalarAsync<string>(
                "SELECT name FROM venues WHERE id = @id", new { id }) ?? "your venue";

            // 6. Create invite record with token
            var inviteToken = Guid.NewGuid().ToString("N");
            var inviteId = await conn.ExecuteScalarAsync<Guid>(
                """
                INSERT INTO venue_staff_invites (venue_id, email, role, invited_by, token)
                VALUES (@venueId, @email, @role, @invitedBy, @token)
                RETURNING id
                """,
                new { venueId = id, email = normalizedEmail, role = req.Role, invitedBy = userCtx.UserIdGuid, token = inviteToken });

            // 7. Send recovery link so staff can set their password
            try
            {
                var link = await supabase.GenerateRecoveryLinkAsync(normalizedEmail, ct);
                await email.SendAsync(normalizedEmail, EmailType.StaffInvite, new
                {
                    venueName,
                    staffName = req.Name?.Trim() ?? "Team Member",
                    role = req.Role,
                    setupUrl = link.ActionLink,
                }, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send staff invite email to {Email}", normalizedEmail);
                // Don't fail the invite — the owner can re-send later
            }

            return Results.Ok(new
            {
                inviteId,
                email = normalizedEmail,
                role = req.Role,
                name = req.Name?.Trim(),
                isNewAccount,
                staffUserId,
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/{id}/staff ─────────────────────────────────────────
        // List active staff + pending invites for this venue.
        app.MapGet("/api/venues/{id}/staff", async (
            Guid                 id,
            HttpContext           ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Must be a staff member to view the list
            var isMember = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM venue_staff WHERE venue_id = @id AND user_id = @userId AND accepted_at IS NOT NULL)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isMember) return Results.Forbid();

            var staff = await conn.QueryAsync<dynamic>(
                """
                SELECT vs.id, vs.user_id, vs.role, vs.invited_at, vs.accepted_at,
                       u.raw_user_meta_data->>'display_name' AS display_name,
                       u.email,
                       u.raw_user_meta_data->>'avatar_url' AS avatar_url
                FROM venue_staff vs
                JOIN auth.users u ON u.id = vs.user_id
                WHERE vs.venue_id = @id
                ORDER BY vs.role = 'owner' DESC, vs.accepted_at ASC
                """,
                new { id });

            var pendingInvites = await conn.QueryAsync<dynamic>(
                """
                SELECT id, email, role, expires_at,
                       invited_by, (NOW() > expires_at) AS is_expired
                FROM venue_staff_invites
                WHERE venue_id = @id AND used_at IS NULL
                ORDER BY expires_at DESC
                """,
                new { id });

            return Results.Ok(new { staff, pendingInvites });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/venues/{id}/staff/{staffId} ──────────────────────────────
        // Owner updates a staff member's role.
        app.MapPut("/api/venues/{id}/staff/{staffId}", async (
            Guid                          id,
            Guid                          staffId,
            [FromBody] UpdateStaffRequest req,
            HttpContext                   ctx,
            IDbConnectionFactory         db,
            CancellationToken            ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM venue_staff WHERE venue_id = @id AND user_id = @userId AND role = 'owner' AND accepted_at IS NOT NULL)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            if (req.Role is not ("manager" or "cashier"))
                return Results.BadRequest(new { error = "Role must be 'manager' or 'cashier'." });

            // Prevent changing the owner's own role
            var target = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT user_id, role FROM venue_staff WHERE id = @staffId AND venue_id = @id",
                new { staffId, id });
            if (target is null) return Results.NotFound();
            if ((string)target.role == "owner")
                return Results.BadRequest(new { error = "Cannot change the owner's role." });

            await conn.ExecuteAsync(
                "UPDATE venue_staff SET role = @role WHERE id = @staffId AND venue_id = @id",
                new { role = req.Role, staffId, id });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/venues/{id}/staff/{staffId} ───────────────────────────
        // Owner removes a staff member.
        app.MapDelete("/api/venues/{id}/staff/{staffId}", async (
            Guid                 id,
            Guid                 staffId,
            HttpContext           ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM venue_staff WHERE venue_id = @id AND user_id = @userId AND role = 'owner' AND accepted_at IS NOT NULL)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            var target = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT role FROM venue_staff WHERE id = @staffId AND venue_id = @id",
                new { staffId, id });
            if (target is null) return Results.NotFound();
            if ((string)target.role == "owner")
                return Results.BadRequest(new { error = "Cannot remove the venue owner." });

            await conn.ExecuteAsync(
                "DELETE FROM venue_staff WHERE id = @staffId AND venue_id = @id",
                new { staffId, id });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/venues/staff/accept ─────────────────────────────────────
        // Staff accepts an invite using the invite token (auto-called on first login).
        app.MapPost("/api/venues/staff/accept", async (
            [FromBody] AcceptInviteRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory          db,
            CancellationToken             ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Lookup by token or by email match
            dynamic? invite;
            if (!string.IsNullOrWhiteSpace(req.Token))
            {
                invite = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    """
                    SELECT id, venue_id, email, role FROM venue_staff_invites
                    WHERE token = @token AND used_at IS NULL AND expires_at > NOW()
                    """,
                    new { token = req.Token });
            }
            else
            {
                // Auto-accept: find pending invite for this user's email
                var userEmail = await conn.ExecuteScalarAsync<string>(
                    "SELECT email FROM auth.users WHERE id = @userId",
                    new { userId = userCtx.UserIdGuid });

                invite = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    """
                    SELECT id, venue_id, email, role FROM venue_staff_invites
                    WHERE email = @email AND used_at IS NULL AND expires_at > NOW()
                    ORDER BY invited_by DESC LIMIT 1
                    """,
                    new { email = userEmail });
            }

            if (invite is null)
                return Results.NotFound(new { error = "No valid invite found." });

            // Check not already a member
            var alreadyMember = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM venue_staff WHERE venue_id = @venueId AND user_id = @userId)",
                new { venueId = (Guid)invite.venue_id, userId = userCtx.UserIdGuid });

            if (!alreadyMember)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO venue_staff (venue_id, user_id, role, invited_by, accepted_at)
                    VALUES (@venueId, @userId, @role, (SELECT invited_by FROM venue_staff_invites WHERE id = @inviteId), NOW())
                    """,
                    new { venueId = (Guid)invite.venue_id, userId = userCtx.UserIdGuid, role = (string)invite.role, inviteId = (Guid)invite.id });
            }

            // Mark invite as used
            await conn.ExecuteAsync(
                "UPDATE venue_staff_invites SET used_at = NOW() WHERE id = @inviteId",
                new { inviteId = (Guid)invite.id });

            // Return venue info
            var venue = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, name FROM venues WHERE id = @venueId",
                new { venueId = (Guid)invite.venue_id });

            return Results.Ok(new
            {
                accepted = true,
                venueId = (Guid)invite.venue_id,
                venueName = venue?.name ?? "Unknown",
                role = (string)invite.role,
            });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/venues/staff/my-venues ───────────────────────────────────
        // Staff sees all venues they belong to.
        app.MapGet("/api/venues/staff/my-venues", async (
            HttpContext           ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var venues = await conn.QueryAsync<dynamic>(
                """
                -- Venues where user is direct owner
                SELECT v.id, v.name, v.address, v.city, v.country, v.card_image,
                       'owner' AS role, v.created_at AS accepted_at
                FROM venues v
                WHERE v.owner_id = @userId AND v.deleted_at IS NULL

                UNION

                -- Venues where user is staff member (manager/cashier)
                SELECT v.id, v.name, v.address, v.city, v.country, v.card_image,
                       vs.role, vs.accepted_at
                FROM venue_staff vs
                JOIN venues v ON v.id = vs.venue_id AND v.deleted_at IS NULL
                WHERE vs.user_id = @userId AND vs.accepted_at IS NOT NULL
                  AND v.owner_id != @userId

                ORDER BY accepted_at DESC
                """,
                new { userId = userCtx.UserIdGuid });

            // Also return pending invites for this user's email
            var userEmail = await conn.ExecuteScalarAsync<string>(
                "SELECT email FROM auth.users WHERE id = @userId",
                new { userId = userCtx.UserIdGuid });

            var pendingInvites = await conn.QueryAsync<dynamic>(
                """
                SELECT vsi.id AS invite_id, vsi.venue_id, vsi.role, vsi.expires_at,
                       v.name AS venue_name
                FROM venue_staff_invites vsi
                JOIN venues v ON v.id = vsi.venue_id AND v.deleted_at IS NULL
                WHERE vsi.email = @email AND vsi.used_at IS NULL AND vsi.expires_at > NOW()
                ORDER BY vsi.expires_at DESC
                """,
                new { email = userEmail });

            return Results.Ok(new { venues, pendingInvites });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/venues/{id}/staff/resend ────────────────────────────────
        // Owner re-sends the invite email for a pending invite.
        app.MapPost("/api/venues/{id}/staff/resend/{inviteId}", async (
            Guid                    id,
            Guid                    inviteId,
            HttpContext              ctx,
            IDbConnectionFactory    db,
            ISupabaseAdminClient    supabase,
            IEmailService           email,
            ILogger<Program>        logger,
            CancellationToken       ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            var isOwner = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM venue_staff WHERE venue_id = @id AND user_id = @userId AND role = 'owner' AND accepted_at IS NOT NULL)",
                new { id, userId = userCtx.UserIdGuid });
            if (!isOwner) return Results.Forbid();

            var invite = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT email, role FROM venue_staff_invites WHERE id = @inviteId AND venue_id = @id AND used_at IS NULL",
                new { inviteId, id });
            if (invite is null) return Results.NotFound(new { error = "Invite not found." });

            var venueName = await conn.ExecuteScalarAsync<string>(
                "SELECT name FROM venues WHERE id = @id", new { id }) ?? "your venue";

            // Reset expiry
            await conn.ExecuteAsync(
                "UPDATE venue_staff_invites SET expires_at = NOW() + INTERVAL '7 days' WHERE id = @inviteId",
                new { inviteId });

            try
            {
                var link = await supabase.GenerateRecoveryLinkAsync((string)invite.email, ct);
                await email.SendAsync((string)invite.email, EmailType.StaffInvite, new
                {
                    venueName,
                    staffName = "Team Member",
                    role = (string)invite.role,
                    setupUrl = link.ActionLink,
                }, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resend invite to {Email}", (string)invite.email);
                return Results.Json(new { error = "Failed to send email. Try again later." }, statusCode: 502);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");
    }

    // ── Request DTOs ──────────────────────────────────────────────────────────
    private sealed record InviteStaffRequest(string Email, string Role, string? Name, string? Phone);
    private sealed record UpdateStaffRequest(string Role);
    private sealed record AcceptInviteRequest(string? Token);
}
