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
    public void CurrentAct_UsesProviderTypeAndStartEndWithoutIsActive()
    {
        using var json = JsonDocument.Parse("""
            [{"uuid":"act-one","type":"EAresSeasonType::Act","startTime":"2026-10-14T00:00:00Z","endTime":"2027-01-06T00:00:00Z"}]
            """);
        var method = typeof(ValorantStatsSyncService).GetMethod("FindCurrentAct", BindingFlags.NonPublic | BindingFlags.Static)!;
        var act = method.Invoke(null, [json.RootElement, DateTimeOffset.Parse("2026-10-28T00:00:00Z")]);
        act.Should().NotBeNull();
        act!.GetType().GetProperty("ActId")!.GetValue(act).Should().Be("act-one");
    }

    [Theory]
    [InlineData(900, true)]
    [InlineData(1000, false)]
    [InlineData(1100, false)]
    public void HistoryCoverage_RequiresMatchBeforeActStart(long oldestMatchMs, bool expected)
    {
        var method = typeof(ValorantStatsSyncService).GetMethod("HistoryCoversActStart", BindingFlags.NonPublic | BindingFlags.Static)!;
        var json = "{\"history\":[{\"gameStartTimeMillis\":" + oldestMatchMs + "}]}";
        var result = method.Invoke(null, [json, DateTimeOffset.FromUnixTimeMilliseconds(1000)]);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 12)]
    [InlineData(15, 7)]
    [InlineData(15, 2)]
    public void RankedMatches_UpdateLatestRankAndIgnoreUnrankedTiers(int previousCurrent, int matchTier)
    {
        var service = typeof(ValorantStatsSyncService);
        var statsType = service.GetNestedType("AggregatedStats", BindingFlags.NonPublic)!;
        var stats = Activator.CreateInstance(statsType, nonPublic: true)!;
        statsType.GetField("LatestCompetitiveTier")!.SetValue(stats, previousCurrent);
        using var match = JsonDocument.Parse($$$"""
            {"matchInfo":{"queueId":"competitive"},"teams":[{"teamId":"Blue","won":true}],
             "players":[{"puuid":"player","characterId":"agent-a","teamId":"Blue","competitiveTier":{{{matchTier}}},
                         "stats":{"kills":10,"deaths":5,"assists":2,"score":500}}]}
            """);
        service.GetMethod("ExtractPlayerStats", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [match.RootElement, "player", stats]);
        statsType.GetField("LatestCompetitiveTier")!.GetValue(stats).Should().Be(matchTier >= 3 ? matchTier : previousCurrent);
    }

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

    [Fact]
    public void HeadshotExtraction_AccumulatesFromRoundResults()
    {
        using var match = JsonDocument.Parse("""
            {"matchInfo":{"queueId":"competitive"},"teams":[{"teamId":"Blue","won":true}],
             "players":[{"puuid":"player","characterId":"agent-b","teamId":"Blue","competitiveTier":10,
                         "stats":{"kills":8,"deaths":4,"assists":1,"score":300}}],
             "roundResults":[
               {"playerStats":[
                 {"puuid":"player","damage":[
                   {"headshots":3,"bodyshots":5,"legshots":2},
                   {"headshots":1,"bodyshots":3,"legshots":0}
                 ]},
                 {"puuid":"other","damage":[{"headshots":10,"bodyshots":0,"legshots":0}]}
               ]},
               {"playerStats":[
                 {"puuid":"player","damage":[
                   {"headshots":2,"bodyshots":4,"legshots":1}
                 ]}
               ]}
             ]}
            """);

        var (headshots, shots) = ValorantMatchArchive.ExtractHeadshots(match.RootElement, "player");
        headshots.Should().Be(6);
        shots.Should().Be(21);

        var service = typeof(ValorantStatsSyncService);
        var statsType = service.GetNestedType("AggregatedStats", BindingFlags.NonPublic)!;
        var stats = Activator.CreateInstance(statsType, nonPublic: true)!;
        service.GetMethod("ExtractPlayerStats", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [match.RootElement, "player", stats]);

        statsType.GetField("TotalHeadshots")!.GetValue(stats).Should().Be(6);
        statsType.GetField("TotalShots")!.GetValue(stats).Should().Be(21);

        var agents = (IDictionary)statsType.GetField("AgentStats")!.GetValue(stats)!;
        var agent = agents["agent-b"]!;
        agent.GetType().GetField("Headshots")!.GetValue(agent).Should().Be(6);
        agent.GetType().GetField("Shots")!.GetValue(agent).Should().Be(21);
        agent.GetType().GetField("Assists")!.GetValue(agent).Should().Be(1);
    }

    [Fact]
    public void HeadshotExtraction_DistinguishesMissingDataFromZeroHits()
    {
        using var missing = JsonDocument.Parse("""{"roundResults":[]}""");
        using var zero = JsonDocument.Parse("""{"roundResults":[{"playerStats":[{"puuid":"player","damage":[{"headshots":0,"bodyshots":3,"legshots":0}]}]}]}""");
        var unknown = ValorantMatchArchive.ExtractHeadshotData(missing.RootElement, "player");
        var known = ValorantMatchArchive.ExtractHeadshotData(zero.RootElement, "player");
        unknown.Complete.Should().BeFalse();
        known.Complete.Should().BeTrue();
        known.Headshots.Should().Be(0);
        known.Shots.Should().Be(3);
    }

    [Fact]
    public void RestoreAgentStats_RoundTripsNewFields()
    {
        var service = typeof(ValorantStatsSyncService);
        var statsType = service.GetNestedType("AggregatedStats", BindingFlags.NonPublic)!;
        var stats = Activator.CreateInstance(statsType, nonPublic: true)!;
        var agents = statsType.GetField("AgentStats")!.GetValue(stats)!;
        var json = """
            [{"agent_id":"agent-x","games_played":10,"wins":6,"kills":80,"deaths":40,
              "assists":15,"headshots":30,"shots":120}]
            """;
        service.GetMethod("RestoreAgentStats", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [json, agents]);

        var agent = ((IDictionary)agents)["agent-x"]!;
        agent.GetType().GetField("Assists")!.GetValue(agent).Should().Be(15);
        agent.GetType().GetField("Headshots")!.GetValue(agent).Should().Be(30);
        agent.GetType().GetField("Shots")!.GetValue(agent).Should().Be(120);
    }

    [Fact]
    public void RestoreAgentStats_DefaultsToZeroWhenFieldsMissing()
    {
        var service = typeof(ValorantStatsSyncService);
        var statsType = service.GetNestedType("AggregatedStats", BindingFlags.NonPublic)!;
        var stats = Activator.CreateInstance(statsType, nonPublic: true)!;
        var agents = statsType.GetField("AgentStats")!.GetValue(stats)!;
        var json = """[{"agent_id":"agent-old","games_played":5,"wins":3,"kills":30,"deaths":15}]""";
        service.GetMethod("RestoreAgentStats", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [json, agents]);

        var agent = ((IDictionary)agents)["agent-old"]!;
        agent.GetType().GetField("Assists")!.GetValue(agent).Should().Be(0);
        agent.GetType().GetField("Headshots")!.GetValue(agent).Should().Be(0);
        agent.GetType().GetField("Shots")!.GetValue(agent).Should().Be(0);
    }
}
