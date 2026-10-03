using System.Data;
using Dapper;

namespace Esportra.Api.ScoreboardOcr;

/// <summary>Builds the name candidates (username, Riot ID, linked Riot account) for both match competitors.</summary>
public static class ScoreboardRosterLoader
{
    private sealed record RosterRow(Guid CompetitorId, Guid UserId, string? Username, string? RiotTag, string? RiotGameName);

    public static async Task<OcrRosters> LoadAsync(IDbConnection conn, Guid team1Id, Guid team2Id)
    {
        var rows = await conn.QueryAsync<RosterRow>(
            """
            SELECT tm.team_id AS CompetitorId, tm.user_id AS UserId,
                   p.username AS Username, p.riot_tag AS RiotTag, pra.game_name AS RiotGameName
            FROM team_members tm
            LEFT JOIN profiles p ON p.id = tm.user_id
            LEFT JOIN player_riot_accounts pra ON pra.user_id = tm.user_id
            WHERE tm.team_id = ANY(@ids) AND tm.is_active = TRUE AND tm.role <> 'coach'
            UNION ALL
            SELECT tp.id, tp.user_id, p.username, p.riot_tag, pra.game_name
            FROM tournament_participants tp
            LEFT JOIN profiles p ON p.id = tp.user_id
            LEFT JOIN player_riot_accounts pra ON pra.user_id = tp.user_id
            WHERE tp.id = ANY(@ids) AND tp.participant_type = 'solo' AND tp.user_id IS NOT NULL
            LIMIT 60
            """,
            new { ids = new[] { team1Id, team2Id } });

        var list = rows.ToList();
        return new OcrRosters(ToPlayers(list, team1Id), ToPlayers(list, team2Id));
    }

    private static IReadOnlyList<OcrRosterPlayer> ToPlayers(IEnumerable<RosterRow> rows, Guid competitorId) =>
        rows.Where(r => r.CompetitorId == competitorId)
            .GroupBy(r => r.UserId)
            .Select(g => new OcrRosterPlayer(g.Key.ToString(), CandidateNames(g)))
            .Where(p => p.Names.Count > 0)
            .Take(15)
            .ToList();

    private static IReadOnlyList<string> CandidateNames(IEnumerable<RosterRow> rows) =>
        rows.SelectMany(r => new[] { r.RiotGameName, StripTag(r.RiotTag), r.Username })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

    private static string? StripTag(string? riotTag) =>
        string.IsNullOrWhiteSpace(riotTag) ? null : riotTag.Split('#', 2)[0];
}
