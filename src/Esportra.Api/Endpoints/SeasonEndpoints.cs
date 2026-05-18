using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

public static class SeasonEndpoints
{
    public static void MapSeasonEndpoints(this WebApplication app)
    {
        app.MapGet("/api/seasons", async (
            string? status,
            string? game,
            string? q,
            bool? mine,
            int limit,
            int offset,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            limit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 100);
            offset = Math.Max(offset, 0);
            var userCtx = ctx.Items["UserContext"] as UserContext;

            using var conn = db.CreateConnection();
            var rows = await conn.QueryAsync<SeasonListRow>(
                """
                SELECT s.id, s.name, s.slug, s.game, s.status, s.start_date, s.end_date, s.created_at,
                       COALESCE(st.cnt, 0)::int AS tournament_count,
                       COALESCE(sp.cnt, 0)::int AS participant_count
                FROM public.seasons s
                LEFT JOIN (SELECT season_id, COUNT(*) AS cnt FROM public.season_tournaments GROUP BY season_id) st ON st.season_id = s.id
                LEFT JOIN (SELECT season_id, COUNT(*) AS cnt FROM public.season_participants WHERE status <> 'rejected' GROUP BY season_id) sp ON sp.season_id = s.id
                WHERE s.deleted_at IS NULL
                  AND (@status IS NULL OR s.status = @status)
                  AND (@game IS NULL OR s.game ILIKE @game)
                  AND (@q IS NULL OR s.name ILIKE @query OR s.slug ILIKE @query)
                  AND (
                      COALESCE(@mine, FALSE) = FALSE
                      OR (@userId IS NOT NULL AND (
                          s.owner_user_id = @userId
                          OR EXISTS (SELECT 1 FROM public.season_staff ss WHERE ss.season_id = s.id AND ss.user_id = @userId)
                      ))
                  )
                  AND (
                      s.is_public = TRUE
                      OR (@userId IS NOT NULL AND (
                          s.owner_user_id = @userId
                          OR EXISTS (SELECT 1 FROM public.season_staff ss WHERE ss.season_id = s.id AND ss.user_id = @userId)
                          OR EXISTS (SELECT 1 FROM public.organizations o WHERE o.id = s.organization_id AND o.owner_id = @userId)
                      ))
                      OR @isAdmin = TRUE
                  )
                ORDER BY s.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new
                {
                    status,
                    game,
                    q,
                    query = "%" + (q ?? "").Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%",
                    mine = mine ?? false,
                    userId = userCtx?.UserIdGuid,
                    isAdmin = SeasonEndpointHelpers.IsPlatformAdmin(userCtx),
                    limit,
                    offset
                });

            return Results.Ok(rows);
        });

        app.MapPost("/api/seasons", async (
            [FromBody] CreateSeasonRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Season name is required." });
            if (req.Name.Length > 120) return Results.BadRequest(new { error = "Season name must be 120 characters or fewer." });
            if (string.IsNullOrWhiteSpace(req.Game)) return Results.BadRequest(new { error = "Game is required." });
            if (req.Game.Length > 60) return Results.BadRequest(new { error = "Game must be 60 characters or fewer." });
            if (req.Description?.Length > SeasonConstants.MaxDescriptionLength) return Results.BadRequest(new { error = "Description must be 2000 characters or fewer." });

            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();

            var slug = await SeasonEndpointHelpers.CreateUniqueSeasonSlugAsync(conn, tx, req.Name, null);
            var season = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO public.seasons
                    (name, slug, game, description, participant_mode, owner_user_id, organization_id,
                     start_date, end_date, banner_url, logo_url, settings)
                VALUES
                    (@name, @slug, @game, @description, @participantMode, @ownerUserId, @organizationId,
                     @startDate, @endDate, @bannerUrl, @logoUrl, '{}'::jsonb)
                RETURNING id, name, slug, status
                """,
                new
                {
                    name = req.Name.Trim(),
                    slug,
                    game = req.Game.Trim(),
                    description = req.Description,
                    participantMode = SeasonEndpointHelpers.NormalizeParticipantMode(req.ParticipantMode),
                    ownerUserId = userCtx.UserIdGuid,
                    organizationId = req.OrganizationId,
                    startDate = req.StartDate,
                    endDate = req.EndDate,
                    bannerUrl = req.BannerUrl,
                    logoUrl = req.LogoUrl
                }, tx);

            var seasonId = (Guid)season.id;
            var rootNodeId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO public.season_nodes (season_id, name, slug, node_type, display_order, status)
                VALUES (@seasonId, @name, @slug, 'root', 0, 'draft')
                RETURNING id
                """,
                new { seasonId, name = req.Name.Trim(), slug }, tx);

            tx.Commit();
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.create", seasonId, req.Name, new { req.Game }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, seasonId, ct);

            return Results.Ok(new { id = seasonId, name = (string)season.name, slug = (string)season.slug, rootNodeId, status = (string)season.status });
        }).RequireAuthorization("Organizer");

        app.MapGet("/api/seasons/{idOrSlug}", async (
            string idOrSlug,
            HttpContext ctx,
            IDbConnectionFactory db,
            CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var userCtx = ctx.Items["UserContext"] as UserContext;
            var season = await SeasonEndpointHelpers.GetSeasonDetailAsync(conn, idOrSlug);
            if (season is null) return Results.NotFound();

            var canManage = await SeasonEndpointHelpers.CanManageSeasonAsync(conn, season.Id, userCtx);
            if (!season.IsPublic && !canManage) return Results.Forbid();

            var nodes = (await conn.QueryAsync<SeasonNodeRow>(
                """
                SELECT sn.id, sn.season_id, sn.parent_node_id, sn.name, sn.slug, sn.node_type, sn.display_order,
                       sn.region, sn.city, sn.country, sn.linked_tournament_id, sn.linked_stage_id, sn.status,
                       sn.registration_deadline, sn.starts_at, sn.ends_at, sn.metadata::text AS metadata, sn.created_at, sn.updated_at,
                       t.name AS linked_tournament_name, ts.name AS linked_stage_name
                FROM public.season_nodes sn
                LEFT JOIN public.tournaments t ON t.id = sn.linked_tournament_id
                LEFT JOIN public.tournament_stages ts ON ts.id = sn.linked_stage_id
                WHERE sn.season_id = @seasonId
                ORDER BY sn.display_order ASC, sn.created_at ASC
                """,
                new { seasonId = season.Id })).AsList();

            var rules = await conn.QueryAsync<SeasonRuleDataRow>(
                """
                SELECT id, source_node_id, source_stage_id, destination_node_id,
                       placement_start AS placement_from, placement_end AS placement_to,
                       points AS points_awarded, qualification_status, auto_create_qualification, region_key
                FROM public.season_point_rules
                WHERE season_id = @seasonId
                ORDER BY placement_start ASC, created_at ASC
                """,
                new { seasonId = season.Id });

            var staff = await conn.QueryAsync<SeasonStaffMemberRow>(
                """
                SELECT ss.user_id, ss.role, p.username, p.full_name
                FROM public.season_staff ss
                LEFT JOIN public.profiles p ON p.id = ss.user_id
                WHERE ss.season_id = @seasonId
                ORDER BY ss.created_at ASC
                """,
                new { seasonId = season.Id });

            return Results.Ok(new
            {
                season,
                nodes,
                tree = SeasonEndpointHelpers.BuildTree(nodes),
                rules,
                staff,
                permissions = new { canManage, isPublic = season.IsPublic }
            });
        });

        app.MapPut("/api/seasons/{id:guid}", async (
            Guid id,
            [FromBody] UpdateSeasonRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            if (req.Name?.Length > SeasonConstants.MaxNameLength) return Results.BadRequest(new { error = "Season name must be 120 characters or fewer." });
            if (req.Game?.Length > SeasonConstants.MaxGameLength) return Results.BadRequest(new { error = "Game must be 60 characters or fewer." });
            if (req.Description?.Length > SeasonConstants.MaxDescriptionLength) return Results.BadRequest(new { error = "Description must be 2000 characters or fewer." });

            var updated = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                UPDATE public.seasons
                SET name = COALESCE(@name, name),
                    game = COALESCE(@game, game),
                    slug = COALESCE(@slug, slug),
                    description = @description,
                    participant_mode = COALESCE(@participantMode, participant_mode),
                    status = COALESCE(@status, status),
                    is_public = COALESCE(@isPublic, is_public),
                    allow_manual_overrides = COALESCE(@allowManualOverrides, allow_manual_overrides),
                    start_date = @startDate,
                    end_date = @endDate,
                    banner_url = @bannerUrl,
                    logo_url = @logoUrl,
                    settings = CASE WHEN @settings IS NULL THEN settings ELSE @settings::jsonb END,
                    version = version + 1,
                    updated_at = NOW()
                WHERE id = @id AND deleted_at IS NULL
                RETURNING id, name
                """,
                new
                {
                    id,
                    name = req.Name,
                    game = req.Game,
                    slug = req.Slug,
                    description = req.Description,
                    participantMode = SeasonEndpointHelpers.NormalizeParticipantMode(req.ParticipantMode),
                    status = SeasonEndpointHelpers.NormalizeSeasonStatus(req.Status),
                    isPublic = req.IsPublic,
                    allowManualOverrides = req.AllowManualOverrides,
                    startDate = req.StartDate,
                    endDate = req.EndDate,
                    bannerUrl = req.BannerUrl,
                    logoUrl = req.LogoUrl,
                    settings = req.Settings is null ? null : JsonSerializer.Serialize(req.Settings)
                });

            if (updated is null) return Results.NotFound();
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.update", id, (string)updated.name, new { req.Status }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            var season = await SeasonEndpointHelpers.GetSeasonDetailAsync(conn, id.ToString());
            if (season is null) return Results.NotFound();
            var nodes = (await conn.QueryAsync<SeasonNodeRow>(
                """
                SELECT sn.id, sn.season_id, sn.parent_node_id, sn.name, sn.slug, sn.node_type, sn.display_order,
                       sn.region, sn.city, sn.country, sn.linked_tournament_id, sn.linked_stage_id, sn.status,
                       sn.registration_deadline, sn.starts_at, sn.ends_at, sn.metadata::text AS metadata, sn.created_at, sn.updated_at,
                       t.name AS linked_tournament_name, ts.name AS linked_stage_name
                FROM public.season_nodes sn
                LEFT JOIN public.tournaments t ON t.id = sn.linked_tournament_id
                LEFT JOIN public.tournament_stages ts ON ts.id = sn.linked_stage_id
                WHERE sn.season_id = @seasonId
                ORDER BY sn.display_order ASC, sn.created_at ASC
                """,
                new { seasonId = id })).AsList();
            var rules = await conn.QueryAsync<SeasonRuleDataRow>(
                """
                SELECT id, source_node_id, source_stage_id, destination_node_id,
                       placement_start AS placement_from, placement_end AS placement_to,
                       points AS points_awarded, qualification_status, auto_create_qualification, region_key
                FROM public.season_point_rules
                WHERE season_id = @seasonId
                ORDER BY placement_start ASC, created_at ASC
                """,
                new { seasonId = id });
            var staff = await conn.QueryAsync<SeasonStaffMemberRow>(
                """
                SELECT ss.user_id, ss.role, p.username, p.full_name
                FROM public.season_staff ss
                LEFT JOIN public.profiles p ON p.id = ss.user_id
                WHERE ss.season_id = @seasonId
                ORDER BY ss.created_at ASC
                """,
                new { seasonId = id });
            return Results.Ok(new { season, nodes, tree = SeasonEndpointHelpers.BuildTree(nodes), rules, staff, permissions = new { canManage = true, isPublic = season.IsPublic } });
        }).RequireAuthorization("Organizer");

        app.MapDelete("/api/seasons/{id:guid}", async (
            Guid id,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            var affected = await conn.ExecuteAsync(
                "UPDATE public.seasons SET deleted_at = NOW(), updated_at = NOW(), version = version + 1 WHERE id = @id AND deleted_at IS NULL",
                new { id });
            if (affected == 0) return Results.NotFound();

            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.delete", id, "Season", new { }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { deleted = true });
        }).RequireAuthorization("Organizer");

        app.MapPost("/api/seasons/{id:guid}/publish", async (Guid id, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
            await SeasonEndpointHelpers.SetSeasonStatusAsync(id, "published", ctx, db, audit, cache, ct)).RequireAuthorization("Organizer");

        app.MapPost("/api/seasons/{id:guid}/start", async (Guid id, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
            await SeasonEndpointHelpers.SetSeasonStatusAsync(id, "active", ctx, db, audit, cache, ct)).RequireAuthorization("Organizer");

        app.MapPost("/api/seasons/{id:guid}/complete", async (Guid id, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
            await SeasonEndpointHelpers.SetSeasonStatusAsync(id, "completed", ctx, db, audit, cache, ct)).RequireAuthorization("Organizer");

        app.MapPost("/api/seasons/{id:guid}/sync-status", async (Guid id, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            var now = DateTime.UtcNow;
            var current = await conn.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT status, start_date, end_date FROM public.seasons WHERE id = @id AND deleted_at IS NULL",
                new { id });
            if (current is null) return Results.NotFound();
            var status = (string)current.status;
            if (status is "published" && current.start_date is not null && (DateTime)current.start_date <= now) status = "active";
            if (status is "active" && current.end_date is not null && (DateTime)current.end_date <= now) status = "completed";
            return await SeasonEndpointHelpers.SetSeasonStatusAsync(id, status, ctx, db, audit, cache, ct);
        }).RequireAuthorization("Organizer");

        app.MapPost("/api/seasons/{id:guid}/cancel", async (Guid id, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
            await SeasonEndpointHelpers.SetSeasonStatusAsync(id, "cancelled", ctx, db, audit, cache, ct)).RequireAuthorization("Organizer");

        app.MapPost("/api/seasons/{id:guid}/archive", async (Guid id, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
            await SeasonEndpointHelpers.SetSeasonStatusAsync(id, "archived", ctx, db, audit, cache, ct)).RequireAuthorization("Organizer");

        app.MapPost("/api/seasons/{id:guid}/duplicate", async (
            Guid id,
            [FromBody] DuplicateSeasonRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            using var tx = conn.BeginTransaction();
            var source = await conn.QuerySingleOrDefaultAsync<dynamic>("""
                SELECT id, name, slug, game, description, participant_mode, status, owner_user_id, organization_id,
                       is_public, allow_manual_overrides, start_date, end_date, banner_url, logo_url, settings
                FROM public.seasons WHERE id = @id AND deleted_at IS NULL
                """, new { id }, tx);
            if (source is null) { tx.Rollback(); return Results.NotFound(); }

            var requestedName = req.Name ?? req.NewName;
            var requestedSlug = req.Slug ?? req.NewSlug;
            var name = string.IsNullOrWhiteSpace(requestedName) ? $"{(string)source.name} Copy" : requestedName.Trim();
            if (name.Length > 120) { tx.Rollback(); return Results.BadRequest(new { error = "Season name must be 120 characters or fewer." }); }
            if (requestedSlug?.Length > 140) { tx.Rollback(); return Results.BadRequest(new { error = "Season slug must be 140 characters or fewer." }); }
            var slug = await SeasonEndpointHelpers.CreateUniqueSeasonSlugAsync(conn, tx, string.IsNullOrWhiteSpace(requestedSlug) ? name : requestedSlug, null);
            var newSeason = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO public.seasons
                    (name, slug, game, description, participant_mode, status, owner_user_id, organization_id,
                     is_public, allow_manual_overrides, start_date, end_date, banner_url, logo_url, settings)
                VALUES
                    (@name, @slug, @game, @description, @participantMode, 'draft', @ownerUserId, @organizationId,
                     @isPublic, @allowManualOverrides, @startDate, @endDate, @bannerUrl, @logoUrl, @settings::jsonb)
                RETURNING id, name, slug, status
                """,
                new
                {
                    name,
                    slug,
                    game = (string)source.game,
                    description = (string?)source.description,
                    participantMode = (string)source.participant_mode,
                    ownerUserId = userCtx.UserIdGuid,
                    organizationId = (Guid?)source.organization_id,
                    isPublic = (bool)source.is_public,
                    allowManualOverrides = (bool)source.allow_manual_overrides,
                    startDate = (DateTime?)source.start_date,
                    endDate = (DateTime?)source.end_date,
                    bannerUrl = (string?)source.banner_url,
                    logoUrl = (string?)source.logo_url,
                    settings = source.settings?.ToString() ?? "{}"
                }, tx);

            var newSeasonId = (Guid)newSeason.id;
            var nodeRows = (await conn.QueryAsync<SeasonNodeCloneRow>(
                """
                SELECT id, parent_node_id, name, slug, node_type, display_order, region, city, country,
                       registration_deadline, starts_at, ends_at, metadata
                FROM public.season_nodes
                WHERE season_id = @id
                ORDER BY parent_node_id NULLS FIRST, display_order ASC, created_at ASC
                """,
                new { id }, tx)).AsList();
            var map = new Dictionary<Guid, Guid>();
            foreach (var node in nodeRows)
            {
                var newNodeId = Guid.NewGuid();
                map[node.Id] = newNodeId;
                await conn.ExecuteAsync(
                    """
                    INSERT INTO public.season_nodes
                        (id, season_id, parent_node_id, name, slug, node_type, display_order, region, city, country,
                         linked_tournament_id, linked_stage_id, status, registration_deadline, starts_at, ends_at, metadata)
                    VALUES
                        (@newNodeId, @newSeasonId, @parentNodeId, @name, @slug, @nodeType, @displayOrder, @region, @city, @country,
                         NULL, NULL, 'draft', @registrationDeadline, @startsAt, @endsAt, @metadata::jsonb)
                    """,
                    new
                    {
                        newNodeId,
                        newSeasonId,
                        parentNodeId = node.ParentNodeId.HasValue && map.TryGetValue(node.ParentNodeId.Value, out var mappedParent) ? mappedParent : (Guid?)null,
                        name = node.Name,
                        slug = node.Slug,
                        nodeType = node.NodeType,
                        displayOrder = node.DisplayOrder,
                        node.Region,
                        node.City,
                        node.Country,
                        node.RegistrationDeadline,
                        node.StartsAt,
                        node.EndsAt,
                        metadata = node.Metadata ?? "{}"
                    }, tx);
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO public.season_staff (season_id, user_id, role)
                SELECT @newSeasonId, user_id, role
                FROM public.season_staff
                WHERE season_id = @id AND user_id <> @ownerUserId
                ON CONFLICT (season_id, user_id) DO NOTHING
                """,
                new { newSeasonId, id, ownerUserId = userCtx.UserIdGuid }, tx);

            tx.Commit();
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.duplicate", newSeasonId, name, new { sourceSeasonId = id }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, newSeasonId, ct);
            return Results.Ok(new { success = true, seasonId = newSeasonId, id = newSeasonId, name = (string)newSeason.name, slug = (string)newSeason.slug, status = (string)newSeason.status });
        }).RequireAuthorization("Organizer");

        app.MapGet("/api/seasons/{id:guid}/audit", async (Guid id, int limit, int offset, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct) =>
        {
            limit = Math.Clamp(limit <= 0 ? SeasonConstants.DefaultPaginationLimit : limit, 1, SeasonConstants.MaxPaginationLimit);
            offset = Math.Max(offset, 0);
            var userCtx = ctx.Items["UserContext"] as UserContext;
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var rows = await conn.QueryAsync<SeasonAuditLogEntryRow>(
                """
                SELECT al.id, al.target_id AS season_id, al.admin_id AS actor_id,
                       p.username AS actor_username, al.action_type AS action,
                       al.target_type AS entity_type, al.target_id AS entity_id,
                       al.details::text AS details, NULL::text AS reason, al.created_at
                FROM public.audit_logs al
                LEFT JOIN public.profiles p ON p.id = al.admin_id
                WHERE al.target_type = 'season' AND al.target_id = @id
                ORDER BY al.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { id, limit, offset });
            return Results.Ok(rows);
        }).RequireAuthorization("Authenticated");
    }
}

internal static class SeasonEndpointHelpers
{
    public static bool IsPlatformAdmin(UserContext? userCtx) => userCtx is not null
        && (userCtx.Roles.Contains("admin", StringComparer.OrdinalIgnoreCase)
            || userCtx.Roles.Contains("super_admin", StringComparer.OrdinalIgnoreCase));

    public static async Task<bool> CanManageSeasonAsync(IDbConnection conn, Guid seasonId, UserContext? userCtx, IDbTransaction? tx = null)
    {
        if (userCtx is null) return false;
        if (IsPlatformAdmin(userCtx)) return true;
        return await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1
                FROM public.seasons s
                LEFT JOIN public.organizations o ON o.id = s.organization_id
                LEFT JOIN public.season_staff ss ON ss.season_id = s.id AND ss.user_id = @userId
                WHERE s.id = @seasonId
                  AND s.deleted_at IS NULL
                  AND (s.owner_user_id = @userId OR o.owner_id = @userId OR ss.role IN ('co_organizer','admin'))
            )
            """,
            new { seasonId, userId = userCtx.UserIdGuid }, tx);
    }

    public static async Task<SeasonDetailRow?> GetSeasonDetailAsync(IDbConnection conn, string idOrSlug)
    {
        return await conn.QuerySingleOrDefaultAsync<SeasonDetailRow>(
            """
            SELECT s.id, s.name, s.slug, s.description, s.game, s.participant_mode, s.status,
                   s.owner_user_id, s.organization_id, s.is_public, s.allow_manual_overrides,
                   s.start_date, s.end_date, s.banner_url, s.logo_url, s.settings::text AS settings, s.created_at, s.updated_at,
                   COALESCE(p.username, '') AS owner_username, p.full_name AS owner_full_name
            FROM public.seasons s
            LEFT JOIN public.profiles p ON p.id = s.owner_user_id
            WHERE s.deleted_at IS NULL
              AND (s.id::text = @idOrSlug OR s.slug = @idOrSlug)
            LIMIT 1
            """,
            new { idOrSlug });
    }

    public static async Task<string> CreateUniqueSeasonSlugAsync(IDbConnection conn, IDbTransaction? tx, string name, Guid? excludingSeasonId)
    {
        var slug = Slugify(name);
        var unique = slug;
        var suffix = 1;
        while (suffix < 1000 && await conn.QuerySingleAsync<bool>(
            """
            SELECT EXISTS(
                SELECT 1 FROM public.seasons
                WHERE LOWER(slug) = LOWER(@slug)
                  AND deleted_at IS NULL
                  AND (@excludingSeasonId IS NULL OR id <> @excludingSeasonId)
            )
            """,
            new { slug = unique, excludingSeasonId }, tx))
        {
            suffix++;
            unique = $"{slug}-{suffix}";
        }
        if (suffix >= 1000) throw new InvalidOperationException("Unable to generate a unique season slug.");
        return unique;
    }

    public static string Slugify(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? $"season-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}" : slug;
    }

    public static string NormalizeParticipantMode(string? mode) => string.Equals(mode, "solo", StringComparison.OrdinalIgnoreCase) ? "solo" : "team";

    public static string? NormalizeSeasonStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "draft", "published", "active", "completed", "archived", "cancelled" };
        return allowed.Contains(status) ? status.ToLowerInvariant() : null;
    }

    public static string NormalizeNodeType(string? type)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "root", "qualifier", "event", "stage", "final", "custom" };
        return !string.IsNullOrWhiteSpace(type) && allowed.Contains(type) ? type.ToLowerInvariant() : "event";
    }

    public static string NormalizeNodeStatus(string? status)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "draft", "scheduled", "live", "completed", "archived" };
        return !string.IsNullOrWhiteSpace(status) && allowed.Contains(status) ? status.ToLowerInvariant() : "draft";
    }

    public static string NormalizeTournamentRole(string? role)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qualifier", "event", "finals", "custom" };
        return !string.IsNullOrWhiteSpace(role) && allowed.Contains(role) ? role.ToLowerInvariant() : "event";
    }

    public static async Task<IResult> SetSeasonStatusAsync(Guid id, string status, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

        using var tx = conn.BeginTransaction();
        var locked = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT id, status, version FROM public.seasons WHERE id = @id AND deleted_at IS NULL FOR UPDATE",
            new { id }, tx);
        if (locked is null) { tx.Rollback(); return Results.NotFound(); }

        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            """
            UPDATE public.seasons
            SET status = @status, updated_at = NOW(), version = version + 1
            WHERE id = @id AND deleted_at IS NULL AND version = @version
            RETURNING id, name, slug, status, updated_at
            """,
            new { id, status, version = locked.version }, tx);
        if (row is null) { tx.Rollback(); return Results.Conflict("Season status update failed due to concurrent modification"); }

        tx.Commit();

        await LogSeasonAuditAsync(audit, userCtx, $"season.{status}", id, (string)row.name, new { status }, ct);
        await InvalidateSeasonCacheAsync(cache, id, ct);
        return Results.Ok(row);
    }

    public static async Task LogSeasonAuditAsync(AuditService audit, UserContext userCtx, string action, Guid seasonId, string seasonName, object details, CancellationToken ct)
    {
        await audit.LogCustomAsync(userCtx.UserIdGuid, userCtx.Email, action, TargetType.Season, seasonId, seasonName, details, AuditSeverity.Low, ct);
    }

    public static async Task InvalidateSeasonCacheAsync(HybridCache cache, Guid seasonId, CancellationToken ct)
    {
        try
        {
            await cache.RemoveByTagAsync("season-list", ct);
            await cache.RemoveByTagAsync($"season:{seasonId}", ct);
            await cache.RemoveByTagAsync($"season-detail:{seasonId}", ct);
            await cache.RemoveByTagAsync($"season-standings:{seasonId}", ct);
            await cache.RemoveByTagAsync($"season-advancement:{seasonId}", ct);
        }
        catch { }
    }

    public static List<SeasonTreeNode> BuildTree(IReadOnlyCollection<SeasonNodeRow> nodes)
    {
        var lookup = nodes.ToDictionary(n => n.Id, n => new SeasonTreeNode(n.Id, n.Name, n.NodeType, n.Status, []));
        var roots = new List<SeasonTreeNode>();
        foreach (var node in nodes.OrderBy(n => n.DisplayOrder).ThenBy(n => n.CreatedAt))
        {
            if (node.ParentNodeId.HasValue && lookup.TryGetValue(node.ParentNodeId.Value, out var parent)) parent.Children.Add(lookup[node.Id]);
            else roots.Add(lookup[node.Id]);
        }
        return roots;
    }
}

public sealed record CreateSeasonRequest(
    string Name,
    string Game,
    string? Description = null,
    string? ParticipantMode = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    string? BannerUrl = null,
    string? LogoUrl = null,
    Guid? OrganizationId = null);

public sealed record UpdateSeasonRequest(
    string? Name = null,
    string? Game = null,
    string? ParticipantMode = null,
    string? Status = null,
    string? Slug = null,
    string? Description = null,
    bool? IsPublic = null,
    bool? AllowManualOverrides = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    string? BannerUrl = null,
    string? LogoUrl = null,
    object? Settings = null,
    int? Version = null);

public sealed record DuplicateSeasonRequest(string? Name = null, string? Slug = null, string? NewName = null, string? NewSlug = null);

public sealed record SeasonListRow(Guid Id, string Name, string Slug, string Game, string Status, DateTime? StartDate, DateTime? EndDate, DateTime CreatedAt, int TournamentCount, int ParticipantCount);

public sealed record SeasonDetailRow(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    string Game,
    string ParticipantMode,
    string Status,
    Guid OwnerUserId,
    Guid? OrganizationId,
    bool IsPublic,
    bool AllowManualOverrides,
    DateTime? StartDate,
    DateTime? EndDate,
    string? BannerUrl,
    string? LogoUrl,
    string? Settings,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string OwnerUsername,
    string? OwnerFullName);

public sealed record SeasonNodeRow(
    Guid Id,
    Guid SeasonId,
    Guid? ParentNodeId,
    string Name,
    string? Slug,
    string NodeType,
    int DisplayOrder,
    string? Region,
    string? City,
    string? Country,
    Guid? LinkedTournamentId,
    Guid? LinkedStageId,
    string Status,
    DateTime? RegistrationDeadline,
    DateTime? StartsAt,
    DateTime? EndsAt,
    string? Metadata,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? LinkedTournamentName,
    string? LinkedStageName);

public sealed record SeasonNodeCloneRow(
    Guid Id,
    Guid? ParentNodeId,
    string Name,
    string? Slug,
    string NodeType,
    int DisplayOrder,
    string? Region,
    string? City,
    string? Country,
    DateTime? RegistrationDeadline,
    DateTime? StartsAt,
    DateTime? EndsAt,
    string? Metadata);

public sealed record SeasonRuleDataRow(
    Guid Id,
    Guid? SourceNodeId,
    Guid? SourceStageId,
    Guid? DestinationNodeId,
    int PlacementFrom,
    int PlacementTo,
    int PointsAwarded,
    string? QualificationStatus,
    bool AutoCreateQualification,
    string? RegionKey);

public sealed record SeasonStaffMemberRow(Guid UserId, string Role, string? Username, string? FullName);
public sealed record SeasonTreeNode(Guid Id, string Name, string Type, string Status, List<SeasonTreeNode> Children);
public sealed record SeasonAuditLogEntryRow(Guid Id, Guid SeasonId, Guid ActorId, string? ActorUsername, string Action, string EntityType, Guid? EntityId, string? Details, string? Reason, DateTime CreatedAt);

