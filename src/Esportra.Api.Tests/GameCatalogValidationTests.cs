using System.Text.Json;
using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests;

public class GameCatalogValidationTests
{
    [Fact]
    public void ValidateCatalog_RejectsDuplicateSlug()
    {
        var json = """
        {
          "catalogVersion": "test",
          "schemaVersion": 1,
          "games": [
            {
              "slug": "valorant",
              "name": "Valorant",
              "type": "bracket",
              "defaultMode": "competitive",
              "features": {},
              "modes": [{ "key": "competitive", "name": "Competitive", "teamSize": 5 }],
              "tournamentCapabilities": {
                "defaultStructure": "double_elimination",
                "supportedStructures": [{ "key": "double_elimination", "name": "Double Elimination" }]
              }
            },
            {
              "slug": "valorant",
              "name": "Valorant 2",
              "type": "bracket",
              "defaultMode": "competitive",
              "features": {},
              "modes": [{ "key": "competitive", "name": "Competitive", "teamSize": 5 }],
              "tournamentCapabilities": {
                "defaultStructure": "double_elimination",
                "supportedStructures": [{ "key": "double_elimination", "name": "Double Elimination" }]
              }
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var games = doc.RootElement.GetProperty("games").EnumerateArray().ToArray();

        var ex = Assert.ThrowsAny<Exception>(() => InvokeValidateCatalog(games));
        Assert.Contains("Duplicate game slug", ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public void ValidateCatalog_RequiresBrConfig_ForBattleRoyale()
    {
        var json = """
        {
          "games": [{
            "slug": "fortnite",
            "name": "Fortnite",
            "type": "battle_royale",
            "defaultMode": "squads",
            "features": {},
            "modes": [{ "key": "squads", "name": "Squads", "teamSize": 4 }],
            "tournamentCapabilities": {
              "defaultStructure": "battle_royale",
              "supportedStructures": [{ "key": "battle_royale", "name": "Battle Royale" }]
            }
          }]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var games = doc.RootElement.GetProperty("games").EnumerateArray().ToArray();

        var ex = Assert.ThrowsAny<Exception>(() => InvokeValidateCatalog(games));
        Assert.Contains("brConfig", ex.InnerException?.Message ?? ex.Message);
    }

    private static void InvokeValidateCatalog(JsonElement[] games)
    {
        var method = typeof(GameCatalogService).GetMethod(
            "ValidateCatalog",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [games]);
    }
}
