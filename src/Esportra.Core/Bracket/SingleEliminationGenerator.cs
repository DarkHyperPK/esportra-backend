namespace Esportra.Core.Bracket;

public sealed class SingleEliminationGenerator : IBracketGenerator
{
    public BracketGraph Generate(
        IReadOnlyList<(string Id, string Name)> teams,
        string tournamentId,
        string? stageId          = null,
        int     bestOf           = 1,
        int?    bracketSize      = null,
        int?    advancementCount = null,
        BracketConfig? config    = null)
    {
        var versionId = Guid.NewGuid().ToString();
        var nodes     = new List<BracketNode>();
        var edges     = new List<BracketEdge>();

        int numTeams   = teams.Count;
        int targetSize = bracketSize ?? Math.Max(numTeams, 2);
        int P          = (int)Math.Pow(2, Math.Ceiling(Math.Log2(targetSize)));

        int fullRounds        = (int)Math.Round(Math.Log2(P));
        int effectiveAdvCount = (advancementCount is > 0) ? advancementCount.Value : 1;
        int targetRemaining   = (int)Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(1, effectiveAdvCount))));
        int numRounds         = Math.Max(1, fullRounds - (int)Math.Round(Math.Log2(targetRemaining)));

        var seeded   = SeedTeams(teams, P);
        var matchMap = new Dictionary<string, BracketNode>();

        // 1. Create nodes
        for (int r = 0; r < numRounds; r++)
        {
            int matchesInRound = P / (int)Math.Pow(2, r + 1);
            for (int i = 0; i < matchesInRound; i++)
            {
                string? team1Id = null, team2Id = null;
                if (r == 0)
                {
                    team1Id = seeded.ElementAtOrDefault(i * 2)?.Id;
                    team2Id = seeded.ElementAtOrDefault(i * 2 + 1)?.Id;
                }

                var match = new BracketNode(
                    Id:          Guid.NewGuid().ToString(),
                    VersionId:   versionId,
                    RoundIndex:  r,
                    MatchNumber: i + 1,
                    BracketType: "winners",
                    Status:      "pending",
                    BestOf:      bestOf,
                    Team1Id:     team1Id,
                    Team2Id:     team2Id);

                nodes.Add(match);
                matchMap[$"{r}-{i + 1}"] = match;
            }
        }

        // 2. Create edges
        for (int r = 0; r < numRounds - 1; r++)
        {
            int matchesInRound = P / (int)Math.Pow(2, r + 1);
            for (int i = 0; i < matchesInRound; i++)
            {
                if (!matchMap.TryGetValue($"{r}-{i + 1}",                    out var current)) continue;
                if (!matchMap.TryGetValue($"{r + 1}-{(int)Math.Ceiling((i + 1) / 2.0)}", out var next))    continue;

                edges.Add(new BracketEdge(
                    Id:            Guid.NewGuid().ToString(),
                    VersionId:     versionId,
                    SourceMatchId: current.Id,
                    TargetMatchId: next.Id,
                    Type:          "winner",
                    TargetSlot:    (i + 1) % 2 == 1 ? 1 : 2));
            }
        }

        var version = new BracketVersion(
            Id:            versionId,
            TournamentId:  tournamentId,
            StageId:       stageId,
            VersionNumber: 1,
            Status:        "draft",
            CreatedAt:     DateTime.UtcNow.ToString("o"));

        return new BracketGraph(version, nodes, edges);
    }

    // Standard bracket seeding: 1 vs N, 2 vs N-1, etc.
    private static (string Id, string Name)?[] SeedTeams(IReadOnlyList<(string Id, string Name)> teams, int bracketSize)
    {
        var seeded    = new (string Id, string Name)?[bracketSize];
        var positions = GetStandardBracketSlots(bracketSize);

        for (int i = 0; i < teams.Count; i++)
            seeded[positions[i]] = teams[i];

        return seeded;
    }

    private static int[] GetStandardBracketSlots(int n)
    {
        if (n == 1) return [0];
        if (n == 2) return [0, 1];

        var slots    = new int[n];
        int halfSize = n / 2;
        var upper    = GetStandardBracketSlots(halfSize);

        for (int i = 0; i < halfSize; i++)
        {
            slots[i]         = upper[i] * 2;
            slots[n - 1 - i] = upper[i] * 2 + 1;
        }

        return slots;
    }
}
