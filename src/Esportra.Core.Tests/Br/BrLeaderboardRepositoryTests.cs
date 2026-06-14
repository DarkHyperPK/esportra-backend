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

    [Fact]
    public void StageGlobal_sql_includes_placement_and_kill_point_breakdown()
    {
        Assert.Contains("SUM(rr.placement_points)", BrLeaderboardSql.StageGlobal);
        Assert.Contains("SUM(rr.kill_points)", BrLeaderboardSql.StageGlobal);
        Assert.Contains("COUNT(DISTINCT rr.game_id)", BrLeaderboardSql.StageGlobal);
        Assert.Contains("MIN(rr.placement)", BrLeaderboardSql.StageGlobal);
    }

    [Fact]
    public void ToStageApiPayload_includes_placement_and_kill_points()
    {
        var rows = new[]
        {
            new BrLeaderboardRow(
                TeamId: "team-1",
                TeamName: "Alpha",
                LogoUrl: null,
                GamesPlayed: 2,
                TotalPlacementPoints: 18,
                TotalKillPoints: 6,
                TotalPoints: 24,
                TotalKills: 3,
                Wins: 1,
                BestPlacement: 1,
                AvgPlacement: 2.5),
        };

        var payload = BrLeaderboardRepository.ToStageApiPayload(rows);
        var entry = Assert.Single(payload);

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        Assert.Contains("total_placement_points", json);
        Assert.Contains("total_kill_points", json);
        Assert.Contains("games_played", json);
        Assert.Contains("best_placement", json);
        Assert.Contains("\"total_placement_points\":18", json);
        Assert.Contains("\"total_kill_points\":6", json);
    }
}
