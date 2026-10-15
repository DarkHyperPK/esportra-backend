using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Api.Middleware;
using Esportra.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Esportra.Api.Endpoints;

public static class BroadcastResultProposalEndpoints
{
    public static void MapBroadcastResultProposalEndpoints(this WebApplication app)
    {
        app.MapPost("/api/broadcast/nodes/shows/{showId:guid}/result-proposals", Propose)
            .AllowAnonymous().WithMetadata(new RateLimitPolicyMetadata("strict"));
        app.MapGet("/api/broadcast/shows/{showId:guid}/result-proposals", List)
            .RequireAuthorization("Authenticated");
        app.MapPost("/api/broadcast/shows/{showId:guid}/result-proposals/{proposalId:guid}/review", Review)
            .RequireAuthorization("Authenticated").WithMetadata(new RateLimitPolicyMetadata("strict"));
    }

    private static async Task<IResult> Propose(Guid showId, [FromBody] ResultProposalRequest req,
        HttpContext ctx, IDbConnectionFactory db)
    {
        var node = await BroadcastHelpers.ValidateNodeBearerAsync(ctx, db);
        if (node is null) return Results.Unauthorized();
        if (req.NodeId != node.NodeId) return Results.Conflict(new { error = "This result belongs to the original desktop pairing." });
        if (req.ClientKey == Guid.Empty || req.MatchId == Guid.Empty || req.Team1Id == Guid.Empty || req.Team2Id == Guid.Empty || req.MapNumber is < 1 or > 5 ||
            req.TeamAScore < 0 || req.TeamBScore < 0 || req.TeamAScore == req.TeamBScore ||
            string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Length > 500)
            return Results.BadRequest(new { error = "Choose a match, a decisive map result and a reason (up to 500 characters)." });
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        var scope = await conn.QuerySingleOrDefaultAsync<Guid?>("""
            SELECT bs.id FROM broadcast_shows bs JOIN broadcast_nodes bn ON bn.org_id = bs.org_id
            WHERE bs.id = @showId AND bn.id = @nodeId AND bn.is_active = true
            FOR SHARE OF bs, bn
            """, new { showId, nodeId = node.NodeId }, tx);
        if (!scope.HasValue) return Results.NotFound();
        var prior = await conn.QuerySingleOrDefaultAsync<Proposal>("""
            SELECT id AS Id, match_id AS MatchId, node_id AS NodeId, map_number AS MapNumber,
                team1_score AS TeamAScore, team2_score AS TeamBScore, reason AS Reason, status AS Status
            FROM broadcast_result_proposals WHERE show_id = @showId AND client_key = @clientKey
            """, new { showId, req.ClientKey }, tx);
        if (prior is not null)
            return prior.NodeId == node.NodeId && prior.MatchId == req.MatchId && prior.MapNumber == req.MapNumber &&
                prior.TeamAScore == req.TeamAScore && prior.TeamBScore == req.TeamBScore && prior.Reason == req.Reason.Trim()
                ? Results.Ok(new { proposalId = prior.Id, status = prior.Status, idempotent = true })
                : Results.Conflict(new { error = "This delivery key already belongs to another result." });
        var match = await conn.QuerySingleOrDefaultAsync<DesktopBroadcastEndpoints.BroadcastSeriesState>("""
            SELECT bm.team1_id AS Team1Id, bm.team2_id AS Team2Id, bm.status AS Status, COALESCE(bm.best_of, 1) AS BestOf
            FROM brkt_matches bm JOIN broadcast_show_matches bsm ON bsm.match_id = bm.id
            JOIN broadcast_shows bs ON bs.id = bsm.show_id
            JOIN brkt_versions bv ON bv.id = bm.version_id JOIN tournaments t ON t.id = bv.tournament_id
            WHERE bsm.show_id = @showId AND bm.id = @matchId AND bsm.match_code = @matchCode
                AND t.organization_id = bs.org_id AND t.deleted_at IS NULL
                AND (bs.tournament_id IS NULL OR bs.tournament_id = t.id) AND bv.status IN ('published', 'active')
            FOR UPDATE OF bm
            """, new { showId, req.MatchId, req.MatchCode }, tx);
        if (match?.Team1Id is null || match.Team2Id is null || req.MapNumber > match.BestOf ||
            match.Team1Id != req.Team1Id || match.Team2Id != req.Team2Id || match.BestOf != req.BestOf)
            return Results.Conflict(new { error = "The match code, teams or series format changed. Reopen the selected match." });
        // The match lock also serializes concurrent retries before the unique client key insert.
        var id = await conn.QuerySingleOrDefaultAsync<Guid?>("""
            INSERT INTO broadcast_result_proposals
                (show_id, match_id, node_id, client_key, team1_id, team2_id, best_of, map_number, team1_score, team2_score, reason)
            VALUES (@showId, @matchId, @nodeId, @clientKey, @team1Id, @team2Id, @bestOf, @mapNumber, @teamAScore, @teamBScore, @reason)
            ON CONFLICT (show_id, client_key) DO NOTHING RETURNING id
            """, new
        {
            showId,
            req.MatchId,
            nodeId = node.NodeId,
            req.ClientKey,
            match.Team1Id,
            match.Team2Id,
            match.BestOf,
            req.MapNumber,
            req.TeamAScore,
            req.TeamBScore,
            reason = req.Reason.Trim()
        }, tx);
        if (!id.HasValue) return Results.Conflict(new { error = "Delivery is already being recorded. Retry the same result." });
        tx.Commit();
        return Results.Ok(new { proposalId = id.Value, status = "pending" });
    }

    private static async Task<IResult> List(Guid showId, HttpContext ctx, IDbConnectionFactory db)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        using var conn = db.CreateConnection();
        var visible = await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM broadcast_shows bs JOIN organizations o ON o.id = bs.org_id
                WHERE bs.id = @showId AND (o.owner_id = @userId OR EXISTS(SELECT 1 FROM broadcast_crew bc
                    WHERE bc.show_id = bs.id AND bc.user_id = @userId AND bc.is_active = true)))
            """, new { showId, userId = user.UserIdGuid });
        if (!visible) return Results.NotFound();
        var canReview = await CanReview(conn, showId, user.UserIdGuid);
        var proposals = await conn.QueryAsync<Proposal>("""
            SELECT p.id AS Id, p.match_id AS MatchId, p.node_id AS NodeId, p.map_number AS MapNumber,
                p.team1_score AS TeamAScore, p.team2_score AS TeamBScore, p.reason AS Reason, p.status AS Status,
                p.created_at AS CreatedAt, p.reviewed_at AS ReviewedAt,
                a.name AS TeamAName, b.name AS TeamBName, bm.status AS MatchStatus,
                mg.team1_score AS CurrentTeamAScore, mg.team2_score AS CurrentTeamBScore
            FROM broadcast_result_proposals p JOIN brkt_matches bm ON bm.id = p.match_id
            LEFT JOIN teams a ON a.id = p.team1_id LEFT JOIN teams b ON b.id = p.team2_id
            LEFT JOIN brkt_match_games mg ON mg.match_id = p.match_id AND mg.game_number = p.map_number
            WHERE p.show_id = @showId ORDER BY (p.status = 'pending') DESC,
                CASE WHEN p.status = 'pending' THEN p.created_at END ASC, p.created_at DESC LIMIT 100
            """, new { showId });
        return Results.Ok(new { canReview, proposals });
    }

    private static async Task<IResult> Review(Guid showId, Guid proposalId, [FromBody] ResultReviewRequest req,
        HttpContext ctx, IDbConnectionFactory db, IHubContext<MatchHub> matchHub, CancellationToken ct)
    {
        if (ctx.Items["UserContext"] is not UserContext user) return Results.Unauthorized();
        if (req.Decision is not ("apply" or "reject")) return Results.BadRequest(new { error = "Choose apply or reject." });
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        // Lock the permission row so revocation and review cannot race past one another.
        if (!await CanReview(conn, showId, user.UserIdGuid, tx)) return Results.StatusCode(403);
        var proposal = await conn.QuerySingleOrDefaultAsync<Proposal>("""
            SELECT id AS Id, match_id AS MatchId, map_number AS MapNumber, team1_score AS TeamAScore,
                team2_score AS TeamBScore, team1_id AS Team1Id, team2_id AS Team2Id, best_of AS BestOf, status AS Status
            FROM broadcast_result_proposals WHERE id = @proposalId AND show_id = @showId FOR UPDATE
            """, new { proposalId, showId }, tx);
        if (proposal is null) return Results.NotFound();
        var decisionStatus = req.Decision == "apply" ? "applied" : "rejected";
        if (proposal.Status != "pending")
            return proposal.Status == decisionStatus ? Results.Ok(new { ok = true, status = proposal.Status, idempotent = true })
                : Results.Conflict(new { error = "This proposal was already reviewed. Reload the result list." });
        string? matchStatus = null;
        if (req.Decision == "apply")
        {
            var showStatus = await conn.QuerySingleOrDefaultAsync<string>("SELECT status FROM broadcast_shows WHERE id = @showId FOR SHARE", new { showId }, tx);
            if (showStatus is null or "ended" or "cancelled") return Results.Conflict(new { error = "This show is closed. Use tournament result management to review it." });
            var match = await conn.QuerySingleOrDefaultAsync<DesktopBroadcastEndpoints.BroadcastSeriesState>("""
                SELECT bm.team1_id AS Team1Id, bm.team2_id AS Team2Id, bm.status AS Status, COALESCE(bm.best_of, 1) AS BestOf
                FROM brkt_matches bm JOIN broadcast_show_matches bsm ON bsm.match_id = bm.id
                JOIN broadcast_shows bs ON bs.id = bsm.show_id
                JOIN brkt_versions bv ON bv.id = bm.version_id JOIN tournaments t ON t.id = bv.tournament_id
                WHERE bm.id = @matchId AND bs.id = @showId AND bs.status NOT IN ('ended', 'cancelled')
                    AND t.organization_id = bs.org_id AND t.deleted_at IS NULL
                    AND (bs.tournament_id IS NULL OR bs.tournament_id = t.id) AND bv.status IN ('published', 'active')
                FOR UPDATE OF bm
                """, new { matchId = proposal.MatchId, showId }, tx);
            if (match is null || match.Team1Id != proposal.Team1Id || match.Team2Id != proposal.Team2Id || match.BestOf != proposal.BestOf ||
                match.Status is not ("pending" or "scheduled" or "in_progress"))
                return Results.Conflict(new { error = "This match is finalized or its setup changed. Use tournament result management to review it." });
            var current = await conn.QuerySingleOrDefaultAsync<MapScore>("""
                SELECT team1_score AS TeamAScore, team2_score AS TeamBScore FROM brkt_match_games
                WHERE match_id = @matchId AND game_number = @mapNumber
                """, new { matchId = proposal.MatchId, proposal.MapNumber }, tx);
            if (current?.TeamAScore != req.CurrentTeamAScore || current?.TeamBScore != req.CurrentTeamBScore)
                return Results.Conflict(new { error = "The official map score changed. Reload and review its latest value." });
            await DesktopBroadcastEndpoints.UpsertGameResult(conn, proposal.MatchId,
                new GameResultRequest(proposal.MapNumber, proposal.TeamAScore, proposal.TeamBScore), tx);
            matchStatus = await DesktopBroadcastEndpoints.TryCompleteMatchAsync(conn, proposal.MatchId, proposal.BestOf, match, tx);
            var gameId = await conn.QuerySingleAsync<Guid>("SELECT id FROM brkt_match_games WHERE match_id = @matchId AND game_number = @mapNumber", new { matchId = proposal.MatchId, proposal.MapNumber }, tx);
            await conn.ExecuteAsync("""
                INSERT INTO broadcast_corrections
                    (show_id, corrected_by, correction_type, target_id, target_type, before_state, after_state, reason, source)
                VALUES (@showId, @userId, 'air_result_review', @gameId, 'brkt_match_game', @beforeState::jsonb,
                    @afterState::jsonb, 'Operator proposal approved after review', 'broadcast')
                """, new
            {
                showId,
                userId = user.UserIdGuid,
                gameId,
                beforeState = JsonSerializer.Serialize(current),
                afterState = JsonSerializer.Serialize(new { proposalId, mapNumber = proposal.MapNumber, teamAScore = proposal.TeamAScore, teamBScore = proposal.TeamBScore, verified = false })
            }, tx);
        }
        await conn.ExecuteAsync("""
            UPDATE broadcast_result_proposals SET status = @status, reviewed_by = @userId, reviewed_at = NOW() WHERE id = @proposalId;
            INSERT INTO broadcast_events (show_id, event_id, event_type, payload, source)
            VALUES (@showId, @eventId, 'MatchResultReviewed', @payload::jsonb, 'web');
            """, new
        {
            status = decisionStatus,
            userId = user.UserIdGuid,
            proposalId,
            showId,
            eventId = Guid.NewGuid().ToString(),
            payload = JsonSerializer.Serialize(new
            {
                proposalId,
                matchId = proposal.MatchId,
                proposal.MapNumber,
                decision = req.Decision,
                operatorUserId = user.UserIdGuid
            })
        }, tx);
        tx.Commit();
        if (req.Decision == "apply")
            await matchHub.Clients.Group(MatchHub.MatchGroup(proposal.MatchId.ToString())).SendAsync(MatchHubEvents.BroadcastGameResult,
                new { matchId = proposal.MatchId, mapNumber = proposal.MapNumber, teamAScore = proposal.TeamAScore, teamBScore = proposal.TeamBScore }, ct);
        return Results.Ok(new { ok = true, status = decisionStatus, matchStatus });
    }

    private static async Task<bool> CanReview(IDbConnection conn, Guid showId, Guid userId, IDbTransaction? tx = null) =>
        await conn.QuerySingleOrDefaultAsync<bool?>("""
            SELECT can_submit_result FROM broadcast_crew WHERE show_id = @showId AND user_id = @userId AND is_active = true
            LIMIT 1 FOR SHARE
            """, new { showId, userId }, tx) == true;

    private sealed class Proposal
    {
        public Guid Id { get; set; }
        public Guid MatchId { get; set; }
        public Guid? NodeId { get; set; }
        public int MapNumber { get; set; }
        public int TeamAScore { get; set; }
        public int TeamBScore { get; set; }
        public Guid Team1Id { get; set; }
        public Guid Team2Id { get; set; }
        public int BestOf { get; set; }
        public string Reason { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string? TeamAName { get; set; }
        public string? TeamBName { get; set; }
        public string? MatchStatus { get; set; }
        public int? CurrentTeamAScore { get; set; }
        public int? CurrentTeamBScore { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ReviewedAt { get; set; }
    }
    private sealed record MapScore(int TeamAScore, int TeamBScore);
}

public sealed record ResultProposalRequest(Guid ClientKey, Guid NodeId, Guid MatchId, string MatchCode, Guid Team1Id, Guid Team2Id, int BestOf, int MapNumber, int TeamAScore, int TeamBScore, string Reason);
public sealed record ResultReviewRequest(string Decision, int? CurrentTeamAScore, int? CurrentTeamBScore);
