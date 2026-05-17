using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Esportra.Core.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace Esportra.Api.Endpoints;

public static class SeasonOpsEndpoints
{
    public static void MapSeasonOpsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/seasons/{id:guid}/tournaments", async (Guid id, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct) =>
        {
            using var conn = db.CreateConnection();
            if (!await CanViewSeasonAsync(conn, id, ctx.Items["UserContext"] as UserContext)) return Results.Forbid();
            var rows = await conn.QueryAsync<SeasonTournamentDetailsRow>(
                """
                SELECT st.id, t.name, t.slug, t.game, t.status::text AS status, t.start_date, t.end_date,
                       st.season_role, st.season_stage_order,
                       (SELECT COUNT(*) FROM public.tournament_participants tp WHERE tp.tournament_id = t.id AND tp.status NOT IN ('rejected','cancelled'))::int AS current_participants,
                       t.id AS tournament_id, t.name AS tournament_name, t.status::text AS tournament_status,
                       COALESCE(sn.name, t.name) AS display_name, st.season_role AS role, t.region
                FROM public.season_tournaments st
                JOIN public.tournaments t ON t.id = st.tournament_id
                LEFT JOIN public.season_nodes sn ON sn.season_id = st.season_id AND sn.linked_tournament_id = t.id
                WHERE st.season_id = @id AND t.deleted_at IS NULL
                ORDER BY st.season_stage_order ASC, st.created_at ASC
                """,
                new { id });
            return Results.Ok(rows);
        });

        app.MapPost("/api/seasons/{id:guid}/tournaments", async (
            Guid id,
            [FromBody] AddSeasonTournamentRequest req,
            HttpContext ctx,
            IDbConnectionFactory db,
            AuditService audit,
            HybridCache cache,
            CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Tournament name is required." });
            if (req.Name.Length > 120) return Results.BadRequest(new { error = "Tournament name must be 120 characters or fewer." });
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

            var season = await conn.QuerySingleOrDefaultAsync<dynamic>("SELECT id, name, game, organization_id FROM public.seasons WHERE id = @id AND deleted_at IS NULL", new { id });
            if (season is null) return Results.NotFound();

            using var tx = conn.BeginTransaction();
            var slug = await CreateUniqueTournamentSlugAsync(conn, tx, req.Name);
            var now = DateTime.UtcNow;
            var startDate = req.StartDate ?? now.AddDays(14);
            var endDate = req.EndDate ?? startDate.AddHours(2);
            var registrationDeadline = req.RegistrationDeadline ?? startDate.AddDays(-1);
            var role = SeasonEndpointHelpers.NormalizeTournamentRole(req.Role);
            var tournament = await conn.QuerySingleAsync<dynamic>(
                """
                INSERT INTO public.tournaments (
                    name, description, slug, game, format, max_teams, min_teams, team_size,
                    entry_fee, prize_pool, start_date, end_date, registration_deadline,
                    status, organization_id, is_public, organizer_id, settings, region, currency
                ) VALUES (
                    @name, @description, @slug, @game, @format, @maxTeams, 2, @teamSize,
                    0, 0, @startDate, @endDate, @registrationDeadline,
                    'open'::tournament_status, @organizationId, TRUE, @organizerId, @settings::jsonb, @region, 'USD'
                )
                RETURNING id, slug
                """,
                new
                {
                    name = req.Name.Trim(),
                    description = $"Tournament for {(string)season.name}",
                    slug,
                    game = (string)season.game,
                    format = req.Format ?? "single_elimination",
                    maxTeams = req.MaxTeams ?? 16,
                    teamSize = req.TeamSize ?? 5,
                    startDate,
                    endDate,
                    registrationDeadline,
                    organizationId = (Guid?)season.organization_id,
                    organizerId = userCtx.UserIdGuid,
                    settings = JsonSerializer.Serialize(new { seasonId = id, seasonRole = role, displayName = req.DisplayName, registrationType = role is "finals" ? "closed" : "open" }),
                    region = req.Region
                }, tx);

            var tournamentId = (Guid)tournament.id;
            var seasonTournamentId = await conn.QuerySingleAsync<Guid>(
                """
                INSERT INTO public.season_tournaments (season_id, tournament_id, season_role, season_stage_order)
                VALUES (@seasonId, @tournamentId, @role, COALESCE((SELECT MAX(season_stage_order) + 1 FROM public.season_tournaments WHERE season_id = @seasonId), 0))
                RETURNING id
                """,
                new { seasonId = id, tournamentId, role }, tx);

            await conn.ExecuteAsync(
                """
                INSERT INTO public.season_nodes
                    (season_id, parent_node_id, name, slug, node_type, display_order, region, linked_tournament_id, status, starts_at, ends_at, metadata)
                VALUES
                    (@seasonId,
                     (SELECT id FROM public.season_nodes WHERE season_id = @seasonId AND node_type = 'root' LIMIT 1),
                     @name, @slug, @nodeType,
                     COALESCE((SELECT MAX(display_order) + 1 FROM public.season_nodes WHERE season_id = @seasonId), 1),
                     @region, @tournamentId, 'scheduled', @startsAt, @endsAt, @metadata::jsonb)
                """,
                new
                {
                    seasonId = id,
                    name = req.DisplayName ?? req.Name.Trim(),
                    slug,
                    nodeType = role == "finals" ? "final" : role,
                    req.Region,
                    tournamentId,
                    startsAt = startDate,
                    endsAt = endDate,
                    metadata = JsonSerializer.Serialize(new { role, format = req.Format, maxTeams = req.MaxTeams })
                }, tx);

            tx.Commit();
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.tournament.create", id, (string)season.name, new { tournamentId, req.Name, role }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            try { await cache.RemoveByTagAsync("tournament-list", ct); } catch { }
            return Results.Ok(new { seasonTournamentId, tournamentId, slug = (string)tournament.slug });
        }).RequireAuthorization("Organizer");

        app.MapDelete("/api/seasons/{id:guid}/tournaments/{tournamentId:guid}", async (Guid id, Guid tournamentId, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            await conn.ExecuteAsync("DELETE FROM public.season_tournaments WHERE season_id = @id AND tournament_id = @tournamentId", new { id, tournamentId });
            await conn.ExecuteAsync("UPDATE public.season_nodes SET linked_tournament_id = NULL, updated_at = NOW() WHERE season_id = @id AND linked_tournament_id = @tournamentId", new { id, tournamentId });
            await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.tournament.unlink", id, "Season", new { tournamentId }, ct);
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("Organizer");

        app.MapGet("/api/seasons/{id:guid}/participants", async (Guid id, int limit, int offset, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct) =>
        {
            limit = Math.Clamp(limit <= 0 ? SeasonConstants.DefaultPaginationLimit : limit, 1, SeasonConstants.MaxPaginationLimit);
            offset = Math.Max(offset, 0);
            using var conn = db.CreateConnection();
            if (!await CanViewSeasonAsync(conn, id, ctx.Items["UserContext"] as UserContext)) return Results.Forbid();
            var rows = await conn.QueryAsync<SeasonParticipantRow>(
                """
                SELECT sp.id, sp.season_id, sp.team_id, COALESCE(t.name, sp.team_name) AS team_name,
                       COALESCE(t.logo_url, sp.team_logo_url) AS team_logo_url,
                       COALESCE(t.slug, sp.team_slug) AS team_slug,
                       sp.status, sp.registered_by, sp.notes, sp.created_at, sp.updated_at
                FROM public.season_participants sp
                LEFT JOIN public.teams t ON t.id = sp.team_id
                WHERE sp.season_id = @id
                ORDER BY sp.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { id, limit, offset });
            return Results.Ok(rows);
        });

        app.MapPost("/api/seasons/{id:guid}/register", async (Guid id, HttpContext ctx, IDbConnectionFactory db, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            RegisterSeasonRequest? req = null;
            if (ctx.Request.ContentLength.GetValueOrDefault() > 0)
                req = await JsonSerializer.DeserializeAsync<RegisterSeasonRequest>(ctx.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            if (req?.Notes?.Length > 500) return Results.BadRequest(new { error = "Notes must be 500 characters or fewer." });
            using var conn = db.CreateConnection();
            var season = await conn.QuerySingleOrDefaultAsync<dynamic>("SELECT id, status FROM public.seasons WHERE id = @id AND deleted_at IS NULL", new { id });
            if (season is null) return Results.NotFound();
            var status = (string)season.status;
            if (status is not "published" and not "active") return Results.BadRequest(new { error = "Season is not accepting registrations." });

            var team = await conn.QuerySingleOrDefaultAsync<dynamic>(
                """
                SELECT t.id, t.name, t.logo_url, t.slug
                FROM public.teams t
                WHERE (@teamId IS NULL OR t.id = @teamId)
                  AND EXISTS (
                      SELECT 1 FROM public.team_members tm
                      WHERE tm.team_id = t.id AND tm.user_id = @userId AND tm.is_active = TRUE
                        AND tm.role IN ('captain','owner','admin')
                  )
                ORDER BY t.created_at ASC
                LIMIT 1
                """,
                new { teamId = req?.TeamId, userId = userCtx.UserIdGuid });
            if (team is null) return Results.Forbid();

            var row = await conn.QuerySingleAsync<SeasonParticipantRow>(
                """
                INSERT INTO public.season_participants
                    (season_id, team_id, team_name, team_logo_url, team_slug, status, registered_by, notes)
                VALUES
                    (@seasonId, @teamId, @teamName, @teamLogoUrl, @teamSlug, 'pending', @registeredBy, @notes)
                ON CONFLICT (season_id, team_id) DO UPDATE SET
                    status = CASE WHEN public.season_participants.status = 'rejected' THEN 'pending' ELSE public.season_participants.status END,
                    notes = COALESCE(EXCLUDED.notes, public.season_participants.notes),
                    updated_at = NOW()
                RETURNING id, season_id, team_id, team_name, team_logo_url, team_slug, status, registered_by, notes, created_at, updated_at
                """,
                new
                {
                    seasonId = id,
                    teamId = (Guid)team.id,
                    teamName = (string)team.name,
                    teamLogoUrl = (string?)team.logo_url,
                    teamSlug = (string?)team.slug,
                    registeredBy = userCtx.UserIdGuid,
                    notes = req?.Notes
                });
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return Results.Ok(row);
        }).RequireAuthorization("Authenticated");

        app.MapDelete("/api/seasons/{id:guid}/participants/{participantId:guid}", async (Guid id, Guid participantId, HttpContext ctx, IDbConnectionFactory db, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var affected = await conn.ExecuteAsync("DELETE FROM public.season_participants WHERE id = @participantId AND season_id = @id", new { id, participantId });
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return affected == 0 ? Results.NotFound() : Results.Ok(new { deleted = true });
        }).RequireAuthorization("Organizer");

        app.MapGet("/api/seasons/{id:guid}/standings", async (Guid id, int limit, int offset, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct) =>
        {
            limit = Math.Clamp(limit <= 0 ? SeasonConstants.DefaultPaginationLimit : limit, 1, SeasonConstants.MaxPaginationLimit);
            offset = Math.Max(offset, 0);
            using var conn = db.CreateConnection();
            if (!await CanViewSeasonAsync(conn, id, ctx.Items["UserContext"] as UserContext)) return Results.Forbid();
            var rows = await conn.QueryAsync<SeasonStandingRow>(
                """
                SELECT ss.id, ss.season_id, ss.team_id, t.name AS team_name, t.logo_url AS team_logo_url,
                       ss.total_points, ss.qualification_status, ss.version, ss.created_at, ss.updated_at,
                       ss.standing_rank, t.name AS display_name
                FROM public.season_standings ss
                LEFT JOIN public.teams t ON t.id = ss.team_id
                WHERE ss.season_id = @id
                ORDER BY ss.standing_rank NULLS LAST, ss.total_points DESC, ss.updated_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { id, limit, offset });
            return Results.Ok(rows);
        });

        app.MapPost("/api/seasons/{id:guid}/recalculate", async (Guid id, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, SeasonStandingsSyncService seasonSync, CancellationToken ct) =>
            await RecalculateSeasonAsync(id, ctx, db, audit, cache, seasonSync, ct)).RequireAuthorization("Organizer");

        app.MapGet("/api/seasons/{id:guid}/qualifications", async (Guid id, int limit, int offset, HttpContext ctx, IDbConnectionFactory db, CancellationToken ct) =>
        {
            limit = Math.Clamp(limit <= 0 ? SeasonConstants.DefaultPaginationLimit : limit, 1, SeasonConstants.MaxPaginationLimit);
            offset = Math.Max(offset, 0);
            using var conn = db.CreateConnection();
            if (!await CanViewSeasonAsync(conn, id, ctx.Items["UserContext"] as UserContext)) return Results.Forbid();
            var rows = await conn.QueryAsync<SeasonQualificationRecordRow>(
                """
                SELECT qr.id, qr.season_id, qr.destination_node_id, qr.status, qr.qualification_type,
                       qr.display_name, src.name AS source_node_name, qr.team_id, qr.user_id,
                       qr.notes, qr.created_at, qr.updated_at
                FROM public.season_qualification_records qr
                LEFT JOIN public.season_nodes src ON src.id = qr.source_node_id
                WHERE qr.season_id = @id
                ORDER BY qr.created_at DESC
                LIMIT @limit OFFSET @offset
                """,
                new { id, limit, offset });
            return Results.Ok(rows);
        });

        app.MapPut("/api/seasons/{id:guid}/qualifications/{recordId:guid}", async (Guid id, Guid recordId, [FromBody] UpdateQualificationRequest req, HttpContext ctx, IDbConnectionFactory db, HybridCache cache, CancellationToken ct) =>
        {
            var userCtx = ctx.Items["UserContext"] as UserContext;
            if (userCtx is null) return Results.Unauthorized();
            if (req.Notes?.Length > SeasonConstants.MaxNotesLength) return Results.BadRequest(new { error = "Notes must be 500 characters or fewer." });
            using var conn = db.CreateConnection();
            if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();
            var row = await conn.QuerySingleOrDefaultAsync<SeasonQualificationRecordRow>(
                """
                UPDATE public.season_qualification_records
                SET status = COALESCE(@status, status),
                    qualification_type = COALESCE(@qualificationType, qualification_type),
                    notes = COALESCE(@notes, notes),
                    updated_at = NOW()
                WHERE id = @recordId AND season_id = @id
                RETURNING id, season_id, destination_node_id, status, qualification_type, display_name,
                          NULL::text AS source_node_name, team_id, user_id, notes, created_at, updated_at
                """,
                new { id, recordId, status = NormalizeQualificationStatus(req.Status), qualificationType = NormalizeQualificationType(req.QualificationType), notes = req?.Notes });
            await SeasonEndpointHelpers.InvalidateSeasonCacheAsync(cache, id, ct);
            return row is null ? Results.NotFound() : Results.Ok(row);
        }).RequireAuthorization("Organizer");
    }

    private static async Task<bool> CanViewSeasonAsync(System.Data.IDbConnection conn, Guid seasonId, UserContext? userCtx)
    {
        var isPublic = await conn.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.seasons WHERE id = @seasonId AND deleted_at IS NULL AND is_public = TRUE)", new { seasonId });
        return isPublic || await SeasonEndpointHelpers.CanManageSeasonAsync(conn, seasonId, userCtx);
    }

    private static async Task<string> CreateUniqueTournamentSlugAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, string name)
    {
        var slug = SeasonEndpointHelpers.Slugify(name);
        var unique = slug;
        var suffix = 1;
        while (suffix < 1000 && await conn.QuerySingleAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.tournaments WHERE LOWER(slug) = LOWER(@slug))", new { slug = unique }, tx))
        {
            suffix++;
            unique = $"{slug}-{suffix}";
        }
        if (suffix >= SeasonConstants.MaxSlugCollisionAttempts) throw new InvalidOperationException("Unable to generate a unique tournament slug.");
        return unique;
    }

    public static async Task<IResult> RecalculateSeasonAsync(Guid id, HttpContext ctx, IDbConnectionFactory db, AuditService audit, HybridCache cache, SeasonStandingsSyncService seasonSync, CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        if (!await SeasonEndpointHelpers.CanManageSeasonAsync(conn, id, userCtx)) return Results.Forbid();

        await seasonSync.RecalculateSeasonStandingsAsync(id, cache, ct);
        await SeasonEndpointHelpers.LogSeasonAuditAsync(audit, userCtx, "season.standings.recalculate", id, "Season", new { }, ct);
        return Results.Ok(new { success = true });
    }
    private static string? NormalizeQualificationStatus(string? status)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qualified", "eliminated", "pending" };
        return !string.IsNullOrWhiteSpace(status) && allowed.Contains(status) ? status.ToLowerInvariant() : null;
    }

    private static string? NormalizeQualificationType(string? type)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qualified", "wildcard", "reserve" };
        return !string.IsNullOrWhiteSpace(type) && allowed.Contains(type) ? type.ToLowerInvariant() : null;
    }
}

public sealed record AddSeasonTournamentRequest(
    string Name,
    string Role,
    string? Region,
    string? DisplayName,
    string? Format,
    int? MaxTeams,
    int? TeamSize,
    DateTime? StartDate,
    DateTime? EndDate,
    DateTime? RegistrationDeadline);

public sealed record RegisterSeasonRequest(Guid? TeamId = null, string? Notes = null);
public sealed record UpdateQualificationRequest(string? Status = null, string? QualificationType = null, string? Notes = null);

public sealed record SeasonTournamentDetailsRow(
    Guid Id,
    string Name,
    string Slug,
    string Game,
    string Status,
    DateTime? StartDate,
    DateTime? EndDate,
    string? SeasonRole,
    int? SeasonStageOrder,
    int CurrentParticipants,
    Guid? TournamentId,
    string? TournamentName,
    string? TournamentStatus,
    string? DisplayName,
    string Role,
    string? Region);

public sealed record SeasonParticipantRow(
    Guid Id,
    Guid SeasonId,
    Guid TeamId,
    string TeamName,
    string? TeamLogoUrl,
    string? TeamSlug,
    string Status,
    Guid? RegisteredBy,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SeasonStandingRow(
    Guid Id,
    Guid SeasonId,
    Guid TeamId,
    string? TeamName,
    string? TeamLogoUrl,
    int TotalPoints,
    string? QualificationStatus,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int? StandingRank,
    string? DisplayName);

public sealed record SeasonQualificationRecordRow(
    Guid Id,
    Guid SeasonId,
    Guid? DestinationNodeId,
    string Status,
    string? QualificationType,
    string? DisplayName,
    string? SourceNodeName,
    Guid? TeamId,
    Guid? UserId,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt);




