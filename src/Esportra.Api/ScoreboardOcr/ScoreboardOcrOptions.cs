namespace Esportra.Api.ScoreboardOcr;

/// <summary>Connection settings for the internal scoreboard OCR service (services/scoreboard-ocr).</summary>
public sealed class ScoreboardOcrOptions
{
    public const string SectionName = "ScoreboardOcr";

    /// <summary>Internal URL, e.g. http://scoreboard-ocr:8090. Empty disables the feature.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Shared secret sent as X-Service-Token; must match SCOREBOARD_OCR_TOKEN on the service.</summary>
    public string ServiceToken { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(ServiceToken)
        && !ServiceToken.StartsWith("REPLACE_WITH_", StringComparison.Ordinal);
}
