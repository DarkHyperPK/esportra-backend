using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

/// <summary>
/// Team and player aggregates over a team's verified scrim maps, built from the same per-map report the map page uses.
/// Round-weighted means; null when a denominator is zero; every rate carries its sample.
/// </summary>
public static class ScrimReportMath
{
    public const int MinMapsForMapHighlight = 3, MinMapsForPlayerHighlight = 3, MinRoundsForSideHighlight = 40, MinPistolRounds = 6, MinDuelsForOpeningHighlight = 8;

    private static double? Pct(int part, int whole) => whole > 0 ? Math.Round(100.0 * part / whole, 1) : null;
    private static Rate Rate(int count, int of) => new(count, of, Pct(count, of));
    private static bool Won(ReportMatch m) => m.Game.Result == "win";

    public static TeamReport Team(IReadOnlyList<ReportMatch> matches, bool capped)
    {
        var ordered = matches.OrderBy(m => m.Game.StartedAt ?? DateTime.MinValue).ToList();
        var series = ordered.Select((m, i) => Point(m, i + 1)).ToList();
        var rounds = ordered.SelectMany(m => m.Report.Rounds.Select(r => (Match: m, Round: r))).Where(x => x.Round.Won is not null).ToList();
        var mine = ordered.Sum(m => m.Game.RoundsWon);
        var theirs = ordered.Sum(m => m.Game.RoundsLost);
        var wins = ordered.Count(Won);

        var report = new TeamReport(
            new ReportSample(ordered.Count, ordered.FirstOrDefault()?.Game.StartedAt, ordered.LastOrDefault()?.Game.StartedAt, capped),
            wins, ordered.Count(m => m.Game.Result == "loss"), Pct(wins, ordered.Count), Pct(mine, mine + theirs), series, Maps(ordered),
            SideRate(rounds, "attack"), SideRate(rounds, "defense"),
            SumRates(ordered.Select(m => m.Report.Opening.FirstBloodRounds)), SumRates(ordered.Select(m => m.Report.Opening.WinAfterFirstBlood)),
            SumRates(ordered.Select(m => m.Report.Opening.WinAfterFirstDeath)), SumRates(ordered.Select(m => m.Report.Economy.PistolRounds)),
            Buys(ordered), Players(ordered), CoachTeamMath.Aggregate(ordered), []);
        return report with { Highlights = ReportHighlights.Team(report).Concat(CoachFindings.Build(report.Coach)).ToList() };
    }

    private static SeriesPoint Point(ReportMatch m, int index)
    {
        var mineRaw = m.Match.Players.Where(p => p.Side == m.Match.MySide).ToList();
        var rounds = mineRaw.Sum(p => p.Rounds);
        var firstBlood = m.Report.Opening.FirstBloodRounds;
        var pistol = m.Report.Economy.PistolRounds;
        return new SeriesPoint(
            index, m.Game.StartedAt, m.Game.Map, m.Game.OpponentName, m.Game.Result, m.Game.RoundsWon, m.Game.RoundsLost,
            Pct(m.Game.RoundsWon, m.Game.RoundsWon + m.Game.RoundsLost),
            rounds > 0 ? Math.Round((double)mineRaw.Sum(p => p.Score) / rounds, 1) : null,
            rounds > 0 ? Math.Round((double)mineRaw.Sum(p => p.Damage) / rounds, 1) : null,
            firstBlood.Pct, pistol.Of > 0 ? pistol.Count == pistol.Of : null);
    }

    private static IReadOnlyList<TeamMapRow> Maps(List<ReportMatch> matches) =>
        matches.Where(m => !string.IsNullOrEmpty(m.Game.Map)).GroupBy(m => m.Game.Map!).Select(g =>
        {
            var rw = g.Sum(m => m.Game.RoundsWon);
            var rl = g.Sum(m => m.Game.RoundsLost);
            var rounds = g.SelectMany(m => m.Report.Rounds.Select(r => (Match: m, Round: r))).Where(x => x.Round.Won is not null).ToList();
            return new TeamMapRow(g.Key, g.Count(), g.Count(Won), rw, rl, Pct(rw, rw + rl), SideRate(rounds, "attack"), SideRate(rounds, "defense"));
        }).OrderByDescending(r => r.Games).ThenBy(r => r.Map, StringComparer.Ordinal).ToList();

    private static Rate SideRate(List<(ReportMatch Match, RoundLine Round)> rounds, string role)
    {
        var side = rounds.Where(x => x.Round.MyRole == role).ToList();
        return Rate(side.Count(x => x.Round.Won == true), side.Count);
    }

    private static Rate SumRates(IEnumerable<Rate> rates)
    {
        var list = rates.ToList();
        return Rate(list.Sum(r => r.Count), list.Sum(r => r.Of));
    }

    private static IReadOnlyList<BuyAggregate> Buys(List<ReportMatch> matches) =>
        new[] { "pistol", "eco", "force", "full" }
            .Select(b => new BuyAggregate(b, SumRates(matches.SelectMany(m => m.Report.Economy.WinRateByBuy.Where(x => x.Buy == b).Select(x => x.RoundsWon)))))
            .Where(b => b.RoundsWon.Of > 0).ToList();

    public static IReadOnlyList<PlayerRow> Players(List<ReportMatch> matches)
    {
        var rows = new List<(Guid User, string? Name, ReportMatch Match, InPlayer Raw, PlayerLine Line)>();
        foreach (var m in matches)
        {
            var keys = InsightsCommon.Keys(m.Match);
            foreach (var p in m.Match.Players.Where(p => p.Side == m.Match.MySide))
            {
                if (!m.UsersByPuuid.TryGetValue(p.Puuid, out var user)) continue;
                var line = m.Report.Players.First(l => l.Key == keys[p.Puuid]);
                rows.Add((user.UserId, user.Name, m, p, line));
            }
        }
        return rows.GroupBy(r => r.User).Select(g => PlayerOf(g.Key, g.ToList())).OrderByDescending(p => p.Maps).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    internal static PlayerRow PlayerOf(Guid user, List<(Guid User, string? Name, ReportMatch Match, InPlayer Raw, PlayerLine Line)> items)
    {
        var rounds = items.Sum(i => i.Line.Rounds);
        var shots = items.Sum(i => i.Raw.Head + i.Raw.Body + i.Raw.Leg);
        var kills = items.Sum(i => i.Raw.Kills);
        var deaths = items.Sum(i => i.Raw.Deaths);
        var acsPerMap = items.Where(i => i.Line.Acs is not null).Select(i => i.Line.Acs!.Value).ToList();
        double? Weighted(Func<PlayerLine, double?> f) => rounds > 0 && items.All(i => f(i.Line) is not null) ? Math.Round(items.Sum(i => f(i.Line)!.Value * i.Line.Rounds) / rounds, 1) : null;
        return new PlayerRow(
            user, items.Select(i => i.Name).FirstOrDefault(n => n is not null), items.Count, rounds,
            Weighted(l => l.Acs), Weighted(l => l.Adr), items.Count == 0 ? null : Math.Round((double)kills / Math.Max(deaths, 1), 2), Weighted(l => l.Kast),
            Pct(items.Sum(i => i.Raw.Head), shots), kills, deaths, items.Sum(i => i.Raw.Assists), items.Sum(i => i.Raw.FirstKills), items.Sum(i => i.Raw.FirstDeaths),
            items.Sum(i => i.Raw.K2), items.Sum(i => i.Raw.K3), items.Sum(i => i.Raw.K4), items.Sum(i => i.Raw.K5), items.Sum(i => i.Raw.Plants), items.Sum(i => i.Raw.Defuses),
            acsPerMap.Count >= 2 ? Math.Round(Math.Sqrt(acsPerMap.Sum(a => Math.Pow(a - acsPerMap.Average(), 2)) / (acsPerMap.Count - 1)), 1) : null,
            items.Where(i => i.Raw.Agent is not null).GroupBy(i => i.Raw.Agent!.ToLowerInvariant()).Select(a => new AgentMaps(a.Key, a.Count())).OrderByDescending(a => a.Maps).ThenBy(a => a.Agent, StringComparer.Ordinal).ToList());
    }
}
