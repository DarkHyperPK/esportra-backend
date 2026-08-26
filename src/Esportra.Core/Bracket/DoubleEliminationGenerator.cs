namespace Esportra.Core.Bracket;

public sealed class DoubleEliminationGenerator : IBracketGenerator
{
    public BracketGraph Generate(
        IReadOnlyList<(Guid Id, string Name)> teams,
        Guid tournamentId,
        Guid? stageId = null,
        StageRoundConfiguration? roundConfig = null,
        int? bracketSize = null,
        int? advancementCount = null,
        BracketConfig? config = null)
    {
        roundConfig ??= StageRoundConfiguration.PerStage("double_elimination", 1);

        var versionId = Guid.NewGuid();
        var nodes = new List<BracketNode>();
        var edges = new List<BracketEdge>();

        int numTeams = teams.Count;
        int targetSize = bracketSize ?? Math.Max(numTeams, 2);
        int P = (int)Math.Pow(2, Math.Ceiling(Math.Log2(targetSize)));

        int numUpperRounds = (int)Math.Round(Math.Log2(P));
        int numLowerRounds = 2 * numUpperRounds - 2;

        var seeded = BracketSeeding.SeedTeams(teams, P);
        var matchMap = new Dictionary<string, BracketNode>();

        // 1. Upper bracket nodes
        for (int r = 0; r < numUpperRounds; r++)
        {
            int matchesInRound = P / (int)Math.Pow(2, r + 1);
            for (int i = 0; i < matchesInRound; i++)
            {
                Guid? t1 = null, t2 = null;
                int? t1Seed = null, t2Seed = null;
                if (r == 0)
                {
                    var slot1 = seeded.ElementAtOrDefault(i * 2);
                    var slot2 = seeded.ElementAtOrDefault(i * 2 + 1);
                    t1 = slot1?.Id;
                    t2 = slot2?.Id;
                    t1Seed = slot1?.Seed;
                    t2Seed = slot2?.Seed;
                }

                int matchBestOf = roundConfig.GetBestOf(r, "winners", numUpperRounds);

                var match = new BracketNode(
                    Id: Guid.NewGuid(), VersionId: versionId,
                    RoundIndex: r, MatchNumber: i + 1,
                    BracketType: "winners", Status: "pending",
                    BestOf: matchBestOf, Team1Id: t1, Team2Id: t2,
                    Team1Seed: t1Seed, Team2Seed: t2Seed);

                nodes.Add(match);
                matchMap[$"winners-{r}-{i + 1}"] = match;
            }
        }

        // 2. Lower bracket nodes
        for (int r = 0; r < numLowerRounds; r++)
        {
            int matchesInRound = (int)Math.Pow(2, Math.Floor((numLowerRounds - 1 - r) / 2.0));
            for (int i = 0; i < matchesInRound; i++)
            {
                int matchBestOf = roundConfig.GetBestOf(r, "losers", numLowerRounds);

                var match = new BracketNode(
                    Id: Guid.NewGuid(), VersionId: versionId,
                    RoundIndex: r, MatchNumber: i + 1,
                    BracketType: "losers", Status: "pending", BestOf: matchBestOf);

                nodes.Add(match);
                matchMap[$"losers-{r}-{i + 1}"] = match;
            }
        }

        // 3. Grand final node
        int gfBestOf = roundConfig.GetBestOf(0, "final", 1);

        var gf = new BracketNode(
            Id: Guid.NewGuid(), VersionId: versionId,
            RoundIndex: numUpperRounds, MatchNumber: 1,
            BracketType: "final", Status: "pending", BestOf: gfBestOf);

        nodes.Add(gf);
        matchMap["final-0-1"] = gf;

        // 4. Upper bracket edges
        for (int r = 0; r < numUpperRounds - 1; r++)
        {
            int matchesInRound = P / (int)Math.Pow(2, r + 1);
            for (int i = 0; i < matchesInRound; i++)
            {
                if (!matchMap.TryGetValue($"winners-{r}-{i + 1}", out var current)) continue;

                // Winner → next WB round
                int nextMatchNum = (int)Math.Ceiling((i + 1) / 2.0);
                if (matchMap.TryGetValue($"winners-{r + 1}-{nextMatchNum}", out var next))
                {
                    edges.Add(new BracketEdge(Guid.NewGuid(), versionId,
                        current.Id, next.Id, "winner", (i + 1) % 2 == 1 ? 1 : 2));
                }

                // Loser → lower bracket
                int lr = r == 0 ? 0 : 2 * r - 1;
                if (lr < numLowerRounds)
                {
                    int dropMatchNum = r == 0 ? (int)Math.Ceiling((i + 1) / 2.0) : (i + 1);
                    if (matchMap.TryGetValue($"losers-{lr}-{dropMatchNum}", out var loserMatch))
                    {
                        int slot = r == 0 ? ((i + 1) % 2 == 1 ? 1 : 2) : 1;
                        edges.Add(new BracketEdge(Guid.NewGuid(), versionId,
                            current.Id, loserMatch.Id, "loser", slot));
                    }
                }
            }
        }

        // Upper final → GF (slot 1) + LB final (loser)
        if (matchMap.TryGetValue($"winners-{numUpperRounds - 1}-1", out var upperFinal))
        {
            edges.Add(new BracketEdge(Guid.NewGuid(), versionId,
                upperFinal.Id, gf.Id, "winner", 1));

            if (matchMap.TryGetValue($"losers-{numLowerRounds - 1}-1", out var lbFinal))
            {
                edges.Add(new BracketEdge(Guid.NewGuid(), versionId,
                    upperFinal.Id, lbFinal.Id, "loser", 1));
            }
        }

        // 5. Lower bracket edges
        for (int r = 0; r < numLowerRounds - 1; r++)
        {
            int matchesInRound = (int)Math.Pow(2, Math.Floor((numLowerRounds - 1 - r) / 2.0));
            for (int i = 0; i < matchesInRound; i++)
            {
                if (!matchMap.TryGetValue($"losers-{r}-{i + 1}", out var current)) continue;

                int nextMatchNum = r % 2 == 0 ? (i + 1) : (int)Math.Ceiling((i + 1) / 2.0);
                if (!matchMap.TryGetValue($"losers-{r + 1}-{nextMatchNum}", out var next)) continue;

                int slot = r % 2 == 0 ? 2 : ((i + 1) % 2 == 1 ? 1 : 2);
                edges.Add(new BracketEdge(Guid.NewGuid(), versionId,
                    current.Id, next.Id, "winner", slot));
            }
        }

        // Lower final → GF slot 2
        if (matchMap.TryGetValue($"losers-{numLowerRounds - 1}-1", out var lowerFinal))
        {
            edges.Add(new BracketEdge(Guid.NewGuid(), versionId,
                lowerFinal.Id, gf.Id, "winner", 2));
        }

        var version = new BracketVersion(versionId, tournamentId, stageId, 1, "draft",
            DateTime.UtcNow.ToString("o"));

        return new BracketGraph(version, nodes, edges);
    }

}
