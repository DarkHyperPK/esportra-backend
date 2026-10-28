using System.Reflection;
using System.Text.Json;
using Esportra.Api.Services;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed class ValorantMatchArchiveTests
{
    [Fact]
    public void Matchlist_IncludesBothActsAllQueuesDeduplicatesAndHasNoFiveHundredMatchCap()
    {
        var history = Enumerable.Range(0, 650).Select(i => new { matchId = $"match-{i}", gameStartTimeMillis = 1000L + i, queueId = i % 2 == 0 ? "competitive" : "unrated" }).ToList();
        history.Add(new { matchId = "match-1", gameStartTimeMillis = 1001L, queueId = "unrated" });
        history.Add(new { matchId = "old", gameStartTimeMillis = 999L, queueId = "competitive" });
        var result = ValorantMatchArchive.CandidateIds(JsonSerializer.Serialize(new { history }), DateTimeOffset.FromUnixTimeMilliseconds(1000));
        result.Should().HaveCount(650);
        result.Should().Contain("match-0").And.Contain("match-649").And.NotContain("old");
    }

    [Fact]
    public void Matchlist_KeepsEntriesWithoutAUsableTimestampForDetailLookup()
    {
        const string json = """{"history":[{"matchId":"missing"},{"matchId":"malformed","gameStartTimeMillis":"unknown"},{"matchId":"old","gameStartTimeMillis":999}]}""";
        var ids = ValorantMatchArchive.CandidateIds(json, DateTimeOffset.FromUnixTimeMilliseconds(1000));
        ids.Should().Equal("missing", "malformed");
    }

    [Fact]
    public void CurrentActSelection_SortsUnorderedHistoryAndDoesNotCountDuplicateIds()
    {
        var history = new[] {
            new { matchId = "old", gameStartTimeMillis = 900L, queueId = "competitive" },
            new { matchId = "new", gameStartTimeMillis = 1200L, queueId = "competitive" },
            new { matchId = "cursor", gameStartTimeMillis = 1100L, queueId = "competitive" },
            new { matchId = "new", gameStartTimeMillis = 1200L, queueId = "competitive" },
            new { matchId = "other-mode", gameStartTimeMillis = 1300L, queueId = "unrated" },
        };
        var method = typeof(ValorantStatsSyncService).GetMethod("ParseCompetitiveMatchIds", BindingFlags.NonPublic | BindingFlags.Static)!;
        var ids = (List<string>)method.Invoke(null, [JsonSerializer.Serialize(new { history }), "cursor", false, DateTimeOffset.FromUnixTimeMilliseconds(1000)])!;
        ids.Should().Equal("new");
    }

    [Fact]
    public void CurrentActSelection_DoesNotStopAfterFiveHundredCompetitiveMatches()
    {
        var history = Enumerable.Range(0, 650).Select(i => new { matchId = $"game-{i}", gameStartTimeMillis = 1000L + i, queueId = "competitive" });
        var method = typeof(ValorantStatsSyncService).GetMethod("ParseCompetitiveMatchIds", BindingFlags.NonPublic | BindingFlags.Static)!;
        var ids = (List<string>)method.Invoke(null, [JsonSerializer.Serialize(new { history }), null, true, DateTimeOffset.FromUnixTimeMilliseconds(1000)])!;
        ids.Should().HaveCount(650);
    }

    [Fact]
    public void MatchDetails_ExtractOnlyLinkedPlayersStatsAndPreserveDraws()
    {
        const string json = """
            {"matchInfo":{"matchId":"match-1","seasonId":"act-previous","gameStartMillis":1500,"queueId":"unrated"},
             "teams":[{"teamId":"Blue","won":false},{"teamId":"Red","won":false}],
             "players":[{"puuid":"other-private","teamId":"Red","characterId":"other-agent","stats":{"kills":99,"deaths":1,"assists":1,"score":1}},
                        {"puuid":"owner-private","teamId":"Blue","characterId":"agent-a","competitiveTier":10,"stats":{"kills":12,"deaths":8,"assists":4,"score":900}}]}
            """;
        var match = ValorantMatchArchive.ParseMatch(json, "match-1", "owner-private");
        match.Should().NotBeNull();
        match!.ActId.Should().Be("act-previous");
        match.QueueId.Should().Be("unrated");
        match.AgentId.Should().Be("agent-a");
        match.Kills.Should().Be(12);
        match.Won.Should().BeNull();
        JsonSerializer.Serialize(match).Should().NotContain("private");
        ValorantMatchArchive.ParseMatch(json, "wrong-id", "owner-private").Should().BeNull();
        ValorantMatchArchive.ParseMatch(json, "match-1", "missing").Should().BeNull();
    }

    [Fact]
    public void CurrentAct_IdentifiesCurrentActAcrossEpisodeBoundary()
    {
        using var json = JsonDocument.Parse("""
            [{"uuid":"future","type":"EAresSeasonType::Act","startTime":"2027-01-01T00:00:00Z","endTime":"2027-03-01T00:00:00Z"},
             {"uuid":"previous","type":"EAresSeasonType::Act","startTime":"2026-08-01T00:00:00Z","endTime":"2026-10-01T00:00:00Z"},
             {"uuid":"current","type":"EAresSeasonType::Act","startTime":"2026-10-01T00:00:00Z","endTime":"2027-01-01T00:00:00Z"}]
            """);
        var method = typeof(ValorantStatsSyncService).GetMethod("FindCurrentAct", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [json.RootElement, DateTimeOffset.Parse("2026-10-28T00:00:00Z")]);
        result.Should().NotBeNull();
        result!.GetType().GetProperty("ActId")!.GetValue(result).Should().Be("current");
        result.GetType().GetProperty("ActId")!.GetValue(result).Should().Be("current");
    }
}
