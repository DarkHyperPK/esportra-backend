using Esportra.Api.SponsorAnalytics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class SponsorAnalyticsIdentityTests
{
    private static SponsorAnalyticsIdentity CreateIdentity() => new(Options.Create(new SponsorAnalyticsOptions
    {
        IdentityHmacKey = "test-key-with-at-least-thirty-two-characters",
        IdentityKeyVersion = 1,
        IdentityLifetimeDays = 91,
    }));

    [Fact]
    public void CreateLookup_IsDeterministicForSameSponsor()
    {
        var identity = CreateIdentity();
        var sponsorId = Guid.NewGuid();

        var first = identity.CreateLookup(sponsorId, "authenticated", "user-id");
        var second = identity.CreateLookup(sponsorId, "authenticated", "user-id");

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void CreateLookup_SeparatesSponsors()
    {
        var identity = CreateIdentity();

        var first = identity.CreateLookup(Guid.NewGuid(), "authenticated", "user-id");
        var second = identity.CreateLookup(Guid.NewGuid(), "authenticated", "user-id");

        Assert.False(first.SequenceEqual(second));
    }

    [Theory]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        "iphone")]
    [InlineData("Mozilla/5.0 (iPod touch; CPU iPhone OS 16_0 like Mac OS X)", "iphone")]
    [InlineData(
        "Mozilla/5.0 (iPad; CPU OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1",
        "ipad")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36",
        "android-phone")]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 13; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "android-tablet")]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "windows-pc")]
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",
        "mac")]
    [InlineData(
        "Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0",
        "linux-pc")]
    [InlineData(
        "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "linux-pc")]
    [InlineData("Mozilla/5.0 CustomBrowser/2.0", "desktop-web")]
    [InlineData("SomeUnknownBrowser/1.0", "unknown-web")]
    [InlineData("", "unknown-web")]
    public void CoarseClientClass_ReturnsExpected(string userAgent, string expected)
    {
        Assert.Equal(expected, SponsorAnalyticsIdentity.CoarseClientClass(userAgent));
    }

    [Theory]
    [InlineData("curl/8.4.0", true)]
    [InlineData("python-requests/2.31.0", true)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1)", true)]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        false)]
    public void IsBot_ReturnsExpected(string userAgent, bool expected)
    {
        Assert.Equal(expected, SponsorAnalyticsIdentity.IsBot(userAgent));
    }
}
