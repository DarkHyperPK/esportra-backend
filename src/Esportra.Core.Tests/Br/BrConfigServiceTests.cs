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
    }
}
