namespace Esportra.Core.Bracket;

/// <summary>
/// Circle algorithm round-robin scheduler with optional group support.
/// </summary>
public sealed class RoundRobinGenerator : IBracketGenerator
{
    public BracketGraph Generate(
        IReadOnlyList<(Guid Id, string Name)> teams,
        Guid tournamentId,
        Guid? stageId = null,
        int bestOf = 1,
        int? bracketSize = null,   // interpreted as number of groups
        int? advancementCount = null,
        BracketConfig? config = null)
    {
        var versionId = Guid.NewGuid();
        var nodes = new List<BracketNode>();
        var edges = new List<BracketEdge>();

        string? dailyStart = config?.DailyStartTime ?? "20:00";
        DateTime? startDate = config?.TournamentStartDate is { } s ? DateTime.Parse(s) : null;

        int numGroups = bracketSize ?? 1;
        var groups = new List<List<(Guid Id, string Name)>>(numGroups);
        for (int i = 0; i < numGroups; i++) groups.Add([]);

        // Snake seeding distribution
        for (int idx = 0; idx < teams.Count; idx++)
        {
            int cycle = idx / numGroups;
            int groupIndex = cycle % 2 == 0 ? idx % numGroups : numGroups - 1 - (idx % numGroups);
            groups[groupIndex].Add(teams[idx]);
        }

        int matchCounter = 1;

        for (int gi = 0; gi < numGroups; gi++)
        {
            string groupId = $"Group {(char)('A' + gi)}";
            var groupTeams = groups[gi];
            var roundMatches = GenerateCircleSchedule(groupTeams);

            for (int ri = 0; ri < roundMatches.Count; ri++)
            {
                string? scheduledTime = null;
                if (startDate.HasValue)
                {
                    var roundDate = startDate.Value.AddDays(ri);
                    var parts = dailyStart!.Split(':');
                    roundDate = roundDate.Date
                        .AddHours(int.Parse(parts[0]))
                        .AddMinutes(int.Parse(parts[1]));
                    scheduledTime = roundDate.ToString("o");
                }

                var round = roundMatches[ri];
                for (int mi = 0; mi < round.Count; mi++)
                {
                    var (t1, t2) = round[mi];
                    nodes.Add(new BracketNode(
                        Id: Guid.NewGuid(),
                        VersionId: versionId,
                        RoundIndex: ri,
                        MatchNumber: matchCounter++,
                        BracketType: "group",
                        GroupId: groupId,
                        RoundNumber: ri + 1,
                        Status: "pending",
                        Team1Id: t1.Id,
                        Team2Id: t2.Id,
                        BestOf: bestOf,
                        ScheduledTime: scheduledTime,
                        X: gi * 400,
                        Y: ri * 150 + mi * 80));
                }
            }
        }

        var version = new BracketVersion(versionId, tournamentId, stageId, 1, "draft",
            DateTime.UtcNow.ToString("o"));

        return new BracketGraph(version, nodes, edges);
    }

    /// <summary>Circle algorithm: N teams → N-1 rounds, each team plays once per round.</summary>
    private static List<List<((Guid Id, string Name) Team1, (Guid Id, string Name) Team2)>>
        GenerateCircleSchedule(List<(Guid Id, string Name)> teams)
    {
        var rounds = new List<List<((Guid, string), (Guid, string))>>();
        if (teams.Count < 2) return rounds;

        var participants = new List<(Guid Id, string Name)>(teams);

        bool hasBye = participants.Count % 2 != 0;
        if (hasBye)
            participants.Add((Guid.Empty, "BYE"));

        int n = participants.Count;
        int numRounds = n - 1;
        var circle = new List<(Guid, string)>(participants);

        for (int round = 0; round < numRounds; round++)
        {
            var roundMatches = new List<((Guid, string), (Guid, string))>();

            for (int i = 0; i < n / 2; i++)
            {
                var t1 = circle[i];
                var t2 = circle[n - 1 - i];

                if (t1.Item1 == Guid.Empty || t2.Item1 == Guid.Empty) continue;
                roundMatches.Add((t1, t2));
            }

            rounds.Add(roundMatches);

            // Rotate: fix position 0, shift rest
            var newCircle = new List<(Guid, string)>(n) { circle[0] };
            newCircle.Add(circle[n - 1]);
            for (int i = 1; i < n - 1; i++)
                newCircle.Add(circle[i]);
            circle = newCircle;
        }

        return rounds;
    }
}
