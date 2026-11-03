using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

public sealed record TrendPoint(
    int Index, DateTime? Date, string? Map, string Result, double? RoundWinPct, double? TeamAcs, double? FirstKillPct, double? TradedPct, double? RollingRoundWinPct, double? RollingAcs);

/// <summary>One figure in two windows. Verdict is "up", "down" or "flat": flat when the change is within the noise threshold for that figure.</summary>
public sealed record TrendMetric(string Key, string Label, double? Current, double? Previous, double? Change, string Verdict);

public sealed record TrendWindow(string Label, bool Enough, int CurrentMaps, int PreviousMaps, IReadOnlyList<TrendMetric> Metrics);

public sealed record Trends(ReportSample Sample, IReadOnlyList<TrendPoint> Series, IReadOnlyList<TrendWindow> Windows);

/// <summary>
/// How the team is moving: per-map series with trailing means, and the latest maps against the ones before. Reuses the team report for each window, so the
/// figures mean exactly what they mean there. A comparison needs enough maps in both windows, and a change inside the noise threshold reads "flat".
/// </summary>
public static class TrendsMath
{
    private const int RollingMaps = 5, MinRolling = 3;

    private static readonly (string Key, string Label, double Noise)[] Metrics =
    [
        ("round_win", "Round win rate (%)", 5), ("acs", "Team ACS", 10), ("first_kill", "Rounds you got the first kill (%)", 8),
        ("traded", "Deaths avenged within 5 s (%)", 8), ("post_plant", "Won when you planted (%)", 10), ("pistol", "Pistol rounds won (%)", 15),
    ];

    private static double? TeamAcs(ReportMatch m)
    {
        var mine = m.Match.Players.Where(p => p.Side == m.Match.MySide).ToList();
        var rounds = mine.Sum(p => p.Rounds);
        return rounds > 0 ? Math.Round((double)mine.Sum(p => p.Score) / rounds, 1) : null;
    }

    private static double? Pct(int part, int whole) => whole > 0 ? Math.Round(100.0 * part / whole, 1) : null;

    public static Trends Build(IReadOnlyList<ReportMatch> matches, bool capped, DateTime now)
    {
        var ordered = matches.OrderBy(m => m.Game.StartedAt ?? DateTime.MinValue).ToList();
        var points = new List<TrendPoint>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            var trailing = ordered.Skip(Math.Max(0, i - RollingMaps + 1)).Take(Math.Min(i + 1, RollingMaps)).ToList();
            double? Mean(Func<ReportMatch, double?> f) => trailing.Count >= MinRolling && trailing.All(t => f(t) is not null) ? Math.Round(trailing.Average(t => f(t)!.Value), 1) : null;
            double? RoundWin(ReportMatch x) => Pct(x.Game.RoundsWon, x.Game.RoundsWon + x.Game.RoundsLost);
            points.Add(new TrendPoint(i + 1, m.Game.StartedAt, m.Game.Map, m.Game.Result, RoundWin(m), TeamAcs(m), m.Report.Opening.FirstBloodRounds.Pct,
                Pct(m.Report.Coach.Trades.Traded, m.Report.Coach.Trades.Deaths), Mean(RoundWin), Mean(TeamAcs)));
        }
        var windows = new List<TrendWindow>
        {
            Window("Last 5 maps against the 5 before", ordered.TakeLast(5).ToList(), ordered.Skip(Math.Max(0, ordered.Count - 10)).Take(Math.Max(0, Math.Min(5, ordered.Count - 5))).ToList(), 5),
            Window("Last 10 maps against the 10 before", ordered.TakeLast(10).ToList(), ordered.Skip(Math.Max(0, ordered.Count - 20)).Take(Math.Max(0, Math.Min(10, ordered.Count - 10))).ToList(), 10),
            Window("This month against last month", InMonth(ordered, now, 0), InMonth(ordered, now, -1), 3),
        };
        return new Trends(new ReportSample(ordered.Count, ordered.FirstOrDefault()?.Game.StartedAt, ordered.LastOrDefault()?.Game.StartedAt, capped), points, windows);
    }

    private static List<ReportMatch> InMonth(List<ReportMatch> all, DateTime now, int offset)
    {
        var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(offset);
        return all.Where(m => m.Game.StartedAt is { } t && t >= start && t < start.AddMonths(1)).ToList();
    }

    private static TrendWindow Window(string label, List<ReportMatch> current, List<ReportMatch> previous, int needed)
    {
        var enough = current.Count >= needed && previous.Count >= needed;
        if (!enough) return new TrendWindow(label, false, current.Count, previous.Count, []);
        var a = Figures(current);
        var b = Figures(previous);
        var metrics = Metrics.Select(m =>
        {
            var (cur, prev) = (a[m.Key], b[m.Key]);
            var change = cur is not null && prev is not null ? Math.Round(cur.Value - prev.Value, 1) : (double?)null;
            var verdict = change is null ? "flat" : Math.Abs(change.Value) < m.Noise ? "flat" : change > 0 ? "up" : "down";
            return new TrendMetric(m.Key, m.Label, cur, prev, change, verdict);
        }).ToList();
        return new TrendWindow(label, true, current.Count, previous.Count, metrics);
    }

    private static Dictionary<string, double?> Figures(List<ReportMatch> window)
    {
        var t = ScrimReportMath.Team(window, false);
        var rounds = window.Sum(m => m.Match.Players.Where(p => p.Side == m.Match.MySide).Sum(p => p.Rounds));
        var score = window.Sum(m => m.Match.Players.Where(p => p.Side == m.Match.MySide).Sum(p => p.Score));
        return new Dictionary<string, double?>
        {
            ["round_win"] = t.RoundWinPct,
            ["acs"] = rounds > 0 ? Math.Round((double)score / rounds, 1) : null,
            ["first_kill"] = t.FirstBloodRounds.Pct,
            ["traded"] = Pct(t.Coach.Trades.Traded, t.Coach.Trades.Deaths),
            ["post_plant"] = t.Coach.PostPlant.Pct,
            ["pistol"] = t.Pistol.Pct,
        };
    }
}
