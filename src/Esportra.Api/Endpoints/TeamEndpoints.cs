using System.Data;
using System.Dynamic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Esportra.Api.Helpers;
using Npgsql;
using Esportra.Api.Hubs;
using Esportra.Api.Middleware;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Core.Tournaments;
using Esportra.Infrastructure.Email;
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
            string? ids,
            string? owner_id,
            string? game,
            string? q,
            int limit = 50,
            int offset = 0,
            IDbConnectionFactory db = null!,
            CancellationToken ct = default) =>
        {
            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);
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
                    """
                    SELECT id::text, name, logo_url, 'team' AS competitor_kind
                    FROM teams
                    WHERE id = ANY(@idList)
                    UNION ALL
                    SELECT tp.id::text,
                           COALESCE(tp.team_name, p.username, 'Player') AS name,
                           p.avatar_url AS logo_url,
                           CASE WHEN tp.participant_type = 'solo' THEN 'solo' ELSE 'participant' END AS competitor_kind
                    FROM tournament_participants tp
                    LEFT JOIN profiles p ON p.id = tp.user_id
                    WHERE tp.id = ANY(@idList)
                    """,
                    new { idList });
                return Results.Ok(byIds);
            }

            Guid? ownerGuid = Guid.TryParse(owner_id, out var og) ? og : null;
            var teams = await conn.QueryAsync<dynamic>(
                $"""
                SELECT t.*
                FROM teams t
                WHERE (@ownerGuid IS NULL OR t.owner_id = @ownerGuid)
                  AND (@q IS NULL OR t.name ILIKE '%' || @q || '%')
                  AND (@game IS NULL OR EXISTS (
                      SELECT 1 FROM team_rosters tr
                      WHERE tr.team_id = t.id AND LOWER(tr.game) = LOWER(@game)))
                  AND {TeamKindSql.RealTeamWhere}
                ORDER BY t.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { ownerGuid, game, q, limit, offset });
            return Results.Ok(teams);
        });

        // ── GET /api/teams/me ─────────────────────────────────────────────────
        // Returns all teams where the user is a member or owner.
        // Single query replacing the previous 3-query + N RPC calls pattern.
        app.MapGet("/api/teams/me", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            var teams = await conn.QueryAsync<dynamic>(
                $"""
                SELECT t.*,
                       COALESCE(jsonb_agg(
                           jsonb_build_object(
                               'id',             p.id,
                               'username',       p.username,
                               'full_name',      p.full_name,
                               'avatar_url',     p.avatar_url,
                               'card_image_url', p.card_image_url,
                               'riot_tag',       p.riot_tag,
                               'discord_handle', p.social_links->>'discord_handle',
                               'role',           tm.role,
                               'joined_at',      tm.joined_at,
                               'is_active',      tm.is_active
                           ) ORDER BY tm.display_order, tm.role, p.username
                       ) FILTER (WHERE p.id IS NOT NULL), '[]'::jsonb) AS members
                FROM teams t
                LEFT JOIN team_members tm ON tm.team_id = t.id AND tm.is_active = TRUE
                LEFT JOIN profiles p ON p.id = tm.user_id
                WHERE t.id IN (
                    SELECT team_id FROM team_members WHERE user_id = @userId AND is_active = TRUE
                    UNION
                    SELECT id FROM teams WHERE owner_id = @userId
                )
                AND {TeamKindSql.RealTeamWhere}
                GROUP BY t.id
                ORDER BY t.created_at DESC
                """,
                new { userId = userCtx.UserIdGuid });

            foreach (var t in teams) ParseJsonbFields(t, "members");
            return Results.Ok(teams);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{identifier} — accepts UUID or slug ───────────────
        app.MapGet("/api/teams/{identifier}", async (
            string identifier,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            bool isGuid = Guid.TryParse(identifier, out var teamId);
            var sql = isGuid
                ? """
                  SELECT t.*,
                         COALESCE(jsonb_agg(
                             jsonb_build_object(
                                 'id',             p.id,
                                 'username',       p.username,
                                 'full_name',      p.full_name,
                                 'avatar_url',     p.avatar_url,
                                 'card_image_url', p.card_image_url,
                                 'riot_tag',       p.riot_tag,
                                 'discord_handle', p.social_links->>'discord_handle',
                                 'role',           tm.role,
                                 'joined_at',      tm.joined_at,
                                 'is_active',      tm.is_active
                             ) ORDER BY tm.display_order, tm.role, p.username
                         ) FILTER (WHERE p.id IS NOT NULL), '[]'::jsonb) AS members
                  FROM teams t
                  LEFT JOIN team_members tm ON tm.team_id = t.id AND tm.is_active = TRUE
                  LEFT JOIN profiles p ON p.id = tm.user_id
                  WHERE t.id = @teamId
                  GROUP BY t.id
                  """
                : """
                  SELECT t.*,
                         COALESCE(jsonb_agg(
                             jsonb_build_object(
                                 'id',             p.id,
                                 'username',       p.username,
                                 'full_name',      p.full_name,
                                 'avatar_url',     p.avatar_url,
                                 'card_image_url', p.card_image_url,
                                 'riot_tag',       p.riot_tag,
                                 'discord_handle', p.social_links->>'discord_handle',
                                 'role',           tm.role,
                                 'joined_at',      tm.joined_at,
                                 'is_active',      tm.is_active
                             ) ORDER BY tm.display_order, tm.role, p.username
                         ) FILTER (WHERE p.id IS NOT NULL), '[]'::jsonb) AS members
                  FROM teams t
                  LEFT JOIN team_members tm ON tm.team_id = t.id AND tm.is_active = TRUE
                  LEFT JOIN profiles p ON p.id = tm.user_id
                  WHERE t.slug = @identifier
                  GROUP BY t.id
                  """;

            var team = isGuid
                ? await conn.QuerySingleOrDefaultAsync<dynamic>(sql, new { teamId })
                : await conn.QuerySingleOrDefaultAsync<dynamic>(sql, new { identifier });

            if (team is null) return Results.NotFound();
            ParseJsonbFields(team, "members");
            return Results.Ok(team);
        });

        // ── GET /api/teams/{id}/tournament-history ────────────────────────────
        app.MapGet("/api/teams/{id:guid}/tournament-history", async (
            Guid id,
            IDbConnectionFactory db,
            int page = 1,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            var offset = (page - 1) * 20;
            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT
                    t.id            AS tournament_id,
                    t.slug          AS tournament_slug,
                    t.name          AS tournament_name,
                    t.game,
                    t.format,
                    t.start_date,
                    t.status        AS tournament_status,
                    tpl.placement
                FROM tournament_participants tp
                JOIN tournaments t ON t.id = tp.tournament_id
                LEFT JOIN tournament_placements tpl
                    ON tpl.tournament_id = t.id
                    AND tpl.team_id = tp.team_id
                WHERE tp.team_id = @id
                  AND tp.status != 'disqualified'
                  AND t.status != 'cancelled'
                ORDER BY t.start_date DESC
                LIMIT 20 OFFSET @offset
                """,
                new { id, offset });

            var count = await conn.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*)
                FROM tournament_participants tp
                JOIN tournaments t ON t.id = tp.tournament_id
                WHERE tp.team_id = @id
                  AND tp.status != 'disqualified'
                  AND t.status != 'cancelled'
                """,
                new { id });

            return Results.Ok(new
            {
                items = rows,
                page,
                pageSize = 20,
                has_more = count > (long)page * 20,
            });
        }).WithMetadata(new RateLimitPolicyMetadata("public"));

        // ── GET /api/teams/{id}/match-history ──────────────────────────────────────
        app.MapGet("/api/teams/{id:guid}/match-history", async (
            Guid id,
            IDbConnectionFactory db,
            int page = 1,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            var offset = (page - 1) * 20;
            using var conn = db.CreateConnection();

            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT
                    bm.id                                                           AS match_id,
                    bm.round_index,
                    bm.bracket_type,
                    bm.team1_score,
                    bm.team2_score,
                    bm.winner_id,
                    bm.is_walkover,
                    CASE WHEN bm.winner_id = @id THEN 'win'
                         WHEN bm.winner_id IS NOT NULL THEN 'loss'
                         ELSE 'draw' END                                            AS result,
                    CASE WHEN bm.team1_id = @id THEN bm.team1_score
                         ELSE bm.team2_score END                                    AS our_score,
                    CASE WHEN bm.team1_id = @id THEN bm.team2_score
                         ELSE bm.team1_score END                                    AS opp_score,
                    COALESCE(opp_t.name, opp_tp.team_name, 'TBD')                 AS opponent_name,
                    opp_t.logo_url                                                  AS opponent_logo_url,
                    t.id                                                            AS tournament_id,
                    t.slug                                                          AS tournament_slug,
                    t.name                                                          AS tournament_name,
                    t.game,
                    COALESCE(bm.updated_at, bm.scheduled_time)                    AS match_date
                FROM brkt_matches bm
                JOIN brkt_versions bv ON bv.id = bm.version_id
                JOIN tournament_stages ts ON ts.id = bv.stage_id
                JOIN tournaments t ON t.id = ts.tournament_id
                LEFT JOIN teams opp_t
                    ON opp_t.id = CASE WHEN bm.team1_id = @id THEN bm.team2_id ELSE bm.team1_id END
                LEFT JOIN tournament_participants opp_tp
                    ON opp_tp.id = CASE WHEN bm.team1_id = @id THEN bm.team2_id ELSE bm.team1_id END
                   AND opp_t.id IS NULL
                WHERE (bm.team1_id = @id OR bm.team2_id = @id)
                  AND bm.status = 'completed'
                ORDER BY COALESCE(bm.updated_at, bm.scheduled_time) DESC NULLS LAST
                LIMIT 20 OFFSET @offset
                """,
                new { id, offset });

            var count = await conn.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*)
                FROM brkt_matches bm
                WHERE (bm.team1_id = @id OR bm.team2_id = @id)
                  AND bm.status = 'completed'
                """,
                new { id });

            return Results.Ok(new { items = rows, page, pageSize = 20, total = count });
        }).WithMetadata(new RateLimitPolicyMetadata("public"));

        // ── POST /api/teams ───────────────────────────────────────────────────
        app.MapPost("/api/teams", async (
            [FromBody] CreateTeamRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            try
            {
                var baseSlug = TeamSlug.Generate(req.Name);
                dynamic? team = null;
                try
                {
                    team = await conn.QuerySingleAsync<dynamic>(
                        """
                        INSERT INTO teams (name, tag, game, game_format, logo_url, description,
                                          owner_id, is_active, country_code, team_kind, slug)
                        VALUES (@name, @tag, @game, @gameFormat, @logoUrl, @description,
                                @ownerId, TRUE, @countryCode, 'team', @slug)
                        RETURNING id, name, tag, game, game_format, logo_url, description,
                                 owner_id, is_active, country_code, created_at, slug
                        """,
                        new
                        {
                            name = req.Name,
                            tag = req.Tag,
                            game = req.Game ?? "General",
                            gameFormat = req.GameFormat,
                            logoUrl = req.LogoUrl,
                            description = req.Description,
                            ownerId = userCtx.UserIdGuid,
                            countryCode = req.CountryCode,
                            slug = baseSlug,
                        },
                        tx);
                }
                catch (PostgresException ex) when (ex.SqlState == "23505")
                {
                    var fallbackSlug = $"{baseSlug}-{Guid.NewGuid().ToString("N")[..8]}";
                    team = await conn.QuerySingleAsync<dynamic>(
                        """
                        INSERT INTO teams (name, tag, game, game_format, logo_url, description,
                                          owner_id, is_active, country_code, team_kind, slug)
                        VALUES (@name, @tag, @game, @gameFormat, @logoUrl, @description,
                                @ownerId, TRUE, @countryCode, 'team', @slug)
                        RETURNING id, name, tag, game, game_format, logo_url, description,
                                 owner_id, is_active, country_code, created_at, slug
                        """,
                        new
                        {
                            name = req.Name,
                            tag = req.Tag,
                            game = req.Game ?? "General",
                            gameFormat = req.GameFormat,
                            logoUrl = req.LogoUrl,
                            description = req.Description,
                            ownerId = userCtx.UserIdGuid,
                            countryCode = req.CountryCode,
                            slug = fallbackSlug,
                        },
                        tx);
                }

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
            Guid id,
            [FromBody] UpdateTeamRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
                    logo_url    = CASE WHEN @removeLogo THEN NULL ELSE COALESCE(@logoUrl, logo_url) END,
                    banner_url  = CASE WHEN @removeBanner THEN NULL ELSE COALESCE(@bannerUrl, banner_url) END,
                    website_url = COALESCE(@websiteUrl, website_url),
                    country_code = COALESCE(@countryCode, country_code),
                    updated_at  = NOW()
                WHERE id = @id
                RETURNING id, name, tag, game, game_format, logo_url, banner_url, website_url,
                         description, owner_id, is_active, country_code, created_at, updated_at
                """,
                new
                {
                    id,
                    name = req.Name,
                    tag = req.Tag,
                    description = req.Description,
                    logoUrl = req.LogoUrl,
                    bannerUrl = req.BannerUrl,
                    websiteUrl = req.WebsiteUrl,
                    countryCode = req.CountryCode,
                    removeLogo = req.RemoveLogo,
                    removeBanner = req.RemoveBanner,
                });

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/{id} — disband (cascade in a transaction) ───────
        app.MapDelete("/api/teams/{id}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
                await conn.ExecuteAsync("DELETE FROM team_members WHERE team_id = @id", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM tournament_participants WHERE team_id = @id", new { id }, tx);
                await conn.ExecuteAsync("DELETE FROM teams WHERE id = @id", new { id }, tx);
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
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> hub,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();

            // Owner cannot leave — must transfer captaincy first
            var isOwner = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT owner_id = @userId FROM teams WHERE id = @id", new { id, userId = userCtx.UserIdGuid });
            if (isOwner)
                return Results.BadRequest(new { error = "Transfer captaincy before leaving." });

            using var tx = conn.BeginTransaction();
            List<Guid> memberIds;
            string? teamName;
            string? leaverName;
            try
            {
                // Capture audience and display names before the rows disappear
                memberIds = (await conn.QueryAsync<Guid>(
                    "SELECT user_id FROM team_members WHERE team_id = @id AND is_active = TRUE",
                    new { id }, tx)).ToList();
                teamName = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT name FROM teams WHERE id = @id", new { id });
                leaverName = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT username FROM profiles WHERE id = @userId", new { userId = userCtx.UserIdGuid });

                await conn.ExecuteAsync(
                    "DELETE FROM team_members WHERE team_id = @id AND user_id = @userId",
                    new { id, userId = userCtx.UserIdGuid }, tx);

                // Remove lineup memberships so the leaver doesn't linger as a ghost in rosters
                await conn.ExecuteAsync(
                    """
                    DELETE FROM team_roster_members trm
                    USING team_rosters tr
                    WHERE trm.roster_id = tr.id AND tr.team_id = @id AND trm.user_id = @userId
                    """,
                    new { id, userId = userCtx.UserIdGuid }, tx);

                // Drop stale pending invitations for this user on this team
                await conn.ExecuteAsync(
                    "DELETE FROM team_invitations WHERE team_id = @id AND invited_user_id = @userId AND status = 'pending'",
                    new { id, userId = userCtx.UserIdGuid }, tx);

                // Notify remaining members atomically with the departure.
                // The leaver's own client refreshes via the mutation's onSuccess handler.
                var others = memberIds.Where(m => m != userCtx.UserIdGuid).ToList();
                var updateTitle = $"{teamName ?? "Team"} Roster Update";
                var updateMessage = $"{leaverName ?? "A player"} left the team.";

                await TeamNotifications.InsertAsync(conn, others,
                    TeamNotifications.RosterUpdated, updateTitle, updateMessage, id, tx);

                tx.Commit();

                await TeamNotifications.PushAsync(hub, others,
                    TeamNotifications.RosterUpdated, updateTitle, updateMessage);
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/{id}/members/{userId} ───────────────────────────
        app.MapDelete("/api/teams/{id}/members/{userId}", async (
            Guid id,
            Guid userId,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> hub,
            DiscordNotificationService discord,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            var ownerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT owner_id FROM teams WHERE id = @id", new { id });
            if (ownerId is null) return Results.NotFound(new { error = "Team not found." });
            if (userId == ownerId)
                return Results.BadRequest(new { error = "The team captain cannot be removed. Transfer captaincy first." });

            using var tx = conn.BeginTransaction();
            List<Guid> memberIds;
            string? teamName;
            string? removedName;
            try
            {
                // Capture audience and display names before the rows disappear
                memberIds = (await conn.QueryAsync<Guid>(
                    "SELECT user_id FROM team_members WHERE team_id = @id AND is_active = TRUE",
                    new { id }, tx)).ToList();
                teamName = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT name FROM teams WHERE id = @id", new { id });
                removedName = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT username FROM profiles WHERE id = @userId", new { userId });

                // Remove lineup memberships so the user doesn't linger as a ghost in rosters
                await conn.ExecuteAsync(
                    """
                    DELETE FROM team_roster_members trm
                    USING team_rosters tr
                    WHERE trm.roster_id = tr.id AND tr.team_id = @id AND trm.user_id = @userId
                    """,
                    new { id, userId }, tx);

                var affected = await conn.ExecuteAsync(
                    "DELETE FROM team_members WHERE team_id = @id AND user_id = @userId",
                    new { id, userId }, tx);

                // Drop stale pending invitations for this user on this team
                await conn.ExecuteAsync(
                    "DELETE FROM team_invitations WHERE team_id = @id AND invited_user_id = @userId AND status = 'pending'",
                    new { id, userId }, tx);

                // Notify atomically with the removal
                var removedTitle = $"You've been removed from {teamName ?? "the team"}";
                var removedMessage = $"The captain has removed you from the {teamName ?? "team"} roster.";
                var updateTitle = $"{teamName ?? "Team"} Roster Update";
                var updateMessage = $"{removedName ?? "A player"} was removed from the roster.";

                await TeamNotifications.InsertAsync(conn, [userId],
                    TeamNotifications.MemberRemoved, removedTitle, removedMessage, id, tx);
                var others = memberIds.Where(m => m != userId && m != userCtx.UserIdGuid).ToList();
                await TeamNotifications.InsertAsync(conn, others,
                    TeamNotifications.RosterUpdated, updateTitle, updateMessage, id, tx);

                tx.Commit();

                // Real-time fan-out (post-commit; delivery failures are non-critical)
                await TeamNotifications.PushAsync(hub, [userId],
                    TeamNotifications.MemberRemoved, removedTitle, removedMessage);
                await TeamNotifications.PushAsync(hub, others,
                    TeamNotifications.RosterUpdated, updateTitle, updateMessage);

                var gameSlug = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT game FROM teams WHERE id = @id", new { id });
                var teamUrlBase = config["FrontendUrl"] ?? "https://esportra.com";
                await discord.TrySendDmAsync(userId, "team_member_removed",
                    "Removed from Team",
                    $"You have been removed from the team.\n\n[View →]({teamUrlBase}/player/teams)",
                    gameSlug);

                return Results.Ok(new { success = true, removed = affected > 0 });
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/transfer-captain ─────────────────────────────
        // Atomic: demote old captain → promote new → transfer team.owner_id → update registrations
        app.MapPost("/api/teams/{id}/transfer-captain", async (
            Guid id,
            [FromBody] TransferCaptainRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> hub,
            DiscordNotificationService discord,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertOwner(conn, id, userCtx.UserIdGuid);

            var newCaptainIdGuid = Guid.Parse(req.NewCaptainId);

            using var tx = conn.BeginTransaction();
            try
            {
                // Capture audience and display names before the roles change
                var memberIds = (await conn.QueryAsync<Guid>(
                    "SELECT user_id FROM team_members WHERE team_id = @id AND is_active = TRUE",
                    new { id }, tx)).ToList();
                var teamName = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT name FROM teams WHERE id = @id", new { id });
                var newCaptainName = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT username FROM profiles WHERE id = @userId", new { userId = newCaptainIdGuid });

                // Step 1: demote current captain
                await conn.ExecuteAsync(
                    "UPDATE team_members SET role = 'member' WHERE team_id = @id AND user_id = @userId AND role = 'captain'",
                    new { id, userId = userCtx.UserIdGuid }, tx);

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

                // Notify atomically with the promotion.
                // The acting captain's own client refreshes via the mutation's onSuccess handler.
                var captainTitle = $"You are now the captain of {teamName ?? "the team"}";
                var captainMessage = "Leadership of the roster has been transferred to you.";
                var updateTitle = $"{teamName ?? "Team"} Roster Update";
                var updateMessage = $"{newCaptainName ?? "A player"} is now the team captain.";

                await TeamNotifications.InsertAsync(conn, [newCaptainIdGuid],
                    TeamNotifications.CaptainChanged, captainTitle, captainMessage, id, tx);
                var others = memberIds.Where(m => m != newCaptainIdGuid && m != userCtx.UserIdGuid).ToList();
                await TeamNotifications.InsertAsync(conn, others,
                    TeamNotifications.RosterUpdated, updateTitle, updateMessage, id, tx);

                tx.Commit();

                // Real-time fan-out (post-commit; delivery failures are non-critical)
                await TeamNotifications.PushAsync(hub, [newCaptainIdGuid],
                    TeamNotifications.CaptainChanged, captainTitle, captainMessage);
                await TeamNotifications.PushAsync(hub, others,
                    TeamNotifications.RosterUpdated, updateTitle, updateMessage);

                var gameSlug = await conn.QuerySingleOrDefaultAsync<string?>(
                    "SELECT game FROM teams WHERE id = @id", new { id });
                var captainUrlBase = config["FrontendUrl"] ?? "https://esportra.com";
                await discord.TrySendDmAsync(newCaptainIdGuid, "team_captain_changed",
                    "You're Now Captain",
                    $"You are now the captain of the team.\n\n[View →]({captainUrlBase}/player/teams)",
                    gameSlug);
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/teams/{id}/members/{userId}/role ─────────────────────────
        app.MapPut("/api/teams/{id}/members/{userId}/role", async (
            Guid id,
            Guid userId,
            [FromBody] ChangeRoleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var allowedRoles = new HashSet<string> { "member", "substitute", "coach" };
            if (!allowedRoles.Contains(req.Role))
                return Results.BadRequest(new { error = $"Invalid role. Allowed: {string.Join(", ", allowedRoles)}" });

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            // Prevent changing captain's role via this endpoint
            var targetRole = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT role FROM team_members WHERE team_id = @id AND user_id = @userId AND is_active = TRUE",
                new { id, userId });
            if (targetRole is null)
                return Results.NotFound(new { error = "Member not found." });
            if (targetRole == "captain")
                return Results.BadRequest(new { error = "Cannot change captain role. Use transfer-captain instead." });

            // Enforce max 2 coaches
            if (req.Role == "coach")
            {
                var coachCount = await conn.QuerySingleAsync<int>(
                    "SELECT COUNT(*) FROM team_members WHERE team_id = @id AND role = 'coach' AND is_active = TRUE",
                    new { id });
                if (coachCount >= 2)
                    return Results.BadRequest(new { error = "Maximum 2 coaches per team." });
            }

            await conn.ExecuteAsync(
                "UPDATE team_members SET role = @role::team_member_role WHERE team_id = @id AND user_id = @userId AND is_active = TRUE",
                new { id, userId, role = req.Role });

            return Results.Ok(new { success = true, userId, role = req.Role });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/teams/{id}/members/order ─────────────────────────────────
        app.MapPut("/api/teams/{id}/members/order", async (
            Guid id,
            [FromBody] ReorderMembersRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            // Captain must be order 0
            var captainId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT user_id FROM team_members WHERE team_id = @id AND role = 'captain' AND is_active = TRUE",
                new { id });

            foreach (var item in req.Order)
            {
                var uid = Guid.Parse(item.UserId);
                if (uid == captainId && item.DisplayOrder != 0)
                    return Results.BadRequest(new { error = "Captain must be position 0." });

                await conn.ExecuteAsync(
                    "UPDATE team_members SET display_order = @order WHERE team_id = @id AND user_id = @uid AND is_active = TRUE",
                    new { id, uid, order = item.DisplayOrder });
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/invite ───────────────────────────────────────
        app.MapPost("/api/teams/{id}/invite", async (
            Guid id,
            [FromBody] TeamInviteRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService emailService,
            DiscordNotificationService discord,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var reqUserIdGuid = Guid.Parse(req.UserId);

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            // Check not already a member of this team
            var alreadyMember = await conn.QuerySingleOrDefaultAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM team_members WHERE team_id = @id AND user_id = @userId AND is_active = TRUE)",
                new { id, userId = reqUserIdGuid });
            if (alreadyMember)
                return Results.Conflict(new { error = "User is already a member of this team." });

            // Check if user is already on another team for the same game
            var existingTeamName = await conn.QuerySingleOrDefaultAsync<string>(
                """
                SELECT t.name FROM team_members tm
                JOIN teams t ON t.id = tm.team_id
                WHERE tm.user_id = @userId AND tm.is_active = TRUE
                  AND t.game = (SELECT game FROM teams WHERE id = @teamId)
                  AND t.id != @teamId
                LIMIT 1
                """,
                new { userId = reqUserIdGuid, teamId = id });
            if (existingTeamName is not null)
                return Results.Conflict(new { error = $"Player is already on another team ({existingTeamName}) for this game." });

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
                    RETURNING id, team_id, invited_user_id, invited_by_user_id, invited_by, status, message, created_at
                    """,
                    new { id = existingInviteId.Value, message = req.Message });
            }
            else
            {
                invite = await conn.QuerySingleAsync<dynamic>(
                    """
                    INSERT INTO team_invitations (team_id, invited_user_id, invited_by_user_id, invited_by, status, message)
                    VALUES (@teamId, @userId, @invitedBy, @invitedBy, 'pending', @message)
                    RETURNING id, team_id, invited_user_id, invited_by_user_id, invited_by, status, message, created_at
                    """,
                    new { teamId = id, userId = reqUserIdGuid, invitedBy = userCtx.UserIdGuid, message = req.Message });
            }

            // Notification to invitee
            var teamInfo = await conn.QuerySingleOrDefaultAsync<(string? Name, string? Game)>(
                "SELECT name, game FROM teams WHERE id = @id", new { id });
            var inviteTeamName = teamInfo.Name;
            var gameSlug = teamInfo.Game;
            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'team_invite', @title,
                        @msg, '/teams', @data::jsonb, FALSE)
                """,
                new
                {
                    userId = reqUserIdGuid,
                    title = $"🤝 You're Invited to {inviteTeamName ?? "a Team"}!",
                    msg = $"You've been recruited to join {inviteTeamName ?? "a team"}. Accept the invite and jump into the action!",
                    data = System.Text.Json.JsonSerializer.Serialize(new { team_id = id, invite_id = ((Guid)invite.id).ToString() })
                });

            var inviteUrlBase = config["FrontendUrl"] ?? "https://esportra.com";
            await discord.TrySendDmAsync(reqUserIdGuid, "team_invite", "Team Invitation",
                $"You've been invited to join the team.\n\n[View →]({inviteUrlBase}/player/teams)",
                gameSlug);

            // Send team invite email
            try
            {
                var emailInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                    """
                    SELECT p.email, p.username AS invitee_name,
                           t.name AS team_name,
                           cap.username AS captain_name
                    FROM profiles p
                    CROSS JOIN teams t
                    LEFT JOIN profiles cap ON cap.id = @captainId
                    WHERE p.id = @inviteeId AND t.id = @teamId
                    """,
                    new { inviteeId = reqUserIdGuid, teamId = id, captainId = userCtx.UserIdGuid });

                if (emailInfo?.email is not null)
                {
                    var frontendUrl = config["FrontendUrl"] ?? "https://esportra.com";
                    await emailService.SendAsync(
                        (string)emailInfo.email,
                        EmailType.TeamInvite,
                        new
                        {
                            inviteeName = (string?)emailInfo.invitee_name ?? "there",
                            teamName = (string?)emailInfo.team_name ?? "a team",
                            captainName = (string?)emailInfo.captain_name ?? "A teammate",
                            acceptUrl = $"{frontendUrl}/player/teams"
                        },
                        ct);
                }
            }
            catch { /* email failure should not block invite creation */ }

            return Results.Ok(invite);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/me/invites ─────────────────────────────────────────
        app.MapGet("/api/teams/me/invites", async (
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid inviteId,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService catalog,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

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
                    INSERT INTO team_members (team_id, user_id, role, is_active, joined_at)
                    VALUES (@teamId, @userId, 'member', TRUE, NOW())
                    ON CONFLICT (team_id, user_id) DO UPDATE SET is_active = TRUE, role = 'member', joined_at = NOW()
                    """,
                    new { teamId = invite.team_id, userId = userCtx.UserIdGuid }, tx);

                if (invite.roster_id is not null)
                {
                    var rosterId = (Guid)invite.roster_id;
                    var onRoster = await conn.QuerySingleAsync<bool>(
                        """
                        SELECT EXISTS(
                            SELECT 1 FROM team_roster_members
                            WHERE roster_id = @rosterId AND user_id = @userId
                        )
                        """,
                        new { rosterId, userId = userCtx.UserIdGuid }, tx);

                    if (!onRoster)
                    {
                        string rosterRole;
                        try
                        {
                            rosterRole = await RosterMemberValidationHelper.ResolveRoleForNewMemberAsync(
                                conn, catalog, rosterId, "starter", null, tx);
                            await RosterMemberValidationHelper.ValidateCanAddMemberAsync(
                                conn, catalog, rosterId, rosterRole, tx: tx);
                        }
                        catch (GameCatalogValidationException ex)
                        {
                            tx.Rollback();
                            return Results.BadRequest(new { error = ex.Message });
                        }

                        await conn.ExecuteAsync(
                            """
                            INSERT INTO team_roster_members (roster_id, user_id, roster_role, is_starter)
                            VALUES (@rosterId, @userId, @rosterRole::public.roster_member_role, @isStarter)
                            ON CONFLICT (roster_id, user_id) DO NOTHING
                            """,
                            new
                            {
                                rosterId,
                                userId = userCtx.UserIdGuid,
                                rosterRole,
                                isStarter = rosterRole == "starter",
                            },
                            tx);
                    }
                }

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
            Guid inviteId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid inviteId,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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

        // ── POST /api/teams/batch ─────────────────────────────────────────────
        // Batch team lookup by IDs
        app.MapPost("/api/teams/batch", async (
            [FromBody] TeamBatchRequest req,
            IDbConnectionFactory db,
            CancellationToken ct) =>
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
            Guid id,
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
            Guid id,
            IDbConnectionFactory db) =>
        {
            using var conn = db.CreateConnection();
            var members = await conn.QueryAsync<dynamic>(
                """
                SELECT tm.user_id, tm.role, p.username, p.email, p.avatar_url, p.card_image_url,
                       ra.puuid AS riot_puuid, ra.game_name AS riot_game_name, ra.tag_line AS riot_tag_line,
                       vs.kd, vs.win_rate, vs.hs_percent, vs.latest_match_id
                FROM team_members tm
                JOIN profiles p ON p.id = tm.user_id
                LEFT JOIN riot_accounts ra ON ra.user_id = tm.user_id
                LEFT JOIN leaderboard vs ON vs.user_id = tm.user_id AND vs.game = 'valorant'
                WHERE tm.team_id = @id AND tm.is_active = true
                ORDER BY tm.display_order, tm.role, p.username
                """,
                new { id });
            return Results.Ok(members);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id}/invites ───────────────────────────────────────
        app.MapGet("/api/teams/{id}/invites", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            var invites = await conn.QueryAsync<dynamic>(
                """
                SELECT ti.id, ti.invited_email, ti.invited_user_id, ti.roster_id, ti.created_at,
                       jsonb_build_object('username', p.username, 'avatar_url', p.avatar_url) AS profiles
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
            HttpContext ctx,
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
            Guid id,
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
                               'is_starter', rm.is_starter,
                               'roster_role', rm.roster_role
                           ) ORDER BY rm.display_order, rm.created_at
                       ) FILTER (WHERE rm.user_id IS NOT NULL), '[]') AS members
                FROM team_rosters r
                LEFT JOIN team_roster_members rm ON rm.roster_id = r.id
                    AND EXISTS (
                        SELECT 1 FROM team_members tm
                        WHERE tm.team_id = r.team_id AND tm.user_id = rm.user_id AND tm.is_active = TRUE)
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
            Guid id,
            [FromBody] CreateRosterRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService catalog) =>
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

            var rosterId = (Guid)roster!.id;
            var ownerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT owner_id FROM teams WHERE id = @teamId",
                new { teamId = id });
            if (ownerId is not null)
            {
                try
                {
                    await RosterMemberValidationHelper.ValidateCanAddMemberAsync(
                        conn, catalog, rosterId, "starter");
                }
                catch (GameCatalogValidationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                await conn.ExecuteAsync(
                    """
                    INSERT INTO team_roster_members (roster_id, user_id, roster_role, is_starter)
                    VALUES (@rosterId, @ownerId, 'starter'::public.roster_member_role, TRUE)
                    ON CONFLICT (roster_id, user_id) DO NOTHING
                    """,
                    new { rosterId, ownerId });
            }

            if (!string.IsNullOrWhiteSpace(req.Game))
            {
                await conn.ExecuteAsync(
                    """
                    UPDATE teams
                    SET game = @game
                    WHERE id = @teamId
                      AND (game IS NULL OR LOWER(game) = 'general')
                    """,
                    new { teamId = id, game = req.Game });
            }

            return Results.Created($"/api/teams/{id}/rosters/{rosterId}", roster);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/teams/{id}/rosters/{rosterId} ────────────────────────────
        app.MapPut("/api/teams/{id}/rosters/{rosterId}", async (
            Guid id,
            Guid rosterId,
            [FromBody] UpdateRosterRequest req,
            HttpContext ctx,
            IDbConnectionFactory db) =>
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
            Guid id,
            Guid rosterId,
            HttpContext ctx,
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
            Guid id,
            Guid rosterId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                """
                SELECT trm.user_id, trm.is_starter, trm.roster_role::text AS roster_role,
                       tm.role::text AS team_role,
                       p.username, p.full_name, p.avatar_url, p.riot_tag, p.steam_tag
                FROM team_roster_members trm
                JOIN team_rosters tr ON tr.id = trm.roster_id
                LEFT JOIN team_members tm ON tm.team_id = tr.team_id AND tm.user_id = trm.user_id AND tm.is_active = TRUE
                LEFT JOIN profiles p ON p.id = trm.user_id
                WHERE trm.roster_id = @rosterId
                ORDER BY trm.display_order ASC,
                         CASE trm.roster_role WHEN 'starter' THEN 0 WHEN 'substitute' THEN 1 WHEN 'coach' THEN 2 ELSE 3 END,
                         trm.is_starter DESC, p.username ASC
                """,
                new { rosterId });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/rosters/{rosterId}/members ───────────────────
        app.MapPost("/api/teams/{id}/rosters/{rosterId}/members", async (
            Guid id,
            Guid rosterId,
            [FromBody] RosterMemberRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService catalog) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            var userIdGuid = Guid.Parse(req.UserId);

            // Domain invariant: a player cannot join a roster without being an active team member
            var isTeamMember = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM team_members
                    WHERE team_id = @teamId AND user_id = @userId AND is_active = TRUE
                )
                """,
                new { teamId = id, userId = userIdGuid });
            if (!isTeamMember)
                return Results.BadRequest(new { error = "Player must be a team member before joining a roster." });

            var alreadyOnRoster = await conn.QuerySingleAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM team_roster_members
                    WHERE roster_id = @rosterId AND user_id = @userId
                )
                """,
                new { rosterId, userId = userIdGuid });

            string rosterRole;
            try
            {
                if (alreadyOnRoster)
                {
                    rosterRole = ResolveRosterRole(req.RosterRole, req.IsStarter);
                    await RosterMemberValidationHelper.ValidateRoleChangeAsync(
                        conn, catalog, rosterId, userIdGuid, rosterRole);
                }
                else
                {
                    rosterRole = await RosterMemberValidationHelper.ResolveRoleForNewMemberAsync(
                        conn, catalog, rosterId, req.RosterRole, req.IsStarter);
                    await RosterMemberValidationHelper.ValidateCanAddMemberAsync(
                        conn, catalog, rosterId, rosterRole);
                }
            }
            catch (GameCatalogValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO team_roster_members (roster_id, user_id, roster_role, is_starter)
                VALUES (@rosterId, @userId, @rosterRole::public.roster_member_role, @isStarter)
                ON CONFLICT (roster_id, user_id) DO UPDATE
                    SET roster_role = EXCLUDED.roster_role,
                        is_starter = EXCLUDED.is_starter
                """,
                new
                {
                    rosterId,
                    userId = userIdGuid,
                    rosterRole,
                    isStarter = rosterRole == "starter",
                });
            return Results.Ok(new { success = true, rosterRole, isStarter = rosterRole == "starter" });
        }).RequireAuthorization("Authenticated");

        // ── DELETE /api/teams/{id}/rosters/{rosterId}/members/{userId} ────────
        app.MapDelete("/api/teams/{id}/rosters/{rosterId}/members/{userId}", async (
            Guid id,
            Guid rosterId,
            Guid userId,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            var ownerId = await conn.QuerySingleOrDefaultAsync<Guid?>(
                "SELECT owner_id FROM teams WHERE id = @teamId",
                new { teamId = id });
            if (ownerId == userId)
                return Results.BadRequest(new { error = "Captain cannot be removed from the roster." });

            await conn.ExecuteAsync(
                "DELETE FROM team_roster_members WHERE roster_id = @rosterId AND user_id = @userId",
                new { rosterId, userId });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/teams/{id}/rosters/{rosterId}/members/{userId}/starter ───
        app.MapPut("/api/teams/{id}/rosters/{rosterId}/members/{userId}/starter", async (
            Guid id,
            Guid rosterId,
            Guid userId,
            [FromBody] ToggleStarterRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService catalog) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            var rosterRole = req.IsStarter ? "starter" : "substitute";

            try
            {
                await RosterMemberValidationHelper.ValidateRoleChangeAsync(
                    conn, catalog, rosterId, userId, rosterRole);
            }
            catch (GameCatalogValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await conn.ExecuteAsync(
                """
                UPDATE team_roster_members
                SET is_starter = @isStarter,
                    roster_role = @rosterRole::public.roster_member_role
                WHERE roster_id = @rosterId AND user_id = @userId
                """,
                new { isStarter = req.IsStarter, rosterRole, rosterId, userId });
            return Results.Ok(new { success = true, rosterRole, isStarter = req.IsStarter });
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/teams/{id}/rosters/{rosterId}/members/{userId}/role ──────
        app.MapPut("/api/teams/{id}/rosters/{rosterId}/members/{userId}/role", async (
            Guid id,
            Guid rosterId,
            Guid userId,
            [FromBody] UpdateRosterRoleRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            GameCatalogService catalog) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            if (req.RosterRole is not ("starter" or "substitute" or "coach"))
                return Results.BadRequest(new { error = "Roster role must be starter, substitute, or coach." });

            using var conn = db.CreateConnection();
            await AssertCaptain(conn, id, userCtx.UserIdGuid);

            var onRoster = await conn.QuerySingleOrDefaultAsync<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM team_roster_members
                    WHERE roster_id = @rosterId AND user_id = @userId
                )
                """,
                new { rosterId, userId });
            if (!onRoster)
                return Results.NotFound(new { error = "Member is not on this roster." });

            try
            {
                await RosterMemberValidationHelper.ValidateRoleChangeAsync(
                    conn, catalog, rosterId, userId, req.RosterRole);
            }
            catch (GameCatalogValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var isStarter = req.RosterRole == "starter";
            await conn.ExecuteAsync(
                """
                UPDATE team_roster_members
                SET roster_role = @rosterRole::public.roster_member_role,
                    is_starter = @isStarter
                WHERE roster_id = @rosterId AND user_id = @userId
                """,
                new { rosterRole = req.RosterRole, isStarter, rosterId, userId });
            return Results.Ok(new { success = true, rosterRole = req.RosterRole, isStarter });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/rosters/{rosterId}/invite ────────────────────
        app.MapPost("/api/teams/{id}/rosters/{rosterId}/invite", async (
            Guid id,
            Guid rosterId,
            [FromBody] RosterInviteRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IHubContext<NotificationHub> hub,
            DiscordNotificationService discord,
            IConfiguration config) =>
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
            var teamInfo = await conn.QuerySingleOrDefaultAsync<(string? Name, string? Game)>(
                "SELECT name, game FROM teams WHERE id = @id", new { id });
            var teamName = teamInfo.Name;
            var gameSlug = teamInfo.Game;
            var rosterName = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT name FROM team_rosters WHERE id = @rosterId", new { rosterId });

            await conn.ExecuteAsync(
                """
                INSERT INTO notifications (user_id, type, title, message, link, data, is_read)
                VALUES (@userId, 'team_invite', @title,
                        @msg, '/player/teams', @data::jsonb, false)
                """,
                new
                {
                    userId = reqUserIdGuid,
                    title = $"🤝 You're Invited to {teamName ?? "a Team"}!",
                    msg = $"You've been recruited to join {teamName}{(rosterName is not null ? $" ({rosterName})" : "")}. Accept and get in the game!",
                    data = System.Text.Json.JsonSerializer.Serialize(new { team_id = id, roster_id = rosterId })
                });

            await hub.Clients.Group(NotificationHub.UserGroup(req.UserId))
                .SendAsync(NotificationHubEvents.NewNotification, new { type = "team_invite" });

            var rosterInviteUrlBase = config["FrontendUrl"] ?? "https://esportra.com";
            await discord.TrySendDmAsync(reqUserIdGuid, "team_invite", "Team Invitation",
                $"You've been invited to join the team.\n\n[View →]({rosterInviteUrlBase}/player/teams)",
                gameSlug);

            return Results.Ok(invite);
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/announce ──────────────────────────────────────
        app.MapPost("/api/teams/{id}/announce", async (
            Guid id,
            [FromBody] AnnounceRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
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
                SELECT uid, 'team_announcement', @title, @message,
                       jsonb_build_object('team_id', @teamId, 'team_name', @teamName), false
                FROM UNNEST(@userIds::uuid[]) AS uid
                """,
                new { title = $"📣 {teamName ?? "Team"} Announcement", message = req.Message, teamId = id, teamName, userIds = memberIds.ToArray() });

            // Push real-time
            foreach (var uid in memberIds)
                await hub.Clients.Group(NotificationHub.UserGroup(uid.ToString()))
                    .SendAsync(NotificationHubEvents.NewNotification, new { type = "team_announcement" });

            return Results.Ok(new { sent = memberIds.Count });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id}/registrations ─────────────────────────────────
        app.MapGet("/api/teams/{id}/registrations", async (
            Guid id,
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
                  AND tp.status NOT IN ('cancelled', 'rejected')
                """,
                new { id });
            return Results.Ok(registrations);
        }).RequireAuthorization("Authenticated");

        // ── PUT /api/profiles/{id}/card-image ─────────────────────────────────
        app.MapPut("/api/profiles/{id}/card-image", async (
            Guid id,
            [FromBody] CardImageRequest req,
            HttpContext ctx,
            IDbConnectionFactory db) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            // Only allow updating own profile or admin
            if (userCtx.UserIdGuid != id && !StaffAuthHelper.IsPlatformAdmin(userCtx))
                return Results.Forbid();

            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE profiles SET card_image_url = @url WHERE id = @id",
                new { url = req.Url, id });
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Authenticated");

        // ── POST /api/teams/{id}/rosters/{rosterId}/invite-batch ──────────────
        app.MapPost("/api/teams/{id}/rosters/{rosterId}/invite-batch", async (
            Guid id,
            Guid rosterId,
            [FromBody] BatchRosterInviteRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            IEmailService emailService,
            IConfiguration config,
            IHubContext<NotificationHub> hub) =>
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

                try
                {
                    var emailInfo = await conn.QuerySingleOrDefaultAsync<dynamic>(
                        """
                        SELECT p.username AS invitee_name, t.name AS team_name, cap.username AS captain_name
                        FROM profiles p
                        CROSS JOIN teams t
                        LEFT JOIN profiles cap ON cap.id = @captainId
                        WHERE p.id = @inviteeId AND t.id = @teamId
                        """,
                        new { inviteeId = inviteeUserIdGuid, teamId = id, captainId = userCtx.UserIdGuid });

                    if (invitee.Email is not null)
                    {
                        var frontendUrl = config["FrontendUrl"] ?? "https://esportra.com";
                        await emailService.SendAsync(
                            invitee.Email,
                            EmailType.TeamInvite,
                            new
                            {
                                inviteeName = (string?)emailInfo?.invitee_name ?? "there",
                                teamName = (string?)emailInfo?.team_name ?? "a team",
                                captainName = (string?)emailInfo?.captain_name ?? "A teammate",
                                acceptUrl = $"{frontendUrl}/player/teams"
                            },
                            default);
                    }
                }
                catch { }
            }

            return Results.Ok(new { sent });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/my-captain-teams ──────────────────────────────────
        // Returns real teams where the current user is captain (excludes solo/mock adapters).
        app.MapGet("/api/teams/my-captain-teams", async (
            HttpContext ctx,
            string? game,
            int limit,
            int offset,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();

            var pageLimit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 100);
            var pageOffset = Math.Max(offset, 0);

            using var conn = db.CreateConnection();
            var teams = await conn.QueryAsync<dynamic>(
                $"""
                SELECT DISTINCT t.*
                FROM teams t
                LEFT JOIN team_members tm
                  ON tm.team_id = t.id AND tm.user_id = @userId AND tm.is_active = TRUE
                WHERE (t.owner_id = @userId OR (tm.user_id = @userId AND tm.role = 'captain'))
                  AND {TeamKindSql.RealTeamWhere}
                  AND (@game IS NULL OR EXISTS (
                      SELECT 1 FROM team_rosters tr
                      WHERE tr.team_id = t.id AND LOWER(tr.game) = LOWER(@game)))
                ORDER BY t.name ASC
                LIMIT @pageLimit OFFSET @pageOffset
                """, new { userId = userCtx.UserIdGuid, game, pageLimit, pageOffset });
            return Results.Ok(teams);
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/captains ───────────────────────────────────────────
        // Returns teams with their captain's profile info
        app.MapGet("/api/teams/captains", async (
            string? game,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<dynamic>(
                $"""
                SELECT t.id, t.name, t.tag, t.game, t.logo_url,
                       p.id AS captain_id, p.username AS captain_username, p.avatar_url AS captain_avatar
                FROM teams t
                JOIN team_members tm ON tm.team_id = t.id AND tm.role = 'captain' AND tm.is_active = TRUE
                JOIN profiles p ON p.id = tm.user_id
                WHERE (@game IS NULL OR t.game = @game)
                  AND {TeamKindSql.RealTeamWhere}
                ORDER BY t.name ASC
                LIMIT 100
                """, new { game });
            return Results.Ok(rows);
        });

        // ── GET /api/teams/members ────────────────────────────────────────────
        // Returns team members for a given user_id or team_id
        // Supports: ?teamId=, ?userId=, ?team_ids=a,b&user_id=x&roles=captain,owner&is_active=true
        app.MapGet("/api/teams/members", async (
            HttpContext ctx,
            Guid? userId,
            Guid? teamId,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();

            // ── Multi-team query (team_ids + user_id + roles + is_active) ──
            var teamIdsParam = ctx.Request.Query["team_ids"].FirstOrDefault();
            var userIdParam = ctx.Request.Query["user_id"].FirstOrDefault();
            var rolesParam = ctx.Request.Query["roles"].FirstOrDefault();

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
                    SELECT tm.team_id::text as team_id, tm.user_id::text as user_id,
                           tm.role, tm.is_active, tm.joined_at,
                           p.username, p.avatar_url, p.riot_tag, p.steam_tag,
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
                    $"""
                    SELECT tm.*, t.name AS team_name, t.logo_url AS team_logo, t.game
                    FROM team_members tm
                    JOIN teams t ON t.id = tm.team_id
                    WHERE tm.user_id = @effectiveUserId AND tm.is_active = TRUE
                      AND {TeamKindSql.RealTeamWhere}
                    ORDER BY tm.joined_at DESC
                    """, new { effectiveUserId });
                return Results.Ok(rows);
            }

            return Results.BadRequest(new { error = "user_id or team_id required" });
        }).RequireAuthorization("Authenticated");

        // ── GET /api/teams/{id}/captain ───────────────────────────────────────
        // Returns the current captain of a team
        app.MapGet("/api/teams/{id}/captain", async (
            Guid id,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var captain = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT tm.user_id::text as user_id, tm.role, tm.joined_at,
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

    private static string ResolveRosterRole(string? requestedRole, bool? isStarter)
    {
        if (requestedRole is "starter" or "substitute" or "coach")
            return requestedRole;

        if (isStarter.HasValue)
            return isStarter.Value ? "starter" : "substitute";

        return "starter";
    }

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
    string Name,
    string Tag,
    string? Game = "General",
    string? GameFormat = "squad",
    string? LogoUrl = null,
    string? Description = null,
    string? CountryCode = null,
    List<TeamMemberSeed>? Members = null);

public sealed record TeamMemberSeed(string UserId, string Role = "member");

public sealed record UpdateTeamRequest(
    string? Name = null,
    string? Tag = null,
    string? Description = null,
    string? LogoUrl = null,
    string? BannerUrl = null,
    string? WebsiteUrl = null,
    string? CountryCode = null,
    bool RemoveLogo = false,
    bool RemoveBanner = false);

public sealed record TransferCaptainRequest(string NewCaptainId);

public sealed record ChangeRoleRequest(string Role);

public sealed record TeamInviteRequest(string UserId, string? Message = null);

public sealed record CreateRosterRequest(string Name, string Game, string? Format = null, int TeamSize = 5);

public sealed record UpdateRosterRequest(string Name);

public sealed record RosterMemberRequest(string UserId, string? RosterRole = null, bool? IsStarter = null);

public sealed record UpdateRosterRoleRequest(string RosterRole);

public sealed record ToggleStarterRequest(bool IsStarter);

public sealed record RosterInviteRequest(string UserId, string? Email = null);

public sealed record AnnounceRequest(string Message);

public sealed record CardImageRequest(string Url);

public sealed record BatchRosterInviteRequest(List<BatchInvitee> Invitees);

public sealed record BatchInvitee(string UserId, string? Email = null);

public sealed record TeamBatchRequest(List<string> Ids);

public sealed record ReorderMembersRequest(List<MemberOrderItem> Order);

public sealed record MemberOrderItem(string UserId, int DisplayOrder);

internal static class TeamSlug
{
    internal static string Generate(string name)
    {
        var slug = name.ToLowerInvariant().Trim();
        slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = Regex.Replace(slug, @"\s+", "-");
        return slug.Trim('-');
    }
}
