using System.Text.Json;
using Esportra.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Air;

public sealed class AirGamePackManifestTests
{
    private static readonly string Sha = new('a', 64);
    private static readonly string Small = new('b', 64);

    [Fact]
    public void FilePaths_ReturnsContentAddressedPaths_ForFilesAndThumbnails()
    {
        var json = JsonSerializer.Serialize(new { game = "pubgm", version = "1", assets = new[] { new { id = "map", file = new { sha256 = Sha, ext = "jpg" }, thumb = new { sha256 = Small, ext = "png" } } } });
        var paths = AirGamePackEndpoints.FilePaths("pubgm", json);
        paths.Should().BeEquivalentTo(new Dictionary<string, string> { [Sha] = $"pubgm/files/{Sha}.jpg", [Small] = $"pubgm/files/{Small}.png" });
    }

    [Fact]
    public void FilePaths_SkipsEntriesThatCouldPointElsewhere_WhenManifestIsMalformed()
    {
        var json = JsonSerializer.Serialize(new
        {
            game = "pubgm",
            assets = new[]
            {
                new { file = new { sha256 = "../../secret", ext = "png" } },
                new { file = new { sha256 = Sha, ext = "json" } },
                new { file = new { sha256 = Sha.ToUpperInvariant(), ext = "png" } },
            },
        });
        AirGamePackEndpoints.FilePaths("pubgm", json).Should().BeEmpty();
    }

    [Fact]
    public void FilePaths_Throws_WhenManifestIsForAnotherGame()
    {
        var act = () => AirGamePackEndpoints.FilePaths("pubgm", """{"game":"freefire","assets":[]}""");
        act.Should().Throw<JsonException>();
    }
}
