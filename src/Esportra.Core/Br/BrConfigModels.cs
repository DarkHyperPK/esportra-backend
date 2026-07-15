namespace Esportra.Core.Br;

public enum BrMapMode
{
    None,
    FixedStage,
    PerRound,
    Rotation,
}

public enum BrStageFormat
{
    SingleLobby,
    StaticGroups,
    GroupRotation,
    MultiLobbyCut,
}

public enum BrLeaderboardScope
{
    StageGlobal,
    PerSeedGroup,
    PerLobby,
}

public enum BrAdvancementMode
{
    TopNPerGroup,
    TopNPerLobby,
    TopNOverall,
    Threshold,
    None,
}

public enum BrLobbyFormation
{
    Single,
    PerSeedGroup,
    WavePairings,
    ParallelCut,
}

public sealed record BrScoringSettings(int[] Placements, int KillPoints, int? KillCap);

public sealed record BrMapConfig(BrMapMode Mode, IReadOnlyList<string> Pool, string? FixedMap);

public sealed record ResolvedStageBrConfig(
    BrScoringSettings Scoring,
    BrTiebreaker Tiebreaker,
    BrMapConfig Map,
    int? GameCount);

public sealed record BrAdvancementConfig(BrAdvancementMode Mode, int? Count);

/// <summary>API contract for resolved stage BR settings (single source of truth for frontend).</summary>
public sealed record ResolvedBrStageConfigDto(
    int GamesPerLobby,
    BrMapConfig MapConfig,
    BrScoringSettings Scoring,
    string Tiebreaker,
    string Format,
    BrAdvancementConfig Advancement,
    BrLobbyFormation LobbyFormation,
    BrLeaderboardScope LeaderboardScope,
    int PlayersPerLobby,
    string MapScope);
