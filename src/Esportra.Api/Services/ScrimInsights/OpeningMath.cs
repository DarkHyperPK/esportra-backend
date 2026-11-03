namespace Esportra.Api.Services.ScrimInsights;

/// <summary>Opening duels: the first kill of each round, who got it, who lost it, and how rounds went afterwards.</summary>
internal static class OpeningMath
{
    public static InKill? FirstBlood(IEnumerable<InKill> roundKills) =>
        roundKills.Where(k => k.Killer is not null).OrderBy(k => k.RoundMs ?? int.MaxValue).ThenBy(k => k.Ordinal).FirstOrDefault();

    public static Opening Build(InMatch m, IReadOnlyDictionary<string, string> keys)
    {
        var firsts = m.Rounds.Select(r => (Round: r, Kill: FirstBlood(m.Kills.Where(k => k.Round == r.Num)))).Where(x => x.Kill is not null).ToList();
        var players = m.Players.OrderBy(p => keys[p.Puuid], new KeyOrder()).Select(p => PlayerRow(p, firsts, keys)).ToList();

        bool Mine(InKill k) => InsightsCommon.SideOf(m, k.Killer) == m.MySide;
        var mineFirst = firsts.Where(x => Mine(x.Kill!)).ToList();
        var theirsFirst = firsts.Where(x => !Mine(x.Kill!)).ToList();
        Rate WithRole(string role) => InsightsCommon.RateOf(
            firsts.Count(x => InsightsCommon.MyRole(m, x.Round) == role && Mine(x.Kill!)),
            firsts.Count(x => InsightsCommon.MyRole(m, x.Round) == role));

        return new Opening(
            players,
            InsightsCommon.RateOf(mineFirst.Count(x => InsightsCommon.Won(m, x.Round) == true), mineFirst.Count),
            InsightsCommon.RateOf(theirsFirst.Count(x => InsightsCommon.Won(m, x.Round) == true), theirsFirst.Count),
            InsightsCommon.RateOf(mineFirst.Count, firsts.Count), WithRole("attack"), WithRole("defense"));
    }

    private static OpeningPlayer PlayerRow(InPlayer p, List<(InRound Round, InKill? Kill)> firsts, IReadOnlyDictionary<string, string> keys)
    {
        var kills = firsts.Where(x => x.Kill!.Killer == p.Puuid).Select(x => x.Kill!).ToList();
        var deaths = firsts.Where(x => x.Kill!.Victim == p.Puuid).Select(x => x.Kill!).ToList();
        return new OpeningPlayer(
            keys[p.Puuid], kills.Count, deaths.Count, InsightsCommon.RateOf(kills.Count, kills.Count + deaths.Count),
            InsightsCommon.Mean(kills.Where(k => k.RoundMs is not null).Select(k => k.RoundMs!.Value)),
            InsightsCommon.Mean(deaths.Where(k => k.RoundMs is not null).Select(k => k.RoundMs!.Value)));
    }

    /// <summary>Orders "p2" before "p10".</summary>
    internal sealed class KeyOrder : IComparer<string>
    {
        public int Compare(string? a, string? b) => int.Parse(a![1..]).CompareTo(int.Parse(b![1..]));
    }
}
