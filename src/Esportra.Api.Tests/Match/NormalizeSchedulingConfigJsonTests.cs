using System.Text.Json;
using Esportra.Api.Endpoints;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Match;

public sealed class NormalizeSchedulingConfigJsonTests
{
    [Theory]
    [InlineData("selfPlayEnabled", "self_play_enabled")]
    [InlineData("checkinEnabled", "checkin_enabled")]
    [InlineData("checkinWindowMinutes", "checkin_window_minutes")]
    [InlineData("roundDeadline", "round_deadline")]
    [InlineData("roundDeadlines", "round_deadlines")]
    [InlineData("scheduleStartTime", "schedule_start_time")]
    [InlineData("matchIntervalMinutes", "match_interval_minutes")]
    [InlineData("dailyStartTime", "daily_start_time")]
    [InlineData("schedulingMode", "scheduling_mode")]
    public void NormalizeSchedulingConfigJson_RenamesCamelCaseKey_ToSnakeCase(
        string camelKey, string snakeKey)
    {
        var input = $@"{{""{camelKey}"": true}}";

        var result = MatchSystemEndpoints.NormalizeSchedulingConfigJson(input);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty(snakeKey, out _).Should().BeTrue(
            because: $"{camelKey} should be renamed to {snakeKey}");
        doc.RootElement.TryGetProperty(camelKey, out _).Should().BeFalse(
            because: $"original key {camelKey} should be removed");
    }

    [Fact]
    public void NormalizeSchedulingConfigJson_PreservesUnknownKey_Unchanged()
    {
        const string input = """{"customKey": "hello", "another_unknown": 42}""";

        var result = MatchSystemEndpoints.NormalizeSchedulingConfigJson(input);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("customKey", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("another_unknown", out _).Should().BeTrue();
    }

    [Fact]
    public void NormalizeSchedulingConfigJson_SnakeCaseWins_WhenBothFormsPresent()
    {
        // If input contains both camelCase and snake_case form,
        // the snake_case entry is preserved and the camelCase entry is dropped.
        const string input = """{"checkinEnabled": true, "checkin_enabled": false}""";

        var result = MatchSystemEndpoints.NormalizeSchedulingConfigJson(input);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.TryGetProperty("checkin_enabled", out var val).Should().BeTrue();
        val.GetBoolean().Should().BeFalse(because: "snake_case value wins");
        doc.RootElement.TryGetProperty("checkinEnabled", out _).Should().BeFalse();
    }
}
