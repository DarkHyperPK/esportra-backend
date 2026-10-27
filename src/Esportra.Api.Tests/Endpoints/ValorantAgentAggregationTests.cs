using System.Collections;
using System.Reflection;
using System.Text.Json;
using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed class ValorantAgentAggregationTests
{
    [Fact]
    public void ExistingAgentCounters_AreRetainedWhenAnotherMatchIsAdded()
    {
        var service = typeof(ValorantStatsSyncService);
        var statsType = service.GetNestedType("AggregatedStats", BindingFlags.NonPublic)!;
        var stats = Activator.CreateInstance(statsType, nonPublic: true)!;
        var agents = statsType.GetField("AgentStats")!.GetValue(stats)!;
        service.GetMethod("RestoreAgentStats", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            ["[{\"agent_id\":\"agent-a\",\"games_played\":4,\"wins\":2,\"kills\":40,\"deaths\":20}]", agents]);
        using var match = JsonDocument.Parse("""
            {"matchInfo":{"queueId":"competitive"},"teams":[{"teamId":"Blue","won":true}],
             "players":[{"puuid":"player","characterId":"agent-a","teamId":"Blue","competitiveTier":10,
                         "stats":{"kills":10,"deaths":5,"assists":2,"score":500}}]}
            """);
        var accepted = service.GetMethod("ExtractPlayerStats", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [match.RootElement, "player", stats]);
        accepted.Should().Be(true);
        var agent = ((IDictionary)agents)["agent-a"]!;
        agent.GetType().GetField("GamesPlayed")!.GetValue(agent).Should().Be(5);
        agent.GetType().GetField("Wins")!.GetValue(agent).Should().Be(3);
        agent.GetType().GetField("Kills")!.GetValue(agent).Should().Be(50);
        agent.GetType().GetField("Deaths")!.GetValue(agent).Should().Be(25);
    }
}
