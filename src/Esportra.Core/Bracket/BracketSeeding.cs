namespace Esportra.Core.Bracket;

public static class BracketSeeding
{
    /// <summary>
    /// Standard 1-vs-N bracket seeding. Returns a nullable array of size <paramref name="bracketSize"/>;
    /// empty positions are null (TBD/BYE slots).
    /// </summary>
    public static (Guid Id, string Name, int Seed)?[] SeedTeams(
        IReadOnlyList<(Guid Id, string Name)> teams, int bracketSize)
    {
        var seeded = new (Guid Id, string Name, int Seed)?[bracketSize];
        var positions = GetStandardBracketSlots(bracketSize);

        var count = Math.Min(teams.Count, bracketSize);
        for (int i = 0; i < count; i++)
            seeded[positions[i]] = (teams[i].Id, teams[i].Name, i + 1);

        return seeded;
    }

    /// <summary>
    /// Round-0 pairings for one RR/Swiss group using the circle algorithm:
    /// participants[i] vs participants[n-1-i]. For odd-size groups a virtual BYE
    /// occupies index n-1 — those pairings are skipped (top seed in group gets the BYE).
    /// </summary>
    public static IReadOnlyList<(T Team1, T Team2)> GetRound0Pairings<T>(IReadOnlyList<T> groupTeams)
    {
        int n = groupTeams.Count % 2 == 0 ? groupTeams.Count : groupTeams.Count + 1;
        var pairings = new List<(T, T)>();
        for (int i = 0; i < n / 2; i++)
        {
            int j = n - 1 - i;
            if (j >= groupTeams.Count) continue; // virtual BYE — top seed gets the pass
            pairings.Add((groupTeams[i], groupTeams[j]));
        }
        return pairings;
    }

    public static int[] GetStandardBracketSlots(int n)
    {
        if (n <= 0) return [];
        if (n == 1) return [0];
        if (n == 2) return [0, 1];

        var slots = new int[n];
        int halfSize = n / 2;
        var upper = GetStandardBracketSlots(halfSize);

        for (int i = 0; i < halfSize; i++)
        {
            slots[i] = upper[i] * 2;
            slots[n - 1 - i] = upper[i] * 2 + 1;
        }

        return slots;
    }
}
