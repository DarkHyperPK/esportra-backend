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
    public void ComputeHeadshotPercent_ReturnsNullWhenNoShots()
    {
        Assert.Null(ValorantMatchStatsHelper.ComputeHeadshotPercent(0, 0, 0));
        Assert.Equal(40d, ValorantMatchStatsHelper.ComputeHeadshotPercent(4, 6, 0));
    }
}
