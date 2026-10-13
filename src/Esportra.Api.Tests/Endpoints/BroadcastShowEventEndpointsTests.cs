using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastDrainEndpointsTests
{
    [Fact]
    public async Task ScopedDrain_PreservesEndedShowAndOriginalTime_Idempotently()
    {
        var (token, orgId, showId, nodeId) = await SeedNodeAndLiveShowAsync();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE broadcast_shows SET status = 'ended' WHERE id = @showId", new { showId });
        var eventId = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.Parse("2026-10-10T10:20:00Z");
        var client = CreateNodeClient(token);
        var body = new { events = new[] { new { eventId, eventType = "SegmentTake", payload = new { segmentName = "Opening" }, ts = timestamp } } };
        var route = $"/api/broadcast/shows/{showId}/events/drain";
        for (var retry = 0; retry < 2; retry++)
        {
            var response = await client.PostAsJsonAsync(route, body);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32().Should().Be(1);
        }
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_events WHERE show_id = @showId AND event_id = @eventId AND node_id = @nodeId AND created_at = @timestamp", new { showId, eventId, nodeId, timestamp })).Should().Be(1);
    }

    [Fact]
    public async Task ScopedDrain_RejectsShowOutsideNodeOrganization()
    {
        var (token, _, _, _) = await SeedNodeAndLiveShowAsync();
        var (_, _, otherShow, _) = await SeedNodeAndLiveShowAsync();
        var response = await CreateNodeClient(token).PostAsJsonAsync($"/api/broadcast/shows/{otherShow}/events/drain", new { events = new[] { new { eventId = Guid.NewGuid().ToString(), eventType = "SegmentTake" } } });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
