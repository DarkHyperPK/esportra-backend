namespace Esportra.Api.Auth;

public sealed class RecoveryOptions
{
    public const string SectionName = "Recovery";

    public string MainRedirectUrl { get; init; } = string.Empty;

    public string PartnerRedirectUrl { get; init; } = string.Empty;
}