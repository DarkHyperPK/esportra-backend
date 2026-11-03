namespace Esportra.Api.Services.ScrimInsights;

internal static class InsightsCommon
{
    public static double? Pct(int part, int whole) => whole > 0 ? Math.Round(100.0 * part / whole, 1) : null;

    public static double? Ratio(double part, int whole) => whole > 0 ? Math.Round(part / whole, 1) : null;

    public static Rate RateOf(int count, int of) => new(count, of, Pct(count, of));

    public static double? Mean(IEnumerable<int> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 1);
    }

    /// <summary>"p1".."p10": viewer's team first, each side ordered by score then PUUID so keys are stable for a match.</summary>
    public static Dictionary<string, string> Keys(InMatch m)
    {
        var ordered = m.Players.Where(p => p.Side == m.MySide).OrderByDescending(p => p.Score).ThenBy(p => p.Puuid, StringComparer.Ordinal)
            .Concat(m.Players.Where(p => p.Side != m.MySide).OrderByDescending(p => p.Score).ThenBy(p => p.Puuid, StringComparer.Ordinal));
        var keys = new Dictionary<string, string>();
        foreach (var p in ordered) keys[p.Puuid] = $"p{keys.Count + 1}";
        return keys;
    }

    public static string? SideOf(InMatch m, string? puuid) => puuid is null ? null : m.Players.FirstOrDefault(p => p.Puuid == puuid)?.Side;

    /// <summary>The viewer's team's role in a round: the winner's role when it won, otherwise the opposite. Null when Riot gave no role.</summary>
    public static string? MyRole(InMatch m, InRound r)
    {
        var role = r.WinnerRole?.ToLowerInvariant();
        if (r.WinnerSide is null || role is not ("attacker" or "defender")) return null;
        var winnerRole = role == "attacker" ? "attack" : "defense";
        return r.WinnerSide == m.MySide ? winnerRole : winnerRole == "attack" ? "defense" : "attack";
    }

    /// <summary>
    /// Riot numbers rounds from 0; the report (pistol rounds, halves, "round 5") counts from 1. Shifts every list by one when the
    /// match starts at 0 and leaves an already 1-based match alone.
    /// </summary>
    public static InMatch OneBased(InMatch m)
    {
        if (m.Rounds.Count == 0 || m.Rounds.Min(r => r.Num) != 0) return m;
        return m with
        {
            Rounds = m.Rounds.Select(r => r with { Num = r.Num + 1 }).ToList(),
            Kills = m.Kills.Select(k => k with { Round = k.Round + 1 }).ToList(),
            Economy = m.Economy.Select(e => e with { Round = e.Round + 1 }).ToList(),
            Damage = m.Damage.Select(d => d with { Round = d.Round + 1 }).ToList(),
        };
    }

    public static bool? Won(InMatch m, InRound r) => r.WinnerSide is null ? null : r.WinnerSide == m.MySide;
}
