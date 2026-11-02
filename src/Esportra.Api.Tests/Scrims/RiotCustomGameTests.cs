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

    [Fact]
    public void IsCustomGame_ReturnsFalse_WithoutMatchInfo()
    {
        using var doc = JsonDocument.Parse("{}");
        RiotMatchCandidates.IsCustomGame(doc.RootElement).Should().BeFalse();
    }
}
