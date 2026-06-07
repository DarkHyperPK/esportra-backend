using Esportra.Core.Games;
using Xunit;

namespace Esportra.Core.Tests;

public class R6MapCatalogTests
{
    [Fact]
    public void ResolveImageUrl_Builds_Staging_Style_Public_Url_When_Missing()
    {
        var url = R6MapCatalog.ResolveImageUrl(
            R6MapCatalog.GameName,
            "Kafe Dostoyevsky",
            null,
            "https://staging.esportra.com");

        Assert.Equal(
            "https://staging.esportra.com/storage/v1/object/public/system.assets.games/r6/maps/kafe-dostoyevsky.avif",
            url);
    }

    [Fact]
    public void ResolveImageUrl_Preserves_Existing_Value()
    {
        const string existing = "https://example.com/bank.avif";
        var url = R6MapCatalog.ResolveImageUrl(R6MapCatalog.GameName, "Bank", existing, "https://staging.esportra.com");
        Assert.Equal(existing, url);
    }

    [Fact]
    public void GetSeedEntries_Covers_All_Active_Maps()
    {
        Assert.Equal(27, R6MapCatalog.GetSeedEntries().Count);
    }
}
