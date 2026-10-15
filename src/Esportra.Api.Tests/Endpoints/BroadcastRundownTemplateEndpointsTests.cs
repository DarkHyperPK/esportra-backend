using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Core.Broadcast;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    [Fact]
    public async Task RundownTemplate_UsesEligibleMatchFormat_PreservesSettings_AndRetriesWithoutDuplicates()
    {
        var setup = await SeedSetupMatchAsync(); var client = _factory.CreateAuthenticatedClient(setup.Owner);
        await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/matches", new { matchId = setup.Match, matchCode = "FINAL" });
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE brkt_matches SET best_of = 3 WHERE id = @match", new { match = setup.Match });
        var body = new { matchId = setup.Match, template = "bo3", requestId = Guid.NewGuid() };
        foreach (var attempt in Enumerable.Range(0, 2))
        {
            var result = await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/rundown/templates", body);
            result.StatusCode.Should().Be(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
            (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alreadyImported").GetBoolean().Should().Be(attempt == 1);
        }
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM rundown_segments WHERE show_id = @showId", new { showId = setup.Show })).Should().Be(17);
        await conn.ExecuteAsync("UPDATE brkt_matches SET best_of = 1 WHERE id = @match", new { match = setup.Match });
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/rundown/templates", new { body.matchId, template = "bo1", body.requestId })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await conn.ExecuteAsync("UPDATE brkt_matches SET best_of = 3 WHERE id = @match", new { match = setup.Match });
        var config = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{setup.Show}/configuration");
        var items = config.GetProperty("rundown");
        items.EnumerateArray().Where(item => item.GetProperty("type").GetString() == "match").Select(item => item.GetProperty("plan").GetProperty("mapNumber").GetInt32()).Should().Equal(1, 2, 3);
        items[0].GetProperty("plan").GetProperty("mode").GetString().Should().Be("manual");
        items[0].GetProperty("matchId").GetString().Should().Be(setup.Match.ToString());
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/rundown/templates", new { matchId = setup.Match, template = "bo1", requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var (other, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(other).PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/rundown/templates", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await conn.ExecuteAsync("UPDATE brkt_matches SET status = 'completed', winner_id = @team WHERE id = @match", new { team = setup.TeamA, match = setup.Match });
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/rundown/templates", body)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RundownPlan_SavesValidatedSettings_RejectsUnsupportedAutomation_AndProjectsCorruptSettingsAsManual()
    {
        var (owner, org) = await SeedOrgOwnerAsync(); var show = await SeedShowAsync(org, owner);
        var client = _factory.CreateAuthenticatedClient(owner); await using var conn = _seeder.OpenConnection();
        var segment = await conn.QuerySingleAsync<Guid>("INSERT INTO rundown_segments (show_id, title, type, config) VALUES (@show, 'Map', 'match', '{\"notes\":\"Keep this note\"}'::jsonb) RETURNING id", new { show });
        var plan = new BroadcastSegmentPlan("round_live", "automatic", "window", 5, ["ingame"], "Game", new("fade", "Fade", 300), 1);
        var path = $"/api/broadcast/shows/{show}/segments/{segment}/plan";
        var saved = await client.PutAsJsonAsync(path, plan);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        (await conn.ExecuteScalarAsync<string>("SELECT config->>'notes' FROM rundown_segments WHERE id = @segment", new { segment })).Should().Be("Keep this note");
        var config = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{show}/configuration");
        config.GetProperty("rundown")[0].GetProperty("plan").GetProperty("holdSeconds").GetInt32().Should().Be(5);
        (await client.PutAsJsonAsync(path, plan with { Trigger = "veto_complete" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync(path, plan with { Graphics = ["not-a-screen"] })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var (other, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(other).PutAsJsonAsync(path, plan)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await conn.ExecuteAsync("UPDATE rundown_segments SET config = config || '{\"plan\":{\"mode\":\"automatic\",\"trigger\":\"not-supported\"}}'::jsonb WHERE id = @segment", new { segment });
        var repaired = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{show}/configuration");
        repaired.GetProperty("rundown")[0].GetProperty("plan").ValueKind.Should().Be(JsonValueKind.Null);
        repaired.GetProperty("rundown")[0].GetProperty("planWarning").GetString().Should().Contain("stays manual");
        await conn.ExecuteAsync("UPDATE broadcast_shows SET status = 'live' WHERE id = @show", new { show });
        (await client.PutAsJsonAsync(path, plan)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
