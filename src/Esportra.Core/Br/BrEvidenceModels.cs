namespace Esportra.Core.Br;

public sealed record BrEvidenceEntry(
    string TeamId,
    string TeamName,
    string? LogoUrl,
    string ImageUrl,
    string SubmittedAt,
    int? Placement,
    int? Kills,
    bool Reviewed,
    int? GameNumber = null);
