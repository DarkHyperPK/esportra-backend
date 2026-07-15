using Esportra.Core.Notifications;
using Xunit;

namespace Esportra.Core.Tests.Notifications;

public sealed class CaptainMatchLinkBuilderTests
{
    [Fact]
    public void BuildLink_uses_tournament_slug_and_match_id()
    {
        var matchId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var link = CaptainMatchLinkBuilder.BuildLink("summer-cup", matchId);

        Assert.Equal($"/tournaments/summer-cup/captain-match/{matchId}", link);
    }

    [Fact]
    public void BuildLink_falls_back_to_tournaments_list_when_slug_missing()
    {
        var matchId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var link = CaptainMatchLinkBuilder.BuildLink(null, matchId);

        Assert.Equal("/tournaments", link);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildLink_falls_back_when_slug_blank(string slug)
    {
        var matchId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var link = CaptainMatchLinkBuilder.BuildLink(slug, matchId);

        Assert.Equal("/tournaments", link);
    }
}
