using System.Data;
using Dapper;
using Esportra.Core.Bracket;

namespace Esportra.Core.Tournaments.Resolvers;

internal sealed record MatchRoundRow(
    int RoundIndex,
    Guid? WinnerId,
    Guid? Team1Id,
    Guid? Team2Id);

public sealed class SwissStandingsResolver(StandingsService standings) : IStandingsResolver
{
    public string Format => "swiss";

    public async Task<List<StandingsRow>> ResolveAsync(StandingsContext ctx, CancellationToken ct)
    {
        var standingsList = await standings.CalculateStandingsAsync(ctx.StageId, ct: ct);
        if (standingsList.Count == 0) return [];

        var roundResults = await BuildRoundResultsAsync(ctx.Conn, ctx.StageId);
        return AssignRanksAndTies(standingsList, roundResults);
    }

    internal static List<StandingsRow> AssignRanksAndTies(
        List<TeamStanding> standingsList,
        Dictionary<Guid, IReadOnlyList<string>> roundResults)
    {
        var result = new List<StandingsRow>(standingsList.Count);
        int rank = 1;

        for (int i = 0; i < standingsList.Count;)
        {
            int j = FindBandEnd(standingsList, i);
            bool isTied = j - i > 1;
            for (int k = i; k < j; k++)
            {
                roundResults.TryGetValue(standingsList[k].TeamId, out var rr);
                result.Add(ToRow(standingsList[k], rank, isTied, rr));
            }
            rank += j - i;
            i = j;
        }

        return result;
    }

    internal static Dictionary<Guid, IReadOnlyList<string>> BuildAllTeamRoundResults(
        List<MatchRoundRow> matches)
    {
        var roundIndices = matches
            .Select(m => m.RoundIndex)
            .Distinct()
            .OrderBy(r => r)
            .ToList();

        var teamIds = matches
            .SelectMany(m => new Guid?[] { m.Team1Id, m.Team2Id })
            .OfType<Guid>()
            .Distinct();

        return teamIds.ToDictionary(
            teamId => teamId,
            teamId => (IReadOnlyList<string>)roundIndices
                .Select(r => GetRoundResult(teamId, r, matches))
                .ToArray());
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> BuildRoundResultsAsync(
        IDbConnection conn, Guid stageId)
    {
        var rows = (await conn.QueryAsync<MatchRoundRow>(
            """
            SELECT m.round_index, m.winner_id, m.team1_id, m.team2_id
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            WHERE v.stage_id = @stageId
              AND m.status = 'completed'
            ORDER BY m.round_index
            """,
            new { stageId })).AsList();
        return rows.Count == 0 ? [] : BuildAllTeamRoundResults(rows);
    }

    private static string GetRoundResult(Guid teamId, int roundIndex, List<MatchRoundRow> matches)
    {
        var match = matches.FirstOrDefault(m =>
            m.RoundIndex == roundIndex &&
            (m.Team1Id == teamId || m.Team2Id == teamId));
        if (match is null) return "bye";
        return match.WinnerId == teamId ? "win" : "loss";
    }

    private static int FindBandEnd(List<TeamStanding> list, int start)
    {
        int j = start + 1;
        while (j < list.Count
            && list[j].Points == list[start].Points
            && list[j].Buchholz == list[start].Buchholz
            && list[j].ScoreDiff == list[start].ScoreDiff
            && list[j].Wins == list[start].Wins)
        {
            j++;
        }
        return j;
    }

    private static StandingsRow ToRow(
        TeamStanding s, int rank, bool isTied, IReadOnlyList<string>? roundResults) =>
        new()
        {
            Rank = rank,
            IsTied = isTied,
            TeamId = s.TeamId,
            TeamName = s.TeamName,
            Played = s.Played,
            Wins = s.Wins,
            Losses = s.Losses,
            Ties = s.Ties,
            Points = s.Points,
            Buchholz = s.Buchholz,
            ScoreDiff = s.ScoreDiff,
            RoundResults = roundResults,
        };
}
