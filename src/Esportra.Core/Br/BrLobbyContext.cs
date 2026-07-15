namespace Esportra.Core.Br;

public sealed record BrLobbyContext(
    Guid StageId,
    Guid? GroupId,
    int WaveNumber,
    string Status,
    Guid TournamentId,
    int TeamSize,
    string? Game = null,
    object? Settings = null,
    object? StageConfig = null);
