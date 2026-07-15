namespace Esportra.Contracts.Requests;

public sealed record TeamSeedDto(Guid Id, string Name);

public sealed record GenerateBracketRequest(
    Guid TournamentId,
    Guid? StageId,
    string Format,          // single_elimination | double_elimination | round_robin | swiss
    IReadOnlyList<TeamSeedDto> Teams,
    int BestOf = 1,
    string? BoMode = "per_stage",
    Dictionary<string, int>? RoundBoOverrides = null,
    int? BracketSize = null,
    int? AdvancementCount = null,
    string? DailyStartTime = "20:00",
    string? TournamentStartDate = null,
    int? SwissGroups = null,
    int? SwissRounds = null);

public sealed record SwissNextRoundRequest(
    Guid StageId,
    Guid VersionId,
    int CurrentRound);

public sealed record VetoInitRequest(
    Guid TournamentId,
    Guid? Team1Id,
    Guid? Team2Id,
    int BestOf,
    string Game = "valorant");

public sealed record VetoBanRequest(string MapId);
public sealed record VetoPickRequest(string MapId);
public sealed record VetoPickSideRequest(string MapId, string Side);

public sealed record UpdateMatchBestOfRequest(int BestOf);
