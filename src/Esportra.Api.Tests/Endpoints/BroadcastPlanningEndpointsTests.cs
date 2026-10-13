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
    public async Task UpdateShow_PreservesOmittedScheduleAndSupportsOwnerTitleChange()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId);
        var client = _factory.CreateAuthenticatedClient(ownerId);
        var schedule = "2026-11-20T12:00:00Z";
        (await client.PatchAsJsonAsync($"/api/broadcast/shows/{showId}", new { scheduledAt = schedule })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PatchAsJsonAsync($"/api/broadcast/shows/{showId}", new { title = "Renamed show" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var show = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}");
        show.GetProperty("title").GetString().Should().Be("Renamed show");
        show.GetProperty("scheduledAt").GetDateTime().Should().Be(DateTime.Parse(schedule).ToUniversalTime());
    }

    [Fact]
    public async Task UpdateShow_RejectsLiveTitleChangeAndNonOwner()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId, status: "live");
        var client = _factory.CreateAuthenticatedClient(ownerId);
        (await client.PatchAsJsonAsync($"/api/broadcast/shows/{showId}", new { title = "New title" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var (otherOwner, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(otherOwner).PatchAsJsonAsync($"/api/broadcast/shows/{showId}", new { title = "New title" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateShow_RejectsTournamentFromAnotherOrganization()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var (otherOwner, otherOrg) = await SeedOrgOwnerAsync();
        var tournamentId = await _seeder.SeedTournamentAsync(otherOwner);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE tournaments SET organization_id = @otherOrg WHERE id = @tournamentId", new { otherOrg, tournamentId });
        var showId = await SeedShowAsync(orgId, ownerId);
        (await _factory.CreateAuthenticatedClient(ownerId).PatchAsJsonAsync($"/api/broadcast/shows/{showId}", new { tournamentId })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Talent_AddListRemoveAndOwnerBoundary()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId);
        var client = _factory.CreateAuthenticatedClient(ownerId);
        var created = await client.PostAsJsonAsync($"/api/broadcast/shows/{showId}/talent", new { name = "Caster One", role = "caster" });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var roster = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}/talent");
        roster.GetArrayLength().Should().Be(1);
        var (otherOwner, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(otherOwner).DeleteAsync($"/api/broadcast/shows/{showId}/talent/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/broadcast/shows/{showId}/talent/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rundown_ReorderIsAtomicAndRejectsStaleIds()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO rundown_segments(id, show_id, title, type, display_order) SELECT id, @showId, 'Segment', 'pre_show', position::integer FROM unnest(@ids::uuid[]) WITH ORDINALITY AS segments(id, position)", new { showId, ids });
        var client = _factory.CreateAuthenticatedClient(ownerId);
        var order = ids.Reverse().ToArray();
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{showId}/segments/reorder", new { segmentIds = order })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await conn.QueryAsync<Guid>("SELECT id FROM rundown_segments WHERE show_id = @showId ORDER BY display_order", new { showId })).Should().Equal(order);
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{showId}/segments/reorder", new { segmentIds = new[] { ids[0], Guid.NewGuid(), ids[2] } })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conn.QueryAsync<Guid>("SELECT id FROM rundown_segments WHERE show_id = @showId ORDER BY display_order", new { showId })).Should().Equal(order);
    }
}
