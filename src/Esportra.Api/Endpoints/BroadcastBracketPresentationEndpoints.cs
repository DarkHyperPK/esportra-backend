using Dapper;
using Esportra.Contracts.Database;
using Esportra.Core.Tournaments;

namespace Esportra.Api.Endpoints;

public static class BroadcastBracketPresentationEndpoints
{
    public static void MapBroadcastBracketPresentationEndpoints(this WebApplication app) => app.MapGet("/api/broadcast/nodes/shows/{showId:guid}/bracket", Get);
    private static async Task<IResult> Get(Guid showId, HttpContext ctx, IDbConnectionFactory db, IEnumerable<IStandingsResolver> resolvers, CancellationToken ct)
    {
        var node = await BroadcastHelpers.ValidateNodeBearerAsync(ctx, db); if (node is null) return Results.Unauthorized();
        using var connection = db.CreateConnection();
        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM broadcast_shows WHERE id=@showId AND org_id=@orgId AND published_configuration IS NOT NULL)", new { showId, orgId = node.OrgId })) return Results.NotFound();
        var rows = (await connection.QueryAsync<BracketRow>($"""
            WITH candidates AS (
              SELECT v.*,row_number() OVER(PARTITION BY COALESCE(v.stage_id,v.tournament_id) ORDER BY (v.status='active') DESC,v.version_number DESC) AS choice
              FROM brkt_versions v JOIN tournaments t ON t.id=v.tournament_id JOIN broadcast_shows s ON s.id=@showId
              WHERE v.status IN ('published','active') AND t.organization_id=s.org_id AND t.deleted_at IS NULL
              AND (s.tournament_id=v.tournament_id OR (s.tournament_id IS NULL AND EXISTS(
                SELECT 1 FROM broadcast_show_matches sm JOIN brkt_matches assigned ON assigned.id=sm.match_id
                JOIN brkt_versions assigned_v ON assigned_v.id=assigned.version_id WHERE sm.show_id=s.id AND assigned_v.tournament_id=v.tournament_id)))
            )
            SELECT m.id AS MatchId,m.round_index AS Round,m.match_number AS MatchNumber,m.bracket_type AS Side,m.group_id AS GroupId,
              m.team1_score AS Score1,m.team2_score AS Score2,m.winner_id AS WinnerId,m.status AS Status,m.team1_id AS Team1Id,m.team2_id AS Team2Id,
              COALESCE(t1.name,tp1.team_name,sp1.username,'To be decided') AS Team1,COALESCE(t2.name,tp2.team_name,sp2.username,'To be decided') AS Team2,
              COALESCE(t1.logo_url,sp1.avatar_url) AS Logo1,COALESCE(t2.logo_url,sp2.avatar_url) AS Logo2,
              COALESCE(v.stage_id,v.id) AS StageId,COALESCE(st.name,'Tournament bracket') AS StageName,COALESCE(st.format,'single_elimination') AS Format,
              v.tournament_id AS TournamentId,v.status AS VersionStatus,st.config::text AS Config,
              winner.target_match_id AS NextMatchId,loser.target_match_id AS LoserNextMatchId
            FROM candidates v JOIN brkt_matches m ON m.version_id=v.id
            LEFT JOIN tournament_stages st ON st.id=v.stage_id
            {BracketTeamResolutionSql.Team1Joins}
            {BracketTeamResolutionSql.Team2Joins}
            LEFT JOIN brkt_advancements winner ON winner.source_match_id=m.id AND winner.version_id=v.id AND winner.type='winner'
            LEFT JOIN brkt_advancements loser ON loser.source_match_id=m.id AND loser.version_id=v.id AND loser.type='loser'
            WHERE v.choice=1 ORDER BY COALESCE(st.stage_order,0),m.round_index,m.match_number LIMIT 2000
            """, new { showId })).ToArray();
        var stages = new List<object>();
        foreach (var stage in rows.GroupBy(row => row.StageId))
        {
            var first = stage.First(); List<StandingsRow> standings = [];
            var resolver = resolvers.FirstOrDefault(item => item.Format == first.Format);
            if (resolver is not null && first.VersionStatus == "active" && first.Format is "swiss" or "round_robin") standings = await resolver.ResolveAsync(new StandingsContext(first.TournamentId, first.StageId, first.Config, connection), ct);
            stages.Add(new { id = stage.Key, label = first.StageName, format = first.Format, standings, matches = stage.Select(row => new { id = row.MatchId, matchId = row.MatchId, round = row.Round, side = row.Side switch { "losers" => "lower", "final" => "final", "group" or "swiss_round" => "group", _ => "upper" }, group = row.GroupId, row.NextMatchId, row.LoserNextMatchId }) });
        }
        return Results.Ok(new { stages, matches = rows.Select(row => new { matchId = row.MatchId, row.Status, scores = new[] { row.Score1, row.Score2 }, row.WinnerId, teams = new[] { new { id = row.Team1Id, name = row.Team1, logoUrl = row.Logo1 }, new { id = row.Team2Id, name = row.Team2, logoUrl = row.Logo2 } } }), fetchedAt = DateTimeOffset.UtcNow });
    }
    private sealed record BracketRow(Guid MatchId, int Round, int MatchNumber, string Side, string? GroupId, int? Score1, int? Score2, Guid? WinnerId, string Status, Guid? Team1Id, Guid? Team2Id, string Team1, string Team2, string? Logo1, string? Logo2, Guid StageId, string StageName, string Format, Guid TournamentId, string VersionStatus, string? Config, Guid? NextMatchId, Guid? LoserNextMatchId);
}
