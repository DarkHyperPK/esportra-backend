namespace Esportra.Core.Tournaments;

public sealed class PrizeDistributionService
{
    public PrizeDistributionValidation Validate(PrizeDistributionConfig config)
    {
        if (config.Placements is null || config.Placements.Count == 0)
            return new(false, "At least one placement entry is required.", 0);

        foreach (var entry in config.Placements)
        {
            if (string.IsNullOrWhiteSpace(entry.Label))
                return new(false, $"Placement at position {entry.Position} must have a label.", 0);
            if (entry.Position < 1)
                return new(false, $"Position must be >= 1 (got {entry.Position}).", 0);
            if (entry.Percentage < 0)
                return new(false, $"Percentage for '{entry.Label}' cannot be negative.", 0);
            if (entry.SharedCount < 1)
                return new(false, $"SharedCount for '{entry.Label}' must be >= 1.", 0);

            if (entry.Rewards?.Any(r => string.Equals(r.Type, RewardType.Cash, StringComparison.OrdinalIgnoreCase)) == true)
                return new(false, $"Use prize_amount for cash — 'cash' is not valid as a reward item in '{entry.Label}'.", 0);
        }

        // Positions must not overlap (each position covers [pos, pos + shared_count - 1])
        var sorted = config.Placements.OrderBy(p => p.Position).ToList();
        int nextExpected = 1;
        for (int i = 0; i < sorted.Count; i++)
        {
            var entry = sorted[i];
            if (entry.Position < nextExpected)
                return new(false, $"Placement positions overlap at position {entry.Position}.", 0);
            nextExpected = entry.Position + entry.SharedCount;
        }

        decimal total = config.Placements.Sum(p => p.Percentage);
        if (total > 100.01m)
            return new(false, $"Percentages sum to {total:F2}%, which exceeds 100%.", total);

        return new(true, null, total);
    }

    public List<ResolvedPlacement> CalculateAmounts(
        PrizeDistributionConfig config,
        decimal prizePool,
        IReadOnlyList<(Guid TeamId, string TeamName, int Placement)> orderedTeams)
    {
        var results = new List<ResolvedPlacement>();
        var sortedBands = config.Placements.OrderBy(p => p.Position).ToList();
        var placementCounts = orderedTeams
            .GroupBy(t => t.Placement)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (teamId, teamName, placement) in orderedTeams)
        {
            var band = sortedBands.FirstOrDefault(b =>
                placement >= b.Position && placement < b.Position + b.SharedCount);

            if (band is null)
            {
                var count = placementCounts[placement];
                var isTied = count > 1;
                results.Add(new ResolvedPlacement(
                    teamId, teamName, placement,
                    isTied ? RangeLabel(placement, count) : OrdinalLabel(placement),
                    0m, [], IsTied: isTied));
                continue;
            }

            decimal bandTotal = prizePool * (band.Percentage / 100m);
            decimal perTeam = band.SharedCount > 1
                ? Math.Round(bandTotal / band.SharedCount, 2)
                : bandTotal;

            results.Add(new ResolvedPlacement(
                teamId, teamName, placement,
                band.Label, perTeam,
                band.Rewards ?? [],
                IsTied: band.SharedCount > 1));
        }

        return results;
    }

    public List<PrizeDistributionTemplate> GetTemplates(string format, int teamCount) =>
        PrizeDistributionTemplates.GetTemplates(format, teamCount);

    private static string OrdinalLabel(int position)
    {
        var suffix = (position % 100) switch
        {
            11 or 12 or 13 => "th",
            _ => (position % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            }
        };
        return $"{position}{suffix}";
    }

    private static string RangeLabel(int start, int count) =>
        $"{OrdinalLabel(start)}–{OrdinalLabel(start + count - 1)}";
}
