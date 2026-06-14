namespace Esportra.Core.Br;

public sealed record BrLeaderboardRow(
    string TeamId,
    string TeamName,
    string? LogoUrl,
    int GamesPlayed,
    long TotalPlacementPoints,
    long TotalKillPoints,
    long TotalPoints,
    long TotalKills,
    long Wins,
    int BestPlacement,
    double? AvgPlacement);

public sealed record BrLeaderboardApiRow(
    string TeamId,
    string TeamName,
    string? LogoUrl,
    int GamesPlayed,
    long TotalPlacementPoints,
    long TotalKillPoints,
    long TotalPoints,
    long TotalKills,
    long Wins,
    int BestPlacement);
