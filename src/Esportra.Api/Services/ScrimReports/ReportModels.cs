using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Services.ScrimReports;

/// <summary>One verified scrim map of the team, with Riot's record of it and who on the roster was which Riot player.</summary>
public sealed record ReportGame(
    Guid GameId, DateTime? StartedAt, string? Map, Guid OpponentId, string OpponentName, string Result, int RoundsWon, int RoundsLost);

public sealed record RosterUser(Guid UserId, string? Name);

public sealed record ReportMatch(ReportGame Game, InMatch Match, InsightsReport Report, IReadOnlyDictionary<string, RosterUser> UsersByPuuid);

public sealed record ReportSample(int Maps, DateTime? From, DateTime? To, bool Capped);

public sealed record SeriesPoint(
    int Index, DateTime? Date, string? Map, string Opponent, string Result, int RoundsWon, int RoundsLost, double? RoundWinPct,
    double? TeamAcs, double? TeamAdr, double? FirstBloodPct, bool? PistolWon);

public sealed record TeamMapRow(string Map, int Games, int Wins, int RoundsWon, int RoundsLost, double? RoundWinPct, Rate Attack, Rate Defense);

public sealed record AgentMaps(string Agent, int Maps);

/// <summary>One roster player across the report's maps. Each figure is round-weighted over the maps it comes from.</summary>
public sealed record PlayerRow(
    Guid UserId, string? Name, int Maps, int Rounds, double? Acs, double? Adr, double? Kd, double? Kast, double? HsPct,
    int Kills, int Deaths, int Assists, int FirstKills, int FirstDeaths, int Multi2, int Multi3, int Multi4, int Multi5,
    int Plants, int Defuses, double? AcsSd, IReadOnlyList<AgentMaps> Agents);

/// <summary>A finding the evidence supports. Tone is "good", "watch" or "neutral"; PlayerId points at a roster player when it is about one.</summary>
public sealed record Highlight(string Kind, string Tone, string Title, string Detail, Guid? PlayerId);

public sealed record BuyAggregate(string Buy, Rate RoundsWon);

public sealed record TeamReport(
    ReportSample Sample, int Wins, int Losses, double? WinRatePct, double? RoundWinPct, IReadOnlyList<SeriesPoint> Series, IReadOnlyList<TeamMapRow> Maps,
    Rate Attack, Rate Defense, Rate FirstBloodRounds, Rate WinAfterFirstBlood, Rate WinAfterFirstDeath, Rate Pistol,
    IReadOnlyList<BuyAggregate> Buys, IReadOnlyList<PlayerRow> Players, IReadOnlyList<Highlight> Highlights);
