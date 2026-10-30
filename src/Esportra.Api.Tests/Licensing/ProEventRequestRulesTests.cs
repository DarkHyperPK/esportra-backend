using Esportra.Api.Auth;
using Esportra.Api.Endpoints;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Licensing;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Licensing;

public sealed class ProEventRequestRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 24);
    private static ProEventRequest Valid() => new(Guid.NewGuid(), "Saturday Cup", "Valorant",
        "host@example.test", Today.AddDays(7), Today.AddDays(8), 64, AccessToken: new string('A', 64));

    [Fact]
    public void AcceptsAnEventBriefWithoutPaymentOrAccountClaims() =>
        ProEventRequestRules.Validate(Valid(), Today).Should().BeNull();

    [Fact]
    public void RejectsPastDatesAndInvertedDates()
    {
        ProEventRequestRules.Validate(Valid() with { StartsOn = Today.AddDays(-1) }, Today).Should().NotBeNull();
        ProEventRequestRules.Validate(Valid() with { EndsOn = Today }, Today).Should().NotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10001)]
    public void RejectsInvalidCounts(int count) =>
        ProEventRequestRules.Validate(Valid() with { TeamCount = count }, Today).Should().NotBeNull();

    [Fact]
    public void RejectsHoneypotControlCharactersAndInvalidEmail()
    {
        foreach (var request in new[] { Valid() with { Website = "spam" }, Valid() with { EventName = "Cup\nInjected" },
                     Valid() with { Email = "not-an-email" }, Valid() with { RequestId = Guid.Empty },
                     Valid() with { Email = "Host <host@example.test>" } })
            ProEventRequestRules.Validate(request, Today).Should().NotBeNull();
    }

    [Fact]
    public void NormalizesOnlyValidatedText()
    {
        var request = Valid() with { EventName = " Saturday Cup ", Email = " Host@Example.Test " };
        ProEventRequestRules.Validate(request, Today).Should().BeNull();
        var clean = ProEventRequestRules.Normalize(request);
        clean.EventName.Should().Be("Saturday Cup");
        clean.Email.Should().Be("host@example.test");
    }

    [Fact]
    public void ManifestExposesOnlyCreationPublicly()
    {
        RoutePermissionManifest.Resolve("POST", "/api/pro-event-requests")!.Level.Should().Be(AuthLevel.Public);
        RoutePermissionManifest.Resolve("GET", "/api/pro-event-requests").Should().BeNull();
        RoutePermissionManifest.Resolve("GET", "/api/admin/pro-event-requests")!.Level.Should().Be(AuthLevel.AdminAny);
    }
}
