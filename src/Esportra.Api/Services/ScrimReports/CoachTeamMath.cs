using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

public sealed record ClutchVersus(int Versus, int Attempts, int Wins);

/// <summary>One roster player's decisive moments across the report's maps.</summary>
public sealed record CoachUserRow(
    Guid UserId, string? Name, int Maps, int Deaths, int Traded, int UntradedFirstDeaths, int TradeKills, int ClutchAttempts, int ClutchWins,
    int EntryDuels, int EntryWins, int MultiKillRounds, int MultiKillRoundsWon);

/// <summary>What decided rounds across maps: the per-map coach reads added up, plus a row per player.</summary>
public sealed record CoachTeam(
    TradeCounts Trades, Rate Led, Rate Trailed, IReadOnlyList<ClutchVersus> Clutches, Rate PostPlant, IReadOnlyList<SiteRate> PostPlantBySite,
    Rate NoPlantOnAttack, Rate Retake, IReadOnlyList<SiteRate> RetakeBySite, Rate AfterPistolWin, Rate AfterPistolLoss, Rate AntiEco, Rate EcoUpsets,
    Rate LoadoutAhead, Rate LoadoutBehind, double? AvgFirstKillMs, Rate SlowRounds, IReadOnlyList<CoachUserRow> Players);

public static class CoachTeamMath
{
    private static Rate Sum(IEnumerable<Rate> rates)
    {
        var list = rates.ToList();
        return InsightsCommon.RateOf(list.Sum(r => r.Count), list.Sum(r => r.Of));
    }

    private static IReadOnlyList<SiteRate> Sites(IEnumerable<IReadOnlyList<SiteRate>> perMap) =>
        perMap.SelectMany(s => s).GroupBy(s => s.Site).Select(g => new SiteRate(g.Key, Sum(g.Select(s => s.Won)))).OrderBy(s => s.Site, StringComparer.Ordinal).ToList();

    public static CoachTeam Aggregate(IReadOnlyList<ReportMatch> matches)
    {
        var c = matches.Select(m => m.Report.Coach).ToList();
        var firstKills = c.Where(x => x.AvgFirstKillMs is not null).ToList();
        return new CoachTeam(
            new TradeCounts(c.Sum(x => x.Trades.Deaths), c.Sum(x => x.Trades.Traded), c.Sum(x => x.Trades.TradeKills)),
            Sum(c.Select(x => x.LedRounds)), Sum(c.Select(x => x.TrailedRounds)),
            c.SelectMany(x => x.Clutches).GroupBy(l => l.Versus).Select(g => new ClutchVersus(g.Key, g.Count(), g.Count(l => l.Won))).OrderBy(v => v.Versus).ToList(),
            Sum(c.Select(x => x.PostPlant)), Sites(c.Select(x => x.PostPlantBySite)), Sum(c.Select(x => x.NoPlantOnAttack)), Sum(c.Select(x => x.Retake)), Sites(c.Select(x => x.RetakeBySite)),
            Sum(c.Select(x => x.AfterPistolWin)), Sum(c.Select(x => x.AfterPistolLoss)), Sum(c.Select(x => x.AntiEco)), Sum(c.Select(x => x.EcoUpsets)),
            Sum(c.Select(x => x.LoadoutAhead)), Sum(c.Select(x => x.LoadoutBehind)),
            firstKills.Count == 0 ? null : Math.Round(firstKills.Average(x => x.AvgFirstKillMs!.Value), 0), Sum(c.Select(x => x.SlowRounds)), Players(matches));
    }

    public static IReadOnlyList<CoachUserRow> Players(IReadOnlyList<ReportMatch> matches)
    {
        var rows = new List<(RosterUser User, CoachPlayer Line)>();
        foreach (var m in matches)
        {
            var keys = InsightsCommon.Keys(m.Match);
            foreach (var p in m.Match.Players.Where(p => p.Side == m.Match.MySide))
                if (m.UsersByPuuid.TryGetValue(p.Puuid, out var user) && m.Report.Coach.Players.FirstOrDefault(l => l.Key == keys[p.Puuid]) is { } line) rows.Add((user, line));
        }
        return rows.GroupBy(r => r.User.UserId).Select(g => new CoachUserRow(
            g.Key, g.Select(r => r.User.Name).FirstOrDefault(n => n is not null), g.Count(), g.Sum(r => r.Line.Deaths), g.Sum(r => r.Line.Traded), g.Sum(r => r.Line.UntradedFirstDeaths),
            g.Sum(r => r.Line.TradeKills), g.Sum(r => r.Line.ClutchAttempts), g.Sum(r => r.Line.ClutchWins), g.Sum(r => r.Line.EntryDuels), g.Sum(r => r.Line.EntryWins),
            g.Sum(r => r.Line.MultiKillRounds), g.Sum(r => r.Line.MultiKillRoundsWon))).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }
}
