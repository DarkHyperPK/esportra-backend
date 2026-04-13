namespace Esportra.Contracts.Requests;

public sealed record ValidateLicenseRequest(
    string DeviceFingerprint);

public sealed record ActivateLicenseRequest(
    string LicenseKey,
    string DeviceFingerprint);

public sealed record CreateOverlayLayoutRequest(
    string  Name,
    string  Game              = "valorant",
    int     ResolutionWidth   = 1920,
    int     ResolutionHeight  = 1080,
    string? WidgetsJson       = null,
    bool    IsPublic          = false);

public sealed record UpdateOverlayLayoutRequest(
    string? Name              = null,
    string? WidgetsJson       = null,
    bool?   IsPublic          = null,
    string? ThumbnailUrl      = null,
    int?    ResolutionWidth   = null,
    int?    ResolutionHeight  = null);
