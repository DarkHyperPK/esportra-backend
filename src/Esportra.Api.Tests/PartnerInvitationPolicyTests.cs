using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests;

public sealed class PartnerInvitationPolicyTests
{
    [Fact]
    public void Lifetime_IsTwentyFourHours()
    {
        Assert.Equal(TimeSpan.FromHours(24), PartnerInvitationPolicy.Lifetime);
    }

    [Theory]
    [InlineData(" Partner@Example.COM ", "partner@example.com")]
    [InlineData("owner@example.com", "owner@example.com")]
    public void NormalizeEmail_ReturnsCanonicalEmail(string email, string expected)
    {
        Assert.Equal(expected, PartnerInvitationPolicy.NormalizeEmail(email));
    }

    [Theory]
    [InlineData("owner@example.com", true)]
    [InlineData("invalid", false)]
    [InlineData("", false)]
    public void IsValidEmail_ReturnsExpectedResult(string email, bool expected)
    {
        Assert.Equal(expected, PartnerInvitationPolicy.IsValidEmail(email));
    }

    [Fact]
    public void IsValidToken_AcceptsSixtyFourHexCharacters()
    {
        Assert.True(PartnerInvitationPolicy.IsValidToken(new string('A', 64)));
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void IsValidToken_RejectsInvalidTokens(string token)
    {
        Assert.False(PartnerInvitationPolicy.IsValidToken(token));
    }

    [Fact]
    public void IsSupportedRole_RejectsViewerForMvp()
    {
        Assert.True(PartnerInvitationPolicy.IsSupportedRole("owner"));
        Assert.False(PartnerInvitationPolicy.IsSupportedRole("viewer"));
    }
}
