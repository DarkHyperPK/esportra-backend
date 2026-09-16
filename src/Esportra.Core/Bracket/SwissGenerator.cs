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
        StageRoundConfiguration? roundConfig = null,
        int? bracketSize = null,   // interpreted as total rounds
        int? advancementCount = null,
        BracketConfig? config = null)
    {
        roundConfig ??= StageRoundConfiguration.PerStage("swiss", 1);
        int bestOf = roundConfig.DefaultBestOf;
        var versionId = Guid.NewGuid();
        var nodes = new List<BracketNode>();
        var edges = new List<BracketEdge>();

        int swissGroups = config?.SwissGroups ?? 1;
        int matchCounter = 1;

        // Sort by seed index (position in list) - preserve original seed (1-based)
        var sorted = teams.Select((t, i) => (t.Id, t.Name, Seed: i + 1)).ToList();

        // Split into groups
        var groups = new List<List<(Guid Id, string Name, int Seed)>>(swissGroups);
        for (int i = 0; i < swissGroups; i++) groups.Add([]);

        foreach (var (team, idx) in sorted.Select((t, i) => (t, i)))
            groups[idx % swissGroups].Add(team);

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var groupTeams = groups[gi];
            string? groupId = swissGroups > 1 ? $"Group {(char)('A' + gi)}" : null;

            // For odd groups the lowest-ranked team (worst seed = last in sorted list) gets the BYE
            (Guid Id, string Name, int Seed)? byeTeam = null;
            var activeTeams = groupTeams;
            if (groupTeams.Count % 2 != 0)
            {
                byeTeam = groupTeams[^1];
                activeTeams = groupTeams.Take(groupTeams.Count - 1).ToList();
            }

            // Slide pairing: top half vs bottom half of even-sized active list
            int half = activeTeams.Count / 2;
            var topHalf = activeTeams.Take(half).ToList();
            var bottomHalf = activeTeams.Skip(half).ToList();

            for (int i = 0; i < topHalf.Count; i++)
            {
                nodes.Add(new BracketNode(
                    Id: Guid.NewGuid(),
                    VersionId: versionId,
                    RoundIndex: 0,
                    MatchNumber: matchCounter++,
                    BracketType: "swiss_round",
                    RoundNumber: 1,
                    Status: "pending",
                    BestOf: bestOf,
                    Team1Id: topHalf[i].Id,
                    Team2Id: bottomHalf[i].Id,
                    GroupId: groupId,
                    Team1Seed: topHalf[i].Seed,
                    Team2Seed: bottomHalf[i].Seed));
            }

            if (byeTeam is not null)
            {
                nodes.Add(new BracketNode(
                    Id: Guid.NewGuid(),
                    VersionId: versionId,
                    RoundIndex: 0,
                    MatchNumber: matchCounter++,
                    BracketType: "swiss_round",
                    RoundNumber: 1,
                    Status: "pending",
                    BestOf: bestOf,
                    Team1Id: byeTeam.Value.Id,
                    Team2Id: null,
                    GroupId: groupId,
                    Team1Seed: byeTeam.Value.Seed,
                    Team2Seed: null));
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
        Guid stageId, Guid versionId, int currentRound, StageRoundConfiguration? roundConfig = null, CancellationToken ct = default)
    {
        var standingsList = await standings.CalculateStandingsAsync(stageId, ct: ct);
        if (standingsList.Count < 2)
            return (false, "Not enough teams with completed matches to generate next round.");

        using var conn = db.CreateConnection();

        var totalTeams = await conn.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM (
                SELECT DISTINCT slot_id FROM (
                    SELECT m.team1_id AS slot_id
                    FROM brkt_matches m
                    JOIN brkt_versions v ON v.id = m.version_id
                    WHERE v.stage_id = @stageId AND m.team1_id IS NOT NULL
                    UNION
                    SELECT m.team2_id AS slot_id
                    FROM brkt_matches m
                    JOIN brkt_versions v ON v.id = m.version_id
                    WHERE v.stage_id = @stageId AND m.team2_id IS NOT NULL
                ) slots
            ) counted
            """,
            new { stageId });
        int teamCount = Math.Max(totalTeams, standingsList.Count);
        int maxRounds = (int)Math.Ceiling(Math.Log2(teamCount));
        if (currentRound >= maxRounds)
            return (false, $"Round limit reached ({maxRounds} rounds).");

        var history = await LoadMatchHistoryAsync(versionId, conn);
        if (history.ExistingCount == 0)
            return (false, "No match history found. Cannot generate pairings.");

        var newMatches = new List<object>();
        int matchCounter = history.ExistingCount + 1;
        int nextRound = currentRound + 1;

        foreach (var (groupId, teamIds) in history.GroupMap)
        {
            var groupStandings = standingsList
                .Where(s => teamIds.Contains(s.TeamId))
                .ToList();

            if (groupStandings.Count == 0) continue;

            var pairings = GenerateGroupPairings(groupStandings, history.PlayedMap);

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
                    best_of = roundConfig?.GetBestOf(nextRound - 1, "swiss_round", maxRounds) ?? 1,
                    group_id = groupId == "default" ? null : groupId,
                    team1_seed = history.TeamSeedMap.GetValueOrDefault(t1.TeamId),
                    team2_seed = t2 is not null ? history.TeamSeedMap.GetValueOrDefault(t2.TeamId) : (int?)null
                });
            }
        }

        if (newMatches.Count == 0)
            return (false, "No matches generated.");

        await conn.ExecuteAsync(@"
            INSERT INTO public.brkt_matches
                (id, version_id, round_index, match_number, bracket_type, round_number, status, team1_id, team2_id, best_of, group_id, team1_seed, team2_seed)
            VALUES
                (@id, @version_id, @round_index, @match_number, @bracket_type, @round_number, @status, @team1_id, @team2_id, @best_of, @group_id, @team1_seed, @team2_seed)",
            newMatches);

        return (true, null);
    }

    private static async Task<MatchHistory> LoadMatchHistoryAsync(Guid versionId, System.Data.IDbConnection conn)
    {
        var rows = (await conn.QueryAsync(
            "SELECT team1_id, team2_id, team1_seed, team2_seed, group_id FROM public.brkt_matches WHERE version_id = @versionId",
            new { versionId })).AsList();

        var playedMap = new HashSet<string>();
        var groupMap = new Dictionary<string, HashSet<Guid>>();
        var teamGroupMap = new Dictionary<Guid, string>();
        var teamSeedMap = new Dictionary<Guid, int>();

        foreach (var m in rows)
        {
            string gid = (string?)m.group_id ?? "default";
            if (!groupMap.ContainsKey(gid)) groupMap[gid] = [];

            if (m.team1_id is Guid t1)
            {
                groupMap[gid].Add(t1);
                teamGroupMap[t1] = gid;
                if (m.team1_seed is int s1 && !teamSeedMap.ContainsKey(t1))
                    teamSeedMap[t1] = s1;
            }
            if (m.team2_id is Guid t2)
            {
                groupMap[gid].Add(t2);
                teamGroupMap[t2] = gid;
                if (m.team2_seed is int s2 && !teamSeedMap.ContainsKey(t2))
                    teamSeedMap[t2] = s2;
            }

            if (m.team1_id is not null && m.team2_id is not null)
            {
                playedMap.Add($"{m.team1_id}-{m.team2_id}");
                playedMap.Add($"{m.team2_id}-{m.team1_id}");
            }
        }

        return new MatchHistory(playedMap, groupMap, teamGroupMap, teamSeedMap, rows.Count);
    }

    private static List<(TeamStanding T1, TeamStanding? T2)> GenerateGroupPairings(
        List<TeamStanding> groupStandings,
        HashSet<string> playedMap)
    {
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

        while (floaters.Count >= 2)
        {
            var f1 = floaters[0];
            floaters.RemoveAt(0);
            int oppIdx = floaters.FindIndex(f => !playedMap.Contains($"{f1.TeamId}-{f.TeamId}"));
            if (oppIdx < 0) oppIdx = 0;
            pairings.Add((f1, floaters[oppIdx]));
            floaters.RemoveAt(oppIdx);
        }

        if (floaters.Count > 0)
        {
            pairings.Add((floaters[0], null));
        }

        return pairings;
    }

    private sealed record MatchHistory(
        HashSet<string> PlayedMap,
        Dictionary<string, HashSet<Guid>> GroupMap,
        Dictionary<Guid, string> TeamGroupMap,
        Dictionary<Guid, int> TeamSeedMap,
        int ExistingCount);
}
