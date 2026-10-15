using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Hubs;
using Esportra.Contracts.Database;
using Esportra.Contracts.Broadcast;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    [Fact]
    public async Task NodeSession_RequiresActiveNodeToken_AndNeverReturnsCredential()
    {
        var (_, org) = await SeedOrgOwnerAsync();
        var token = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        await using var conn = _seeder.OpenConnection();
        var nodeId = await conn.QuerySingleAsync<Guid>("INSERT INTO broadcast_nodes (org_id, name, token_hash) VALUES (@org, 'Test PC', @hash) RETURNING id", new { org, hash });
        var client = _factory.CreateClient();
        (await client.GetAsync("/api/broadcast/nodes/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        var response = await client.GetFromJsonAsync<JsonElement>("/api/broadcast/nodes/session");
        response.GetProperty("nodeId").GetGuid().Should().Be(nodeId);
        response.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("nodeId", "active");
        await conn.ExecuteAsync("UPDATE broadcast_nodes SET is_active = false WHERE id = @nodeId", new { nodeId });
        var revoked = await client.GetAsync("/api/broadcast/nodes/session");
        revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("node_not_active");
    }

    [Fact]
    public async Task NodeHealth_HeartbeatPersistsSafeRuntime_AndOwnerCanReadIt()
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        await using var conn = _seeder.OpenConnection();
        var nodeId = await conn.QuerySingleAsync<Guid>("INSERT INTO broadcast_nodes (org_id, name, token_hash) VALUES (@org, 'Production PC', @hash) RETURNING id", new { org, hash = Guid.NewGuid().ToString("N") });
        var (hub, context) = CreateNodeHealthHub(nodeId);
        var runtime = new NodeRuntimeStatus("production", true, false, "stale", true, false, true, "0.1.0", [new NodeOutputHealth(Guid.NewGuid(), "Gameplay", 2, DateTimeOffset.UtcNow)]);
        await hub.NodeHeartbeat(new HeartbeatPayload(DateTimeOffset.UtcNow, Runtime: runtime));

        var client = _factory.CreateAuthenticatedClient(owner);
        var nodes = await client.GetFromJsonAsync<JsonElement>($"/api/broadcast/nodes?orgId={org}");
        nodes.GetArrayLength().Should().Be(1);
        var health = JsonDocument.Parse(nodes[0].GetProperty("runtimeStatus").GetString()!).RootElement;
        health.GetProperty("feedState").GetString().Should().Be("stale");
        health.GetProperty("obsConnected").GetBoolean().Should().BeFalse();
        health.GetProperty("outputs")[0].GetProperty("connectionCount").GetInt32().Should().Be(2);
        nodes[0].GetProperty("runtimeSeenAt").ValueKind.Should().Be(JsonValueKind.String);
        nodes[0].TryGetProperty("tokenHash", out _).Should().BeFalse();
        context.DidNotReceive().Abort();
        var (outsider, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(outsider).GetAsync($"/api/broadcast/nodes?orgId={org}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NodeHealth_InvalidAndLegacyHeartbeatsDoNotRefreshRuntime_AndRevokedNodeIsAborted()
    {
        var (_, org) = await SeedOrgOwnerAsync();
        await using var conn = _seeder.OpenConnection();
        var nodeId = await conn.QuerySingleAsync<Guid>("INSERT INTO broadcast_nodes (org_id, name, token_hash, runtime_status, runtime_seen_at) VALUES (@org, 'Observer', @hash, '{\"feedState\":\"stale\"}'::jsonb, '2026-01-01T00:00:00Z') RETURNING id", new { org, hash = Guid.NewGuid().ToString("N") });
        var (hub, context) = CreateNodeHealthHub(nodeId);
        await hub.NodeHeartbeat(new HeartbeatPayload(DateTimeOffset.UtcNow));
        await hub.NodeHeartbeat(new HeartbeatPayload(DateTimeOffset.UtcNow, Runtime: new NodeRuntimeStatus("production", true, true, "invented", false, false, true, "0.1.0")));
        (await conn.ExecuteScalarAsync<DateTime>("SELECT runtime_seen_at FROM broadcast_nodes WHERE id = @nodeId", new { nodeId })).Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        (await conn.ExecuteScalarAsync<string>("SELECT runtime_status->>'feedState' FROM broadcast_nodes WHERE id = @nodeId", new { nodeId })).Should().Be("stale");
        await conn.ExecuteAsync("UPDATE broadcast_nodes SET is_active = false, last_seen_at = NULL WHERE id = @nodeId", new { nodeId });
        await hub.NodeHeartbeat(new HeartbeatPayload(DateTimeOffset.UtcNow));
        context.Received(1).Abort();
        (await conn.ExecuteScalarAsync<DateTime?>("SELECT last_seen_at FROM broadcast_nodes WHERE id = @nodeId", new { nodeId })).Should().BeNull();
    }

    private (BroadcastHub Hub, HubCallerContext Context) CreateNodeHealthHub(Guid nodeId)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(Guid.NewGuid().ToString("N"));
        context.Items.Returns(new Dictionary<object, object?> { ["ConnectionType"] = "node", ["NodeId"] = nodeId });
        var hub = new BroadcastHub(_factory.Services.GetRequiredService<IDbConnectionFactory>(), NullLogger<BroadcastHub>.Instance) { Context = context };
        return (hub, context);
    }
}
