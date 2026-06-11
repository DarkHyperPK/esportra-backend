using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests;

public class GameCatalogBannerSeedTests
{
    [Theory]
    [InlineData("valorant", "https://images.igdb.com")]
    [InlineData("Valorant", "https://images.igdb.com")]
    [InlineData("Fortnite", "https://images.igdb.com")]
    [InlineData("CS2", "https://images.igdb.com")]
    public void TryResolve_KnownGames_ReturnsHttpsBanner(string key, string expectedPrefix)
    {
        var banner = GameCatalogBannerSeed.TryResolve(key);

        Assert.NotNull(banner);
        Assert.StartsWith(expectedPrefix, banner, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://images.igdb.com/igdb/image/upload/t_1080p/test.jpg", "https://images.igdb.com/igdb/image/upload/t_1080p/test.jpg")]
    [InlineData("http://example.com/banner.jpg", "")]
    [InlineData("/games/valorant/header.jpg", "")]
    public void NormalizeAbsoluteHttpsUrl_AcceptsHttpsOnly(string input, string expected)
    {
        var result = GameCatalogBannerSeed.NormalizeAbsoluteHttpsUrl(input);
        if (string.IsNullOrEmpty(expected))
            Assert.Null(result);
        else
            Assert.Equal(expected, result);
    }
}
