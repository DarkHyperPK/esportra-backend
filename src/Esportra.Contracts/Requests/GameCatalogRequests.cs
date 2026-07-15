namespace Esportra.Contracts.Requests;

public sealed record DraftCatalogModeRequest(
    string ModeKey,
    string Name,
    int TeamSize,
    string ParticipantMode = "team",
    bool AllowsSubstitutes = true,
    int? MaxRosterSize = null,
    int? MaxSubstitutes = null,
    bool AllowsCoaches = true,
    int MaxCoaches = 2,
    IReadOnlyList<string>? Aliases = null,
    string? ModeGroup = null,
    string? VariantLabel = null,
    string? MapPoolFilter = null,
    object? Features = null);

public sealed record DraftCatalogStructureRequest(
    string StructureKey,
    string Name,
    bool IsDefault = false);

public sealed record UpsertDraftGameRequest(
    string Name,
    string? Category,
    string GameType,
    string DefaultModeKey,
    object Features,
    object? BrConfig,
    string? LogoUrl,
    string? IconUrl,
    string? CoverUrl,
    string? BannerUrl,
    int SortOrder,
    IReadOnlyList<DraftCatalogModeRequest> Modes,
    IReadOnlyList<DraftCatalogStructureRequest> TournamentStructures,
    IReadOnlyList<string>? Aliases);

public sealed record PublishCatalogRequest(string? Notes);
