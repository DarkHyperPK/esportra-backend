namespace Esportra.Api.Services.ScrimInsights;

/// <summary>One player as Riot recorded them in the match (both teams). Internal: the PUUID never leaves the server.</summary>
public sealed record InPlayer(
    string Puuid, string Side, string? Name, string? Tag, string? Agent, int? Slot,
    int Score, int Rounds, int Kills, int Deaths, int Assists, int Damage, int Head, int Body, int Leg,
    int FirstKills, int FirstDeaths, int K2, int K3, int K4, int K5, int Plants, int Defuses,
    int? Grenade, int? Ability1, int? Ability2, int? Ultimate);

public sealed record InRound(
    int Num, string? WinnerSide, string? WinnerRole, string? ResultCode, string? Planter, string? Defuser,
    int? PlantMs, string? Site, int? PlantX, int? PlantY, int? DefuseMs, int? DefuseX, int? DefuseY);

public sealed record InKill(int Round, int Ordinal, string? Killer, string Victim, string[] Assistants, int? RoundMs, int? X, int? Y, string? Item, string? Type = null);

public sealed record InEcon(int Round, string Puuid, int? Loadout, string? Weapon, string? Armor, int? Spent, int? Remaining);

public sealed record InDamage(int Round, string From, string To, int Damage, int Head, int Body, int Leg);

/// <summary>Everything Riot recorded about one match, from the normalized tables, plus which Riot side the viewer's team played.</summary>
public sealed record InMatch(
    string MySide, IReadOnlyList<InPlayer> Players, IReadOnlyList<InRound> Rounds, IReadOnlyList<InKill> Kills,
    IReadOnlyList<InEcon> Economy, IReadOnlyList<InDamage> Damage)
{
    public string TheirSide => MySide == "Blue" ? "Red" : "Blue";
}
