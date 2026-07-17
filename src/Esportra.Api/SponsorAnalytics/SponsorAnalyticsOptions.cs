namespace Esportra.Api.SponsorAnalytics;

public sealed class SponsorAnalyticsOptions
{
    public const string SectionName = "SponsorAnalytics";

    public string IdentityHmacKey { get; set; } = string.Empty;
    public short IdentityKeyVersion { get; set; } = 1;
    public int MinimumAudience { get; set; } = 10;
    public int IdentityLifetimeDays { get; set; } = 91;
}
