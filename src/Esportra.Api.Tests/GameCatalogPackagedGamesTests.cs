using System.Text.Json;
using Xunit;

namespace Esportra.Api.Tests;

public class GameCatalogPackagedGamesTests
{
    [Fact]
    public void PackagedCatalog_EaFc_ExposesSoloAndTeamModes()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "GameCatalog", "esportsGames.json");
        Assert.True(File.Exists(catalogPath), $"Packaged catalog not found at {catalogPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var games = doc.RootElement.GetProperty("games").EnumerateArray().ToArray();
        var eafc = games.SingleOrDefault(game =>
            string.Equals(game.GetProperty("slug").GetString(), "eafc", StringComparison.OrdinalIgnoreCase));

        Assert.False(eafc.ValueKind == JsonValueKind.Undefined, "EA FC game entry is missing from packaged catalog.");

        var modes = eafc.GetProperty("modes").EnumerateArray().ToArray();
        var soloMode = modes.SingleOrDefault(mode => mode.GetProperty("teamSize").GetInt32() == 1);
        var teamMode = modes.SingleOrDefault(mode => mode.GetProperty("teamSize").GetInt32() == 2);

        Assert.False(soloMode.ValueKind == JsonValueKind.Undefined, "EA FC must expose a 1v1 solo mode.");
        Assert.False(teamMode.ValueKind == JsonValueKind.Undefined, "EA FC must expose a 2v2 team mode.");
        Assert.Equal("solo", soloMode.GetProperty("participantMode").GetString());
        Assert.Equal("team", teamMode.GetProperty("participantMode").GetString());

        var formats = eafc.GetProperty("formats").EnumerateArray()
            .Select(format => format.GetProperty("value").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("1v1", formats);
        Assert.Contains("2v2", formats);
    }
}
