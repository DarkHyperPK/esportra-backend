using System.Data;
using System.Text.Json;
using Dapper;
using Esportra.Api.Services;
using Esportra.Contracts.Database;

namespace Esportra.Api.Endpoints;

public static partial class AirBroadcastEndpoints
{
    private static async Task<Guid?> PackageMatch(IDbConnection conn, Guid packageId, HttpContext ctx, AirAccountService accounts)
    {
        if (!await AuthorizedDevice(conn, ctx, accounts)) return null;
        var id = await conn.QuerySingleOrDefaultAsync<Guid?>("SELECT match_id FROM air_match_packages WHERE id=@packageId", new { packageId });
        if (!id.HasValue) return null;
        if (await CanReviewAirResult(conn, id.Value, AirAccountEndpoints.User(ctx))) return id;
        if ((await Matches(conn, AirAccountEndpoints.User(ctx), id, history: true)).Length == 0) return null;
        var owner = await OwnsPackage(conn, packageId, ctx);
        return owner || await CanReviewAirResult(conn, id.Value, AirAccountEndpoints.User(ctx)) ? id : null;
    }

    private static Task<bool> CanReviewAirResult(IDbConnection conn, Guid matchId, Guid userId, IDbTransaction? tx = null) =>
        conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM brkt_matches m JOIN brkt_versions v ON v.id=m.version_id JOIN tournaments t ON t.id=v.tournament_id
            LEFT JOIN organizations o ON o.id=t.organization_id WHERE m.id=@matchId AND t.deleted_at IS NULL AND
            (t.organizer_id=@userId OR o.owner_id=@userId OR EXISTS(SELECT 1 FROM air_match_crew ac WHERE ac.match_id=m.id AND ac.recipient_id=@userId AND ac.status='accepted' AND ac.can_submit_result=true) OR EXISTS(SELECT 1 FROM broadcast_show_matches sm JOIN broadcast_shows s ON s.id=sm.show_id
            JOIN broadcast_crew c ON c.show_id=s.id WHERE sm.match_id=m.id AND s.org_id=t.organization_id AND c.user_id=@userId AND c.is_active AND c.joined_at IS NOT NULL AND c.can_submit_result=true)))
            """, new { matchId, userId }, tx);

    private static async Task<IResult> ListAirResults(Guid packageId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var conn = db.CreateConnection();
        var match = await PackageMatch(conn, packageId, ctx, accounts);
        if (!match.HasValue) return Results.NotFound();
        var canReview = await CanReviewAirResult(conn, match.Value, AirAccountEndpoints.User(ctx));
        var rows = await conn.QueryAsync<AirResultRow>("""
            SELECT e.id,e.package_id AS PackageId,e.metadata::text AS Document,COALESCE(r.metadata->>'decision','pending') AS Status,
                g.team1_score AS CurrentTeamAScore,g.team2_score AS CurrentTeamBScore
            FROM air_package_events e LEFT JOIN air_package_events r ON r.package_id=e.package_id AND r.event_type='official_result_reviewed' AND r.metadata->>'proposalId'=e.id::text
            LEFT JOIN brkt_match_games g ON g.match_id=@matchId AND g.game_number=(e.metadata->>'mapNumber')::integer
            WHERE (e.package_id=@packageId OR (@canReview AND e.package_id IN (SELECT id FROM air_match_packages WHERE match_id=@matchId))) AND e.event_type='official_result_proposed' ORDER BY e.occurred_at DESC LIMIT 100
            """, new { packageId, matchId = match.Value, canReview });
        return Results.Ok(new { canReview, proposals = rows.Select(r => new { r.Id, r.PackageId, r.Status, r.CurrentTeamAScore, r.CurrentTeamBScore, proposal = JsonSerializer.Deserialize<JsonElement>(r.Document) }) });
    }

    private static async Task<IResult> ProposeAirResult(Guid packageId, AirResultRequest request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        if (request.ClientKey == Guid.Empty || request.MapNumber is < 1 or > 9 || request.TeamAScore < 0 || request.TeamBScore < 0 || request.TeamAScore == request.TeamBScore || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
            return Results.BadRequest(new { error = "Choose a decisive map score and a reason." });
        using var conn = db.CreateConnection();
        var matchId = await PackageMatch(conn, packageId, ctx, accounts);
        if (!matchId.HasValue) return Results.NotFound();
        var match = (await Matches(conn, AirAccountEndpoints.User(ctx), matchId)).SingleOrDefault();
        if (match is null || request.MapNumber > match.BestOf) return Results.Conflict(new { error = "The match is closed or its format changed." });
        var document = JsonSerializer.Serialize(new { mapNumber = request.MapNumber, teamAScore = request.TeamAScore, teamBScore = request.TeamBScore, reason = request.Reason.Trim(), team1Id = match.Team1Id, team2Id = match.Team2Id, bestOf = match.BestOf }, JsonOptions);
        var prior = await conn.QuerySingleOrDefaultAsync<string>("SELECT metadata::text FROM air_package_events WHERE id=@id AND package_id=@packageId AND event_type='official_result_proposed'", new { id = request.ClientKey, packageId });
        if (prior is not null) return JsonNodeEquivalent(prior, document) ? Results.Ok(new { proposalId = request.ClientKey, idempotent = true }) : Results.Conflict(new { error = "This proposal identity already belongs to another result." });
        var count = await conn.ExecuteAsync("INSERT INTO air_package_events(id,package_id,version,occurred_at,event_type,metadata) SELECT @id,id,latest_version,now(),'official_result_proposed',CAST(@document AS jsonb) FROM air_match_packages WHERE id=@packageId ON CONFLICT(id) DO NOTHING", new { id = request.ClientKey, packageId, document });
        return count == 1 ? Results.Ok(new { proposalId = request.ClientKey, status = "pending" }) : Results.Conflict();
    }

    private static bool JsonNodeEquivalent(string a, string b) => System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(a), System.Text.Json.Nodes.JsonNode.Parse(b));

    private static async Task<IResult> ReviewAirResult(Guid packageId, Guid proposalId, ResultReviewRequest request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        if (request.Decision is not ("apply" or "reject")) return Results.BadRequest();
        using var conn = db.CreateConnection();
        var matchId = await PackageMatch(conn, packageId, ctx, accounts);
        if (!matchId.HasValue) return Results.NotFound();
        if (conn.State != ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        if (!await CanReviewAirResult(conn, matchId.Value, AirAccountEndpoints.User(ctx), tx)) return Results.Forbid();
        var row = await conn.QuerySingleOrDefaultAsync<AirResultRow>("SELECT id,metadata::text AS Document FROM air_package_events WHERE id=@proposalId AND package_id=@packageId AND event_type='official_result_proposed' FOR UPDATE", new { proposalId, packageId }, tx);
        if (row is null) return Results.NotFound();
        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM air_package_events WHERE package_id=@packageId AND event_type='official_result_reviewed' AND metadata->>'proposalId'=@proposal)", new { packageId, proposal = proposalId.ToString() }, tx)) return Results.Conflict(new { error = "This proposal was already reviewed." });
        using var document = JsonDocument.Parse(row.Document);
        var proposal = document.RootElement;
        if (request.Decision == "apply")
        {
            var match = await conn.QuerySingleOrDefaultAsync<DesktopBroadcastEndpoints.BroadcastSeriesState>("SELECT m.team1_id AS Team1Id,m.team2_id AS Team2Id,m.status,COALESCE(m.best_of,1) AS BestOf FROM brkt_matches m JOIN brkt_versions v ON v.id=m.version_id WHERE m.id=@matchId AND v.status IN ('active','published') FOR UPDATE OF m", new { matchId }, tx);
            if (match?.Team1Id != proposal.GetProperty("team1Id").GetGuid() || match.Team2Id != proposal.GetProperty("team2Id").GetGuid() || match.BestOf != proposal.GetProperty("bestOf").GetInt32() || match.Status is not ("pending" or "scheduled" or "in_progress")) return Results.Conflict(new { error = "The match changed or is finalized. Review its official record." });
            var map = proposal.GetProperty("mapNumber").GetInt32();
            var current = await conn.QuerySingleOrDefaultAsync<AirOfficialMapScore>("SELECT team1_score AS TeamAScore,team2_score AS TeamBScore FROM brkt_match_games WHERE match_id=@matchId AND game_number=@map", new { matchId, map }, tx);
            if (current?.TeamAScore != request.CurrentTeamAScore || current?.TeamBScore != request.CurrentTeamBScore) return Results.Conflict(new { error = "Official scores changed. Reload before reviewing." });
            await DesktopBroadcastEndpoints.UpsertGameResult(conn, matchId.Value, new GameResultRequest(map, proposal.GetProperty("teamAScore").GetInt32(), proposal.GetProperty("teamBScore").GetInt32()), tx);
            await DesktopBroadcastEndpoints.TryCompleteMatchAsync(conn, matchId.Value, match.BestOf, match, tx);
        }
        await conn.ExecuteAsync("INSERT INTO air_package_events(id,package_id,version,occurred_at,event_type,metadata) SELECT @id,id,latest_version,now(),'official_result_reviewed',CAST(@metadata AS jsonb) FROM air_match_packages WHERE id=@packageId", new { id = Guid.NewGuid(), packageId, metadata = JsonSerializer.Serialize(new { proposalId, decision = request.Decision, reviewedBy = AirAccountEndpoints.User(ctx), beforeScoreA = request.CurrentTeamAScore, beforeScoreB = request.CurrentTeamBScore }) }, tx);
        tx.Commit();
        return Results.Ok(new { status = request.Decision });
    }

    private sealed class AirResultRow { public Guid Id { get; set; } public Guid PackageId { get; set; } public string Document { get; set; } = "{}"; public string? Status { get; set; } public int? CurrentTeamAScore { get; set; } public int? CurrentTeamBScore { get; set; } }
    private sealed record AirOfficialMapScore(int TeamAScore, int TeamBScore);
}

public sealed record AirResultRequest(Guid ClientKey, int MapNumber, int TeamAScore, int TeamBScore, string Reason);
