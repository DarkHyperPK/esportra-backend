namespace Esportra.Contracts.Requests;

public sealed record TeamSeedDto(string Id, string Name);

public sealed record GenerateBracketRequest(
    string                TournamentId,
    string?               StageId,
    string                Format,          // single_elimination | double_elimination | round_robin | swiss
    IReadOnlyList<TeamSeedDto> Teams,
    int                   BestOf           = 1,
    int?                  BracketSize      = null,
    int?                  AdvancementCount = null,
    string?               DailyStartTime   = "20:00",
    string?               TournamentStartDate = null,
    int?                  SwissGroups      = null,
    int?                  SwissRounds      = null);

public sealed record SwissNextRoundRequest(
    string StageId,
    string VersionId,
    int    CurrentRound);

public sealed record VetoInitRequest(
    string TournamentId,
    string? Team1Id,
    string? Team2Id,
    int     BestOf,
    string  Game = "valorant");

public sealed record VetoBanRequest(string MapId);
public sealed record VetoPickRequest(string MapId);
public sealed record VetoPickSideRequest(string MapId, string Side);
