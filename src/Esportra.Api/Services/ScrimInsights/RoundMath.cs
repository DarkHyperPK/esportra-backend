namespace Esportra.Api.Services.ScrimInsights;

/// <summary>The round-by-round story: results, score progression, who opened each round, streaks and half splits.</summary>
internal static class RoundMath
{
    /// <summary>Thresholds for the buy label, on the viewer's or opponent's average loadout value per player.</summary>
    public const int EcoMax = 2000, FullBuyMin = 3900;

    public static string Buy(double avgLoadout, bool pistol) => pistol ? "pistol" : avgLoadout <= EcoMax ? "eco" : avgLoadout >= FullBuyMin ? "full" : "force";

    public static bool IsPistol(int roundNum) => roundNum is 1 or 13;

    public static double? AvgLoadout(InMatch m, int round, string side)
    {
        var puuids = m.Players.Where(p => p.Side == side).Select(p => p.Puuid).ToHashSet();
        var values = m.Economy.Where(e => e.Round == round && puuids.Contains(e.Puuid) && e.Loadout is not null).Select(e => e.Loadout!.Value).ToList();
        return values.Count == 0 ? null : Math.Round(values.Average(), 0);
    }

    public static IReadOnlyList<RoundLine> Lines(InMatch m, IReadOnlyDictionary<string, string> keys)
    {
        int mine = 0, theirs = 0;
        var lines = new List<RoundLine>();
        foreach (var r in m.Rounds.OrderBy(r => r.Num))
        {
            var won = InsightsCommon.Won(m, r);
            if (won == true) mine++; else if (won == false) theirs++;
            var first = OpeningMath.FirstBlood(m.Kills.Where(k => k.Round == r.Num));
            var myLoad = AvgLoadout(m, r.Num, m.MySide);
            var theirLoad = AvgLoadout(m, r.Num, m.TheirSide);
            var pistol = IsPistol(r.Num);
            lines.Add(new RoundLine(
                r.Num, won, InsightsCommon.MyRole(m, r), r.ResultCode, r.Planter is null ? null : r.Site, r.PlantMs, r.Defuser is not null, r.DefuseMs,
                first?.Killer is { } k ? keys.GetValueOrDefault(k) : null, first is null ? null : keys.GetValueOrDefault(first.Victim),
                first is null ? null : InsightsCommon.SideOf(m, first.Killer) == m.MySide, first?.RoundMs, mine, theirs,
                myLoad, theirLoad, myLoad is { } a ? Buy(a, pistol) : null, theirLoad is { } b ? Buy(b, pistol) : null, pistol));
        }
        return lines;
    }

    public static Streaks StreaksOf(IReadOnlyList<RoundLine> lines)
    {
        int win = 0, loss = 0, bestWin = 0, bestLoss = 0;
        foreach (var l in lines)
        {
            if (l.Won == true) { win++; loss = 0; }
            else if (l.Won == false) { loss++; win = 0; }
            else { win = 0; loss = 0; }
            bestWin = Math.Max(bestWin, win);
            bestLoss = Math.Max(bestLoss, loss);
        }
        return new Streaks(bestWin, bestLoss);
    }

    public static Halves HalvesOf(IReadOnlyList<RoundLine> lines)
    {
        Rate Of(Func<RoundLine, bool> inHalf) =>
            InsightsCommon.RateOf(lines.Count(l => inHalf(l) && l.Won == true), lines.Count(l => inHalf(l) && l.Won is not null));
        return new Halves(Of(l => l.Num <= 12), Of(l => l.Num is > 12 and <= 24), Of(l => l.Num > 24));
    }
}
