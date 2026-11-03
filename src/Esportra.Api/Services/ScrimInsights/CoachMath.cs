namespace Esportra.Api.Services.ScrimInsights;

/// <summary>Builds the "what decided rounds" read of one map (rounds numbered from 1): who was alive when, trades, leads, clutches, plants, retakes, economy conversion and tempo.</summary>
public static class CoachMath
{
    public const int SlowRoundMs = 30_000, LoadoutEdge = 1000;

    private sealed class Tally(string key)
    {
        public string Key { get; } = key;
        public int Deaths, Traded, UntradedFirstDeaths, TradeKills, ClutchAttempts, ClutchWins, EntryDuels, EntryWins, MultiKillRounds, MultiKillRoundsWon;
    }

    private static Rate Of(int count, int of) => InsightsCommon.RateOf(count, of);

    public static CoachMap Build(InMatch m, IReadOnlyList<RoundLine> lines, IReadOnlyDictionary<string, string> keys)
    {
        var tallies = m.Players.Where(p => p.Side == m.MySide).ToDictionary(p => p.Puuid, p => new Tally(keys[p.Puuid]));
        int led = 0, ledWon = 0, trailed = 0, trailedWon = 0, noPlant = 0, noPlantWon = 0;
        var clutches = new List<ClutchLine>();
        var plant = new List<(string Site, bool Won)>();
        var retake = new List<(string Site, bool Won)>();
        var firstKills = new List<(int Ms, bool? Won)>();

        foreach (var line in lines)
        {
            var round = m.Rounds.First(r => r.Num == line.Num);
            var steps = RoundStates.Steps(m, line.Num);
            var won = line.Won;
            if (won is not null)
            {
                if (steps.Any(s => s.MineAlive > s.TheirsAlive)) { led++; ledWon += won == true ? 1 : 0; }
                if (steps.Any(s => s.MineAlive < s.TheirsAlive)) { trailed++; trailedWon += won == true ? 1 : 0; }
                if (steps.FirstOrDefault(s => s.ClutchPuuid is not null) is { } c)
                {
                    clutches.Add(new ClutchLine(line.Num, keys[c.ClutchPuuid!], c.TheirsAlive, won == true));
                    var t = tallies[c.ClutchPuuid!];
                    t.ClutchAttempts++; t.ClutchWins += won == true ? 1 : 0;
                }
            }
            Plants(m, round, line, plant, retake, ref noPlant, ref noPlantWon);
            Duels(m, line, tallies);
            if (OpeningMath.FirstBlood(m.Kills.Where(k => k.Round == line.Num)) is { RoundMs: { } ms }) firstKills.Add((ms, won));
        }
        foreach (var k in m.Kills)
        {
            if (k.Killer is not null && tallies.TryGetValue(k.Killer, out var killer) && RoundStates.IsTradeKill(m, k)) killer.TradeKills++;
            if (tallies.TryGetValue(k.Victim, out var dead)) { dead.Deaths++; dead.Traded += RoundStates.IsTraded(m, k) ? 1 : 0; }
        }

        var slow = firstKills.Where(f => f.Ms >= SlowRoundMs && f.Won is not null).ToList();
        return new CoachMap(
            new TradeCounts(tallies.Values.Sum(t => t.Deaths), tallies.Values.Sum(t => t.Traded), tallies.Values.Sum(t => t.TradeKills)),
            Of(ledWon, led), Of(trailedWon, trailed), clutches,
            Of(plant.Count(p => p.Won), plant.Count), Sites(plant), Of(noPlantWon, noPlant), Of(retake.Count(p => p.Won), retake.Count), Sites(retake),
            Pistol(lines, true), Pistol(lines, false), Buys(lines, "full", "eco"), Buys(lines, "eco", "full"), Edge(lines, true), Edge(lines, false),
            InsightsCommon.Mean(firstKills.Select(f => f.Ms)), Of(slow.Count(f => f.Won == true), slow.Count),
            tallies.Values.Select(t => new CoachPlayer(t.Key, t.Deaths, t.Traded, t.UntradedFirstDeaths, t.TradeKills, t.ClutchAttempts, t.ClutchWins, t.EntryDuels, t.EntryWins, t.MultiKillRounds, t.MultiKillRoundsWon)).ToList());
    }

    private static void Plants(InMatch m, InRound round, RoundLine line, List<(string, bool)> plant, List<(string, bool)> retake, ref int noPlant, ref int noPlantWon)
    {
        if (line.Won is null) return;
        var site = round.Site ?? "?";
        if (round.Planter is not null)
        {
            if (InsightsCommon.SideOf(m, round.Planter) == m.MySide) plant.Add((site, line.Won == true)); else retake.Add((site, line.Won == true));
        }
        else if (line.MyRole == "attack") { noPlant++; noPlantWon += line.Won == true ? 1 : 0; }
    }

    /// <summary>Entry duels, untraded first deaths and multi-kill rounds for the team's players.</summary>
    private static void Duels(InMatch m, RoundLine line, Dictionary<string, Tally> tallies)
    {
        var roundKills = m.Kills.Where(k => k.Round == line.Num).ToList();
        if (OpeningMath.FirstBlood(roundKills) is { } first)
        {
            if (first.Killer is not null && tallies.TryGetValue(first.Killer, out var k)) { k.EntryDuels++; k.EntryWins++; }
            if (tallies.TryGetValue(first.Victim, out var v)) { v.EntryDuels++; if (!RoundStates.IsTraded(m, first)) v.UntradedFirstDeaths++; }
        }
        foreach (var g in roundKills.Where(k => k.Killer is not null && tallies.ContainsKey(k.Killer)).GroupBy(k => k.Killer!).Where(g => g.Count() >= 2))
        {
            tallies[g.Key].MultiKillRounds++;
            tallies[g.Key].MultiKillRoundsWon += line.Won == true ? 1 : 0;
        }
    }

    private static IReadOnlyList<SiteRate> Sites(List<(string Site, bool Won)> rows) =>
        rows.GroupBy(r => r.Site).Select(g => new SiteRate(g.Key, Of(g.Count(r => r.Won), g.Count()))).OrderBy(s => s.Site, StringComparer.Ordinal).ToList();

    /// <summary>How the round after a pistol round went, split by whether the pistol was won.</summary>
    private static Rate Pistol(IReadOnlyList<RoundLine> lines, bool pistolWon)
    {
        var next = lines.Where(l => l.Pistol && l.Won is not null && (l.Won == true) == pistolWon)
            .Select(l => lines.FirstOrDefault(n => n.Num == l.Num + 1)).Where(n => n?.Won is not null).ToList();
        return Of(next.Count(n => n!.Won == true), next.Count);
    }

    private static Rate Buys(IReadOnlyList<RoundLine> lines, string mine, string theirs)
    {
        var rows = lines.Where(l => l.Won is not null && l.MyBuy == mine && l.TheirBuy == theirs).ToList();
        return Of(rows.Count(l => l.Won == true), rows.Count);
    }

    private static Rate Edge(IReadOnlyList<RoundLine> lines, bool ahead)
    {
        var rows = lines.Where(l => l.Won is not null && l.MyLoadout is not null && l.TheirLoadout is not null
            && (ahead ? l.MyLoadout - l.TheirLoadout >= LoadoutEdge : l.TheirLoadout - l.MyLoadout >= LoadoutEdge)).ToList();
        return Of(rows.Count(l => l.Won == true), rows.Count);
    }
}
