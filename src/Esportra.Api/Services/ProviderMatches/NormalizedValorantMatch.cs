namespace Esportra.Api.Services.ProviderMatches;

public sealed record NormalizedMatchInfo(
    string ProviderMatchId, string? MapId, string? QueueId, bool IsCustom, string? GameMode, DateTimeOffset? StartedAt,
    int? LengthMs, string? GameVersion, string? SeasonId, bool IsCompleted, string? Region);

public sealed record NormalizedTeam(string TeamId, bool? Won, int? RoundsPlayed, int? RoundsWon, int? NumPoints);

public sealed record NormalizedPlayer(
    string Puuid, string RiotSide, string? GameName, string? TagLine, string? PartyId, string? CharacterId,
    int? CompetitiveTier, bool IsObserver, int Score, int RoundsPlayed, int Kills, int Deaths, int Assists,
    int? PlaytimeMs, int? GrenadeCasts, int? Ability1Casts, int? Ability2Casts, int? UltimateCasts,
    int DamageDealt, int Headshots, int Bodyshots, int Legshots, int FirstKills, int FirstDeaths,
    int Kills2K, int Kills3K, int Kills4K, int Kills5K, int Plants, int Defuses);

public sealed record NormalizedRound(
    int RoundNum, string? RoundResult, string? RoundResultCode, string? RoundCeremony, string? WinningTeam,
    string? WinningTeamRole, string? BombPlanter, string? BombDefuser, int? PlantRoundTimeMs, string? PlantSite,
    int? PlantX, int? PlantY, int? DefuseRoundTimeMs, int? DefuseX, int? DefuseY);

public sealed record NormalizedRoundPlayerStat(
    int RoundNum, string Puuid, int Kills, int Score, int? LoadoutValue, string? Weapon, string? Armor, int? Spent,
    int? Remaining, string? AbilityJson);

public sealed record NormalizedDamage(
    int RoundNum, string Puuid, string Receiver, int Damage, int Headshots, int Bodyshots, int Legshots);

public sealed record NormalizedKill(
    int RoundNum, int Ordinal, string? Killer, string Victim, string[] Assistants, int? TimeSinceGameMs,
    int? TimeSinceRoundMs, int? VictimX, int? VictimY, string? DamageType, string? DamageItem, bool? IsSecondaryFire,
    string? PlayerLocationsJson);

public sealed record NormalizedValorantMatch(
    NormalizedMatchInfo Info,
    IReadOnlyList<NormalizedTeam> Teams,
    IReadOnlyList<NormalizedPlayer> Players,
    IReadOnlyList<NormalizedRound> Rounds,
    IReadOnlyList<NormalizedRoundPlayerStat> RoundPlayerStats,
    IReadOnlyList<NormalizedDamage> Damage,
    IReadOnlyList<NormalizedKill> Kills);
