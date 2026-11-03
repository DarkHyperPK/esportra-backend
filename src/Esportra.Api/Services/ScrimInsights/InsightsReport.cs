namespace Esportra.Api.Services.ScrimInsights;

/// <summary>A figure with the sample it comes from, so the page can say "3 of 5 duels" instead of a bare percentage.</summary>
public sealed record Rate(int Count, int Of, double? Pct);

public sealed record PlayerLine(
    string Key, string Name, string? Tag, string? Agent, bool Mine, int? Slot, int Rounds,
    int Kills, int Deaths, int Assists, double? Acs, double? Adr, double? HsPct, double? BodyPct, double? LegPct,
    double? Kd, double? Kast, int FirstKills, int FirstDeaths, int K2, int K3, int K4, int K5, int Plants, int Defuses,
    int Damage, int DamageReceived, double? DamageShare, double? KillShare, double? DamagePerKill);

public sealed record RoundLine(
    int Num, bool? Won, string? MyRole, string? ResultCode, string? PlantSite, int? PlantMs, bool Defused, int? DefuseMs,
    string? FirstKiller, string? FirstVictim, bool? FirstBloodMine, int? FirstBloodMs,
    int MyScore, int TheirScore, double? MyLoadout, double? TheirLoadout, string? MyBuy, string? TheirBuy, bool Pistol);

public sealed record Streaks(int LongestWin, int LongestLoss);

public sealed record Halves(Rate First, Rate Second, Rate Overtime);

public sealed record OpeningPlayer(string Key, int Kills, int Deaths, Rate DuelsWon, double? AvgMsToFirstKill, double? AvgMsToFirstDeath);

public sealed record Opening(
    IReadOnlyList<OpeningPlayer> Players, Rate WinAfterFirstBlood, Rate WinAfterFirstDeath, Rate FirstBloodRounds,
    Rate AttackFirstBlood, Rate DefenseFirstBlood);

public sealed record KillLine(int Round, int Ordinal, int? RoundMs, string? Killer, string Victim, string[] Assistants, bool KillerMine, string? Weapon, int? X, int? Y);

public sealed record BuyRow(string Buy, Rate RoundsWon);

public sealed record WeaponUse(string Weapon, int Rounds);

public sealed record Economy(
    IReadOnlyList<BuyRow> WinRateByBuy, Rate PistolRounds, double? AvgLoadoutMine, double? AvgLoadoutTheirs,
    double? AvgLoadoutInWonRounds, double? AvgLoadoutInLostRounds, IReadOnlyList<WeaponUse> Weapons, IReadOnlyList<WeaponUse> Armor);

public sealed record AbilityLine(string Key, int? Grenade, int? Ability1, int? Ability2, int? Ultimate, double? PerRound);

public sealed record PlantRow(string Key, int Plants, int Defuses);

public sealed record Objectives(
    IReadOnlyList<PlantRow> ByPlayer, IReadOnlyList<WeaponUse> Sites, double? AvgPlantMs, double? AvgDefuseMs, Rate WinWhenPlanting, Rate WinWhenOpponentPlants);

public sealed record Formula(string Id, string Text);

public sealed record InsightsReport(
    IReadOnlyList<PlayerLine> Players, IReadOnlyList<RoundLine> Rounds, Streaks Streaks, Halves Halves, Opening Opening,
    IReadOnlyList<KillLine> Kills, Economy Economy, IReadOnlyList<AbilityLine> Abilities, Objectives Objectives, IReadOnlyList<Formula> Formulas);
