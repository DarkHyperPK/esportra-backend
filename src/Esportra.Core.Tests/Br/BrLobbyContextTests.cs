using System.Collections;
using Esportra.Core.Br;
using Xunit;

namespace Esportra.Core.Tests.Br;

public sealed class BrLobbyContextTests
{
    [Fact]
    public void MapContext_maps_snake_case_dynamic_row()
    {
        var stageId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var tournamentId = Guid.NewGuid();
        var row = new Dictionary<string, object>
        {
            ["stage_id"] = stageId,
            ["group_id"] = groupId,
            ["wave_number"] = 2,
            ["status"] = "active",
            ["tournament_id"] = tournamentId,
            ["team_size"] = 4,
            ["game"] = "pubg-mobile",
        };

        var context = BrLobbyRepository.MapContext(row);

        Assert.NotNull(context);
        Assert.Equal(stageId, context!.StageId);
        Assert.Equal(groupId, context.GroupId);
        Assert.Equal(2, context.WaveNumber);
        Assert.Equal("active", context.Status);
        Assert.Equal(tournamentId, context.TournamentId);
        Assert.Equal(4, context.TeamSize);
        Assert.Equal("pubg-mobile", context.Game);
    }

    [Fact]
    public void MapContext_returns_null_for_missing_row()
    {
        Assert.Null(BrLobbyRepository.MapContext(null));
        Assert.Null(BrLobbyRepository.MapContext(new object()));
    }
}
