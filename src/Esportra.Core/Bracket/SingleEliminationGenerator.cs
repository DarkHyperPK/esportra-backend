namespace Esportra.Core.Bracket;

public sealed class SingleEliminationGenerator : IBracketGenerator
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
        roundConfig ??= StageRoundConfiguration.PerStage("single_elimination", 1);

        var versionId = Guid.NewGuid();
        var nodes = new List<BracketNode>();
        var edges = new List<BracketEdge>();

        int numTeams = teams.Count;
        int targetSize = bracketSize ?? Math.Max(numTeams, 2);
        int P = (int)Math.Pow(2, Math.Ceiling(Math.Log2(targetSize)));

        int fullRounds = (int)Math.Round(Math.Log2(P));
        int effectiveAdvCount = (advancementCount is > 0) ? advancementCount.Value : 1;
        int targetRemaining = (int)Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(1, effectiveAdvCount))));
        int numRounds = Math.Max(1, fullRounds - (int)Math.Round(Math.Log2(targetRemaining)));

        var seeded = BracketSeeding.SeedTeams(teams, P);
        var matchMap = new Dictionary<string, BracketNode>();

        // 1. Create nodes
        for (int r = 0; r < numRounds; r++)
        {
            int matchesInRound = P / (int)Math.Pow(2, r + 1);
            bool isFinal = r == numRounds - 1 && matchesInRound == 1;
            string bracketType = isFinal ? "final" : "winners";

            for (int i = 0; i < matchesInRound; i++)
            {
                Guid? team1Id = null, team2Id = null;
                int? team1Seed = null, team2Seed = null;
                if (r == 0)
                {
                    var slot1 = seeded.ElementAtOrDefault(i * 2);
                    var slot2 = seeded.ElementAtOrDefault(i * 2 + 1);
                    team1Id = slot1?.Id;
                    team2Id = slot2?.Id;
                    team1Seed = slot1?.Seed;
                    team2Seed = slot2?.Seed;
                }

                int matchBestOf = roundConfig.GetBestOf(r, bracketType, numRounds);

                var match = new BracketNode(
                    Id: Guid.NewGuid(),
                    VersionId: versionId,
                    RoundIndex: r,
                    MatchNumber: i + 1,
                    BracketType: bracketType,
                    Status: "pending",
                    BestOf: matchBestOf,
                    Team1Id: team1Id,
                    Team2Id: team2Id,
                    Team1Seed: team1Seed,
                    Team2Seed: team2Seed);

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
                if (!matchMap.TryGetValue($"{r}-{i + 1}", out var current)) continue;
                if (!matchMap.TryGetValue($"{r + 1}-{(int)Math.Ceiling((i + 1) / 2.0)}", out var next)) continue;

                edges.Add(new BracketEdge(
                    Id: Guid.NewGuid(),
                    VersionId: versionId,
                    SourceMatchId: current.Id,
                    TargetMatchId: next.Id,
                    Type: "winner",
                    TargetSlot: (i + 1) % 2 == 1 ? 1 : 2));
            }
        }

        var version = new BracketVersion(
            Id: versionId,
            TournamentId: tournamentId,
            StageId: stageId,
            VersionNumber: 1,
            Status: "draft",
            CreatedAt: DateTime.UtcNow.ToString("o"));

        return new BracketGraph(version, nodes, edges);
    }

}
