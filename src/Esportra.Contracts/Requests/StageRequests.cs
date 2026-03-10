namespace Esportra.Contracts.Requests;

public sealed record StageDto(
    string? Id,
    string  Name,
    string  Format,
    int     StageOrder,
    int?    BestOf           = 1,
    int?    Capacity         = null,
    int?    AdvancementCount = null);

public sealed record SyncStagesRequest(StageDto[] Stages);

public sealed record SyncMapPoolsRequest(string[] MapIds);
