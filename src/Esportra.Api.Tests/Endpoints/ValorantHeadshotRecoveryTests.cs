using System.Text.Json;
using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed class ValorantHeadshotRecoveryTests
{
    private static ValorantMatchArchive.Match Match(string id, int headshots = 2) => new()
    {
        Id = id,
        AgentId = "agent",
        Won = true,
        Kills = 10,
        Deaths = 5,
        Assists = 3,
        Score = 500,
        Headshots = headshots,
        Shots = 10,
        HeadshotDataComplete = true
    };

    private static ValorantHeadshotRecovery.Totals Totals(int count = 1) => new()
    {
        Matches = count,
        Wins = count,
        Losses = 0,
        Kills = 10 * count,
        Deaths = 5 * count,
        Assists = 3 * count,
        Score = 500 * count,
        PopularAgents = $$"""[{"agent_id":"agent","games_played":{{count}},"wins":{{count}},"kills":{{10 * count}},"deaths":{{5 * count}},"assists":{{3 * count}},"kd_ratio":2,"custom":"preserve"}]"""
    };

    [Fact]
    public void Recovery_RepairsOverallAndAgentHitData_PreservingOtherFields()
    {
        var result = ValorantHeadshotRecovery.Build(Totals(2), [Match("one", 1), Match("two", 5)]);
        result.Should().NotBeNull();
        result!.Percentage.Should().Be(30m);
        result.Headshots.Should().Be(6);
        result.Shots.Should().Be(20);
        using var agents = JsonDocument.Parse(result.PopularAgents!);
        agents.RootElement[0].GetProperty("hs_pct").GetDecimal().Should().Be(30m);
        agents.RootElement[0].GetProperty("headshot_data_complete").GetBoolean().Should().BeTrue();
        agents.RootElement[0].GetProperty("custom").GetString().Should().Be("preserve");
        agents.RootElement[0].GetProperty("kd_ratio").GetDecimal().Should().Be(2m);
        agents.RootElement[0].GetProperty("games_played").GetInt32().Should().Be(2);
    }

    [Fact]
    public void Recovery_PreservesValidZero_AndDoesNotInventRatioForNoHits()
    {
        ValorantHeadshotRecovery.Build(Totals(), [Match("one", 0)])!.Percentage.Should().Be(0m);
        ValorantHeadshotRecovery.Build(Totals(), [Match("one", 0) with { Shots = 0 }])!.Percentage.Should().BeNull();
    }

    [Fact]
    public void AgentRecovery_UsesOnlyCompleteMatchingGames_WithoutClaimingOverallCoverage()
    {
        const string agents = """
            [{"agent_id":"agent","games_played":1,"wins":1,"kills":10,"deaths":5,"assists":3,"headshot_data_complete":false},
             {"agent_id":"missing","games_played":2,"wins":2,"kills":20,"deaths":10,"assists":6,"headshot_data_complete":false}]
            """;
        var matches = new[] { Match("one"), Match("two") with { AgentId = "missing", HeadshotDataComplete = false } };
        ValorantHeadshotRecovery.Build(Totals(3), matches).Should().BeNull();
        using var result = JsonDocument.Parse(ValorantHeadshotRecovery.RepairAgents(agents, matches)!);
        result.RootElement[0].GetProperty("headshot_data_complete").GetBoolean().Should().BeTrue();
        result.RootElement[0].GetProperty("hs_pct").GetDecimal().Should().Be(20m);
        result.RootElement[1].GetProperty("headshot_data_complete").GetBoolean().Should().BeFalse();
        result.RootElement[1].TryGetProperty("hs_pct", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("counter")]
    [InlineData("extra")]
    [InlineData("invalid-hits")]
    public void Recovery_RejectsUnprovenCoverage(string problem)
    {
        var matches = problem switch
        {
            "incomplete" => new[] { Match("one") with { HeadshotDataComplete = false }, Match("two") },
            "missing" => [Match("one")],
            "duplicate" => [Match("one"), Match("one")],
            "counter" => [Match("one") with { Kills = 20 }, Match("two")],
            "extra" => [Match("one"), Match("two"), Match("three")],
            _ => [Match("one") with { Headshots = 11 }, Match("two")]
        };
        ValorantHeadshotRecovery.Build(Totals(2), matches).Should().BeNull();
    }
}
