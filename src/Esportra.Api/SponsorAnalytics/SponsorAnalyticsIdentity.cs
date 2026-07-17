using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Esportra.Api.SponsorAnalytics;

public sealed class SponsorAnalyticsIdentity(IOptions<SponsorAnalyticsOptions> options)
{
    private readonly SponsorAnalyticsOptions _options = options.Value;
    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.Value.IdentityHmacKey);

    public short KeyVersion => _options.IdentityKeyVersion;
    public int LifetimeDays => _options.IdentityLifetimeDays;

    public byte[] CreateLookup(
        Guid sponsorId,
        string identityKind,
        string identityMaterial)
    {
        var value = $"sponsor-audience:v1:{sponsorId:D}:{identityKind}:{identityMaterial}";
        return HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
    }

    public static string CoarseClientClass(string userAgent)
    {
        var normalized = userAgent.ToLowerInvariant();
        if (normalized.Contains("ipad") || normalized.Contains("tablet")) return "tablet-web";
        if (normalized.Contains("mobile") || normalized.Contains("android") || normalized.Contains("iphone")) return "mobile-web";
        if (normalized.Contains("mozilla") || normalized.Contains("chrome") || normalized.Contains("safari")) return "desktop-web";
        return "unknown-web";
    }
}
