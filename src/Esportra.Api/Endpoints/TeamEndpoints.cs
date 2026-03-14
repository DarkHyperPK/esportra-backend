using System.Data;
using System.Dynamic;
using System.Text.Json;
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
        // ── GET /api/teams ──────────────────────────────────────────────────
        // List/search teams with optional filters (ids, owner_id).
        app.MapGet("/api/teams", async (
            string?              ids,
            string?              owner_id,
            string?              q,
            int                  limit  = 50,
            int                  offset = 0,
            IDbConnectionFactory db     = null!,
            CancellationToken    ct     = default) =>
        {
            using var conn = db.CreateConnection();

            if (!string.IsNullOrWhiteSpace(ids))
            {
                var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : (Guid?)null)
                    .Where(g => g.HasValue)
                    .Select(g => g!.Value)
                    .ToArray();
                if (idList.Length == 0) return Results.Ok(Array.Empty<object>());
                var byIds = await conn.QueryAsync<dynamic>(
                    "SELECT * FROM teams WHERE id = ANY(@idList)", new { idList });
                return Results.Ok(byIds);
            }

            Guid? ownerGuid = Guid.TryParse(owner_id, out var og) ? og : null;
            var teams = await conn.QueryAsync<dynamic>(
                """
                SELECT * FROM teams
                WHERE (@ownerGuid IS NULL OR owner_id = @ownerGuid)
                  AND (@q IS NULL OR name ILIKE '%' || @q || '%')
                ORDER BY created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { ownerGuid, q, limit, offset });
            return Results.Ok(teams);
        });

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
                               'joined_at',  tm.joined_at,
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
                new { userId = userCtx.UserIdGuid });

            foreach (var t in teams) ParseJsonbFields(t, "members");
            return Results.Ok(teams);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id} ───────────────────────────────────────────────
        app.MapGet("/api/teams/{id}", async (
            Guid                 id,
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
                               'joined_at',  tm.joined_at,
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

            if (team is null) return Results.NotFound();
            ParseJsonbFields(team, "members");
            return Results.Ok(team);
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
                                      owner_id, is_active, country_code)
                    VALUES (@name, @tag, @game, @gameFormat, @logoUrl, @description,
                            @ownerId, TRUE, @countryCode)
                    RETURNING *
                    """,
                    new
                    {
                        name        = req.Name,
                        tag         = req.Tag,
                        game        = req.Game ?? "General",
                        gameFormat  = req.GameFormat ?? "squad",
                        logoUrl     = req.LogoUrl,
                        description = req.Description,
                        ownerId     = userCtx.UserIdGuid,
                        countryCode = req.CountryCode,
                    },
                    tx);

                Guid teamId = (Guid)team.id;

                // Insert creator as captain
                await conn.ExecuteAsync(
                    "INSERT INTO team_members (team_id, user_id, role, is_active) VALUES (@teamId, @userId, 'captain', TRUE)",
                    new { teamId, userId = userCtx.UserIdGuid }, tx);

                // Insert additional invited members (if pre-seeding roster)
                if (req.Members is { Count: > 0 })
                {
                    var memberRows = req.Members
                        .Where(m => m.UserId != userCtx.UserId)
                        .Select(m => new { teamId, userId = Guid.Parse(m.UserId), role = m.Role })
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
            Guid                     id,
            [FromBody] UpdateTeamRequest req,
            HttpContext               ctx,
            IDbConnectionFactory     db,
            CancellationToken        ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

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
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertOwner(conn, id, userCtx.UserIdGuid);

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
            Guid                 id,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Owner cannot leave — must transfer captaincy first
            var isOwner = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT owner_id = @userId FROM teams WHERE id = @id", new { id, userId = userCtx.UserIdGuid });
            if (isOwner)
                return Results.BadRequest(new { error = "Transfer captaincy before leaving." });

            await conn.ExecuteAsync(
                "DELETE FROM team_members WHERE team_id = @id AND user_id = @userId",
                new { id, userId = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/{id}/members/{userId} ───────────────────────────
        app.MapDelete("/api/teams/{id}/members/{userId}", async (
            Guid                 id,
            Guid                 userId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            await conn.ExecuteAsync(
                "DELETE FROM team_members WHERE team_id = @id AND user_id = @userId",
                new { id, userId });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/transfer-captain ─────────────────────────────
        // Atomic: demote old captain → promote new → transfer team.owner_id → update registrations
        app.MapPost("/api/teams/{id}/transfer-captain", async (
            Guid                              id,
            [FromBody] TransferCaptainRequest  req,
            HttpContext                        ctx,
            IDbConnectionFactory              db,
            CancellationToken                 ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertOwner(conn, id, userCtx.UserIdGuid);

            using var tx = conn.BeginTransaction();
            try
            {
                // Step 1: demote current captain
                await conn.ExecuteAsync(
                    "UPDATE team_members SET role = 'member' WHERE team_id = @id AND user_id = @userId AND role = 'captain'",
                    new { id, userId = userCtx.UserIdGuid }, tx);

                var newCaptainIdGuid = Guid.Parse(req.NewCaptainId);

                // Step 2: promote new captain (must already be a member)
                var affected = await conn.ExecuteAsync(
                    "UPDATE team_members SET role = 'captain' WHERE team_id = @id AND user_id = @newCaptainId",
                    new { id, newCaptainId = newCaptainIdGuid }, tx);

                if (affected == 0)
                {
                    tx.Rollback();
                    return Results.BadRequest(new { error = "New captain is not a team member." });
                }

                // Step 3: transfer ownership
                await conn.ExecuteAsync(
                    "UPDATE teams SET owner_id = @newCaptainId, updated_at = NOW() WHERE id = @id",
                    new { id, newCaptainId = newCaptainIdGuid }, tx);

                // Step 4: update tournament registrations (non-critical)
                await conn.ExecuteAsync(
                    "UPDATE tournament_participants SET team_captain_id = @newCaptainId WHERE team_id = @id",
                    new { id, newCaptainId = newCaptainIdGuid }, tx);

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
            Guid                        id,
            [FromBody] TeamInviteRequest req,
            HttpContext                  ctx,
            IDbConnectionFactory        db,
            CancellationToken           ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var reqUserIdGuid = Guid.Parse(req.UserId);

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            // Check not already a member
            var alreadyMember = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @id AND user_id = @userId AND is_active = TRUE)",
                new { id, userId = reqUserIdGuid });
            if (alreadyMember)
                return Results.Conflict(new { error = "User is already a team member." });

            // Check for existing invite (any status) and upsert
            var existingInviteId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT id FROM team_invitations WHERE team_id = @teamId AND invited_user_id = @userId",
                new { teamId = id, userId = reqUserIdGuid });

            dynamic invite;
            if (existingInviteId is not null)
            {
                invite = await conn.QuerySingleAsync<dynamic>(
                    """
                    UPDATE team_invitations
                    SET status = 'pending', message = @message, created_at = NOW(), responded_at = NULL
                    WHERE id = @id
                    RETURNING *
                    """,
                    new { id = existingInviteId.Value, message = req.Message });
            }
            else
            {
                invite = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO team_invitations (team_id, invited_user_id, invited_by_user_id, invited_by, status, message)
                    VALUES (@teamId, @userId, @invitedBy, @invitedBy, 'pending', @message)
                    RETURNING *
                    """,
                    new { teamId = id, userId = reqUserIdGuid, invitedBy = userCtx.UserIdGuid, message = req.Message });
            }

            // Notification to invitee
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'team_invite', 'Team Invitation',
                        'You have been invited to join a team.', '/teams', @data::jsonb, FALSE)
                """,
                new { userId = reqUserIdGuid, data = System.Text.Json.JsonSerializer.Serialize(new { team_id = id, invite_id = ((Guid)invite.id).ToString() }) });

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
                JOIN profiles p ON p.id = ti.invited_by_user_id
                WHERE ti.invited_user_id = @userId AND ti.status = 'pending'
                ORDER BY ti.created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });

            return Results.Ok(invites);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/invites/{inviteId}/accept ─────────────────────────
        app.MapPost("/api/teams/invites/{inviteId}/accept", async (
            Guid                 inviteId,
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
                    "SELECT * FROM team_invitations WHERE id = @id AND invited_user_id = @userId AND status = 'pending'",
                    new { id = inviteId, userId = userCtx.UserIdGuid }, tx);

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
                    new { teamId = invite.team_id, userId = userCtx.UserIdGuid }, tx);

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
            Guid                 inviteId,
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE team_invitations SET status = 'declined', responded_at = NOW() WHERE id = @id AND invited_user_id = @userId",
                new { id = inviteId, userId = userCtx.UserIdGuid });

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/invites/{inviteId} — revoke (captain only) ─────
        app.MapDelete("/api/teams/invites/{inviteId}", async (
            Guid                 inviteId,
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
                new { id = inviteId, userId = userCtx.UserIdGuid });

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

        // ── POST /api/teams/batch ─────────────────────────────────────────────
        // Batch team lookup by IDs
        app.MapPost("/api/teams/batch", async (
            [FromBody] TeamBatchRequest req,
            IDbConnectionFactory        db,
            CancellationToken           ct) =>
        {
            using var conn = db.CreateConnection();
            if (req.Ids.Count == 0) return Results.Ok(Array.Empty<object>());

            var teams = await conn.QueryAsync<dynamic>(
                "SELECT id, name, logo_url FROM teams WHERE id = ANY(@ids)",
                new { ids = req.Ids.Select(Guid.Parse).ToArray() });
            return Results.Ok(teams);
        });

        // ── Additional team endpoints ─────────────────────────────────────────
        MapTeamExtendedEndpoints(app);
    }

    private static void MapTeamExtendedEndpoints(WebApplication app)
    {
        // ── GET /api/teams/{id}/stats ─────────────────────────────────────────
        app.MapGet("/api/teams/{id}/stats", async (
            Guid                 id,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var matches = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM brkt_matches WHERE (team1_id = @id OR team2_id = @id) AND status = 'completed'",
                new { id });
            var wins = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM brkt_matches WHERE winner_id = @id AND status = 'completed'",
                new { id });
            var tournamentWins = await conn.QuerySingleOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM tournaments WHERE winner_id = @id",
                new { id });

            return Results.Ok(new
            {
                matches,
                wins,
                winRate = matches > 0 ? Math.Round((double)wins / matches * 100) : 0,
                tournamentWins
            });
        });

        // ── GET /api/teams/{id}/members/detailed ─────────────────────────────
        app.MapGet("/api/teams/{id}/members/detailed", async (
            Guid                 id,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var members = await conn.QueryAsync<dynamic>(
                """
                SELECT tm.user_id, tm.role, p.username, p.email, p.avatar_url, p.card_image_url,
                       ra.puuid AS riot_puuid, ra.game_name AS riot_game_name, ra.tag_line AS riot_tag_line,
                       fa.faceit_id, fa.nickname AS faceit_nickname,
                       vs.kd, vs.win_rate, vs.hs_percent, vs.latest_match_id
                FROM team_members tm
                JOIN profiles p ON p.id = tm.user_id
                LEFT JOIN riot_accounts ra ON ra.user_id = tm.user_id
                LEFT JOIN faceit_accounts fa ON fa.user_id = tm.user_id
                LEFT JOIN leaderboard vs ON vs.user_id = tm.user_id AND vs.game = 'valorant'
                WHERE tm.team_id = @id AND tm.is_active = true
                """,
                new { id });
            return Results.Ok(members);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id}/invites ───────────────────────────────────────
        app.MapGet("/api/teams/{id}/invites", async (
            Guid                 id,
            HttpContext           ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            var invites = await conn.QueryAsync<dynamic>(
                """
                SELECT ti.id, ti.invited_email, ti.invited_user_id, ti.roster_id, ti.created_at,
                       p.username, p.avatar_url
                FROM team_invitations ti
                LEFT JOIN profiles p ON p.id = ti.invited_user_id
                WHERE ti.team_id = @id AND ti.status = 'pending'
                ORDER BY ti.created_at DESC
                """,
                new { id });
            return Results.Ok(invites);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/me/pending-invites ────────────────────────────────
        app.MapGet("/api/teams/me/pending-invites", async (
            HttpContext           ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var invites = await conn.QueryAsync<dynamic>(
                """
                SELECT ti.id, ti.team_id, ti.roster_id, t.name AS team_name,
                       tr.name AS roster_name
                FROM team_invitations ti
                JOIN teams t ON t.id = ti.team_id
                LEFT JOIN team_rosters tr ON tr.id = ti.roster_id
                WHERE (ti.invited_user_id = @userId)
                  AND ti.status = 'pending'
                """,
                new { userId = userCtx.UserIdGuid });
            return Results.Ok(invites);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id}/rosters ───────────────────────────────────────
        app.MapGet("/api/teams/{id}/rosters", async (
            Guid                 id,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var rosters = await conn.QueryAsync<dynamic>(
                """
                SELECT r.id, r.name, r.game, r.format, r.team_size,
                       COALESCE(jsonb_agg(
                           jsonb_build_object(
                               'user_id', rm.user_id,
                               'username', p.username,
                               'avatar_url', p.avatar_url,
                               'card_image_url', p.card_image_url,
                               'is_starter', rm.is_starter
                           ) ORDER BY rm.created_at
                       ) FILTER (WHERE rm.user_id IS NOT NULL), '[]') AS members
                FROM team_rosters r
                LEFT JOIN team_roster_members rm ON rm.roster_id = r.id
                LEFT JOIN profiles p ON p.id = rm.user_id
                WHERE r.team_id = @id
                GROUP BY r.id, r.name, r.game, r.format, r.team_size
                ORDER BY r.created_at DESC
                """,
                new { id });
            foreach (var r in rosters) ParseJsonbFields(r, "members");
            return Results.Ok(rosters);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/rosters ──────────────────────────────────────
        app.MapPost("/api/teams/{id}/rosters", async (
            Guid                      id,
            [FromBody] CreateRosterRequest req,
            HttpContext               ctx,
            IDbConnectionFactory      db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            var roster = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                INSERT INTO team_rosters (team_id, name, game, format, team_size)
                VALUES (@teamId, @name, @game, @format, @teamSize)
                RETURNING id, name, game, format, team_size
                """,
                new { teamId = id, name = req.Name, game = req.Game, format = req.Format, teamSize = req.TeamSize });

            return Results.Created($"/api/teams/{id}/rosters/{roster!.id}", roster);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/teams/{id}/rosters/{rosterId} ────────────────────────────
        app.MapPut("/api/teams/{id}/rosters/{rosterId}", async (
            Guid                      id,
            Guid                      rosterId,
            [FromBody] UpdateRosterRequest req,
            HttpContext               ctx,
            IDbConnectionFactory      db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            await conn.ExecuteAsync(
                "UPDATE team_rosters SET name = @name WHERE id = @rosterId AND team_id = @teamId",
                new { name = req.Name, rosterId, teamId = id });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/{id}/rosters/{rosterId} ─────────────────────────
        app.MapDelete("/api/teams/{id}/rosters/{rosterId}", async (
            Guid                 id,
            Guid                 rosterId,
            HttpContext           ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            await conn.ExecuteAsync(
                "DELETE FROM team_rosters WHERE id = @rosterId AND team_id = @teamId",
                new { rosterId, teamId = id });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id}/rosters/{rosterId}/members ────────────────────
        app.MapGet("/api/teams/{id}/rosters/{rosterId}/members", async (
            Guid                 id,
            Guid                 rosterId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT trm.user_id, trm.is_starter, trm.role,
                       p.username, p.full_name, p.avatar_url, p.riot_tag, p.steam_tag
                FROM team_roster_members trm
                LEFT JOIN profiles p ON p.id = trm.user_id
                WHERE trm.roster_id = @rosterId
                ORDER BY trm.is_starter DESC, p.username ASC
                """,
                new { rosterId });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/rosters/{rosterId}/members ───────────────────
        app.MapPost("/api/teams/{id}/rosters/{rosterId}/members", async (
            Guid                            id,
            Guid                            rosterId,
            [FromBody] RosterMemberRequest  req,
            HttpContext                     ctx,
            IDbConnectionFactory            db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            await conn.ExecuteAsync(
                "INSERT INTO team_roster_members (roster_id, user_id) VALUES (@rosterId, @userId) ON CONFLICT DO NOTHING",
                new { rosterId, userId = Guid.Parse(req.UserId) });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/{id}/rosters/{rosterId}/members/{userId} ────────
        app.MapDelete("/api/teams/{id}/rosters/{rosterId}/members/{userId}", async (
            Guid                 id,
            Guid                 rosterId,
            Guid                 userId,
            HttpContext           ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            await conn.ExecuteAsync(
                "DELETE FROM team_roster_members WHERE roster_id = @rosterId AND user_id = @userId",
                new { rosterId, userId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/teams/{id}/rosters/{rosterId}/members/{userId}/starter ───
        app.MapPut("/api/teams/{id}/rosters/{rosterId}/members/{userId}/starter", async (
            Guid                           id,
            Guid                           rosterId,
            Guid                           userId,
            [FromBody] ToggleStarterRequest req,
            HttpContext                    ctx,
            IDbConnectionFactory           db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            await conn.ExecuteAsync(
                "UPDATE team_roster_members SET is_starter = @isStarter WHERE roster_id = @rosterId AND user_id = @userId",
                new { isStarter = req.IsStarter, rosterId, userId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/rosters/{rosterId}/invite ────────────────────
        app.MapPost("/api/teams/{id}/rosters/{rosterId}/invite", async (
            Guid                         id,
            Guid                         rosterId,
            [FromBody] RosterInviteRequest req,
            HttpContext                  ctx,
            IDbConnectionFactory         db,
            IHubContext<NotificationHub> hub) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var reqUserIdGuid = Guid.Parse(req.UserId);
            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            // Check for duplicate pending invite
            var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(
                """
                SELECT id FROM team_invitations
                WHERE team_id = @teamId AND roster_id = @rosterId AND invited_user_id = @userId AND status = 'pending'
                """,
                new { teamId = id, rosterId, userId = reqUserIdGuid });
            if (existing is not null)
                return Results.Conflict(new { error = "Invite already pending" });

            // Check if user is already on a team
            var onTeam = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM team_members WHERE user_id = @userId AND is_active = true)",
                new { userId = reqUserIdGuid });
            if (onTeam)
                return Results.Conflict(new { error = "Player is already on a team" });

            var invite = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO team_invitations (team_id, roster_id, invited_user_id, invited_email, invited_by_user_id, invited_by, status)
                VALUES (@teamId, @rosterId, @userId, @email, @invitedBy, @invitedBy, 'pending')
                RETURNING id, invited_email, invited_user_id, created_at
                """,
                new { teamId = id, rosterId, userId = reqUserIdGuid, email = req.Email, invitedBy = userCtx.UserIdGuid });

            // Notification
            var teamName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM teams WHERE id = @id", new { id });
            var rosterName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM team_rosters WHERE id = @rosterId", new { rosterId });

            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'team_invite', 'Team Invitation',
                        @msg, '/player/teams', @data::jsonb, false)
                """,
                new
                {
                    userId = reqUserIdGuid,
                    msg = $"You have been invited to join {teamName}{(rosterName is not null ? $" ({rosterName})" : "")}.",
                    data = System.Text.Json.JsonSerializer.Serialize(new { team_id = id, roster_id = rosterId })
                });

            await hub.Clients.Group(NotificationHub.UserGroup(req.UserId))
                .SendAsync(NotificationHubEvents.NewNotification, new { type = "team_invite" });

            return Results.Ok(invite);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/announce ──────────────────────────────────────
        app.MapPost("/api/teams/{id}/announce", async (
            Guid                        id,
            [FromBody] AnnounceRequest  req,
            HttpContext                 ctx,
            IDbConnectionFactory        db,
            IHubContext<NotificationHub> hub) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            var teamName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM teams WHERE id = @id", new { id });
            var memberIds = (await conn.QueryAsync<Guid>(
                "SELECT user_id FROM team_members WHERE team_id = @id AND is_active = true",
                new { id })).ToList();

            if (memberIds.Count == 0) return Results.Ok(new { sent = 0 });

            // Batch insert notifications
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, data, is_read)
                SELECT uid, 'team_announcement', 'Team Announcement', @message,
                       jsonb_build_object('team_id', @teamId, 'team_name', @teamName), false
                FROM UNNEST(@userIds::uuid[]) AS uid
                """,
                new { message = req.Message, teamId = id, teamName, userIds = memberIds.ToArray() });

            // Push real-time
            foreach (var uid in memberIds)
                await hub.Clients.Group(NotificationHub.UserGroup(uid.ToString()))
                    .SendAsync(NotificationHubEvents.NewNotification, new { type = "team_announcement" });

            return Results.Ok(new { sent = memberIds.Count });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id}/registrations ─────────────────────────────────
        app.MapGet("/api/teams/{id}/registrations", async (
            Guid                 id,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var registrations = await conn.QueryAsync<dynamic>(
                """
                SELECT tp.*,
                       jsonb_build_object(
                           'id', t.id, 'name', t.name, 'start_date', t.start_date,
                           'game', t.game, 'prize_pool', t.prize_pool, 'slug', t.slug,
                           'winner_id', t.winner_id, 'status', t.status
                       ) AS tournaments
                FROM tournament_participants tp
                JOIN tournaments t ON t.id = tp.tournament_id
                WHERE tp.team_id = @id
                """,
                new { id });
            return Results.Ok(registrations);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/profiles/{id}/card-image ─────────────────────────────────
        app.MapPut("/api/profiles/{id}/card-image", async (
            Guid                        id,
            [FromBody] CardImageRequest req,
            HttpContext                 ctx,
            IDbConnectionFactory        db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Only allow updating own profile or admin
            if (userCtx.UserIdGuid != id && !userCtx.Roles.Contains("admin"))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE profiles SET card_image_url = @url WHERE id = @id",
                new { url = req.Url, id });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/rosters/{rosterId}/invite-batch ──────────────
        app.MapPost("/api/teams/{id}/rosters/{rosterId}/invite-batch", async (
            Guid                               id,
            Guid                               rosterId,
            [FromBody] BatchRosterInviteRequest req,
            HttpContext                        ctx,
            IDbConnectionFactory               db,
            IHubContext<NotificationHub>        hub) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            int sent = 0;
            foreach (var invitee in req.Invitees)
            {
                var inviteeUserIdGuid = Guid.Parse(invitee.UserId);
                // Check duplicate pending invite
                var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(
                    """
                    SELECT id FROM team_invitations
                    WHERE team_id = @teamId AND roster_id = @rosterId AND invited_user_id = @userId AND status = 'pending'
                    """,
                    new { teamId = id, rosterId, userId = inviteeUserIdGuid });
                if (existing is not null) continue;

                // Check already on a team
                var onTeam = await conn.QuerySingleOrDefaultAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM team_members WHERE user_id = @userId AND is_active = true)",
                    new { userId = inviteeUserIdGuid });
                if (onTeam) continue;

                await conn.ExecuteAsync(
                    """
                    INSERT INTO team_invitations (team_id, roster_id, invited_user_id, invited_email, invited_by_user_id, invited_by, status)
                    VALUES (@teamId, @rosterId, @userId, @email, @invitedBy, @invitedBy, 'pending')
                    """,
                    new { teamId = id, rosterId, userId = inviteeUserIdGuid, email = invitee.Email, invitedBy = userCtx.UserIdGuid });
                sent++;
            }

            return Results.Ok(new { sent });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/my-captain-teams ──────────────────────────────────
        // Returns teams where the current user is captain
        app.MapGet("/api/teams/my-captain-teams", async (
            HttpContext          ctx,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var teams = await conn.QueryAsync<dynamic>(
                """
                SELECT t.*
                FROM teams t
                JOIN team_members tm ON tm.team_id = t.id
                WHERE tm.user_id = @userId AND tm.role = 'captain' AND tm.is_active = TRUE
                ORDER BY t.name ASC
                """, new { userId = userCtx.UserIdGuid });
            return Results.Ok(teams);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/captains ───────────────────────────────────────────
        // Returns teams with their captain's profile info
        app.MapGet("/api/teams/captains", async (
            string?              game,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT t.id, t.name, t.tag, t.game, t.logo_url,
                       p.id AS captain_id, p.username AS captain_username, p.avatar_url AS captain_avatar
                FROM teams t
                JOIN team_members tm ON tm.team_id = t.id AND tm.role = 'captain' AND tm.is_active = TRUE
                JOIN profiles p ON p.id = tm.user_id
                WHERE (@game IS NULL OR t.game = @game)
                ORDER BY t.name ASC
                LIMIT 100
                """, new { game });
            return Results.Ok(rows);
        });

        // ── GET /api/teams/members ────────────────────────────────────────────
        // Returns team members for a given user_id or team_id
        // Supports: ?teamId=, ?userId=, ?team_ids=a,b&user_id=x&roles=captain,owner&is_active=true
        app.MapGet("/api/teams/members", async (
            HttpContext           ctx,
            Guid?                userId,
            Guid?                teamId,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();

            // ── Multi-team query (team_ids + user_id + roles + is_active) ──
            var teamIdsParam = ctx.Request.Query["team_ids"].FirstOrDefault();
            var userIdParam  = ctx.Request.Query["user_id"].FirstOrDefault();
            var rolesParam   = ctx.Request.Query["roles"].FirstOrDefault();

            if (!string.IsNullOrEmpty(teamIdsParam))
            {
                var teamIdList = teamIdsParam.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : (Guid?)null)
                    .Where(g => g.HasValue).Select(g => g!.Value).ToArray();

                if (teamIdList.Length == 0)
                    return Results.BadRequest(new { error = "Invalid team_ids" });

                Guid? filterUserId = null;
                if (!string.IsNullOrEmpty(userIdParam) && Guid.TryParse(userIdParam, out var uid))
                    filterUserId = uid;

                var sql = """
                    SELECT tm.*, p.username, p.avatar_url, p.riot_tag, p.steam_tag,
                           t.name AS team_name, t.logo_url AS team_logo
                    FROM team_members tm
                    JOIN profiles p ON p.id = tm.user_id
                    JOIN teams    t ON t.id = tm.team_id
                    WHERE tm.team_id = ANY(@teamIds)
                    """;

                if (filterUserId.HasValue)
                    sql += " AND tm.user_id = @filterUserId";

                if (!string.IsNullOrEmpty(rolesParam))
                {
                    var roles = rolesParam.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(r => r.Trim()).ToArray();
                    sql += " AND tm.role::text = ANY(@roles)";
                    sql += " AND tm.is_active = TRUE ORDER BY tm.role DESC";
                    var rows = await conn.QueryAsync<dynamic>(sql, new { teamIds = teamIdList, filterUserId, roles });
                    return Results.Ok(rows);
                }

                sql += " AND tm.is_active = TRUE ORDER BY tm.role DESC, p.username ASC";
                var result = await conn.QueryAsync<dynamic>(sql, new { teamIds = teamIdList, filterUserId });
                return Results.Ok(result);
            }

            if (teamId.HasValue)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT tm.*, p.username, p.avatar_url, p.riot_tag, p.steam_tag
                    FROM team_members tm
                    JOIN profiles p ON p.id = tm.user_id
                    WHERE tm.team_id = @teamId AND tm.is_active = TRUE
                    ORDER BY tm.role DESC, p.username ASC
                    """, new { teamId });
                return Results.Ok(rows);
            }

            // Support snake_case user_id param too
            var effectiveUserId = userId;
            if (!effectiveUserId.HasValue && !string.IsNullOrEmpty(userIdParam) && Guid.TryParse(userIdParam, out var parsedUid))
                effectiveUserId = parsedUid;

            if (effectiveUserId.HasValue)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    """
                    SELECT tm.*, t.name AS team_name, t.logo_url AS team_logo, t.game
                    FROM team_members tm
                    JOIN teams t ON t.id = tm.team_id
                    WHERE tm.user_id = @effectiveUserId AND tm.is_active = TRUE
                    ORDER BY tm.joined_at DESC
                    """, new { effectiveUserId });
                return Results.Ok(rows);
            }

            return Results.BadRequest(new { error = "user_id or team_id required" });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id}/captain ───────────────────────────────────────
        // Returns the current captain of a team
        app.MapGet("/api/teams/{id}/captain", async (
            Guid                 id,
            IDbConnectionFactory db,
            CancellationToken    ct) =>
        {
            using var conn = db.CreateConnection();
            var captain = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT tm.user_id, tm.role, tm.joined_at,
                       p.username, p.avatar_url, p.riot_tag
                FROM team_members tm
                JOIN profiles p ON p.id = tm.user_id
                WHERE tm.team_id = @id AND tm.role = 'captain' AND tm.is_active = TRUE
                LIMIT 1
                """, new { id });
            return captain is null ? Results.NotFound() : Results.Ok(captain);
        });
    }

    // ── Authorization helpers ─────────────────────────────────────────────────

    /// <summary>Throws 403 if the user is not the team owner (top-level owner_id check).</summary>
    private static async Task AssertOwner(IDbConnection conn, Guid teamId, Guid userId)
    {
        var isOwner = await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT owner_id = @userId FROM teams WHERE id = @teamId",
            new { teamId, userId });
        if (!isOwner)
            throw new UnauthorizedAccessException("Only the team owner can perform this action.");
    }

    /// <summary>Throws 403 if the user is not a captain of the team.</summary>
    private static async Task AssertCaptain(IDbConnection conn, Guid teamId, Guid userId)
    {
        var isCaptain = await conn.QuerySingleOrDefaultAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @teamId AND user_id = @userId AND role = 'captain')",
            new { teamId, userId });
        if (!isCaptain)
            throw new UnauthorizedAccessException("Only a team captain can perform this action.");
    }

    /// <summary>
    /// Dapper maps PostgreSQL jsonb columns to strings on ExpandoObject.
    /// This helper parses known JSON-string fields so they serialize as
    /// proper arrays/objects instead of escaped strings.
    /// </summary>
    private static void ParseJsonbFields(dynamic row, params string[] fields)
    {
        if (row is not IDictionary<string, object?> dict) return;
        foreach (var f in fields)
        {
            if (dict.TryGetValue(f, out var val) && val is string s)
            {
                try { dict[f] = JsonSerializer.Deserialize<JsonElement>(s); }
                catch { /* leave as string if not valid JSON */ }
            }
        }
    }
}

// ── Request records ───────────────────────────────────────────────────────────

public sealed record CreateTeamRequest(
    string  Name,
    string  Tag,
    string? Game        = "General",
    string? GameFormat  = "squad",
    string? LogoUrl     = null,
    string? Description = null,
    string? CountryCode = null,
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

public sealed record CreateRosterRequest(string Name, string Game, string? Format = null, int TeamSize = 5);

public sealed record UpdateRosterRequest(string Name);

public sealed record RosterMemberRequest(string UserId);

public sealed record ToggleStarterRequest(bool IsStarter);

public sealed record RosterInviteRequest(string UserId, string? Email = null);

public sealed record AnnounceRequest(string Message);

public sealed record CardImageRequest(string Url);

public sealed record BatchRosterInviteRequest(List<BatchInvitee> Invitees);

public sealed record BatchInvitee(string UserId, string? Email = null);

public sealed record TeamBatchRequest(List<string> Ids);
