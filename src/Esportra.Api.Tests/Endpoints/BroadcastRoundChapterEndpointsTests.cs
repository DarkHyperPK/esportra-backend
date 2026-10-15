using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    [Fact]
    public async Task RoundChapter_MetadataDrainBuildsOwnerChapter_AndRejectsRawCapture()
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(org, owner, status: "ended");
        var token = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO broadcast_nodes (org_id, name, token_hash) VALUES (@org, 'Chapter PC', @hash); UPDATE broadcast_shows SET started_at = @started WHERE id = @showId", new { org, hash, started, showId });
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        var key = $"round:{Guid.NewGuid()}:HAVEN:1";
        var events = new[] {
            new { eventId = Guid.NewGuid(), eventType = "RoundStarted", payload = JsonSerializer.SerializeToElement(new { segmentId = key, segmentName = "HAVEN · Round 1", segmentType = "round", wallClockMs = started.AddSeconds(10).ToUnixTimeMilliseconds() }) },
            new { eventId = Guid.NewGuid(), eventType = "RoundEnded", payload = JsonSerializer.SerializeToElement(new { segmentId = key, segmentName = "HAVEN · Round 1", segmentType = "round", wallClockMs = started.AddSeconds(50).ToUnixTimeMilliseconds() }) },
            new { eventId = Guid.NewGuid(), eventType = "RoundStarted", payload = JsonSerializer.SerializeToElement(new { rawGep = "forbidden" }) },
        };
        var delivered = await client.PostAsJsonAsync($"/api/broadcast/shows/{showId}/events/drain", new { events });
        var ack = await delivered.Content.ReadFromJsonAsync<JsonElement>();
        ack.GetProperty("accepted").GetInt32().Should().Be(2);
        ack.GetProperty("rejected").GetInt32().Should().Be(1);
        var chapters = await _factory.CreateAuthenticatedClient(owner).GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}/vod-chapters");
        var chapter = chapters.GetProperty("chapters")[0];
        chapter.GetProperty("segmentType").GetString().Should().Be("round");
        chapter.GetProperty("offsetSeconds").GetDouble().Should().BeApproximately(10, 0.001);
        chapter.GetProperty("durationSeconds").GetDouble().Should().Be(40);
        chapter.GetProperty("incomplete").GetBoolean().Should().BeFalse();
    }
}
