using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Broadcast;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    [Fact]
    public async Task Publication_SavesImmutableSnapshot_AllocatesVersions_AndEnforcesOwnership()
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(org, owner);
        var client = _factory.CreateAuthenticatedClient(owner);
        await using var conn = _seeder.OpenConnection();
        var segmentId = await conn.QuerySingleAsync<Guid>("INSERT INTO rundown_segments (show_id, type, title) VALUES (@showId, 'pre_show', 'Opening') RETURNING id", new { showId });
        var first = await client.PostAsync($"/api/broadcast/shows/{showId}/push-config", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rundownVersion").GetInt64().Should().Be(1);
        await conn.ExecuteAsync("UPDATE rundown_segments SET title = 'Updated opening' WHERE id = @segmentId", new { segmentId });
        var publication = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}/publication");
        publication.GetProperty("rundown")[0].GetProperty("title").GetString().Should().Be("Opening");
        var sends = await Task.WhenAll(client.PostAsync($"/api/broadcast/shows/{showId}/push-config", null), client.PostAsync($"/api/broadcast/shows/{showId}/push-config", null));
        var versions = new List<long>();
        foreach (var send in sends) { send.StatusCode.Should().Be(HttpStatusCode.OK); versions.Add((await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rundownVersion").GetInt64()); }
        versions.Should().BeEquivalentTo([2L, 3L]);
        (await conn.ExecuteScalarAsync<long>("SELECT rundown_version FROM broadcast_shows WHERE id = @showId", new { showId })).Should().Be(3);
        var (outsider, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(outsider).GetAsync($"/api/broadcast/shows/{showId}/publication")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.CreateClient().GetAsync($"/api/broadcast/shows/{showId}/publication")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NodePublication_RecoversPublishedPlan_AndRejectsForeignRevokedAndForgedVersionReports()
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        var (foreignOwner, foreignOrg) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(org, owner);
        var foreignShow = await SeedShowAsync(foreignOrg, foreignOwner);
        var ownerClient = _factory.CreateAuthenticatedClient(owner);
        await ownerClient.PostAsync($"/api/broadcast/shows/{showId}/push-config", null);
        await ownerClient.PostAsync($"/api/broadcast/shows/{showId}/push-config", null);
        await using var conn = _seeder.OpenConnection();
        var token = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var nodeId = await conn.QuerySingleAsync<Guid>("INSERT INTO broadcast_nodes (org_id, name, token_hash) VALUES (@org, 'Test producer', @hash) RETURNING id", new { org, hash });
        var nodeClient = _factory.CreateClient(); nodeClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        var recovered = await nodeClient.GetFromJsonAsync<JsonElement>($"/api/broadcast/nodes/shows/{showId}/publication");
        recovered.GetProperty("showId").GetString().Should().Be(showId.ToString());
        recovered.GetProperty("rundownVersion").GetInt64().Should().Be(2);
        (await nodeClient.GetAsync($"/api/broadcast/nodes/shows/{foreignShow}/publication")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var (hub, _) = CreateNodeHealthHub(nodeId);
        var runtime = new NodeRuntimeStatus("production", true, true, "idle", false, false, false, "0.1.0", Plan: new NodeRundownPlan(showId, 1, 2));
        await hub.NodeHeartbeat(new HeartbeatPayload(DateTimeOffset.UtcNow, Runtime: runtime));
        var publication = await ownerClient.GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}/publication");
        publication.GetProperty("nodes").GetArrayLength().Should().Be(1);
        JsonDocument.Parse(publication.GetProperty("nodes")[0].GetProperty("plan").GetString()!).RootElement.GetProperty("pendingVersion").GetInt64().Should().Be(2);
        await hub.NodeHeartbeat(new HeartbeatPayload(DateTimeOffset.UtcNow, Runtime: runtime with { Plan = new NodeRundownPlan(foreignShow, 1) }));
        await hub.NodeHeartbeat(new HeartbeatPayload(DateTimeOffset.UtcNow, Runtime: runtime with { Plan = new NodeRundownPlan(showId, 3) }));
        (await conn.ExecuteScalarAsync<string>("SELECT runtime_status->'plan'->>'showId' FROM broadcast_nodes WHERE id = @nodeId", new { nodeId })).Should().Be(showId.ToString());
        (await conn.ExecuteScalarAsync<long>("SELECT (runtime_status->'plan'->>'version')::bigint FROM broadcast_nodes WHERE id = @nodeId", new { nodeId })).Should().Be(1);
        await conn.ExecuteAsync("UPDATE broadcast_nodes SET is_active = false WHERE id = @nodeId", new { nodeId });
        (await nodeClient.GetAsync($"/api/broadcast/nodes/shows/{showId}/publication")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
