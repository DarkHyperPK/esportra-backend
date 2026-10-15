using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastSponsorProofEndpointsTests
{
    [Fact]
    public async Task ProofReplay_ClosesOpenPlacement_AndAcknowledgesRetriesWithoutDoubleCounting()
    {
        var (token, _, _, showId, _, sponsorId) = await SeedFullContextAsync();
        var client = CreateNodeClient(token);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var endedAt = startedAt.AddSeconds(30);
        var route = $"/api/broadcast/shows/{showId}/sponsor-impressions";
        var open = new { impressions = new[] { new { sponsorId, startedAt, endedAt = (DateTimeOffset?)null, slotType = "lower_third" } } };
        var closed = new { impressions = new[] { new { sponsorId, startedAt, endedAt = (DateTimeOffset?)endedAt, slotType = "lower_third" } } };
        (await client.PostAsJsonAsync(route, open)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(route, closed)).StatusCode.Should().Be(HttpStatusCode.OK);
        var retry = await client.PostAsJsonAsync(route, closed);
        (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acknowledged").GetInt32().Should().Be(1);
        await client.PostAsJsonAsync(route, open);
        await using var conn = _seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM broadcast_sponsor_impressions WHERE show_id = @showId", new { showId })).Should().Be(1);
        var storedEnd = await conn.ExecuteScalarAsync<DateTime>("SELECT ended_at FROM broadcast_sponsor_impressions WHERE show_id = @showId", new { showId });
        storedEnd.Should().BeCloseTo(endedAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ProofReplay_RejectsForeignSponsorAndReversedTimeBeforeWriting()
    {
        var (token, _, _, showId, _, sponsorId) = await SeedFullContextAsync();
        var (_, _, _, _, _, foreignSponsor) = await SeedFullContextAsync();
        var client = CreateNodeClient(token);
        var startedAt = DateTimeOffset.UtcNow;
        var route = $"/api/broadcast/shows/{showId}/sponsor-impressions";
        (await client.PostAsJsonAsync(route, new { impressions = new[] { new { sponsorId = foreignSponsor, startedAt, slotType = "scorebug" } } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(route, new { impressions = new[] { new { sponsorId, startedAt, endedAt = startedAt.AddSeconds(-1), slotType = "scorebug" } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var conn = _seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM broadcast_sponsor_impressions WHERE show_id = @showId", new { showId })).Should().Be(0);
    }
}
