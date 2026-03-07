using System.Data;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

/// <summary>
/// Domain 3: Teams &amp; Roster Management
///
/// Performance fix: replaces N+1 pattern (1 query per team for members)
/// with a single aggregated query using jsonb_agg.
///
/// Security: all write operations verify the caller is team owner/captain
/// via DB check — not just from the JWT claims.
/// </summary>
public static class TeamEndpoints
{
    public static void MapTeamEndpoints(this WebApplication app)
    {
        // ── GET /api/teams/me ─────────────────────────────────────────────────
        // Returns all teams where the user is a member or owner.
        // Single query replacing the previous 3-query + N RPC calls pattern.
        app.MapGet("/api/teams/me", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var teams = await conn.QueryAsync<dynamic>(
                """
                SELECT t.*,
                       COALESCE(jsonb_agg(
                           jsonb_build_object(
                               'id',         p.id,
                               'username',   p.username,
                               'full_name',  p.full_name,
                               'avatar_url', p.avatar_url,
                               'role',       tm.role,
                               'verified',   p.is_verified,
                               'joined_at',  tm.created_at,
                               'is_active',  tm.is_active
                           ) ORDER BY tm.role, p.username
                       ) FILTER (WHERE p.id IS NOT NULL), '[]'::jsonb) AS members
                FROM teams t
                LEFT JOIN team_members tm ON tm.team_id = t.id AND tm.is_active = TRUE
                LEFT JOIN profiles p ON p.id = tm.user_id
                WHERE t.id IN (
                    SELECT team_id FROM team_members WHERE user_id = @userId AND is_active = TRUE
                    UNION
                    SELECT id FROM teams WHERE owner_id = @userId
                )
                GROUP BY t.id
                ORDER BY t.created_at DESC
                """,
                new { userId = userCtx.UserId });

            return Results.Ok(teams);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id} ───────────────────────────────────────────────
        app.MapGet("/api/teams/{id}", async (
            string               id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var team = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT t.*,
                       COALESCE(jsonb_agg(
                           jsonb_build_object(
                               'id',         p.id,
                               'username',   p.username,
                               'full_name',  p.full_name,
                               'avatar_url', p.avatar_url,
                               'role',       tm.role,
                               'verified',   p.is_verified,
                               'joined_at',  tm.created_at,
                               'is_active',  tm.is_active
                           ) ORDER BY tm.role, p.username
                       ) FILTER (WHERE p.id IS NOT NULL), '[]'::jsonb) AS members
                FROM teams t
                LEFT JOIN team_members tm ON tm.team_id = t.id AND tm.is_active = TRUE
                LEFT JOIN profiles p ON p.id = tm.user_id
                WHERE t.id = @id
                GROUP BY t.id
                """,
                new { id });

            return team is null ? Results.NotFound() : Results.Ok(team);
        });

        // ── POST /api/teams ───────────────────────────────────────────────────
        app.MapPost("/api/teams", async (
            [FromBody] CreateTeamRequest req,
            HttpContext                  ctx,
            IDbConnectionFactory        db,
            CancellationToken           ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx   = conn.BeginTransaction();

            try
            {
                // Insert team
                var team = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO teams (name, tag, game, game_format, logo_url, description,
                                      owner_id, is_active, tournament_wins, total_matches)
                    VALUES (@name, @tag, @game, @gameFormat, @logoUrl, @description,
                            @ownerId, TRUE, 0, 0)
                    RETURNING *
                    """,
                    new
                    {
                        name        = req.Name,
                        tag         = req.Tag,
                        game        = req.Game,
                        gameFormat  = req.GameFormat,
                        logoUrl     = req.LogoUrl,
                        description = req.Description,
                        ownerId     = userCtx.UserId,
                    },
                    tx);

                string teamId = team.id;

                // Insert creator as captain
                await conn.ExecuteAsync(
                    "INSERT INTO team_members (team_id, user_id, role, is_active) VALUES (@teamId, @userId, 'captain', TRUE)",
                    new { teamId, userId = userCtx.UserId }, tx);

                // Insert additional invited members (if pre-seeding roster)
                if (req.Members is { Count: > 0 })
                {
                    var memberRows = req.Members
                        .Where(m => m.UserId != userCtx.UserId)
                        .Select(m => new { teamId, userId = m.UserId, role = m.Role })
                        .ToList();

                    if (memberRows.Count > 0)
                    {
                        await conn.ExecuteAsync(
                            "INSERT INTO team_members (team_id, user_id, role, is_active) VALUES (@teamId, @userId, @role, TRUE)",
                            memberRows, tx);
                    }
                }

                tx.Commit();
                return Results.Ok(team);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/teams/{id} ───────────────────────────────────────────────
        app.MapPut("/api/teams/{id}", async (
            string                   id,
            [FromBody] UpdateTeamRequest req,
            HttpContext               ctx,
            IDbConnectionFactory     db,
            CancellationToken        ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserId);

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE teams SET
                    name        = COALESCE(@name, name),
                    tag         = COALESCE(@tag,  tag),
                    description = COALESCE(@description, description),
                    logo_url    = COALESCE(@logoUrl, logo_url),
                    banner_url  = COALESCE(@bannerUrl, banner_url),
                    website_url = COALESCE(@websiteUrl, website_url),
                    country_code = COALESCE(@countryCode, country_code),
                    updated_at  = NOW()
                WHERE id = @id
                RETURNING *
                """,
                new
                {
                    id,
                    name        = req.Name,
                    tag         = req.Tag,
                    description = req.Description,
                    logoUrl     = req.LogoUrl,
                    bannerUrl   = req.BannerUrl,
                    websiteUrl  = req.WebsiteUrl,
                    countryCode = req.CountryCode,
                });

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/{id} — disband (cascade in a transaction) ───────
        app.MapDelete("/api/teams/{id}", async (
            string               id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertOwner(conn, id, userCtx.UserId);

            using var tx = conn.BeginTransaction();
            try
            {
                // Cascade: invitations → members → tournament registrations → team
                await conn.ExecuteAsync("DELETE FROM team_invitations WHERE team_id = @id", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM team_members WHERE team_id = @id",     new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM tournament_participants WHERE team_id = @id", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM teams WHERE id = @id",                 new { id }, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/leave ────────────────────────────────────────
        app.MapPost("/api/teams/{id}/leave", async (
            string               id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Owner cannot leave — must transfer captaincy first
            var isOwner = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT owner_id = @userId FROM teams WHERE id = @id", new { id, userId = userCtx.UserId });
            if (isOwner)
                return Results.BadRequest(new { error = "Transfer captaincy before leaving." });

            await conn.ExecuteAsync(
                "DELETE FROM team_members WHERE team_id = @id AND user_id = @userId",
                new { id, userId = userCtx.UserId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/{id}/members/{userId} ───────────────────────────
        app.MapDelete("/api/teams/{id}/members/{userId}", async (
            string               id,
            string               userId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserId);

            await conn.ExecuteAsync(
                "DELETE FROM team_members WHERE team_id = @id AND user_id = @userId",
                new { id, userId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/transfer-captain ─────────────────────────────
        // Atomic: demote old captain → promote new → transfer team.owner_id → update registrations
        app.MapPost("/api/teams/{id}/transfer-captain", async (
            string                            id,
            [FromBody] TransferCaptainRequest  req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertOwner(conn, id, userCtx.UserId);

            using var tx = conn.BeginTransaction();
            try
            {
                // Step 1: demote current captain
                await conn.ExecuteAsync(
                    "UPDATE team_members SET role = 'member' WHERE team_id = @id AND user_id = @userId AND role = 'captain'",
                    new { id, userId = userCtx.UserId }, tx);

                // Step 2: promote new captain (must already be a member)
                var affected = await conn.ExecuteAsync(
                    "UPDATE team_members SET role = 'captain' WHERE team_id = @id AND user_id = @newCaptainId",
                    new { id, newCaptainId = req.NewCaptainId }, tx);

                if (affected == 0)
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = "New captain is not a team member." });
                }

                // Step 3: transfer ownership
                await conn.ExecuteAsync(
                    "UPDATE teams SET owner_id = @newCaptainId, updated_at = NOW() WHERE id = @id",
                    new { id, newCaptainId = req.NewCaptainId }, tx);

                // Step 4: update tournament registrations (non-critical)
                await conn.ExecuteAsync(
                    "UPDATE tournament_participants SET team_captain_id = @newCaptainId WHERE team_id = @id",
                    new { id, newCaptainId = req.NewCaptainId }, tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/invite ───────────────────────────────────────
        app.MapPost("/api/teams/{id}/invite", async (
            string                      id,
            [FromBody] TeamInviteRequest req,
            HttpContext                  ctx,
            IDbConnectionFactory        db,
            CancellationToken           ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserId);

            // Check not already a member
            var alreadyMember = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @id AND user_id = @userId AND is_active = TRUE)",
                new { id, userId = req.UserId });
            if (alreadyMember)
                return Results.Conflict(new { error = "User is already a team member." });

            // Upsert invite (replace declined with fresh pending)
            var invite = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO team_invitations (team_id, user_id, invited_by, status, message)
                VALUES (@teamId, @userId, @invitedBy, 'pending', @message)
                ON CONFLICT (team_id, user_id)
                    DO UPDATE SET status = 'pending', message = EXCLUDED.message,
                                  created_at = NOW(), responded_at = NULL
                RETURNING *
                """,
                new { teamId = id, userId = req.UserId, invitedBy = userCtx.UserId, message = req.Message });

            // Notification to invitee
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'team_invite', 'Team Invitation',
                        'You have been invited to join a team.', '/teams', @data::jsonb, FALSE)
                """,
                new { userId = req.UserId, data = $"{{\"team_id\":\"{id}\",\"invite_id\":\"{(string?)invite.id}\"}}" });

            return Results.Ok(invite);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/me/invites ─────────────────────────────────────────
        app.MapGet("/api/teams/me/invites", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var invites = await conn.QueryAsync<dynamic>(
                """
                SELECT ti.*,
                       jsonb_build_object('name', t.name, 'game', t.game, 'logo_url', t.logo_url) AS team,
                       jsonb_build_object('username', p.username, 'avatar_url', p.avatar_url)      AS inviter
                FROM team_invitations ti
                JOIN teams    t ON t.id = ti.team_id
                JOIN profiles p ON p.id = ti.invited_by
                WHERE ti.user_id = @userId AND ti.status = 'pending'
                ORDER BY ti.created_at DESC
                """,
                new { userId = userCtx.UserId });

            return Results.Ok(invites);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/invites/{inviteId}/accept ─────────────────────────
        app.MapPost("/api/teams/invites/{inviteId}/accept", async (
            string               inviteId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx   = conn.BeginTransaction();

            try
            {
                var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT * FROM team_invitations WHERE id = @id AND user_id = @userId AND status = 'pending'",
                    new { id = inviteId, userId = userCtx.UserId }, tx);

                if (invite is null)
                    return Results.NotFound(new { error = "Invite not found or already responded to." });

                await conn.ExecuteAsync(
                    "UPDATE team_invitations SET status = 'accepted', responded_at = NOW() WHERE id = @id",
                    new { id = inviteId }, tx);

                await conn.ExecuteAsync(
                    """
                    INSERT INTO team_members (team_id, user_id, role, is_active)
                    VALUES (@teamId, @userId, 'member', TRUE)
                    ON CONFLICT (team_id, user_id) DO UPDATE SET is_active = TRUE, role = 'member'
                    """,
                    new { teamId = (string)invite.team_id, userId = userCtx.UserId }, tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/invites/{inviteId}/decline ────────────────────────
        app.MapPost("/api/teams/invites/{inviteId}/decline", async (
            string               inviteId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE team_invitations SET status = 'declined', responded_at = NOW() WHERE id = @id AND user_id = @userId",
                new { id = inviteId, userId = userCtx.UserId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/invites/{inviteId} — revoke (captain only) ─────
        app.MapDelete("/api/teams/invites/{inviteId}", async (
            string               inviteId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Verify the caller invited this person (is captain of that team)
            var invite = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT team_id FROM team_invitations WHERE id = @id AND invited_by = @userId",
                new { id = inviteId, userId = userCtx.UserId });

            if (invite is null) return Results.Forbid();

            await conn.ExecuteAsync("DELETE FROM team_invitations WHERE id = @id", new { id = inviteId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/profiles/verified ────────────────────────────────────────
        // Used by the team invite modal to search for verified players.
        app.MapGet("/api/profiles/verified", async (
            string?              q,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var users = await conn.QueryAsync<dynamic>(
                """
                SELECT id, username, full_name, avatar_url, is_verified, email
                FROM profiles
                WHERE is_verified = TRUE
                  AND (@q IS NULL OR username ILIKE '%' || @q || '%' OR full_name ILIKE '%' || @q || '%')
                ORDER BY username
                LIMIT 50
                """,
                new { q });

            return Results.Ok(users);
        }).RequireAuthorization("Authenticated");
    }

    // ── Authorization helpers ─────────────────────────────────────────────────

    /// <summary>Throws 403 if the user is not the team owner (top-level owner_id check).</summary>
    private static async Task AssertOwner(IDbConnection conn, string teamId, string userId)
    {
        var isOwner = await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT owner_id = @userId FROM teams WHERE id = @teamId",
            new { teamId, userId });
        if (!isOwner)
            throw new UnauthorizedAccessException("Only the team owner can perform this action.");
    }

    /// <summary>Throws 403 if the user is not a captain of the team.</summary>
    private static async Task AssertCaptain(IDbConnection conn, string teamId, string userId)
    {
        var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @teamId AND user_id = @userId AND role = 'captain')",
            new { teamId, userId });
        if (!isCaptain)
            throw new UnauthorizedAccessException("Only a team captain can perform this action.");
    }
}

// ── Request records ───────────────────────────────────────────────────────────

public sealed record CreateTeamRequest(
    string  Name,
    string  Tag,
    string  Game,
    string  GameFormat,
    string? LogoUrl     = null,
    string? Description = null,
    List<TeamMemberSeed>? Members = null);

public sealed record TeamMemberSeed(string UserId, string Role = "member");

public sealed record UpdateTeamRequest(
    string? Name        = null,
    string? Tag         = null,
    string? Description = null,
    string? LogoUrl     = null,
    string? BannerUrl   = null,
    string? WebsiteUrl  = null,
    string? CountryCode = null);

public sealed record TransferCaptainRequest(string NewCaptainId);

public sealed record TeamInviteRequest(string UserId, string? Message = null);
