namespace Esportra.Api.Services.ScrimInsights;

/// <summary>Spending and loadouts for the viewer's team, from Riot's per-round economy, and how rounds went at each buy level.</summary>
internal static class EconomyMath
{
    public static Economy Build(InMatch m, IReadOnlyList<RoundLine> rounds)
    {
        var mineRounds = rounds.Where(r => r.MyBuy is not null && r.Won is not null).ToList();
        var byBuy = new[] { "pistol", "eco", "force", "full" }
            .Select(b => new BuyRow(b, InsightsCommon.RateOf(mineRounds.Count(r => r.MyBuy == b && r.Won == true), mineRounds.Count(r => r.MyBuy == b))))
            .Where(b => b.RoundsWon.Of > 0).ToList();
        var pistols = rounds.Where(r => r.Pistol && r.Won is not null).ToList();
        var myPuuids = m.Players.Where(p => p.Side == m.MySide).Select(p => p.Puuid).ToHashSet();
        var mine = m.Economy.Where(e => myPuuids.Contains(e.Puuid)).ToList();

        return new Economy(
            byBuy, InsightsCommon.RateOf(pistols.Count(r => r.Won == true), pistols.Count),
            Avg(rounds.Select(r => r.MyLoadout)), Avg(rounds.Select(r => r.TheirLoadout)),
            Avg(rounds.Where(r => r.Won == true).Select(r => r.MyLoadout)), Avg(rounds.Where(r => r.Won == false).Select(r => r.MyLoadout)),
            Frequency(mine.Select(e => e.Weapon)), Frequency(mine.Select(e => e.Armor)));
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(), 0);
    }

    /// <summary>How many player-rounds used each weapon/armor id. Riot asset ids: the page maps them to names or says "Unknown".</summary>
    private static IReadOnlyList<WeaponUse> Frequency(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).GroupBy(v => v!).Select(g => new WeaponUse(g.Key, g.Count()))
            .OrderByDescending(w => w.Rounds).ThenBy(w => w.Weapon, StringComparer.Ordinal).ToList();
}
