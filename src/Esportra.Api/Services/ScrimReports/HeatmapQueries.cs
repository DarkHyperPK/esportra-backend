using System.Data;
using Dapper;

namespace Esportra.Api.Services.ScrimReports;

public static class HeatmapQueries
{
    private sealed record Row(Guid MatchId, int Round, int Ordinal, string? Json);

    /// <summary>Each kill's recorded player locations, per match, for the kinds that need the killer's position. One read for all matches.</summary>
    public static async Task<Dictionary<Guid, Dictionary<(int Round, int Ordinal), string?>>> LocationsAsync(IDbConnection conn, IReadOnlyCollection<Guid> matchIds)
    {
        var rows = await conn.QueryAsync<Row>(
            """
            SELECT match_id AS MatchId, round_num AS Round, kill_ordinal AS Ordinal, player_locations::text AS Json
            FROM public.valorant_match_kills WHERE match_id = ANY(@ids) AND player_locations IS NOT NULL
            """, new { ids = matchIds.ToArray() });
        return rows.GroupBy(r => r.MatchId).ToDictionary(g => g.Key, g => g.ToDictionary(r => (r.Round, r.Ordinal), r => r.Json));
    }
}
