using Esportra.Core.Br;
using Xunit;

namespace Esportra.Core.Tests.Br;

public sealed class BrConfigServiceTests
{
    [Fact]
    public void ResolveGamesPerLobby_uses_stage_override()
    {
        var stageConfig = """{"br":{"gamesPerLobby":4}}""";
        var count = BrConfigService.ResolveGamesPerLobby(null, stageConfig);
        Assert.Equal(4, count);
    }

    [Fact]
    public void CalculatePoints_applies_kill_cap()
    {
        var scoring = new BrScoringSettings([10, 6, 4], 2, 3);
        var (_, killPoints, total) = BrConfigService.CalculatePoints(1, 5, scoring);
        Assert.Equal(6, killPoints);
        Assert.Equal(16, total);
    }

    [Fact]
    public void ResolveForApi_returns_stable_dto()
    {
        var dto = BrConfigService.ResolveForApi(null, """{"br":{"gamesPerLobby":3}}""", null);
        Assert.Equal(3, dto.GamesPerLobby);
        Assert.Equal("static_groups", dto.Format);
        Assert.Equal("none", dto.MapScope);
    }

    [Fact]
    public void DeriveMapScope_uses_game_when_games_model_active()
    {
        var mapConfig = new BrMapConfig(BrMapMode.PerRound, ["Erangel"], null);
        Assert.Equal("game", BrConfigService.DeriveMapScope(mapConfig, gamesModelActive: true));
        Assert.Equal("lobby", BrConfigService.DeriveMapScope(mapConfig, gamesModelActive: false));
        Assert.Equal("none", BrConfigService.DeriveMapScope(new BrMapConfig(BrMapMode.None, [], null), true));
    }

    [Fact]
    public void ResolveMapForGame_matches_rotation_by_game_number()
    {
        var mapConfig = new BrMapConfig(BrMapMode.Rotation, ["A", "B", "C"], null);
        Assert.Equal("B", BrConfigService.ResolveMapForGame(mapConfig, 2, null));
    }
}
