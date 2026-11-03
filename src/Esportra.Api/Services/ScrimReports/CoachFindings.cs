using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

/// <summary>
/// What the numbers say a team should look at, as plain statements with their evidence. Each is a rule over the coach read with a minimum sample,
/// so a thin sample says nothing rather than something wrong. These describe what happened in the rounds, not why.
/// </summary>
public static class CoachFindings
{
    private static string P(Rate r) => $"{r.Count} of {r.Of} ({r.Pct}%)";

    public static IReadOnlyList<Highlight> Build(CoachTeam c)
    {
        var list = new List<Highlight>();
        Plants(c, list);
        Trades(c, list);
        Leads(c, list);
        Economy(c, list);
        Clutches(c, list);
        Entries(c, list);
        Tempo(c, list);
        return list;
    }

    /// <summary>The findings that are about one player: untraded opening deaths, closing clutches, winning or losing opening duels.</summary>
    public static IReadOnlyList<Highlight> ForPlayer(CoachUserRow? player)
    {
        if (player is null) return [];
        var none = new Rate(0, 0, null);
        var alone = new CoachTeam(new TradeCounts(0, 0, 0), none, none, [], none, [], none, none, [], none, none, none, none, none, none, null, none, [player]);
        return Build(alone).Where(f => f.PlayerId == player.UserId).ToList();
    }

    private static Highlight F(string kind, string tone, string title, string detail, Guid? who = null) => new(kind, tone, title, detail, who);

    private static void Plants(CoachTeam c, List<Highlight> l)
    {
        if (c.PostPlant is { Of: >= 8, Pct: { } post })
            l.Add(post <= 50 ? F("post_plant", "watch", "Post-plant rounds are slipping away", $"You won {P(c.PostPlant)} of the rounds where you planted.")
                : post >= 70 ? F("post_plant", "good", "Planting wins rounds", $"You won {P(c.PostPlant)} of the rounds where you planted.") : F("post_plant", "neutral", "Post-plant is average", $"You won {P(c.PostPlant)} of the rounds where you planted."));
        if (c.Retake is { Of: >= 8, Pct: { } rt })
            l.Add(rt <= 25 ? F("retake", "watch", "Retakes rarely work", $"When the opponent planted you won {P(c.Retake)}.") : rt >= 45 ? F("retake", "good", "Retakes work", $"When the opponent planted you won {P(c.Retake)}.") : F("retake", "neutral", "Retakes are about average", $"When the opponent planted you won {P(c.Retake)}."));
        if (c.NoPlantOnAttack is { Of: >= 8, Pct: { } np } && np <= 25)
            l.Add(F("no_plant", "watch", "Attack rounds that never reach a plant are lost", $"You won {P(c.NoPlantOnAttack)} of the attack rounds without a plant."));
    }

    private static void Trades(CoachTeam c, List<Highlight> l)
    {
        if (c.Trades.Deaths >= 20)
        {
            var pct = Math.Round(100.0 * c.Trades.Traded / c.Trades.Deaths, 1);
            if (pct <= 35) l.Add(F("trades", "watch", "Deaths are not being traded", $"Only {c.Trades.Traded} of {c.Trades.Deaths} deaths ({pct}%) were avenged within 5 seconds: players are dying alone."));
            else if (pct >= 55) l.Add(F("trades", "good", "Teammates trade each other", $"{c.Trades.Traded} of {c.Trades.Deaths} deaths ({pct}%) were avenged within 5 seconds."));
        }
        var worst = c.Players.Where(p => p.UntradedFirstDeaths >= 4).OrderByDescending(p => p.UntradedFirstDeaths).FirstOrDefault();
        if (worst is not null)
            l.Add(F("untraded_openers", "watch", $"{worst.Name ?? "A player"}'s opening deaths go untraded", $"{worst.UntradedFirstDeaths} times the first death of a round was not avenged: check spacing around their entries.", worst.UserId));
    }

    private static void Leads(CoachTeam c, List<Highlight> l)
    {
        if (c.Led is { Of: >= 10, Pct: { } led } && led <= 75)
            l.Add(F("thrown_leads", "watch", "Player advantages are being thrown", $"You won {P(c.Led)} of the rounds where you were ahead on players at some point."));
        if (c.Trailed is { Of: >= 10, Pct: { } tr } && tr >= 30)
            l.Add(F("comebacks", "good", "You come back from player disadvantages", $"You won {P(c.Trailed)} of the rounds where you were behind on players at some point."));
    }

    private static void Economy(CoachTeam c, List<Highlight> l)
    {
        if (c.AfterPistolWin is { Of: >= 4, Pct: { } b } && b <= 40)
            l.Add(F("bonus", "watch", "Pistol wins are not converted", $"You won {P(c.AfterPistolWin)} of the rounds after winning a pistol round."));
        if (c.AntiEco is { Of: >= 4, Pct: { } a } && a <= 70)
            l.Add(F("anti_eco", "watch", "Full buys are dropping rounds to eco rounds", $"Against an eco you won {P(c.AntiEco)} of your full-buy rounds."));
        if (c.EcoUpsets is { Of: >= 4, Pct: { } e } && e >= 30)
            l.Add(F("eco_upsets", "good", "Eco rounds steal rounds", $"You won {P(c.EcoUpsets)} of the rounds you played on an eco against a full buy."));
    }

    private static void Clutches(CoachTeam c, List<Highlight> l)
    {
        var attempts = c.Clutches.Sum(v => v.Attempts);
        var wins = c.Clutches.Sum(v => v.Wins);
        if (attempts >= 8)
        {
            var pct = Math.Round(100.0 * wins / attempts, 1);
            if (pct >= 40) l.Add(F("clutches", "good", "Clutches are being closed", $"{wins} of {attempts} clutch situations won ({pct}%)."));
            else if (pct <= 15) l.Add(F("clutches", "watch", "Clutch situations are rarely won", $"{wins} of {attempts} clutch situations won ({pct}%)."));
        }
        var closer = c.Players.Where(p => p.ClutchAttempts >= 4 && p.ClutchWins * 2 >= p.ClutchAttempts).OrderByDescending(p => p.ClutchWins).FirstOrDefault();
        if (closer is not null) l.Add(F("closer", "good", $"{closer.Name ?? "A player"} closes clutches", $"{closer.ClutchWins} of {closer.ClutchAttempts} clutch situations won.", closer.UserId));
    }

    private static void Entries(CoachTeam c, List<Highlight> l)
    {
        foreach (var p in c.Players.Where(p => p.EntryDuels >= 12))
        {
            var pct = Math.Round(100.0 * p.EntryWins / p.EntryDuels, 1);
            if (pct >= 60) l.Add(F("entry_good", "good", $"{p.Name ?? "A player"} wins opening duels", $"{p.EntryWins} of {p.EntryDuels} opening duels won ({pct}%).", p.UserId));
            else if (pct <= 40) l.Add(F("entry_bad", "watch", $"{p.Name ?? "A player"} loses opening duels", $"{p.EntryWins} of {p.EntryDuels} opening duels won ({pct}%).", p.UserId));
        }
    }

    private static void Tempo(CoachTeam c, List<Highlight> l)
    {
        if (c.SlowRounds is not { Of: >= 8, Pct: { } s }) return;
        if (s >= 60) l.Add(F("slow_rounds", "good", "Slow rounds favour you", $"You won {P(c.SlowRounds)} of the rounds where the first kill came after 30 seconds."));
        else if (s <= 35) l.Add(F("slow_rounds", "watch", "Slow rounds go against you", $"You won {P(c.SlowRounds)} of the rounds where the first kill came after 30 seconds."));
    }
}
