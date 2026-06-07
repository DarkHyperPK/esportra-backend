using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests;

public class GameCatalogHashHelperTests
{
    [Fact]
    public void ComputeHash_IsDeterministic_ForSameInput()
    {
        var games = SampleGames();
        var hash1 = GameCatalogHashHelper.ComputeHash("2026.06.08-admin-catalog", 1, games);
        var hash2 = GameCatalogHashHelper.ComputeHash("2026.06.08-admin-catalog", 1, games);

        Assert.Equal(hash1, hash2);
        Assert.Matches("^[a-f0-9]{64}$", hash1);
    }

    [Fact]
    public void ComputeHash_Changes_WhenModeChanges()
    {
        var baseGames = SampleGames();
        var changedGames = SampleGames(teamSize: 6);

        var baseHash = GameCatalogHashHelper.ComputeHash("v1", 1, baseGames);
        var changedHash = GameCatalogHashHelper.ComputeHash("v1", 1, changedGames);

        Assert.NotEqual(baseHash, changedHash);
    }

    [Fact]
    public void ComputeHash_Changes_WhenCatalogVersionChanges()
    {
        var games = SampleGames();
        var hash1 = GameCatalogHashHelper.ComputeHash("v1", 1, games);
        var hash2 = GameCatalogHashHelper.ComputeHash("v2", 1, games);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_Changes_WhenModeOverlayChanges()
    {
        var baseGames = SampleGames();
        var overlayGames = SampleGames(mapPoolFilter: "skirmish");

        var baseHash = GameCatalogHashHelper.ComputeHash("v1", 1, baseGames);
        var overlayHash = GameCatalogHashHelper.ComputeHash("v1", 1, overlayGames);

        Assert.NotEqual(baseHash, overlayHash);
    }

    private static List<CatalogGameHashInput> SampleGames(int teamSize = 5, string? mapPoolFilter = null) =>
    [
        new CatalogGameHashInput(
            "valorant",
            "Valorant",
            "FPS",
            "bracket",
            "competitive",
            new Dictionary<string, object> { ["mapVeto"] = true, ["mapPool"] = true },
            null,
            "https://cdn.example.com/valorant.png",
            null,
            null,
            0,
            [
                new CatalogModeHashInput(
                    "competitive",
                    "Competitive",
                    teamSize,
                    "team",
                    true,
                    null,
                    ["competitive"],
                    null,
                    null,
                    mapPoolFilter),
            ],
            [
                new CatalogStructureHashInput("double_elimination", "Double Elimination", true),
            ],
            ["valorant", "Valorant"]),
    ];
}
