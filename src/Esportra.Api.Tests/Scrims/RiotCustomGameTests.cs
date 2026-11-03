using System.Text.Json;
using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Scrims;

public class RiotCustomGameTests
{
    private static bool IsCustom(string matchInfoJson)
    {
        using var doc = JsonDocument.Parse($"{{\"matchInfo\":{matchInfoJson}}}");
        return RiotMatchCandidates.IsCustomGame(doc.RootElement);
    }

    [Theory]
    [InlineData("{\"queueId\":\"\"}")]
    [InlineData("{\"queueId\":\"custom\"}")]
    [InlineData("{\"queueId\":null}")]
    [InlineData("{}")]
    public void IsCustomGame_ReturnsTrue_ForCustomGames(string info) => IsCustom(info).Should().BeTrue();

    [Theory]
    [InlineData("{\"queueId\":\"competitive\"}")]
    [InlineData("{\"queueId\":\"unrated\"}")]
    [InlineData("{\"queueId\":\"\",\"isRanked\":true}")]
    public void IsCustomGame_ReturnsFalse_ForMatchmadeQueues(string info) => IsCustom(info).Should().BeFalse();

    private static bool Accepts(string matchInfoJson, bool allow)
    {
        using var doc = JsonDocument.Parse($"{{\"matchInfo\":{matchInfoJson}}}");
        return RiotMatchCandidates.Accepts(doc.RootElement, allow);
    }

    [Theory]
    [InlineData("{\"queueId\":\"competitive\"}")]
    [InlineData("{\"queueId\":\"unrated\"}")]
    [InlineData("{\"queueId\":\"\",\"isRanked\":true}")]
    public void Accepts_Matchmade_Queues_Only_When_Allowed(string info)
    {
        Accepts(info, allow: false).Should().BeFalse();
        Accepts(info, allow: true).Should().BeTrue();
    }

    [Fact]
    public void Accepts_Custom_Games_Either_Way()
    {
        Accepts("{\"queueId\":\"\"}", allow: false).Should().BeTrue();
        Accepts("{\"queueId\":\"\"}", allow: true).Should().BeTrue();
    }

    [Fact]
    public void Accepts_Nothing_Without_MatchInfo()
    {
        using var doc = JsonDocument.Parse("{}");
        RiotMatchCandidates.Accepts(doc.RootElement, allowNonCustom: true).Should().BeFalse();
    }

    [Fact]
    public void IsCustomGame_ReturnsFalse_WithoutMatchInfo()
    {
        using var doc = JsonDocument.Parse("{}");
        RiotMatchCandidates.IsCustomGame(doc.RootElement).Should().BeFalse();
    }
}
