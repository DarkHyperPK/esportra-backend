using Esportra.Core.Tournaments;
using Xunit;

namespace Esportra.Core.Tests;

public sealed class SponsorPlacementPolicyTests
{
    [Theory]
    [InlineData("wide_partner", false, 4, SponsorCreativeRole.Banner)]
    [InlineData("sidebar_partner", false, 2, SponsorCreativeRole.Banner)]
    [InlineData("partner_logo", false, 4, SponsorCreativeRole.Logo)]
    [InlineData("card_badge", false, 1, SponsorCreativeRole.Logo)]
    [InlineData("homepage_ticker", true, 10, SponsorCreativeRole.Logo)]
    [InlineData("partner_showcase", true, 6, SponsorCreativeRole.Banner)]
    public void Policy_DefinesScopeCapacityAndRole(string zone, bool isGlobal, int capacity, SponsorCreativeRole role)
    {
        Assert.True(SponsorPlacementPolicy.TryGet(zone, out var policy));
        Assert.Equal(isGlobal, policy.IsGlobal);
        Assert.Equal(capacity, policy.Capacity);
        Assert.Equal(role, policy.RequiredRole);
    }

    [Fact]
    public void IsTierAllowed_FailsClosedForUnknownTier()
    {
        SponsorPlacementPolicy.TryGet("homepage_ticker", out var policy);
        Assert.False(SponsorPlacementPolicy.IsTierAllowed(policy, "unexpected"));
    }

    [Theory]
    [InlineData("wide_partner", 1600, 700, true)]
    [InlineData("wide_partner", 700, 1600, false)]
    [InlineData("sidebar_partner", 600, 1200, true)]
    [InlineData("sidebar_partner", 1200, 600, false)]
    [InlineData("partner_logo", 600, 200, true)]
    [InlineData("partner_logo", 100, 30, false)]
    public void IsAspectRatioValid_EnforcesZoneBounds(string zone, int width, int height, bool expected)
    {
        SponsorPlacementPolicy.TryGet(zone, out var policy);
        Assert.Equal(expected, SponsorPlacementPolicy.IsAspectRatioValid(policy, width, height));
    }
}
