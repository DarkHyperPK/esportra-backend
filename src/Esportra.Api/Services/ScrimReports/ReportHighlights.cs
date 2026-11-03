namespace Esportra.Api.Services.ScrimReports;

/// <summary>
/// What stands out in a team report. Every highlight is a rule over the report's own figures with its evidence in the text,
/// and none is shown from a thin sample. A highlight describes what happened, not why.
/// </summary>
public static class ReportHighlights
{
    public static IReadOnlyList<Highlight> Team(TeamReport r)
    {
        var list = new List<Highlight>();
        AddMaps(r, list);
        AddSides(r, list);
        AddPistol(r, list);
        AddPlayers(r, list);
        AddForm(r, list);
        return list;
    }

    private static void AddMaps(TeamReport r, List<Highlight> list)
    {
        var eligible = r.Maps.Where(m => m.Games >= ScrimReportMath.MinMapsForMapHighlight).ToList();
        if (eligible.Count == 0) return;
        var best = eligible.OrderByDescending(m => (double)m.Wins / m.Games).ThenByDescending(m => m.RoundsWon - m.RoundsLost).First();
        list.Add(new Highlight("best_map", "good", $"Best map: {best.Map}", $"{best.Wins}–{best.Games - best.Wins} over {best.Games} maps, rounds {best.RoundsWon}–{best.RoundsLost}.", null));
        var worst = eligible.Where(m => m.Map != best.Map).OrderBy(m => (double)m.Wins / m.Games).ThenBy(m => m.RoundsWon - m.RoundsLost).FirstOrDefault();
        if (worst is not null && (double)worst.Wins / worst.Games < 0.5)
            list.Add(new Highlight("weakest_map", "watch", $"Weakest map: {worst.Map}", $"{worst.Wins}–{worst.Games - worst.Wins} over {worst.Games} maps, rounds {worst.RoundsWon}–{worst.RoundsLost}.", null));
    }

    private static void AddSides(TeamReport r, List<Highlight> list)
    {
        if (r.Attack.Of < ScrimReportMath.MinRoundsForSideHighlight || r.Defense.Of < ScrimReportMath.MinRoundsForSideHighlight || r.Attack.Pct is null || r.Defense.Pct is null) return;
        var gap = r.Attack.Pct.Value - r.Defense.Pct.Value;
        if (Math.Abs(gap) < 15) return;
        var strong = gap > 0 ? "attack" : "defense";
        var weak = gap > 0 ? "defense" : "attack";
        list.Add(new Highlight("side_gap", "watch", $"Stronger on {strong} than {weak}",
            $"Attack {r.Attack.Pct}% ({r.Attack.Count} of {r.Attack.Of}) against defense {r.Defense.Pct}% ({r.Defense.Count} of {r.Defense.Of}).", null));
    }

    private static void AddPistol(TeamReport r, List<Highlight> list)
    {
        if (r.Pistol.Of < ScrimReportMath.MinPistolRounds || r.Pistol.Pct is null) return;
        if (r.Pistol.Pct >= 60) list.Add(new Highlight("pistol", "good", "Strong pistol rounds", $"{r.Pistol.Count} of {r.Pistol.Of} won ({r.Pistol.Pct}%).", null));
        else if (r.Pistol.Pct <= 40) list.Add(new Highlight("pistol", "watch", "Weak pistol rounds", $"{r.Pistol.Count} of {r.Pistol.Of} won ({r.Pistol.Pct}%).", null));
    }

    private static void AddPlayers(TeamReport r, List<Highlight> list)
    {
        var eligible = r.Players.Where(p => p.Maps >= ScrimReportMath.MinMapsForPlayerHighlight).ToList();
        var fragger = eligible.Where(p => p.Acs is not null).OrderByDescending(p => p.Acs).FirstOrDefault();
        if (fragger is not null) list.Add(new Highlight("top_acs", "good", $"Highest ACS: {fragger.Name ?? "a player"}", $"{fragger.Acs} ACS over {fragger.Maps} maps.", fragger.UserId));
        var opener = eligible.Where(p => p.FirstKills + p.FirstDeaths >= ScrimReportMath.MinDuelsForOpeningHighlight)
            .OrderByDescending(p => (double)p.FirstKills / (p.FirstKills + p.FirstDeaths)).FirstOrDefault();
        if (opener is not null)
            list.Add(new Highlight("best_opener", "good", $"Best opening duels: {opener.Name ?? "a player"}", $"{opener.FirstKills} first kills, {opener.FirstDeaths} first deaths over {opener.Maps} maps.", opener.UserId));
        var risky = eligible.Where(p => p.FirstKills + p.FirstDeaths >= ScrimReportMath.MinDuelsForOpeningHighlight)
            .OrderBy(p => (double)p.FirstKills / (p.FirstKills + p.FirstDeaths)).FirstOrDefault();
        if (risky is not null && risky.UserId != opener?.UserId && (double)risky.FirstKills / (risky.FirstKills + risky.FirstDeaths) < 0.4)
            list.Add(new Highlight("opening_deaths", "watch", $"Loses most opening duels: {risky.Name ?? "a player"}", $"{risky.FirstKills} first kills against {risky.FirstDeaths} first deaths over {risky.Maps} maps.", risky.UserId));
    }

    private static void AddForm(TeamReport r, List<Highlight> list)
    {
        if (r.Series.Count < 10) return;
        var last = r.Series.TakeLast(5).ToList();
        var before = r.Series.Skip(r.Series.Count - 10).Take(5).ToList();
        double Rounds(List<SeriesPoint> s) => 100.0 * s.Sum(p => p.RoundsWon) / Math.Max(1, s.Sum(p => p.RoundsWon + p.RoundsLost));
        var delta = Math.Round(Rounds(last) - Rounds(before), 1);
        if (Math.Abs(delta) < 5) return;
        list.Add(new Highlight("form", delta > 0 ? "good" : "watch", delta > 0 ? "Rounds are going better lately" : "Rounds are going worse lately",
            $"Round win rate over the last 5 maps is {Math.Abs(delta)} points {(delta > 0 ? "higher" : "lower")} than over the 5 before.", null));
    }
}
