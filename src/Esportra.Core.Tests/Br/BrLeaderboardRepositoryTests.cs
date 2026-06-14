using Esportra.Core.Br;
using Xunit;

namespace Esportra.Core.Tests.Br;

public class BrLeaderboardRepositoryTests
{
    [Fact]
    public void StandingsSql_does_not_gate_on_game_completed_status()
    {
        Assert.DoesNotContain("g.status = 'completed'", BrLeaderboardSql.GroupParticipant);
        Assert.DoesNotContain("g.status = 'completed'", BrLeaderboardSql.GroupTeamOnly);
        Assert.DoesNotContain("g.status = 'completed'", BrLeaderboardSql.StageGlobal);
    }

    [Fact]
    public void StandingsSql_aggregates_from_br_lobby_results()
    {
        Assert.Contains("FROM br_lobby_results", BrLeaderboardSql.GroupParticipant);
        Assert.Contains("FROM br_lobby_results", BrLeaderboardSql.StageGlobal);
    }
}
