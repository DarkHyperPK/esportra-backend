using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;
public sealed partial class BroadcastShowEndpointsTests
{
    [Fact]
    public async Task BroadcastBracket_returns_authorized_published_graph_and_keeps_history_out_of_picker()
    {
        var setup = await SeedSetupMatchAsync(); var owner = _factory.CreateAuthenticatedClient(setup.Owner);
        await owner.PostAsJsonAsync($"/api/broadcast/shows/{setup.Show}/matches", new { matchId = setup.Match, matchCode = "FINAL" });
        using var conn = _seeder.OpenConnection(); var org = await conn.ExecuteScalarAsync<Guid>("SELECT org_id FROM broadcast_shows WHERE id=@id", new { id = setup.Show });
        var token = Guid.NewGuid().ToString("N"); var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        await conn.ExecuteAsync("INSERT INTO broadcast_nodes(org_id,name,token_hash) VALUES(@org,'Bracket test',@hash)", new { org, hash });
        var node = _factory.CreateClient(); node.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}"); var path = $"/api/broadcast/nodes/shows/{setup.Show}/bracket";
        (await node.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.PostAsync($"/api/broadcast/shows/{setup.Show}/push-config", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await node.GetAsync(path); result.StatusCode.Should().Be(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
        var graph = await result.Content.ReadFromJsonAsync<JsonElement>(); graph.GetProperty("stages").GetArrayLength().Should().Be(1); graph.GetProperty("matches")[0].GetProperty("teams")[0].GetProperty("name").GetString().Should().Be("North");
        (await node.GetAsync($"/api/broadcast/nodes/shows/{Guid.NewGuid()}/bracket")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await conn.ExecuteAsync("UPDATE brkt_matches SET status='completed',winner_id=@winner,team1_score=1,team2_score=0 WHERE id=@id", new { id = setup.Match, winner = setup.TeamA });
        var config = await owner.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{setup.Show}/configuration"); config.GetProperty("matches").GetArrayLength().Should().Be(0); config.GetProperty("matchHistory").GetArrayLength().Should().Be(1);
        await conn.ExecuteAsync("UPDATE brkt_versions SET status='draft' WHERE id=@id", new { id = setup.Version });
        graph = await node.GetFromJsonAsync<JsonElement>(path); graph.GetProperty("matches").GetArrayLength().Should().Be(0);
    }
}
