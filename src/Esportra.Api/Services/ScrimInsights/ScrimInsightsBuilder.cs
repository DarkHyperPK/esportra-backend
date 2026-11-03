namespace Esportra.Api.Services.ScrimInsights;

/// <summary>Turns one match's Riot records into the per-map report. Pure: no database, no clock, nothing stored.</summary>
public static class ScrimInsightsBuilder
{
    public static readonly IReadOnlyList<Formula> Formulas =
    [
        new("acs", "ACS = score ÷ rounds played."),
        new("adr", "ADR = damage dealt ÷ rounds played."),
        new("kd", "K/D = kills ÷ max(deaths, 1)."),
        new("hs", "Headshot / body / leg share = that hit type ÷ all located hits. Not aim accuracy."),
        new("kast", "KAST = rounds with a kill, an assist, survival, or a trade (killer killed by a teammate within 5 s) ÷ rounds."),
        new("opening", "Opening duel = the first kill of a round. Duels won = opening kills ÷ (opening kills + opening deaths)."),
        new("buy", $"Buy = viewer's average loadout value per player: eco ≤ {RoundMath.EcoMax}, full ≥ {RoundMath.FullBuyMin}, otherwise force. Rounds 1 and 13 are pistol rounds."),
        new("share", "Damage / kill share = the player's total ÷ their team's total in this match."),
        new("abilities", "Ability figures are Riot's per-match cast totals; Riot does not give casts per round."),
    ];

    public static InsightsReport Build(InMatch input)
    {
        var m = InsightsCommon.OneBased(input);
        var keys = InsightsCommon.Keys(m);
        var rounds = RoundMath.Lines(m, keys);
        var kills = m.Kills.OrderBy(k => k.Round).ThenBy(k => k.RoundMs ?? int.MaxValue).ThenBy(k => k.Ordinal)
            .Select(k => new KillLine(
                k.Round, k.Ordinal, k.RoundMs, k.Killer is null ? null : keys.GetValueOrDefault(k.Killer), keys.GetValueOrDefault(k.Victim) ?? "?",
                k.Assistants.Select(a => keys.GetValueOrDefault(a) ?? "?").ToArray(), InsightsCommon.SideOf(m, k.Killer) == m.MySide, k.Item, k.Type, k.X, k.Y)).ToList();
        return new InsightsReport(
            PlayerMath.Lines(m, keys), rounds, RoundMath.StreaksOf(rounds), RoundMath.HalvesOf(rounds), OpeningMath.Build(m, keys),
            kills, EconomyMath.Build(m, rounds), PlayerMath.Abilities(m, keys), PlayerMath.ObjectivesOf(m, keys), Formulas);
    }

    /// <summary>The key for a PUUID, for callers that need to point at a player (the endpoint's name table).</summary>
    public static IReadOnlyDictionary<string, string> KeysFor(InMatch m) => InsightsCommon.Keys(m);
}
