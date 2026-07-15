using Esportra.Api.Helpers;
using Xunit;

namespace Esportra.Api.Tests;

public class GameCatalogHttpHelperTests
{
    [Fact]
    public void MatchesETag_ReturnsTrue_ForQuotedHash()
    {
        const string hash = "abc123";
        Assert.True(GameCatalogHttpHelper.MatchesETag($"\"{hash}\"", hash));
    }

    [Fact]
    public void MatchesETag_ReturnsFalse_ForSubstringCollision()
    {
        const string hash = "abc123";
        Assert.False(GameCatalogHttpHelper.MatchesETag("\"abc1234\"", hash));
    }

    [Fact]
    public void MatchesETag_ReturnsTrue_ForWildcard()
    {
        Assert.True(GameCatalogHttpHelper.MatchesETag("*", "anything"));
    }
}
