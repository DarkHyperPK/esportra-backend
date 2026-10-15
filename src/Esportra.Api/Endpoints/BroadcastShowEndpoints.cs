using System.Text.Json.Serialization;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Esportra.Core.Broadcast;
using Esportra.Core.Tournaments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static partial class BroadcastShowEndpoints
{
    private static readonly HashSet<string> ValidShowStatuses =
        ["draft", "scheduled", "live", "ended", "cancelled"];

    // Terminal states cannot transition to anything.
    // Any non-terminal state → "cancelled" is always allowed.
    private static readonly Dictionary<string, HashSet<string>> AllowedTransitions = new()
    {
        ["draft"] = ["scheduled", "live", "cancelled"],
        ["scheduled"] = ["draft", "live", "cancelled"],
        ["live"] = ["ended", "cancelled"],
        ["ended"] = [],
        ["cancelled"] = [],
    };

    public static void MapBroadcastShowEndpoints(this WebApplication app)
    {
        app.MapBroadcastPlanningEndpoints();
        app.MapBroadcastMatchPlanningEndpoints();
        app.MapBroadcastShowEventEndpoints();
        app.MapBroadcastRundownEndpoints();
        app.MapBroadcastRundownTemplateEndpoints();
        app.MapBroadcastScreenEndpoints();
        app.MapBroadcastTalentEndpoints();
        app.MapBroadcastShowLifecycleEndpoints();
        app.MapBroadcastActivityEndpoints();
        app.MapBroadcastResultProposalEndpoints();
        app.MapGet("/api/broadcast/shows/{showId:guid}/enrollment", GetEnrollment).RequireAuthorization("Authenticated");
        app.MapPut("/api/broadcast/shows/{showId:guid}/enrollment", SetEnrollment).RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
        app.MapGet("/api/broadcast/shows/{showId:guid}/appearance", GetAppearance).RequireAuthorization("Authenticated");
        app.MapPut("/api/broadcast/shows/{showId:guid}/appearance", SaveAppearance).RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("fixed"));
        app.MapGet("/api/broadcast/nodes/shows/{showId:guid}/matches/{matchId:guid}/context", GetNodeMatchContext).AllowAnonymous().WithMetadata(new RateLimitPolicyMetadata("fixed"));
        app.MapGet("/api/broadcast/shows/{showId:guid}/publication", GetPublication)
            .RequireAuthorization("Authenticated");
        app.MapGet("/api/broadcast/nodes/shows/{showId:guid}/publication", GetNodePublication)
            .AllowAnonymous().WithMetadata(new RateLimitPolicyMetadata("fixed"));
        app.MapTournamentBroadcastEndpoints();
        app.MapPost("/api/broadcast/shows", CreateShow)
            .RequireAuthorization("Authenticated")
            .WithMetadata(new RateLimitPolicyMetadata("fixed"));

        app.MapGet("/api/broadcast/shows", ListShows)
            .RequireAuthorization("Authenticated");

        app.MapGet("/api/broadcast/shows/{showId}", GetShow)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/broadcast/shows/{showId}/push-config", PushShowConfig)
            .RequireAuthorization("Authenticated")
            .WithMetadata(new RateLimitPolicyMetadata("fixed"));

        app.MapGet("/api/broadcast/shows/{showId:guid}/configuration", GetConfiguration)
            .RequireAuthorization("Authenticated");

        app.MapPost("/api/broadcast/shows/{showId}/matches", AddShowMatch)
            .RequireAuthorization("Authenticated")
            .WithMetadata(new RateLimitPolicyMetadata("fixed"));

        app.MapDelete("/api/broadcast/shows/{showId}/matches/{matchId}", RemoveShowMatch)
            .RequireAuthorization("Authenticated")
            .WithMetadata(new RateLimitPolicyMetadata("fixed"));

        app.MapMethods("/api/broadcast/shows/{showId}/status", ["PATCH"], UpdateShowStatus)
            .RequireAuthorization("Authenticated")
            .WithMetadata(new RateLimitPolicyMetadata("fixed"));

        app.MapGet("/api/broadcast/shows/{showId}/vod-chapters", GetVodChapters)
            .RequireAuthorization("Authenticated");
    }

    // ── POST /api/broadcast/shows ─────────────────────────────────────────────

    private static async Task<IResult> CreateShow(
        [FromBody] CreateShowRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.OrgId == Guid.Empty)
            return Results.BadRequest(new { error = "orgId is required" });

        var title = req.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200)
            return Results.BadRequest(new { error = "title must be 1–200 characters" });

        using var conn = db.CreateConnection();
        if (!await IsOrgOwnerAsync(conn, req.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);
        if (req.TournamentId.HasValue && !await BroadcastPlanningEndpoints.IsTournamentInOrganizationAsync(conn, req.TournamentId.Value, req.OrgId))
            return Results.BadRequest(new { error = "Tournament must belong to this organization." });

        var showId = await conn.QuerySingleAsync<Guid>(
            """
            INSERT INTO broadcast_shows (org_id, tournament_id, title, status, scheduled_at, created_by)
            VALUES (@orgId, @tournamentId, @title, 'draft', @scheduledAt, @createdBy)
            RETURNING id
            """,
            new
            {
                orgId = req.OrgId,
                tournamentId = req.TournamentId,
                title,
                scheduledAt = req.ScheduledAt,
                createdBy = userCtx.UserIdGuid,
            });

        return Results.Ok(new { showId });
    }

    // ── GET /api/broadcast/shows?orgId= ──────────────────────────────────────

    private static async Task<IResult> ListShows(
        [FromQuery] Guid? orgId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (!orgId.HasValue || orgId.Value == Guid.Empty)
            return Results.BadRequest(new { error = "orgId is required" });

        using var conn = db.CreateConnection();
        if (!await IsOrgOwnerAsync(conn, orgId.Value, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var shows = await conn.QueryAsync<ShowListRow>(
            """
            SELECT s.id AS Id, s.org_id AS OrgId, s.title AS Title, s.status AS Status,
                   s.scheduled_at AS ScheduledAt, s.started_at AS StartedAt, s.ended_at AS EndedAt,
                   s.created_at AS CreatedAt, s.tournament_id AS TournamentId, t.name AS TournamentName
            FROM broadcast_shows s LEFT JOIN tournaments t ON t.id = s.tournament_id AND t.organization_id = s.org_id AND t.deleted_at IS NULL
            WHERE s.org_id = @orgId
            ORDER BY s.created_at DESC LIMIT 200
            """,
            new { orgId = orgId.Value });

        return Results.Ok(shows);
    }

    // ── GET /api/broadcast/shows/{showId} ─────────────────────────────────────

    private static async Task<IResult> GetShow(
        Guid showId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var show = await FetchShowDetailAsync(conn, showId);

        if (show is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, show.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        var matchSlots = await FetchShowMatchSlotsAsync(conn, showId);

        return Results.Ok(new
        {
            show.Id,
            show.OrgId,
            show.Title,
            show.Status,
            show.ScheduledAt,
            show.StartedAt,
            show.EndedAt,
            show.CreatedAt,
            show.UpdatedAt,
            show.LastSyncedAt,
            show.TournamentId,
            show.TournamentName,
            show.TournamentSlug,
            show.NodeCount,
            show.MatchSlotCount,
            show.MatchSlotsWithCode,
            show.TalentCount,
            show.SponsorCount,
            show.CrewCount,
            MatchSlots = matchSlots.Select(m => new
            {
                Id = m.Id.ToString(),
                ShowId = showId.ToString(),
                MatchId = m.MatchId.ToString(),
                m.MatchCode,
                m.DisplayOrder,
                m.Status,
                m.TeamA,
                m.TeamB,
                m.BestOf,
                MatchTitle = $"{m.TeamA} vs {m.TeamB}",
                m.MatchStatus,
                m.ScheduledAt,
            }).ToArray(),
        });
    }

    private static async Task<ShowDetailRow?> FetchShowDetailAsync(
        System.Data.IDbConnection conn, Guid showId) =>
        await conn.QuerySingleOrDefaultAsync<ShowDetailRow>(
            """
            SELECT
                s.id               AS Id,
                s.org_id           AS OrgId,
                s.title            AS Title,
                s.status           AS Status,
                s.scheduled_at     AS ScheduledAt,
                s.started_at       AS StartedAt,
                s.ended_at         AS EndedAt,
                s.created_at       AS CreatedAt,
                s.updated_at       AS UpdatedAt,
                s.last_synced_at   AS LastSyncedAt,
                s.tournament_id    AS TournamentId,
                t.name             AS TournamentName,
                t.slug             AS TournamentSlug,
                (SELECT COUNT(*) FROM broadcast_nodes
                    WHERE org_id = s.org_id AND is_active = true)          AS NodeCount,
                (SELECT COUNT(*) FROM broadcast_show_matches
                    WHERE show_id = s.id)                                   AS MatchSlotCount,
                (SELECT COUNT(*) FROM broadcast_show_matches
                    WHERE show_id = s.id AND match_code IS NOT NULL)        AS MatchSlotsWithCode,
                (SELECT COUNT(*) FROM broadcast_talent
                    WHERE show_id = s.id)                                   AS TalentCount,
                (SELECT COUNT(*) FROM broadcast_show_sponsors
                    WHERE show_id = s.id AND is_active = true)              AS SponsorCount,
                (SELECT COUNT(*) FROM broadcast_crew
                    WHERE show_id = s.id AND is_active = true)              AS CrewCount
            FROM broadcast_shows s
            LEFT JOIN tournaments t ON t.id = s.tournament_id AND t.organization_id = s.org_id AND t.deleted_at IS NULL
            WHERE s.id = @showId
            """,
            new { showId });

    private static async Task<IEnumerable<ShowDetailMatchSlotRow>> FetchShowMatchSlotsAsync(
        System.Data.IDbConnection conn, Guid showId) =>
        await conn.QueryAsync<ShowDetailMatchSlotRow>(
            $"""
            SELECT sm.id AS Id, sm.match_id AS MatchId, sm.match_code AS MatchCode,
                   sm.display_order AS DisplayOrder, sm.status AS Status,
                   COALESCE(t1.name, tp1.team_name, sp1.username, 'TBD') AS TeamA,
                   COALESCE(t2.name, tp2.team_name, sp2.username, 'TBD') AS TeamB,
                   COALESCE(bm.best_of, 1) AS BestOf, bm.status AS MatchStatus, bm.scheduled_time AS ScheduledAt
            FROM broadcast_show_matches sm JOIN brkt_matches bm ON bm.id = sm.match_id
            JOIN brkt_versions bv ON bv.id = bm.version_id JOIN tournaments mt ON mt.id = bv.tournament_id
            JOIN broadcast_shows bs ON bs.id = sm.show_id
            {BracketTeamResolutionSql.BracketMatchTeam1Joins}
            {BracketTeamResolutionSql.BracketMatchTeam2Joins}
            WHERE sm.show_id = @showId AND mt.organization_id = bs.org_id AND mt.deleted_at IS NULL
              AND (bs.tournament_id IS NULL OR bs.tournament_id = mt.id)
            ORDER BY sm.display_order LIMIT 200
            """,
            new { showId });

    // ── POST /api/broadcast/shows/{showId}/push-config ────────────────────────

    private static async Task<IResult> GetConfiguration(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<ShowRow>("SELECT id AS Id, org_id AS OrgId, title AS Title, status AS Status FROM broadcast_shows WHERE id = @showId", new { showId });
        if (show is null) return Results.NotFound();
        if (!await IsOrgOwnerAsync(conn, show.OrgId, user.UserIdGuid)) return Results.StatusCode(403);
        return Results.Ok(await BuildShowConfigPayloadAsync(conn, show, showId));
    }

    private static async Task<IResult> PushShowConfig(
        Guid showId,
        HttpContext ctx,
        IDbConnectionFactory db,
        IHubContext<BroadcastHub> hub,
        CancellationToken ct,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PushShowConfigRequest? request = null)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<ShowRow>(
            "SELECT id AS Id, org_id AS OrgId, title AS Title, status AS Status FROM broadcast_shows WHERE id = @showId",
            new { showId });

        if (show is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, show.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        if (!Esportra.Core.Broadcast.BroadcastShowRules.CanSendConfiguration(show.Status))
            return Results.Conflict(new { error = "Ended or cancelled shows cannot send configuration." });
        var payload = await BuildShowConfigPayloadAsync(conn, show, showId);
        if (request?.MatchId is Guid requestedMatch)
        {
            var match = payload.Matches.FirstOrDefault(item => item.MatchId == requestedMatch.ToString());
            if (match is null) return Results.BadRequest(new { error = "Choose an eligible match in this show." });
            payload = payload with { PreferredMatchCode = match.MatchCode };
        }
        var version = await PublishRundown(conn, showId, payload);
        if (version is null) return Results.Conflict(new { error = "Show closed before publication. Reload it." });
        payload = payload with { RundownVersion = version };
        await hub.Clients.Group(BroadcastHub.OrgGroup(show.OrgId)).SendAsync("ShowConfig", payload, ct);
        return Results.Ok(new { success = true, rundownVersion = version });
    }

    private static async Task<ShowConfigPayload> BuildShowConfigPayloadAsync(
        System.Data.IDbConnection conn, ShowRow show, Guid showId, Guid? retainedMatchId = null)
    {
        var tournament = await conn.QuerySingleOrDefaultAsync<ConfigTournamentRow>("""
            SELECT t.id AS Id, t.name AS Name, t.slug AS Slug, t.game AS Game
            FROM broadcast_shows s JOIN tournaments t ON t.id = s.tournament_id
            WHERE s.id = @showId AND t.deleted_at IS NULL AND t.organization_id = s.org_id
            """, new { showId });
        var matches = await conn.QueryAsync<ConfigMatchRow>(
            $"""
            SELECT sm.match_id AS MatchId, sm.match_code AS MatchCode,
                   sm.display_order AS DisplayOrder, bm.status AS Status,
                   COALESCE(t1.name, tp1.team_name, sp1.username) AS Team1Name,
                   COALESCE(t2.name, tp2.team_name, sp2.username) AS Team2Name,
                   COALESCE(t1.tag, '') AS Team1Tag, COALESCE(t2.tag, '') AS Team2Tag,
                   COALESCE(t1.logo_url, sp1.avatar_url) AS Team1Logo,
                   COALESCE(t2.logo_url, sp2.avatar_url) AS Team2Logo,
                   COALESCE(bm.team1_score, 0) AS Team1Score, COALESCE(bm.team2_score, 0) AS Team2Score,
                   COALESCE(bm.best_of, 1) AS BestOf, bm.scheduled_time AS ScheduledAt,
                   bm.team1_id AS Team1Id, bm.team2_id AS Team2Id
            FROM broadcast_show_matches sm JOIN brkt_matches bm ON bm.id = sm.match_id
            JOIN brkt_versions bv ON bv.id = bm.version_id JOIN tournaments mt ON mt.id = bv.tournament_id
            JOIN broadcast_shows bs ON bs.id = sm.show_id
            {BracketTeamResolutionSql.BracketMatchTeam1Joins}
            {BracketTeamResolutionSql.BracketMatchTeam2Joins}
            WHERE sm.show_id = @showId AND mt.organization_id = bs.org_id AND mt.deleted_at IS NULL
              AND (bs.tournament_id IS NULL OR bs.tournament_id = mt.id)
              AND bv.status IN ('published', 'active') AND bm.team1_id IS NOT NULL AND bm.team2_id IS NOT NULL
              AND ((bm.winner_id IS NULL AND bm.status IN ('pending', 'scheduled', 'in_progress'))
                   OR bm.status = 'completed')
            ORDER BY sm.display_order LIMIT 200
            """,
            new { showId, retainedMatchId });

        var rundown = await ReadRundownPlans(conn, showId);
        var vetoHistory = (await conn.QueryAsync<ConfigVetoRow>("""
            SELECT a.match_id AS MatchId, a.action_number AS ActionNumber, a.action_type AS Action,
                   gm.map_name AS Map, a.team_side AS TeamSide, a.side AS Side
            FROM match_map_veto_actions a
            LEFT JOIN game_maps gm ON gm.id = a.map_id
            WHERE a.match_id = ANY(@matchIds) ORDER BY a.match_id, a.action_number LIMIT 6000
            """, new { matchIds = matches.Select(match => match.MatchId).ToArray() })).ToLookup(action => action.MatchId);
        var mapPool = (await conn.QueryAsync<string>("""
            SELECT gm.map_name FROM tournament_map_pools p JOIN game_maps gm ON gm.id = p.map_id
            WHERE p.tournament_id = @tournamentId ORDER BY gm.map_name LIMIT 100
            """, new { tournamentId = tournament?.Id })).ToArray();
        var rosters = await ReadRegisteredRosters(conn, tournament?.Id, matches.SelectMany(match => new[] { match.Team1Id, match.Team2Id }));
        var slots = matches.Select(match => new ShowMatchSlot(match.MatchId.ToString(), match.MatchCode ?? "MAIN", match.DisplayOrder, match.Status,
            [new ShowTeamSlot(match.Team1Name ?? "TBD", match.Team1Tag, match.Team1Logo, match.Team1Score, match.Team1Id, rosters.GetValueOrDefault(match.Team1Id ?? Guid.Empty) ?? []), new ShowTeamSlot(match.Team2Name ?? "TBD", match.Team2Tag, match.Team2Logo, match.Team2Score, match.Team2Id, rosters.GetValueOrDefault(match.Team2Id ?? Guid.Empty) ?? [])], match.BestOf,
            BroadcastVetoProjection.Project(vetoHistory[match.MatchId].Select(action => new BroadcastVetoAction(action.ActionNumber, action.Action, action.Map, action.TeamSide, action.Side))), match.ScheduledAt)).ToArray();

        var talent = await conn.QueryAsync<TalentRow>(
            """
            SELECT name AS Name, handle AS Handle, role AS Role, display_order AS DisplayOrder
            FROM broadcast_talent
            WHERE show_id = @showId
            ORDER BY display_order
            """,
            new { showId });

        var sponsors = await conn.QueryAsync<SponsorRow>(
            """
            SELECT id AS Id, name AS Name, logo_url AS LogoUrl, display_slot AS DisplaySlot,
                   display_order AS DisplayOrder
            FROM broadcast_show_sponsors
            WHERE show_id = @showId AND is_active = true
            ORDER BY display_order
            """,
            new { showId });

        return new ShowConfigPayload(
            ShowId: showId.ToString(),
            Title: show.Title,
            Status: show.Status,
            Matches: slots.Where(item => item.Status != "completed" || item.MatchId == retainedMatchId?.ToString()).ToArray(),
            Casters: talent.Select(t => new ShowTalentSlot(t.Name, t.Handle, t.Role, t.DisplayOrder)).ToArray(),
            Sponsors: sponsors.Select(s => new ShowSponsorSlot(s.Name, s.LogoUrl, s.DisplaySlot, s.DisplayOrder, s.Id)).ToArray(),
            Rundown: rundown,
            Teams: slots.FirstOrDefault()?.Teams ?? [],
            EventLabel: tournament?.Name ?? show.Title,
            SeriesTotal: slots.FirstOrDefault()?.BestOf ?? 1,
            TournamentId: tournament?.Id.ToString(),
            TournamentSlug: tournament?.Slug,
            Game: tournament?.Game,
            Veto: slots.FirstOrDefault()?.Veto ?? [],
            MapPool: mapPool,
            Appearance: ReadAppearance(await conn.ExecuteScalarAsync<string>("SELECT appearance::text FROM broadcast_shows WHERE id = @showId", new { showId })),
            MatchHistory: slots.Where(item => item.Status == "completed").ToArray()
        );
    }

    // ── POST /api/broadcast/shows/{showId}/matches ────────────────────────────

    private static async Task<IResult> AddShowMatch(
        Guid showId,
        [FromBody] AddShowMatchRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (req.MatchId == Guid.Empty)
            return Results.BadRequest(new { error = "matchId is required" });

        var matchCode = req.MatchCode?.Trim().ToUpperInvariant() ?? "MAIN";
        if (string.IsNullOrWhiteSpace(matchCode) || matchCode.Length > 20 || matchCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            return Results.BadRequest(new { error = "matchCode must contain 1–20 alphanumeric characters" });

        using var conn = db.CreateConnection();
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT org_id FROM broadcast_shows WHERE id = @showId", new { showId });

        if (orgId is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, orgId.Value, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var status = await conn.QuerySingleOrDefaultAsync<string>("SELECT status FROM broadcast_shows WHERE id = @showId FOR UPDATE", new { showId }, tx);
        if (!Esportra.Core.Broadcast.BroadcastShowRules.CanEditPlan(status ?? ""))
            return Results.Conflict(new { error = "Match planning is locked for live or closed shows." });
        if (req.DisplayOrder < 0) return Results.BadRequest(new { error = "displayOrder must be nonnegative." });
        var validMatch = await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM brkt_matches m JOIN brkt_versions v ON v.id = m.version_id
              JOIN tournaments t ON t.id = v.tournament_id JOIN broadcast_shows s ON s.id = @showId
              WHERE m.id = @matchId AND t.organization_id = s.org_id AND v.status IN ('published', 'active')
                AND t.deleted_at IS NULL AND m.winner_id IS NULL
                AND m.team1_id IS NOT NULL AND m.team2_id IS NOT NULL
                AND m.status IN ('pending', 'scheduled', 'in_progress')
                AND (s.tournament_id IS NULL OR s.tournament_id = t.id))
            """, new { showId, matchId = req.MatchId }, tx);
        if (!validMatch) return Results.BadRequest(new { error = "Match must belong to the show's organization and linked tournament." });

        var slotId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            """
            INSERT INTO broadcast_show_matches (show_id, match_id, display_order, match_code)
            VALUES (@showId, @matchId, @displayOrder, @matchCode)
            ON CONFLICT DO NOTHING
            RETURNING id
            """,
            new { showId, matchId = req.MatchId, displayOrder = req.DisplayOrder, matchCode }, tx);

        tx.Commit();

        return slotId.HasValue
            ? Results.Ok(new { slotId = slotId.Value })
            : Results.Json(new { error = "match_or_code_already_in_show" }, statusCode: 409);
    }

    // ── DELETE /api/broadcast/shows/{showId}/matches/{matchId} ────────────────

    private static async Task<IResult> RemoveShowMatch(
        Guid showId,
        Guid matchId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var orgId = await conn.QuerySingleOrDefaultAsync<Guid?>(
            "SELECT org_id FROM broadcast_shows WHERE id = @showId", new { showId });

        if (orgId is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, orgId.Value, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        var status = await conn.QuerySingleOrDefaultAsync<string>("SELECT status FROM broadcast_shows WHERE id = @showId FOR UPDATE", new { showId }, tx);
        if (!Esportra.Core.Broadcast.BroadcastShowRules.CanEditPlan(status ?? ""))
            return Results.Conflict(new { error = "Match planning is locked for live or closed shows." });
        var affected = await conn.ExecuteAsync(
            "DELETE FROM broadcast_show_matches WHERE show_id = @showId AND match_id = @matchId",
            new { showId, matchId }, tx);
        tx.Commit();

        return affected > 0
            ? Results.Ok(new { success = true })
            : Results.NotFound(new { error = "match_slot_not_found" });
    }

    // ── PATCH /api/broadcast/shows/{showId}/status ────────────────────────────

    private static async Task<IResult> UpdateShowStatus(
        Guid showId,
        [FromBody] UpdateShowStatusRequest req,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Status) || !ValidShowStatuses.Contains(req.Status))
            return Results.BadRequest(new { error = "status must be one of: draft, scheduled, live, ended, cancelled" });

        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<ShowRow>(
            "SELECT id AS Id, org_id AS OrgId, title AS Title, status AS Status FROM broadcast_shows WHERE id = @showId",
            new { showId });

        if (show is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, show.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        if (!AllowedTransitions.TryGetValue(show.Status, out var allowed) || !allowed.Contains(req.Status))
            return Results.Json(
                new { error = $"Cannot transition show from '{show.Status}' to '{req.Status}'" },
                statusCode: 409);

        int affected;
        if (req.Status == "live")
        {
            affected = await conn.ExecuteAsync(
                "UPDATE broadcast_shows SET status = @status, updated_at = NOW(), started_at = NOW() WHERE id = @showId AND status = @previousStatus",
                new { status = req.Status, showId, previousStatus = show.Status });
        }
        else if (req.Status == "ended")
        {
            affected = await conn.ExecuteAsync(
                "UPDATE broadcast_shows SET status = @status, updated_at = NOW(), ended_at = NOW() WHERE id = @showId AND status = @previousStatus",
                new { status = req.Status, showId, previousStatus = show.Status });
        }
        else
        {
            affected = await conn.ExecuteAsync(
                "UPDATE broadcast_shows SET status = @status, updated_at = NOW() WHERE id = @showId AND status = @previousStatus",
                new { status = req.Status, showId, previousStatus = show.Status });
        }

        return affected > 0 ? Results.Ok(new { success = true, status = req.Status })
            : Results.Conflict(new { error = "Show state changed. Refresh before trying again." });
    }

    // ── GET /api/broadcast/shows/{showId}/vod-chapters ───────────────────────

    private static async Task<IResult> GetVodChapters(
        Guid showId,
        HttpContext ctx,
        IDbConnectionFactory db,
        CancellationToken ct)
    {
        var userCtx = ctx.Items["UserContext"] as UserContext;
        if (userCtx is null) return Results.Unauthorized();

        using var conn = db.CreateConnection();
        var show = await conn.QuerySingleOrDefaultAsync<ShowChapterRow>(
            """
            SELECT id AS Id, org_id AS OrgId, status AS Status, started_at AS StartedAt
            FROM broadcast_shows
            WHERE id = @showId
            """,
            new { showId });

        if (show is null) return Results.NotFound(new { error = "show_not_found" });
        if (!await IsOrgOwnerAsync(conn, show.OrgId, userCtx.UserIdGuid))
            return Results.Json(new { error = "forbidden" }, statusCode: 403);

        if (show.Status != "ended")
            return Results.Json(new { error = "Show has not ended" }, statusCode: 409);

        var rawEvents = await conn.QueryAsync<SegmentEvent>(
            """
            SELECT
                event_type                           AS EventType,
                payload->>'segmentId'                AS SegmentId,
                payload->>'segmentName'              AS SegmentName,
                payload->>'segmentType'              AS SegmentType,
                (payload->>'wallClockMs')::bigint    AS WallClockMs
            FROM broadcast_events
            WHERE show_id = @showId
              AND event_type IN ('SegmentTake', 'SegmentDone', 'RoundStarted', 'RoundEnded')
              AND (payload->>'wallClockMs') IS NOT NULL
            ORDER BY (payload->>'wallClockMs')::bigint ASC
            """,
            new { showId });

        var showStartedAtMs = show.StartedAt.HasValue
            ? ((DateTimeOffset)show.StartedAt.Value).ToUnixTimeMilliseconds()
            : 0L;

        var chapters = VodChapterCalculator.Compute(rawEvents, showStartedAtMs);

        return Results.Ok(new
        {
            showId = showId.ToString(),
            chapters = chapters.Select(c => new
            {
                segmentId = c.SegmentId,
                segmentName = c.SegmentName,
                segmentType = c.SegmentType,
                offsetSeconds = c.OffsetSeconds,
                durationSeconds = c.DurationSeconds,
                incomplete = c.Incomplete,
            }).ToArray(),
        });
    }

    // ── Private helpers (delegates to BroadcastHelpers) ──────────────────────

    private static Task<bool> IsOrgOwnerAsync(
        System.Data.IDbConnection conn, Guid orgId, Guid userId) =>
        BroadcastHelpers.IsOrgOwnerAsync(conn, orgId, userId);

    // ── Private row types ─────────────────────────────────────────────────────

    private sealed record ShowRow(Guid Id, Guid OrgId, string Title, string Status);
    private sealed record ConfigTournamentRow(Guid Id, string Name, string Slug, string Game);
    private sealed record ConfigVetoRow(Guid MatchId, int ActionNumber, string Action, string? Map, string? TeamSide, string? Side);
    private sealed record ConfigMatchRow(Guid MatchId, string? MatchCode, int DisplayOrder, string Status, string? Team1Name, string? Team2Name, string Team1Tag, string Team2Tag, string? Team1Logo, string? Team2Logo, int Team1Score, int Team2Score, int BestOf, DateTime? ScheduledAt, Guid? Team1Id, Guid? Team2Id);
    private sealed record ShowChapterRow(Guid Id, Guid OrgId, string Status, DateTime? StartedAt);
    private sealed record ShowListRow(Guid Id, Guid OrgId, string Title, string Status,
        DateTime? ScheduledAt, DateTime? StartedAt, DateTime? EndedAt,
        DateTime CreatedAt, Guid? TournamentId, string? TournamentName);
    private sealed record ShowDetailRow(
        Guid Id, Guid OrgId, string Title, string Status,
        DateTime? ScheduledAt, DateTime? StartedAt, DateTime? EndedAt,
        DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastSyncedAt,
        Guid? TournamentId, string? TournamentName, string? TournamentSlug,
        long NodeCount, long MatchSlotCount, long MatchSlotsWithCode,
        long TalentCount, long SponsorCount, long CrewCount);
    private sealed record ShowDetailMatchSlotRow(
        Guid Id, Guid MatchId, string? MatchCode, int DisplayOrder, string Status, string TeamA, string TeamB, int BestOf, string MatchStatus, DateTime? ScheduledAt);
    private sealed record ShowMatchRow(Guid MatchId, string? MatchCode, int DisplayOrder, string Status);
    private sealed record TalentRow(string Name, string? Handle, string Role, int DisplayOrder);
    private sealed record SponsorRow(Guid Id, string Name, string? LogoUrl, string DisplaySlot, int DisplayOrder);
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public sealed record CreateShowRequest(
    [property: JsonPropertyName("orgId")] Guid OrgId,
    string? Title = null,
    [property: JsonPropertyName("tournamentId")] Guid? TournamentId = null,
    [property: JsonPropertyName("scheduledAt")] DateTimeOffset? ScheduledAt = null);

public sealed record AddShowMatchRequest(
    [property: JsonPropertyName("matchId")] Guid MatchId,
    [property: JsonPropertyName("matchCode")] string? MatchCode = null,
    [property: JsonPropertyName("displayOrder")] int DisplayOrder = 0);

public sealed record UpdateShowStatusRequest(string Status);

// ── ShowConfig SignalR payload records ────────────────────────────────────────

public sealed record ShowConfigPayload(
    string ShowId,
    string Title,
    string Status,
    ShowMatchSlot[] Matches,
    ShowTalentSlot[] Casters,
    ShowSponsorSlot[] Sponsors,
    ShowRundownSlot[]? Rundown = null,
    ShowTeamSlot[]? Teams = null,
    string? EventLabel = null,
    int SeriesTotal = 1,
    string? TournamentId = null,
    string? TournamentSlug = null,
    string? Game = null,
    BroadcastVetoStep[]? Veto = null,
    string[]? MapPool = null,
    string? PreferredMatchCode = null,
    long? RundownVersion = null,
    BroadcastAppearance? Appearance = null,
    ShowMatchSlot[]? MatchHistory = null);

public sealed record PushShowConfigRequest(Guid? MatchId = null);

public sealed record ShowRundownSlot(string Id, string Title, string Type, int? DurationSeconds, string? Notes, string? Overlay = null, string? MatchId = null, BroadcastSegmentPlan? Plan = null, string? PlanWarning = null);
public sealed record ShowTeamSlot(string Name, string Tag, string? LogoUrl, int SeriesScore, Guid? TeamId = null, BroadcastRosterName[]? Roster = null);

public sealed record ShowMatchSlot(
    string MatchId,
    string MatchCode,
    int DisplayOrder,
    string Status,
    ShowTeamSlot[]? Teams = null,
    int BestOf = 1,
    BroadcastVetoStep[]? Veto = null,
    DateTime? ScheduledAt = null);

public sealed record ShowTalentSlot(
    string Name,
    string? Handle,
    string Role,
    int DisplayOrder);

public sealed record ShowSponsorSlot(
    string Name,
    string? LogoUrl,
    string DisplaySlot,
    int DisplayOrder,
    Guid? SponsorId = null);
