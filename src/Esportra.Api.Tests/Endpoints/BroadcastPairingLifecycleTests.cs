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
    public async Task Pairing_CodeIsSingleUseAndExpiresInTenMinutes_NodeListOmitsCredential()
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        var organizer = _factory.CreateAuthenticatedClient(owner);
        var issued = await organizer.PostAsJsonAsync("/api/broadcast/nodes/codes", new { orgId = org });
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        var code = body.GetProperty("code").GetString();
        code.Should().HaveLength(8);
        body.GetProperty("expiresAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(5));
        var client = _factory.CreateClient();
        var request = new { code, name = "Test broadcast PC", capabilities = new { mode = "production", obs = true, hud = true, gep = true } };
        var paired = await client.PostAsJsonAsync("/api/broadcast/nodes/pair", request);
        paired.StatusCode.Should().Be(HttpStatusCode.OK);
        var identity = await paired.Content.ReadFromJsonAsync<JsonElement>();
        identity.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        (await client.PostAsJsonAsync("/api/broadcast/nodes/pair", request)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var nodes = await organizer.GetFromJsonAsync<JsonElement>($"/api/broadcast/nodes?orgId={org}");
        nodes[0].GetProperty("id").GetGuid().Should().Be(identity.GetProperty("nodeId").GetGuid());
        nodes[0].TryGetProperty("token", out _).Should().BeFalse();
        nodes[0].TryGetProperty("tokenHash", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Pairing_ExpiredCodeIsRejected_AndOnlyOwnerCanRevokeActiveNode()
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        var organizer = _factory.CreateAuthenticatedClient(owner);
        var issued = await organizer.PostAsJsonAsync("/api/broadcast/nodes/codes", new { orgId = org });
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE broadcast_pairing_codes SET expires_at = NOW() - INTERVAL '1 second' WHERE org_id = @org", new { org });
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/broadcast/nodes/pair", new { code, name = "Expired" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var token = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var nodeId = await conn.QuerySingleAsync<Guid>("INSERT INTO broadcast_nodes (org_id, name, token_hash) VALUES (@org, 'Test PC', @hash) RETURNING id", new { org, hash });
        var (outsider, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(outsider).DeleteAsync($"/api/broadcast/nodes/{nodeId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await organizer.DeleteAsync($"/api/broadcast/nodes/{nodeId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        (await client.GetAsync("/api/broadcast/nodes/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
