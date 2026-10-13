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
    public async Task TournamentBroadcast_CreateBindsTheRouteTournamentAndOrganization()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var tournamentId = await _seeder.SeedTournamentAsync(ownerId);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE tournaments SET organization_id = @orgId WHERE id = @tournamentId", new { orgId, tournamentId });
        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.PostAsJsonAsync($"/api/tournaments/{tournamentId}/broadcast/shows", new { title = "Tournament production" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var showId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("showId").GetGuid();
        var show = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}");
        show.GetProperty("tournamentId").GetGuid().Should().Be(tournamentId);
        show.GetProperty("orgId").GetGuid().Should().Be(orgId);
        show.GetProperty("tournamentSlug").GetString().Should().NotBeNullOrEmpty();
        var workspace = await client.GetFromJsonAsync<JsonElement>($"/api/tournaments/{tournamentId}/broadcast");
        workspace.GetProperty("shows").EnumerateArray().Should().Contain(item => item.GetProperty("id").GetGuid() == showId);
        var configuration = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}/configuration");
        configuration.GetProperty("tournamentId").GetGuid().Should().Be(tournamentId);
        configuration.GetProperty("game").GetString().Should().Be("test-game");
        configuration.GetProperty("eventLabel").GetString().Should().Be("Test Tournament");
    }

    [Fact]
    public async Task TournamentBroadcast_UnassignedTournamentLinksOnlyToAnOwnedOrganization()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var (_, foreignOrg) = await SeedOrgOwnerAsync();
        var tournamentId = await _seeder.SeedTournamentAsync(ownerId);
        var client = _factory.CreateAuthenticatedClient(ownerId);
        (await client.PostAsJsonAsync($"/api/tournaments/{tournamentId}/broadcast/shows", new { title = "Invalid", orgId = foreignOrg })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync($"/api/tournaments/{tournamentId}/broadcast/shows", new { title = "Owned", orgId })).StatusCode.Should().Be(HttpStatusCode.OK);
        await using var conn = _seeder.OpenConnection();
        (await conn.QuerySingleAsync<Guid>("SELECT organization_id FROM tournaments WHERE id = @tournamentId", new { tournamentId })).Should().Be(orgId);
        (await client.PostAsJsonAsync($"/api/tournaments/{tournamentId}/broadcast/shows", new { title = "Move", orgId = foreignOrg })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task TournamentBroadcast_OrganizerDoesNotInheritOrganizationOwnerRights()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var (organizerId, otherOrg) = await SeedOrgOwnerAsync();
        var tournamentId = await _seeder.SeedTournamentAsync(organizerId);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE tournaments SET organization_id = @orgId WHERE id = @tournamentId", new { orgId, tournamentId });
        var client = _factory.CreateAuthenticatedClient(organizerId);
        var workspace = await client.GetFromJsonAsync<JsonElement>($"/api/tournaments/{tournamentId}/broadcast");
        workspace.GetProperty("canManage").GetBoolean().Should().BeFalse();
        workspace.GetProperty("shows").GetArrayLength().Should().Be(0);
        (await client.PostAsJsonAsync($"/api/tournaments/{tournamentId}/broadcast/shows", new { title = "Unauthorized" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (outsider, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(outsider).GetAsync($"/api/tournaments/{tournamentId}/broadcast")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.CreateAuthenticatedClient(ownerId).GetAsync($"/api/tournaments/{tournamentId}/broadcast")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ShowConfiguration_DoesNotReadForeignMatchBindingsInsertedOutsideTheApi()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var (foreignOwner, foreignOrg) = await SeedOrgOwnerAsync();
        var foreignTournament = await _seeder.SeedTournamentAsync(foreignOwner);
        var stage = await _seeder.SeedStageAsync(foreignTournament);
        var version = await _seeder.SeedBracketVersionAsync(foreignTournament, stage);
        var match = await _seeder.SeedMatchAsync(version);
        var showId = await SeedShowAsync(orgId, ownerId);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE tournaments SET organization_id = @foreignOrg WHERE id = @foreignTournament; INSERT INTO broadcast_show_matches (show_id, match_id, match_code) VALUES (@showId, @match, 'BAD')", new { foreignOrg, foreignTournament, showId, match });
        var client = _factory.CreateAuthenticatedClient(ownerId);
        var configuration = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}/configuration");
        configuration.GetProperty("matches").GetArrayLength().Should().Be(0);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}");
        detail.GetProperty("matchSlots").GetArrayLength().Should().Be(0);
    }
}
