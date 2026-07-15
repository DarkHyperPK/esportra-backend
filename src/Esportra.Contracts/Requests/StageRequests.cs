namespace Esportra.Contracts.Requests;

public sealed record StageDto(
    string? Id,
    string Name,
    string Format,
    int StageOrder,
    int? BestOf = 1,
    string? BoMode = "per_stage",
    Dictionary<string, int>? RoundBoOverrides = null,
    int? Capacity = null,
    int? AdvancementCount = null,
    System.Text.Json.JsonElement? Config = null,
    string? StartsAt = null,
    string? EndsAt = null);

public sealed record SyncStagesRequest(StageDto[] Stages);

public sealed record SyncMapPoolsRequest(string[] MapIds);
