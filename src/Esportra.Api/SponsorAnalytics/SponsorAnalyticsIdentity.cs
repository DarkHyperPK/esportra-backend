using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Esportra.Api.SponsorAnalytics;

public sealed class SponsorAnalyticsIdentity(IOptions<SponsorAnalyticsOptions> options)
{
    private readonly SponsorAnalyticsOptions _options = options.Value;
    private readonly byte[] _key = HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(options.Value.IdentityHmacKey),
        Encoding.UTF8.GetBytes("esportra:sponsor-analytics:identity:v1"));

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

    private static readonly string[] BotSignatures =
    [
        "bot", "crawler", "spider", "slurp", "curl/", "wget", "python-",
        "go-http-client", "java/", "okhttp", "headlesschrome", "lighthouse",
        "pagespeed", "pingdom", "uptimerobot", "betteruptime", "statuscake",
        "facebookexternalhit", "discordapp", "twitterbot", "linkedin",
        "whatsapp", "telegrambot", "embedly", "preview", "monitoring",
        "axios/", "node-fetch", "postman",
    ];

    public static bool IsBot(string userAgent)
    {
        var normalized = userAgent.ToLowerInvariant();
        return BotSignatures.Any(normalized.Contains);
    }

    public static string CoarseClientClass(string userAgent)
    {
        var normalized = userAgent.ToLowerInvariant();
        if (IsBot(normalized)) return "bot";
        if (normalized.Contains("ipad") || normalized.Contains("tablet")) return "tablet-web";
        if (normalized.Contains("mobile") || normalized.Contains("android") || normalized.Contains("iphone")) return "mobile-web";
        if (normalized.Contains("mozilla") || normalized.Contains("chrome") || normalized.Contains("safari")
            || normalized.Contains("firefox") || normalized.Contains("edg") || normalized.Contains("opera")
            || normalized.Contains("gecko/")) return "desktop-web";
        return "unknown-web";
    }
}
