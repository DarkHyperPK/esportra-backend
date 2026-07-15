using System.Text.Json;
using Esportra.Api.Services;
using Xunit;

namespace Esportra.Api.Tests;

public class ValorantMatchStatsHelperTests
{
    [Fact]
    public void BuildRoundAggregates_ComputesDamageHeadshotsAndFirstBlood()
    {
        const string json = """
        {
          "roundResults": [
            {
              "playerStats": [
                {
                  "puuid": "player-a",
                  "damage": [
                    { "damage": 120, "headshots": 2, "bodyshots": 1, "legshots": 0 }
                  ],
                  "kills": [
                    { "timeSinceRoundStartMillis": 5000 }
                  ]
                },
                {
                  "puuid": "player-b",
                  "damage": [
                    { "damage": 80, "headshots": 0, "bodyshots": 2, "legshots": 1 }
                  ],
                  "kills": [
                    { "timeSinceRoundStartMillis": 12000 }
                  ]
                }
              ]
            },
            {
              "playerStats": [
                {
                  "puuid": "player-b",
                  "damage": [
                    { "damage": 40, "headshots": 1, "bodyshots": 0, "legshots": 0 }
                  ],
                  "kills": [
                    { "timeSinceRoundStartMillis": 3000 }
                  ]
                }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var aggregates = ValorantMatchStatsHelper.BuildRoundAggregates(doc.RootElement);

        Assert.True(aggregates.TryGetValue("player-a", out var playerA));
        Assert.Equal(120, playerA.TotalDamage);
        Assert.Equal(2, playerA.Headshots);
        Assert.Equal(1, playerA.FirstBloods);

        Assert.True(aggregates.TryGetValue("player-b", out var playerB));
        Assert.Equal(120, playerB.TotalDamage);
        Assert.Equal(1, playerB.FirstBloods);
    }

    [Theory]
    [InlineData(4400, 22, 200)]
    [InlineData(0, 0, null)]
    public void ComputeAcs_ReturnsExpected(int score, int roundsPlayed, int? expected)
    {
        Assert.Equal(expected, ValorantMatchStatsHelper.ComputeAcs(score, roundsPlayed));
    }

    [Theory]
    [InlineData(18, 12, 1.5)]
    [InlineData(5, 0, 5.0)]
    [InlineData(0, 0, null)]
    public void ComputeKdRatio_ReturnsExpected(int kills, int deaths, double? expected)
    {
        Assert.Equal(expected, ValorantMatchStatsHelper.ComputeKdRatio(kills, deaths));
    }

    [Fact]
    public void BuildRoundTimeline_ReturnsWinningTeamPerRound()
    {
        const string json = """
        {
          "roundResults": [
            { "winningTeam": "Blue", "roundResultCode": "Elimination", "playerStats": [] },
            { "winningTeam": "Red", "roundResultCode": "Defuse", "playerStats": [] }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var timeline = ValorantMatchStatsHelper.BuildRoundTimeline(doc.RootElement);

        Assert.Equal(2, timeline.Count);
        Assert.Equal(1, timeline[0].Round);
        Assert.Equal("Blue", timeline[0].WinningTeam);
        Assert.Equal("Elimination", timeline[0].ResultCode);
        Assert.Equal("Red", timeline[1].WinningTeam);
    }

    [Fact]
    public void BuildEconomyTimeline_AggregatesSpentByTeam()
    {
        const string json = """
        {
          "players": [
            { "puuid": "blue-player", "teamId": "Blue" },
            { "puuid": "red-player", "teamId": "Red" }
          ],
          "roundResults": [
            {
              "playerStats": [
                { "puuid": "blue-player", "economy": { "spent": 2900 } },
                { "puuid": "red-player", "economy": { "spent": 800 } }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var timeline = ValorantMatchStatsHelper.BuildEconomyTimeline(doc.RootElement);

        Assert.Single(timeline);
        Assert.Equal(2900, timeline[0].BlueSpent);
        Assert.Equal(800, timeline[0].RedSpent);
    }

    [Fact]
    public void BuildRoundTimeline_UsesRoundNumAndResultFallback()
    {
        const string json = """
        {
          "roundResults": [
            {
              "roundNum": 3,
              "winningTeam": "Blue",
              "roundResult": "Detonate",
              "plantSite": "A",
              "playerStats": []
            },
            {
              "winningTeam": "Red",
              "roundResultCode": "Defuse",
              "playerStats": []
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var timeline = ValorantMatchStatsHelper.BuildRoundTimeline(doc.RootElement);

        Assert.Equal(2, timeline.Count);
        Assert.Equal(3, timeline[0].Round);
        Assert.Equal("Detonate", timeline[0].ResultCode);
        Assert.Equal("A", timeline[0].PlantSite);
        Assert.Equal(2, timeline[1].Round);
        Assert.Equal("Defuse", timeline[1].ResultCode);
    }

    [Fact]
    public void BuildRoundAggregates_UsesKillDtoKillerForFirstBlood()
    {
        const string json = """
        {
          "roundResults": [
            {
              "playerStats": [
                {
                  "puuid": "player-a",
                  "kills": [
                    { "killer": "player-b", "timeSinceRoundStartMillis": 4000 }
                  ]
                },
                {
                  "puuid": "player-b",
                  "kills": [
                    { "killer": "player-b", "timeSinceRoundStartMillis": 4000 }
                  ]
                }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var aggregates = ValorantMatchStatsHelper.BuildRoundAggregates(doc.RootElement);

        Assert.True(aggregates.TryGetValue("player-b", out var playerB));
        Assert.Equal(1, playerB.FirstBloods);
        Assert.True(aggregates.TryGetValue("player-a", out var playerA));
        Assert.Equal(0, playerA.FirstBloods);
    }

    [Fact]
    public void BuildWeaponSummaries_CountsWeaponsFromEconomy()
    {
        const string json = """
        {
          "roundResults": [
            {
              "playerStats": [
                { "economy": { "weapon": "Vandal" } },
                { "economy": { "weapon": "Vandal" } },
                { "economy": { "weapon": "Operator" } }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var summaries = ValorantMatchStatsHelper.BuildWeaponSummaries(doc.RootElement);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("Vandal", summaries[0].Weapon);
        Assert.Equal(2, summaries[0].RoundCount);
    }

    [Fact]
    public void BuildEconomyTimeline_IncludesLoadoutValues()
    {
        const string json = """
        {
          "players": [
            { "puuid": "blue-player", "teamId": "Blue" }
          ],
          "roundResults": [
            {
              "playerStats": [
                { "puuid": "blue-player", "economy": { "spent": 2900, "loadoutValue": 4700 } }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var timeline = ValorantMatchStatsHelper.BuildEconomyTimeline(doc.RootElement);

        Assert.Single(timeline);
        Assert.Equal(2900, timeline[0].BlueSpent);
        Assert.Equal(4700, timeline[0].BlueLoadout);
    }

    [Fact]
    public void ComputeHeadshotPercent_ReturnsNullWhenNoShots()
    {
        Assert.Null(ValorantMatchStatsHelper.ComputeHeadshotPercent(0, 0, 0));
        Assert.Equal(40d, ValorantMatchStatsHelper.ComputeHeadshotPercent(4, 6, 0));
    }
}
