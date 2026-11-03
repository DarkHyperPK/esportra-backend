using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

public sealed record PlayerMapPoint(
    int Index, DateTime? Date, string? Map, string? Agent, string Result, int Rounds, double? Acs, double? Adr, int Kills, int Deaths, int Assists, double? Kd, double? Kast,
    double? HsPct, int FirstKills, int FirstDeaths);

public sealed record PlayerSlice(string Key, int Maps, int Wins, double? WinRatePct, double? Acs, double? Kd, int Rounds);

public sealed record HitShare(int Head, int Body, int Leg, double? HeadPct, double? BodyPct, double? LegPct);

public sealed record PlayerComparison(double? TeamAcs, double? AcsVsTeam, double? Last5Acs, double? Previous5Acs, double? AcsChange);

public sealed record PlayerReport(
    ReportSample Sample, PlayerRow Summary, IReadOnlyList<PlayerMapPoint> Series, IReadOnlyList<PlayerSlice> ByMap, IReadOnlyList<PlayerSlice> ByAgent,
    HitShare Hits, PlayerComparison Comparison, IReadOnlyList<Highlight> Highlights);

/// <summary>One player across the team's verified maps: the same aggregates as a row of the team report, plus their series, splits and how they compare with the team.</summary>
public static class PlayerReportMath
{
    private static double? Pct(int part, int whole) => whole > 0 ? Math.Round(100.0 * part / whole, 1) : null;

    public static PlayerReport? Build(Guid userId, IReadOnlyList<ReportMatch> matches, bool capped)
    {
        var ordered = matches.OrderBy(m => m.Game.StartedAt ?? DateTime.MinValue).ToList();
        var items = new List<(Guid User, string? Name, ReportMatch Match, InPlayer Raw, PlayerLine Line)>();
        foreach (var m in ordered)
        {
            var keys = InsightsCommon.Keys(m.Match);
            var raw = m.Match.Players.FirstOrDefault(p => p.Side == m.Match.MySide && m.UsersByPuuid.TryGetValue(p.Puuid, out var u) && u.UserId == userId);
            if (raw is null) continue;
            items.Add((userId, m.UsersByPuuid[raw.Puuid].Name, m, raw, m.Report.Players.First(l => l.Key == keys[raw.Puuid])));
        }
        if (items.Count == 0) return null;

        var summary = ScrimReportMath.PlayerOf(userId, items);
        var series = items.Select((i, n) => new PlayerMapPoint(n + 1, i.Match.Game.StartedAt, i.Match.Game.Map, i.Raw.Agent?.ToLowerInvariant(), i.Match.Game.Result, i.Line.Rounds,
            i.Line.Acs, i.Line.Adr, i.Raw.Kills, i.Raw.Deaths, i.Raw.Assists, i.Line.Kd, i.Line.Kast, i.Line.HsPct, i.Raw.FirstKills, i.Raw.FirstDeaths)).ToList();
        var head = items.Sum(i => i.Raw.Head); var body = items.Sum(i => i.Raw.Body); var leg = items.Sum(i => i.Raw.Leg); var shots = head + body + leg;
        var comparison = Compare(series, ordered, items);
        var report = new PlayerReport(
            new ReportSample(items.Count, items[0].Match.Game.StartedAt, items[^1].Match.Game.StartedAt, capped), summary, series,
            Slices(items, i => i.Match.Game.Map), Slices(items, i => i.Raw.Agent?.ToLowerInvariant()),
            new HitShare(head, body, leg, Pct(head, shots), Pct(body, shots), Pct(leg, shots)), comparison, []);
        return report with { Highlights = Highlights(report) };
    }

    private static IReadOnlyList<PlayerSlice> Slices(List<(Guid User, string? Name, ReportMatch Match, InPlayer Raw, PlayerLine Line)> items, Func<(Guid User, string? Name, ReportMatch Match, InPlayer Raw, PlayerLine Line), string?> key) =>
        items.Where(i => !string.IsNullOrEmpty(key(i))).GroupBy(key).Select(g =>
        {
            var rounds = g.Sum(i => i.Line.Rounds);
            var kills = g.Sum(i => i.Raw.Kills); var deaths = g.Sum(i => i.Raw.Deaths);
            var wins = g.Count(i => i.Match.Game.Result == "win");
            var acs = rounds > 0 && g.All(i => i.Line.Acs is not null) ? Math.Round(g.Sum(i => i.Line.Acs!.Value * i.Line.Rounds) / rounds, 1) : (double?)null;
            return new PlayerSlice(g.Key!, g.Count(), wins, Pct(wins, g.Count()), acs, Math.Round((double)kills / Math.Max(deaths, 1), 2), rounds);
        }).OrderByDescending(s => s.Maps).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();

    private static PlayerComparison Compare(List<PlayerMapPoint> series, List<ReportMatch> all, List<(Guid User, string? Name, ReportMatch Match, InPlayer Raw, PlayerLine Line)> items)
    {
        var teamLines = all.SelectMany(m => m.Report.Players.Where(p => p.Mine)).ToList();
        var teamRounds = teamLines.Sum(p => p.Rounds);
        double? teamAcs = teamRounds > 0 && teamLines.All(p => p.Acs is not null) ? Math.Round(teamLines.Sum(p => p.Acs!.Value * p.Rounds) / teamRounds, 1) : null;
        double? Window(IEnumerable<PlayerMapPoint> s)
        {
            var l = s.Where(p => p.Acs is not null && p.Rounds > 0).ToList();
            var r = l.Sum(p => p.Rounds);
            return r > 0 ? Math.Round(l.Sum(p => p.Acs!.Value * p.Rounds) / r, 1) : null;
        }
        var last = series.Count >= 10 ? Window(series.TakeLast(5)) : null;
        var prev = series.Count >= 10 ? Window(series.Skip(series.Count - 10).Take(5)) : null;
        var overall = Window(series);
        return new PlayerComparison(teamAcs, overall is not null && teamAcs is not null ? Math.Round(overall.Value - teamAcs.Value, 1) : null, last, prev,
            last is not null && prev is not null ? Math.Round(last.Value - prev.Value, 1) : null);
    }

    private static IReadOnlyList<Highlight> Highlights(PlayerReport r)
    {
        var list = new List<Highlight>();
        var id = r.Summary.UserId;
        var maps = r.ByMap.Where(s => s.Maps >= ScrimReportMath.MinMapsForMapHighlight && s.Acs is not null).ToList();
        if (maps.Count >= 2 && maps.Max(s => s.Acs) != maps.Min(s => s.Acs))
        {
            var best = maps.OrderByDescending(s => s.Acs).First();
            var worst = maps.OrderBy(s => s.Acs).First();
            list.Add(new Highlight("best_map", "good", $"Best map: {best.Key}", $"{best.Acs} ACS over {best.Maps} maps.", id));
            if (worst.Key != best.Key) list.Add(new Highlight("weakest_map", "watch", $"Lowest ACS on {worst.Key}", $"{worst.Acs} ACS over {worst.Maps} maps.", id));
        }
        if (r.Comparison.AcsChange is { } change && Math.Abs(change) >= 15)
            list.Add(new Highlight("form", change > 0 ? "good" : "watch", change > 0 ? "ACS is up lately" : "ACS is down lately",
                $"Last 5 maps {r.Comparison.Last5Acs} ACS against {r.Comparison.Previous5Acs} over the 5 before.", id));
        if (r.Summary.FirstKills + r.Summary.FirstDeaths >= ScrimReportMath.MinDuelsForOpeningHighlight)
        {
            var share = (double)r.Summary.FirstKills / (r.Summary.FirstKills + r.Summary.FirstDeaths);
            if (share >= 0.6) list.Add(new Highlight("opener", "good", "Wins opening duels", $"{r.Summary.FirstKills} first kills, {r.Summary.FirstDeaths} first deaths.", id));
            else if (share <= 0.4) list.Add(new Highlight("opening_deaths", "watch", "Loses opening duels", $"{r.Summary.FirstKills} first kills, {r.Summary.FirstDeaths} first deaths.", id));
        }
        if (r.Summary.AcsSd is { } sd && r.Summary.Acs is { } acs && acs > 0 && r.Series.Count >= 5)
            list.Add(new Highlight("consistency", sd / acs <= 0.2 ? "good" : "neutral", sd / acs <= 0.2 ? "Consistent from map to map" : "Varies a lot from map to map",
                $"ACS varies by about {sd} between maps (average {acs}).", id));
        return list;
    }
}
