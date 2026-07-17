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
    [InlineData("Mozilla/5.0 (iPhone; Mobile)", "mobile-web")]
    [InlineData("Mozilla/5.0 (iPad; Tablet)", "tablet-web")]
    [InlineData("Mozilla/5.0 Chrome/120", "desktop-web")]
    public void CoarseClientClass_ReturnsExpected(string userAgent, string expected)
    {
        Assert.Equal(expected, SponsorAnalyticsIdentity.CoarseClientClass(userAgent));
    }
}
