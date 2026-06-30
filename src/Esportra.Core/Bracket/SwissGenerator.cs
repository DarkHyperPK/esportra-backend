using Dapper;
using Esportra.Contracts.Database;

namespace Esportra.Core.Bracket;

/// <summary>
/// Swiss system bracket generator.
/// Round 1 is generated in-memory; subsequent rounds use DB standings.
/// </summary>
public sealed class SwissGenerator : IBracketGenerator
{
    public BracketGraph Generate(
        IReadOnlyList<(Guid Id, string Name)> teams,
        Guid tournamentId,
        Guid? stageId = null,
        int bestOf = 1,
        int? bracketSize = null,   // interpreted as total rounds
        int? advancementCount = null,
        BracketConfig? config = null)
    {
        var versionId = Guid.NewGuid();
        var nodes = new List<BracketNode>();
        var edges = new List<BracketEdge>();

        int swissGroups = config?.SwissGroups ?? 1;
        int matchCounter = 1;

        // Sort by seed index (position in list)
        var sorted = teams.ToList();

        // Split into groups
        var groups = new List<List<(Guid Id, string Name)>>(swissGroups);
        for (int i = 0; i < swissGroups; i++) groups.Add([]);

        foreach (var (team, idx) in sorted.Select((t, i) => (t, i)))
            groups[idx % swissGroups].Add(team);

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var groupTeams = groups[gi];
            string? groupId = swissGroups > 1 ? $"Group {(char)('A' + gi)}" : null;

            // Slide pairing: top half vs bottom half
            int half = (groupTeams.Count + 1) / 2;
            var topHalf = groupTeams.Take(half).ToList();
            var bottomHalf = groupTeams.Skip(half).ToList();

            for (int i = 0; i < topHalf.Count; i++)
            {
                var t1 = topHalf[i];
                var t2 = i < bottomHalf.Count ? bottomHalf[i] : ((Guid Id, string Name)?)null;

                nodes.Add(new BracketNode(
                    Id: Guid.NewGuid(),
                    VersionId: versionId,
                    RoundIndex: 0,
                    MatchNumber: matchCounter++,
                    BracketType: "swiss_round",
                    RoundNumber: 1,
                    Status: "pending",
                    BestOf: bestOf,
                    Team1Id: t1.Id,
                    Team2Id: t2?.Id,
                    GroupId: groupId));
            }
        }

        var version = new BracketVersion(versionId, tournamentId, stageId, 1, "draft",
            DateTime.UtcNow.ToString("o"));

        return new BracketGraph(version, nodes, edges);
    }
}

/// <summary>Generates subsequent Swiss rounds using DB standings.</summary>
public sealed class SwissNextRoundService(
    IDbConnectionFactory db,
    StandingsService standings)
{
    public async Task<(bool Success, string? Message)> GenerateNextRoundAsync(
        Guid stageId, Guid versionId, int currentRound, CancellationToken ct = default)
    {
        var standingsList = await standings.CalculateStandingsAsync(stageId, ct: ct);
        if (standingsList.Count < 2)
            return (false, "Not enough teams with completed matches to generate next round.");

        using var conn = db.CreateConnection();

        // Use total participant count for max rounds (not just teams with completed matches)
        var totalTeams = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(DISTINCT t.id)
            FROM brkt_matches m
            JOIN brkt_versions v ON v.id = m.version_id
            LEFT JOIN teams t ON t.id = m.team1_id OR t.id = m.team2_id
            WHERE v.stage_id = @stageId AND t.id IS NOT NULL
            """,
            new { stageId });
        int teamCount = Math.Max(totalTeams, standingsList.Count);
        int maxRounds = (int)Math.Ceiling(Math.Log2(teamCount));
        if (currentRound >= maxRounds)
            return (false, $"Round limit reached ({maxRounds} rounds).");

        // Fetch match history for rematch avoidance
        var history = (await conn.QueryAsync(
            "SELECT team1_id, team2_id, group_id FROM public.brkt_matches WHERE version_id = @versionId",
            new { versionId })).AsList();

        if (history.Count == 0)
            return (false, "No match history found. Cannot generate pairings.");

        var playedMap = new HashSet<string>();
        var groupMap = new Dictionary<string, HashSet<Guid>>();
        var teamGroupMap = new Dictionary<Guid, string>();

        foreach (var m in history)
        {
            string gid = (string?)m.group_id ?? "default";
            if (!groupMap.ContainsKey(gid)) groupMap[gid] = [];

            if (m.team1_id is Guid t1) { groupMap[gid].Add(t1); teamGroupMap[t1] = gid; }
            if (m.team2_id is Guid t2) { groupMap[gid].Add(t2); teamGroupMap[t2] = gid; }

            if (m.team1_id is not null && m.team2_id is not null)
            {
                playedMap.Add($"{m.team1_id}-{m.team2_id}");
                playedMap.Add($"{m.team2_id}-{m.team1_id}");
            }
        }

        int existingCount = history.Count;
        var newMatches = new List<object>();
        int matchCounter = existingCount + 1;

        foreach (var (groupId, teamIds) in groupMap)
        {
            var groupStandings = standingsList
                .Where(s => teamIds.Contains(s.TeamId))
                .ToList();

            if (groupStandings.Count == 0) continue;

            // Group by score
            var scoreGroups = groupStandings
                .GroupBy(s => s.Points)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Rank).ToList());

            var sortedScores = scoreGroups.Keys.OrderByDescending(x => x).ToList();
            var pairings = new List<(TeamStanding T1, TeamStanding? T2)>();
            var floaters = new List<TeamStanding>();

            foreach (var score in sortedScores)
            {
                var group = new List<TeamStanding>(floaters);
                group.AddRange(scoreGroups[score]);
                floaters.Clear();

                while (group.Count >= 2)
                {
                    var team1 = group[0];
                    group.RemoveAt(0);

                    int opponentIdx = -1;
                    for (int i = 0; i < group.Count; i++)
                    {
                        if (!playedMap.Contains($"{team1.TeamId}-{group[i].TeamId}"))
                        { opponentIdx = i; break; }
                    }

                    if (opponentIdx >= 0)
                    {
                        var team2 = group[opponentIdx];
                        group.RemoveAt(opponentIdx);
                        pairings.Add((team1, team2));
                    }
                    else
                    {
                        floaters.Add(team1);
                    }
                }

                if (group.Count == 1) floaters.Add(group[0]);
            }

            // Force-pair remaining floaters
            while (floaters.Count >= 2)
            {
                pairings.Add((floaters[0], floaters[1]));
                floaters.RemoveRange(0, 2);
            }
            if (floaters.Count > 0)
            {
                pairings.Add((floaters[0], null));
                floaters.Clear();
            }

            int nextRound = currentRound + 1;
            foreach (var (t1, t2) in pairings)
            {
                newMatches.Add(new
                {
                    id = Guid.NewGuid(),
                    version_id = versionId,
                    round_index = nextRound - 1,
                    match_number = matchCounter++,
                    bracket_type = "swiss_round",
                    round_number = nextRound,
                    status = "pending",
                    team1_id = t1.TeamId,
                    team2_id = t2?.TeamId,
                    winner_id = (Guid?)null,
                    best_of = 1,
                    group_id = groupId == "default" ? null : groupId
                });
            }
        }

        if (newMatches.Count == 0)
            return (false, "No matches generated.");

        await conn.ExecuteAsync(@"
            INSERT INTO public.brkt_matches
                (id, version_id, round_index, match_number, bracket_type, round_number, status, team1_id, team2_id, best_of, group_id)
            VALUES
                (@id, @version_id, @round_index, @match_number, @bracket_type, @round_number, @status, @team1_id, @team2_id, @best_of, @group_id)",
            newMatches);

        return (true, null);
    }
}
