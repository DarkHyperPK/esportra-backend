using Esportra.Core.Br;
using Xunit;

namespace Esportra.Core.Tests.Br;

public class BrScheduleGeneratorTests
{
    [Fact]
    public void K4_produces_three_waves_two_lobbies_each()
    {
        var manifest = BrScheduleGenerator.GenerateRotatingPairwise(4, groupsPerLobby: 2, matchesPerWave: 1);

        Assert.Equal(3, manifest.TotalWaves);
        Assert.Equal(6, manifest.TotalLobbies);
        Assert.Equal(3, manifest.TotalMatches);
        Assert.Equal(3, manifest.Waves.Count);

        Assert.Equal(new[] { "A", "B" }, manifest.Waves[0].Lobbies[0]);
        Assert.Equal(new[] { "C", "D" }, manifest.Waves[0].Lobbies[1]);

        Assert.Equal(new[] { "A", "C" }, manifest.Waves[1].Lobbies[0]);
        Assert.Equal(new[] { "B", "D" }, manifest.Waves[1].Lobbies[1]);

        Assert.Equal(new[] { "A", "D" }, manifest.Waves[2].Lobbies[0]);
        Assert.Equal(new[] { "B", "C" }, manifest.Waves[2].Lobbies[1]);
    }

    [Fact]
    public void ValidateLobbyCapacity_fails_when_roster_exceeds_max()
    {
        var manifest = BrScheduleGenerator.GenerateRotatingPairwise(4);
        var rosters = new Dictionary<string, int>
        {
            ["A"] = 10,
            ["B"] = 10,
            ["C"] = 10,
            ["D"] = 10,
        };

        var err = BrScheduleGenerator.ValidateLobbyCapacity(manifest, rosters, maxLobbySize: 16);
        Assert.NotNull(err);
        Assert.Contains("max 16", err.Message);
    }

    [Fact]
    public void ValidateLobbyCapacity_passes_when_roster_fits()
    {
        var manifest = BrScheduleGenerator.GenerateRotatingPairwise(4);
        var rosters = new Dictionary<string, int>
        {
            ["A"] = 10,
            ["B"] = 10,
            ["C"] = 10,
            ["D"] = 10,
        };

        var err = BrScheduleGenerator.ValidateLobbyCapacity(manifest, rosters, maxLobbySize: 20);
        Assert.Null(err);
    }
}
