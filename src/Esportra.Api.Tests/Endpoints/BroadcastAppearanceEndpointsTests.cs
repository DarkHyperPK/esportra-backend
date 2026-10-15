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
    public async Task Appearance_PreservesOfficialNames_ValidatesScope_RejectsStaleWritesAndLiveEdits()
    {
        var setup = await SeedSetupMatchAsync(); var client = _factory.CreateAuthenticatedClient(setup.Owner);
        await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/matches", new { matchId = setup.Match, matchCode = "FINAL" });
        var path = $"/api/broadcast/shows/{setup.Show}/appearance";
        var appearance = new BroadcastAppearance(BannerTheme: "compact", SponsorSeconds: 12, Matches: new() { [setup.Match] = [new("North broadcast", "#12aBcD"), new()] }, StingerName: "Fixture Stinger");
        var saved = await client.PutAsJsonAsync(path, appearance);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        (await saved.Content.ReadFromJsonAsync<BroadcastAppearance>())!.Revision.Should().Be(1);
        (await client.PutAsJsonAsync(path, appearance)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var current = (await client.GetFromJsonAsync<BroadcastAppearance>(path))!;
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/screens/import", new { matchId = setup.Match, screens = new[] { new { overlay = "ingame", title = "Map" } } })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/rundown/templates", new { matchId = setup.Match, template = "bo1", requestId = Guid.NewGuid() })).StatusCode.Should().Be(HttpStatusCode.OK);
        var config = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{setup.Show}/configuration");
        config.GetProperty("rundown").EnumerateArray().Should().OnlyContain(item => item.GetProperty("plan").GetProperty("transition").GetProperty("name").GetString() == "Fixture Stinger");
        (await client.PutAsJsonAsync(path, current with { StingerName = " " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync(path, current with { Pack = "uninstalled" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync(path, current with { SponsorSeconds = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync(path, current with { Matches = new() { [Guid.NewGuid()] = [new(), new()] } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync(path, current with { Matches = new() { [setup.Match] = [new("<script>"), new()] } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var (outsider, _) = await SeedOrgOwnerAsync(); var foreign = _factory.CreateAuthenticatedClient(outsider);
        (await foreign.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await foreign.PutAsJsonAsync(path, current)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using var conn = _seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<string>("SELECT name FROM teams WHERE id = @id", new { id = setup.TeamA })).Should().Be("North");
        await conn.ExecuteAsync("UPDATE broadcast_shows SET status = 'live' WHERE id = @show", new { show = setup.Show });
        (await client.PutAsJsonAsync(path, current)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task NodeContext_ReadsFreshEligibleSelectedMatch_ExcludesUnpublishedRundown_AndRejectsForeignRevoked()
    {
        var setup = await SeedSetupMatchAsync(); var owner = _factory.CreateAuthenticatedClient(setup.Owner);
        await owner.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/matches", new { matchId = setup.Match, matchCode = "FINAL" });
        await using var conn = _seeder.OpenConnection();
        var org = await conn.ExecuteScalarAsync<Guid>("SELECT org_id FROM broadcast_shows WHERE id = @show", new { show = setup.Show });
        var token = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var nodeId = await conn.QuerySingleAsync<Guid>("INSERT INTO broadcast_nodes (org_id, name, token_hash) VALUES (@org, 'Synthetic producer', @hash) RETURNING id", new { org, hash });
        var node = _factory.CreateClient(); node.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        var path = $"/api/broadcast/nodes/shows/{setup.Show}/matches/{setup.Match}/context";
        (await node.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await owner.PostAsync($"/api/broadcast/shows/{setup.Show}/push-config", null);
        var tournament = await conn.ExecuteScalarAsync<Guid>("SELECT tournament_id FROM broadcast_shows WHERE id = @show", new { show = setup.Show });
        const string lineup = """{"starters":[{"DisplayName":"Synthetic starter","UserId":"not-forwarded"}],"coaches":[{"displayName":"Synthetic coach"}]}""";
        await conn.ExecuteAsync("INSERT INTO tournament_participants (tournament_id, user_id, team_id, status, participant_type, roster_lineup, team_members) VALUES (@tournament, @owner, @team, 'approved', 'team', @lineup::jsonb, '[]'::jsonb)", new { tournament, owner = setup.Owner, team = setup.TeamA, lineup });
        await conn.ExecuteAsync("UPDATE teams SET name = 'North renamed' WHERE id = @id; UPDATE brkt_matches SET scheduled_time = '2026-10-20T14:00:00Z', team1_score = 1 WHERE id = @match; INSERT INTO rundown_segments (show_id, type, title) VALUES (@show, 'pre_show', 'Unpublished item')", new { id = setup.TeamA, match = setup.Match, show = setup.Show });
        var context = await node.GetFromJsonAsync<JsonElement>(path);
        context.TryGetProperty("rundown", out _).Should().BeFalse();
        context.GetProperty("match").GetProperty("teams")[0].GetProperty("name").GetString().Should().Be("North renamed");
        context.GetProperty("match").GetProperty("teams")[0].GetProperty("teamId").GetGuid().Should().Be(setup.TeamA);
        context.GetProperty("match").GetProperty("teams")[0].GetProperty("seriesScore").GetInt32().Should().Be(1);
        var roster = context.GetProperty("match").GetProperty("teams")[0].GetProperty("roster");
        roster[0].GetProperty("name").GetString().Should().Be("Synthetic starter");
        roster[1].GetProperty("role").GetString().Should().Be("coach");
        roster[0].TryGetProperty("userId", out _).Should().BeFalse();
        context.GetProperty("schedule")[0].GetProperty("matchId").GetString().Should().Be(setup.Match.ToString());
        context.GetProperty("match").GetProperty("scheduledAt").GetDateTime().Should().Be(new DateTime(2026, 10, 20, 14, 0, 0));
        (await node.GetAsync($"/api/broadcast/nodes/shows/{Guid.NewGuid()}/matches/{setup.Match}/context")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await conn.ExecuteAsync("UPDATE brkt_matches SET status = 'completed', team1_score = 2, winner_id = @team WHERE id = @match", new { match = setup.Match, team = setup.TeamA });
        var finalized = await node.GetAsync(path);
        finalized.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var finalContext = await finalized.Content.ReadFromJsonAsync<JsonElement>();
        finalContext.GetProperty("matches").GetArrayLength().Should().Be(0);
        finalContext.GetProperty("match").GetProperty("teams")[0].GetProperty("seriesScore").GetInt32().Should().Be(2);
        await conn.ExecuteAsync("UPDATE broadcast_nodes SET is_active = false WHERE id = @nodeId", new { nodeId });
        (await node.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
