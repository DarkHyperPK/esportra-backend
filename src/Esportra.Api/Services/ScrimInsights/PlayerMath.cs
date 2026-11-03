namespace Esportra.Api.Services.ScrimInsights;

/// <summary>Per-player lines: Riot's totals with the derived rates next to them. Opponents are included for this one match only.</summary>
internal static class PlayerMath
{
    public static IReadOnlyList<PlayerLine> Lines(InMatch m, IReadOnlyDictionary<string, string> keys)
    {
        var teamDamage = m.Players.GroupBy(p => p.Side).ToDictionary(g => g.Key, g => g.Sum(p => p.Damage));
        var teamKills = m.Players.GroupBy(p => p.Side).ToDictionary(g => g.Key, g => g.Sum(p => p.Kills));
        return m.Players.OrderBy(p => keys[p.Puuid], new OpeningMath.KeyOrder()).Select(p =>
        {
            var rounds = p.Rounds > 0 ? p.Rounds : m.Rounds.Count;
            var shots = p.Head + p.Body + p.Leg;
            var received = m.Damage.Where(d => d.To == p.Puuid).Sum(d => d.Damage);
            return new PlayerLine(
                keys[p.Puuid], p.Name ?? "Unknown", p.Tag, p.Agent?.ToLowerInvariant(), p.Side == m.MySide, p.Slot, rounds,
                p.Kills, p.Deaths, p.Assists, InsightsCommon.Ratio(p.Score, rounds), InsightsCommon.Ratio(p.Damage, rounds),
                InsightsCommon.Pct(p.Head, shots), InsightsCommon.Pct(p.Body, shots), InsightsCommon.Pct(p.Leg, shots),
                p.Deaths > 0 || p.Kills > 0 ? Math.Round((double)p.Kills / Math.Max(p.Deaths, 1), 2) : null,
                KastMath.Kast(m, p), p.FirstKills, p.FirstDeaths, p.K2, p.K3, p.K4, p.K5, p.Plants, p.Defuses,
                p.Damage, received, InsightsCommon.Pct(p.Damage, teamDamage.GetValueOrDefault(p.Side)),
                InsightsCommon.Pct(p.Kills, teamKills.GetValueOrDefault(p.Side)), InsightsCommon.Ratio(p.Damage, p.Kills));
        }).ToList();
    }

    public static IReadOnlyList<AbilityLine> Abilities(InMatch m, IReadOnlyDictionary<string, string> keys) =>
        m.Players.OrderBy(p => keys[p.Puuid], new OpeningMath.KeyOrder()).Select(p =>
        {
            var parts = new[] { p.Grenade, p.Ability1, p.Ability2, p.Ultimate };
            var rounds = p.Rounds > 0 ? p.Rounds : m.Rounds.Count;
            return new AbilityLine(keys[p.Puuid], p.Grenade, p.Ability1, p.Ability2, p.Ultimate,
                parts.Any(x => x is not null) ? InsightsCommon.Ratio(parts.Sum(x => x ?? 0), rounds) : null);
        }).ToList();

    public static Objectives ObjectivesOf(InMatch m, IReadOnlyDictionary<string, string> keys)
    {
        bool MineSide(string? puuid) => InsightsCommon.SideOf(m, puuid) == m.MySide;
        var minePlants = m.Rounds.Where(r => r.Planter is not null && MineSide(r.Planter)).ToList();
        var theirPlants = m.Rounds.Where(r => r.Planter is not null && !MineSide(r.Planter)).ToList();
        var mineDefuses = m.Rounds.Where(r => r.Defuser is not null && MineSide(r.Defuser)).ToList();
        return new Objectives(
            m.Players.Where(p => p.Plants > 0 || p.Defuses > 0).OrderBy(p => keys[p.Puuid], new OpeningMath.KeyOrder())
                .Select(p => new PlantRow(keys[p.Puuid], p.Plants, p.Defuses)).ToList(),
            minePlants.Where(r => r.Site is not null).GroupBy(r => r.Site!).Select(g => new WeaponUse(g.Key, g.Count()))
                .OrderBy(s => s.Weapon, StringComparer.Ordinal).ToList(),
            InsightsCommon.Mean(minePlants.Where(r => r.PlantMs is not null).Select(r => r.PlantMs!.Value)),
            InsightsCommon.Mean(mineDefuses.Where(r => r.DefuseMs is not null).Select(r => r.DefuseMs!.Value)),
            InsightsCommon.RateOf(minePlants.Count(r => r.WinnerSide == m.MySide), minePlants.Count),
            InsightsCommon.RateOf(theirPlants.Count(r => r.WinnerSide == m.MySide), theirPlants.Count));
    }
}
