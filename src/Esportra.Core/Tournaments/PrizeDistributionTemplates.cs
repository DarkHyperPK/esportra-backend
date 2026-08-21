namespace Esportra.Core.Tournaments;

public static class PrizeDistributionTemplates
{
    public static List<PrizeDistributionTemplate> GetTemplates(string format, int teamCount) =>
        format.ToLowerInvariant() switch
        {
            "single_elimination" => SingleElimination(teamCount),
            "double_elimination" => DoubleElimination(teamCount),
            "round_robin" => RoundRobin(teamCount),
            "swiss" => Swiss(teamCount),
            "battle_royale" => BattleRoyale(teamCount),
            _ => Default(teamCount),
        };

    private static List<PrizeDistributionTemplate> SingleElimination(int teamCount) =>
    [
        new PrizeDistributionTemplate(
            "Top 2 Paid",
            "Only finalist positions receive prizes (winner takes most).",
            new PrizeDistributionConfig("percentage",
            [
                new(1, "1st", 70),
                new(2, "2nd", 30),
            ])),
        new PrizeDistributionTemplate(
            "Top 4 Paid",
            "Finalists and semi-finalists receive prizes — the classic esports split.",
            new PrizeDistributionConfig("percentage", TopFourSingleElim(teamCount))),
        new PrizeDistributionTemplate(
            "Top 8 Paid",
            "Extended payout reaching quarter-finalists.",
            new PrizeDistributionConfig("percentage", TopEightSingleElim(teamCount))),
    ];

    private static List<PrizeDistributionEntry> TopFourSingleElim(int teamCount)
    {
        decimal sf = teamCount >= 8 ? 2 : 1;
        decimal sfShare = Math.Round(15m / sf, 2);
        List<PrizeDistributionEntry> entries =
        [
            new(1, "1st", 50),
            new(2, "2nd", 25),
        ];
        if (teamCount >= 4)
            entries.Add(new(3, "3rd-4th", sfShare, (int)sf));
        return entries;
    }

    private static List<PrizeDistributionEntry> TopEightSingleElim(int teamCount)
    {
        List<PrizeDistributionEntry> entries =
        [
            new(1, "1st", 40),
            new(2, "2nd", 20),
            new(3, "3rd-4th", 8, 2),
        ];
        if (teamCount >= 8)
            entries.Add(new(5, "5th-8th", 3, 4));
        return entries;
    }

    private static List<PrizeDistributionTemplate> DoubleElimination(int teamCount) =>
    [
        new PrizeDistributionTemplate(
            "Top 4 Paid",
            "Rewards the top 4 teams — double elimination naturally separates 3rd and 4th.",
            new PrizeDistributionConfig("percentage",
            [
                new(1, "1st", 50),
                new(2, "2nd", 25),
                new(3, "3rd", 15),
                new(4, "4th", 10),
            ])),
        new PrizeDistributionTemplate(
            "Top 6 Paid",
            "Standard tournament payout reaching top 6 via the losers bracket.",
            new PrizeDistributionConfig("percentage",
            [
                new(1, "1st",   40),
                new(2, "2nd",   22),
                new(3, "3rd",   14),
                new(4, "4th",   10),
                new(5, "5th-6th", 4, 2),
            ])),
        new PrizeDistributionTemplate(
            "Top 8 Paid",
            "Full bracket payout for 8-team double elimination events.",
            new PrizeDistributionConfig("percentage",
            [
                new(1, "1st",   35),
                new(2, "2nd",   20),
                new(3, "3rd",   13),
                new(4, "4th",    9),
                new(5, "5th-6th", 4,   2),
                new(7, "7th-8th", 2.5m, 2),
            ])),
    ];

    private static List<PrizeDistributionTemplate> RoundRobin(int teamCount)
    {
        List<PrizeDistributionTemplate> templates =
        [
            new PrizeDistributionTemplate(
                "Top 2 Paid",
                "Only the top two finishers receive prizes.",
                new PrizeDistributionConfig("percentage",
                [
                    new(1, "1st", 65),
                    new(2, "2nd", 35),
                ])),
            new PrizeDistributionTemplate(
                "Top 4 Paid",
                "Top four teams rewarded with a descending split.",
                new PrizeDistributionConfig("percentage",
                [
                    new(1, "1st", 40),
                    new(2, "2nd", 25),
                    new(3, "3rd", 20),
                    new(4, "4th", 15),
                ])),
        ];

        if (teamCount >= 6)
        {
            var entries = BuildDescendingEntries(teamCount, topHeavy: false);
            templates.Add(new PrizeDistributionTemplate(
                "All Teams Paid",
                "Every team receives a payout on a descending scale.",
                new PrizeDistributionConfig("percentage", entries)));
        }

        return templates;
    }

    private static List<PrizeDistributionTemplate> Swiss(int teamCount)
    {
        int topPaid = teamCount switch
        {
            <= 8 => 4,
            <= 16 => 6,
            <= 32 => 8,
            _ => 10,
        };

        return
        [
            new PrizeDistributionTemplate(
                "Top 2 Paid",
                "Only the top two finishers receive prizes.",
                new PrizeDistributionConfig("percentage",
                [
                    new(1, "1st", 65),
                    new(2, "2nd", 35),
                ])),
            new PrizeDistributionTemplate(
                $"Top {topPaid} Paid",
                $"Top {topPaid} finishers paid — standard for Swiss-format events.",
                new PrizeDistributionConfig("percentage",
                    BuildTopNSwissEntries(topPaid))),
            new PrizeDistributionTemplate(
                "Top Half Paid",
                "Top half of the field receives prizes with a steep descending curve.",
                new PrizeDistributionConfig("percentage",
                    BuildTopNSwissEntries(Math.Max(2, teamCount / 2)))),
        ];
    }

    private static List<PrizeDistributionTemplate> BattleRoyale(int teamCount)
    {
        int topPaid = teamCount switch
        {
            <= 10 => 3,
            <= 20 => 6,
            <= 40 => 10,
            _ => 16,
        };

        return
        [
            new PrizeDistributionTemplate(
                "Top 3 Paid",
                "Podium-only payout — winner takes the majority.",
                new PrizeDistributionConfig("percentage",
                [
                    new(1, "1st", 50),
                    new(2, "2nd", 30),
                    new(3, "3rd", 20),
                ])),
            new PrizeDistributionTemplate(
                $"Top {topPaid} Paid",
                $"Long-tail payout reaching top {topPaid} — common in battle royale events.",
                new PrizeDistributionConfig("percentage",
                    BuildBattleRoyaleEntries(topPaid))),
            new PrizeDistributionTemplate(
                "Top Half Paid",
                "Over half the field earns a payout — maximizes participation incentive.",
                new PrizeDistributionConfig("percentage",
                    BuildBattleRoyaleEntries(Math.Max(3, teamCount / 2)))),
        ];
    }

    private static List<PrizeDistributionTemplate> Default(int teamCount) =>
    [
        new PrizeDistributionTemplate(
            "Winner Takes All",
            "100% of the prize pool awarded to the champion.",
            new PrizeDistributionConfig("percentage",
            [
                new(1, "1st", 100),
            ])),
        new PrizeDistributionTemplate(
            "Top 2 Paid",
            "Standard winner/runner-up split.",
            new PrizeDistributionConfig("percentage",
            [
                new(1, "1st", 70),
                new(2, "2nd", 30),
            ])),
    ];

    // Builds a descending % list for the top N positions in a Swiss/RR context.
    // Uses a geometric decay: each next position gets ~70% of the previous.
    private static List<PrizeDistributionEntry> BuildTopNSwissEntries(int n)
    {
        if (n <= 0) n = 1;
        var rawWeights = Enumerable.Range(0, n).Select(i => Math.Pow(0.7, i)).ToArray();
        double total = rawWeights.Sum();
        var percentages = rawWeights.Select(w => Math.Round(w / total * 100, 2)).ToList();

        // Fix rounding drift on the last entry
        decimal drift = 100m - (decimal)percentages.Sum();
        percentages[^1] = Math.Round(percentages[^1] + (double)drift, 2);

        return percentages.Select((p, i) => new PrizeDistributionEntry(
            i + 1, OrdinalLabel(i + 1), (decimal)p)).ToList();
    }

    // Builds a battle royale long-tail payout. Top 3 share ~55%, rest share ~45%.
    private static List<PrizeDistributionEntry> BuildBattleRoyaleEntries(int n)
    {
        if (n <= 0) n = 1;
        if (n == 1) return [new(1, "1st", 100)];
        if (n == 2) return [new(1, "1st", 60), new(2, "2nd", 40)];
        if (n == 3) return [new(1, "1st", 50), new(2, "2nd", 30), new(3, "3rd", 20)];

        var rawWeights = Enumerable.Range(0, n).Select(i => Math.Pow(0.75, i)).ToArray();
        double total = rawWeights.Sum();
        var percentages = rawWeights.Select(w => Math.Round(w / total * 100, 2)).ToList();

        decimal drift = 100m - (decimal)percentages.Sum();
        percentages[^1] = Math.Round(percentages[^1] + (double)drift, 2);

        return percentages.Select((p, i) => new PrizeDistributionEntry(
            i + 1, OrdinalLabel(i + 1), (decimal)p)).ToList();
    }

    // Builds a descending flat split for round-robin where all teams get something.
    private static List<PrizeDistributionEntry> BuildDescendingEntries(int count, bool topHeavy)
    {
        double decay = topHeavy ? 0.65 : 0.75;
        var rawWeights = Enumerable.Range(0, count).Select(i => Math.Pow(decay, i)).ToArray();
        double total = rawWeights.Sum();
        var percentages = rawWeights.Select(w => Math.Round(w / total * 100, 2)).ToList();

        decimal drift = 100m - (decimal)percentages.Sum();
        percentages[^1] = Math.Round(percentages[^1] + (double)drift, 2);

        return percentages.Select((p, i) => new PrizeDistributionEntry(
            i + 1, OrdinalLabel(i + 1), (decimal)p)).ToList();
    }

    private static string OrdinalLabel(int position) => position switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{position}th",
    };
}
