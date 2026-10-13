using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    [Fact]
    public async Task Duplicate_CopiesPlanningWithFreshIdsAndNoRuntimeHistory()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId, status: "ended");
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("""
            UPDATE broadcast_shows SET scheduled_at = NOW(), started_at = NOW(), ended_at = NOW(), last_synced_at = NOW() WHERE id = @showId;
            INSERT INTO rundown_segments (show_id, type, title, status, duration_seconds, config) VALUES (@showId, 'pre_show', 'Opening', 'done', 90, '{"notes":"Stand by"}');
            INSERT INTO broadcast_talent (show_id, name, role) VALUES (@showId, 'Caster', 'caster');
            INSERT INTO broadcast_show_sponsors (show_id, name) VALUES (@showId, 'Sponsor');
            INSERT INTO broadcast_events (show_id, event_id, event_type) VALUES (@showId, @eventId, 'SegmentTake');
            """, new { showId, eventId = Guid.NewGuid().ToString() });
        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.PostAsync($"/api/broadcast/shows/{showId}/duplicate", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var newId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("showId").GetGuid();
        newId.Should().NotBe(showId);
        var show = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{newId}");
        show.GetProperty("status").GetString().Should().Be("draft");
        show.GetProperty("scheduledAt").ValueKind.Should().Be(JsonValueKind.Null);
        show.GetProperty("lastSyncedAt").ValueKind.Should().Be(JsonValueKind.Null);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM rundown_segments WHERE show_id = @newId AND status = 'pending' AND config->>'notes' = 'Stand by'", new { newId })).Should().Be(1);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_events WHERE show_id = @newId", new { newId })).Should().Be(0);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_talent WHERE show_id = @newId", new { newId })).Should().Be(1);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_show_sponsors WHERE show_id = @newId", new { newId })).Should().Be(1);
    }

    [Fact]
    public async Task Lifecycle_RejectsForeignOwnerAndPreservesHistoricalShows()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var (otherOwner, _) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId);
        var foreign = _factory.CreateAuthenticatedClient(otherOwner);
        (await foreign.DeleteAsync($"/api/broadcast/shows/{showId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await foreign.PostAsync($"/api/broadcast/shows/{showId}/duplicate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO broadcast_events (show_id, event_id, event_type) VALUES (@showId, @eventId, 'SegmentTake')", new { showId, eventId = Guid.NewGuid().ToString() });
        (await _factory.CreateAuthenticatedClient(ownerId).DeleteAsync($"/api/broadcast/shows/{showId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM broadcast_shows WHERE id = @showId", new { showId })).Should().Be(1);
    }

    [Fact]
    public async Task Delete_RemovesUnusedDraftAndPlanning()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO rundown_segments (show_id, type, title) VALUES (@showId, 'custom', 'Draft cue')", new { showId });
        (await _factory.CreateAuthenticatedClient(ownerId).DeleteAsync($"/api/broadcast/shows/{showId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM rundown_segments WHERE show_id = @showId", new { showId })).Should().Be(0);
    }

    [Fact]
    public async Task ClosedShows_CannotPublishAndLiveScheduleOrMatchesCannotChange()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var ended = await SeedShowAsync(orgId, ownerId, status: "ended");
        var live = await SeedShowAsync(orgId, ownerId, status: "live");
        var client = _factory.CreateAuthenticatedClient(ownerId);
        (await client.PostAsync($"/api/broadcast/shows/{ended}/push-config", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PatchAsJsonAsync($"/api/broadcast/shows/{live}", new { scheduledAt = "2026-12-01T12:00:00Z" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{live}/matches", new { matchId = Guid.NewGuid(), matchCode = "MAIN" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"/api/broadcast/shows/{live}/matches/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Activity_PaginatesEqualTimestampsAndOmitsPrivatePayloads()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId);
        var (otherOwner, _) = await SeedOrgOwnerAsync();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO broadcast_events (show_id, event_id, event_type, payload, created_at)
            SELECT @showId, gen_random_uuid()::text, 'SegmentTake', '{"private":"fixture-private-value"}', '2026-10-14T12:00:00Z'::timestamptz FROM generate_series(1,3)
            """, new { showId });
        var client = _factory.CreateAuthenticatedClient(ownerId);
        var firstResponse = await client.GetAsync($"/api/broadcast/shows/{showId}/activity?limit=2");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await firstResponse.Content.ReadAsStringAsync();
        raw.Should().NotContain("fixture-private-value").And.NotContain("payload");
        var first = JsonDocument.Parse(raw).RootElement;
        var cursor = first.GetProperty("nextCursor");
        var beforeAt = Uri.EscapeDataString(cursor.GetProperty("beforeAt").GetString()!);
        var second = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}/activity?limit=2&beforeAt={beforeAt}&beforeId={cursor.GetProperty("beforeId").GetString()}");
        first.GetProperty("items").GetArrayLength().Should().Be(2);
        second.GetProperty("items").GetArrayLength().Should().Be(1);
        second.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        var ids = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(item => item.GetProperty("id").GetGuid());
        ids.Distinct().Count().Should().Be(3);
        (await _factory.CreateAuthenticatedClient(otherOwner).GetAsync($"/api/broadcast/shows/{showId}/activity")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/broadcast/shows/{showId}/activity?limit=101")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/api/broadcast/shows/{showId}/activity?beforeAt={beforeAt}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
