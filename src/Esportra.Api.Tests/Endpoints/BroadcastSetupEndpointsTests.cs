using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    private async Task<(Guid Owner, Guid Show, Guid Match, Guid Version, Guid TeamA, Guid TeamB)> SeedSetupMatchAsync()
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        var tournament = await _seeder.SeedTournamentAsync(owner);
        var stage = await _seeder.SeedStageAsync(tournament);
        var version = await _seeder.SeedBracketVersionAsync(tournament, stage, status: "active");
        var teamA = await _seeder.SeedTeamAsync(owner, "North");
        var teamB = await _seeder.SeedTeamAsync(owner, "South");
        var match = await _seeder.SeedMatchAsync(version, team1Id: teamA, team2Id: teamB);
        var show = await SeedShowAsync(org, owner);
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE tournaments SET organization_id = @org WHERE id = @tournament; UPDATE broadcast_shows SET tournament_id = @tournament WHERE id = @show", new { org, tournament, show });
        return (owner, show, match, version, teamA, teamB);
    }

    [Fact]
    public async Task SetupPicker_ExcludesPlayedAndUnresolvedMatches_AndRejectsPlayedSelection()
    {
        var setup = await SeedSetupMatchAsync();
        var played = await _seeder.SeedMatchAsync(setup.Version, matchNumber: 2, team1Id: setup.TeamA, team2Id: setup.TeamB, matchStatus: "completed");
        await _seeder.SeedMatchAsync(setup.Version, matchNumber: 3);
        var client = _factory.CreateAuthenticatedClient(setup.Owner);
        var choices = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{setup.Show}/available-matches");
        choices.GetArrayLength().Should().Be(1);
        choices[0].GetProperty("id").GetGuid().Should().Be(setup.Match);
        choices[0].GetProperty("status").GetString().Should().Be("pending");
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/matches", new { matchId = played, matchCode = "PLAYED" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetupScreens_ImportPersistsOrderAndConfiguration_RejectsForeignAndLiveWrites()
    {
        var setup = await SeedSetupMatchAsync();
        var client = _factory.CreateAuthenticatedClient(setup.Owner);
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/matches", new { matchId = setup.Match, matchCode = "FINAL" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var body = new { matchId = setup.Match, screens = new[] { new { overlay = "starting-soon", title = "Opening" }, new { overlay = "ingame", title = "Map" } } };
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/screens/import", body)).StatusCode.Should().Be(HttpStatusCode.OK);
        var config = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{setup.Show}/configuration");
        config.GetProperty("rundown")[0].GetProperty("title").GetString().Should().Be("Opening");
        config.GetProperty("rundown")[1].GetProperty("overlay").GetString().Should().Be("ingame");
        config.GetProperty("rundown")[1].GetProperty("matchId").GetString().Should().Be(setup.Match.ToString());
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/push-config", new { matchId = setup.Match })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/push-config", new { matchId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var (outsider, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(outsider).PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/screens/import", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/screens/import", new { matchId = Guid.NewGuid(), screens = body.screens })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/screens/import", new { matchId = setup.Match, screens = new[] { new { overlay = "../control", title = "Invalid" } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var conn = _seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM rundown_segments WHERE show_id = @showId", new { showId = setup.Show })).Should().Be(2);
        await conn.ExecuteAsync("UPDATE broadcast_shows SET status = 'live' WHERE id = @showId", new { showId = setup.Show });
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/screens/import", body)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
