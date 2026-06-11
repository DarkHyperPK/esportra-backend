namespace Esportra.Core.Br;

public sealed record BrMatchupWave(int Wave, IReadOnlyList<IReadOnlyList<string>> Lobbies);

public sealed record BrScheduleManifest(
    int TotalWaves,
    int TotalLobbies,
    int TotalMatches,
    IReadOnlyList<BrMatchupWave> Waves);

public sealed record BrScheduleValidationError(string Message);
