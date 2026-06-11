namespace Esportra.Core.Br;

/// <summary>
/// Deterministic rotating-pairwise schedule (circle method / 1-factorization).
/// </summary>
public static class BrScheduleGenerator
{
    public static IReadOnlyList<string> BuildGroupLabels(int seedGroupCount)
    {
        if (seedGroupCount < 2)
            throw new ArgumentOutOfRangeException(nameof(seedGroupCount), "Need at least 2 seed groups.");

        return Enumerable.Range(0, seedGroupCount)
            .Select(i => ((char)('A' + i)).ToString())
            .ToList();
    }

    /// <summary>
    /// Circle method for P=2: G-1 waves, each with G/2 lobbies of 2 groups.
    /// Fixed group A paired with circle[0]; remaining circle pairs symmetrically.
    /// </summary>
    public static BrScheduleManifest GenerateRotatingPairwise(
        int seedGroupCount,
        int groupsPerLobby = 2,
        int matchesPerWave = 1)
    {
        if (groupsPerLobby != 2)
            throw new NotSupportedException("Only groupsPerLobby=2 is supported in v1.");

        if (seedGroupCount % 2 != 0)
            throw new ArgumentException("seedGroupCount must be even for rotating pairwise.", nameof(seedGroupCount));

        if (seedGroupCount % groupsPerLobby != 0)
            throw new ArgumentException("seedGroupCount must divide evenly by groupsPerLobby.", nameof(seedGroupCount));

        if (matchesPerWave < 1)
            throw new ArgumentOutOfRangeException(nameof(matchesPerWave));

        var labels = BuildGroupLabels(seedGroupCount);
        var fixedLabel = labels[0];
        var circle = labels.Skip(1).ToList();
        var waves = new List<BrMatchupWave>();

        for (var wave = 0; wave < seedGroupCount - 1; wave++)
        {
            var pairs = new List<IReadOnlyList<string>>
            {
                new[] { fixedLabel, circle[0] },
            };

            for (var i = 1; i < circle.Count / 2 + 1; i++)
            {
                var left = circle[i];
                var right = circle[circle.Count - i];
                if (left != right)
                    pairs.Add(new[] { left, right });
            }

            // Deduplicate pairs when circle has only 2 elements (G=4 handled by single inner pair)
            var lobbies = pairs
                .Select(p => (IReadOnlyList<string>)p.OrderBy(x => x, StringComparer.Ordinal).ToArray())
                .ToList();

            for (var m = 0; m < matchesPerWave; m++)
                waves.Add(new BrMatchupWave(wave + 1, lobbies));

            var first = circle[0];
            circle.RemoveAt(0);
            circle.Add(first);
        }

        var distinctWaves = waves.GroupBy(w => w.Wave).Count();

        return new BrScheduleManifest(
            TotalWaves: distinctWaves,
            TotalLobbies: waves.Sum(w => w.Lobbies.Count),
            TotalMatches: waves.Count,
            Waves: waves);
    }

    public static int ComputeLobbyRosterSize(
        IReadOnlyDictionary<string, int> rosterByLabel,
        IReadOnlyList<string> groupLabels)
    {
        return groupLabels.Sum(label =>
            rosterByLabel.TryGetValue(label, out var size) ? size : 0);
    }

    public static BrScheduleValidationError? ValidateLobbyCapacity(
        BrScheduleManifest manifest,
        IReadOnlyDictionary<string, int> rosterByLabel,
        int maxLobbySize)
    {
        foreach (var wave in manifest.Waves)
        {
            foreach (var lobby in wave.Lobbies)
            {
                var size = ComputeLobbyRosterSize(rosterByLabel, lobby);
                if (size > maxLobbySize)
                {
                    return new BrScheduleValidationError(
                        $"Wave {wave.Wave} lobby [{string.Join("+", lobby)}] has {size} units (max {maxLobbySize}).");
                }
            }
        }

        return null;
    }
}
