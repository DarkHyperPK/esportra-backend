using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

public sealed partial class BroadcastShowEndpointsTests
{
    [Fact]
    public async Task Pairing_RoutesWereRetired_WhenWorkstationsMovedToAccountSignIn()
    {
        await SeedOrgOwnerAsync();
        // No pairing handler exists anymore: Air signs in with an account, codes
        // were the scrapped web-broadcast flow. These POSTs may answer 405 rather
        // than 404 because the node-revocation route /nodes/{nodeId} has the same
        // path shape and ASP.NET's method matcher rejects the method before the
        // GUID constraint is evaluated. What matters is that pairing never succeeds.
        foreach (var path in new[] { "/api/broadcast/nodes/codes", "/api/broadcast/nodes/pair" })
        {
            var response = await _factory.CreateClient().PostAsync(path, JsonContent.Create(new { }));
            response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        }
    }

    [Fact]
    public async Task LegacyNodes_OnlyOwnerCanRevokeActiveNode_AndSessionEndsWithIt()
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        var organizer = _factory.CreateAuthenticatedClient(owner);
        await using var conn = _seeder.OpenConnection();
        var token = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var nodeId = await conn.QuerySingleAsync<Guid>("INSERT INTO broadcast_nodes (org_id, name, token_hash) VALUES (@org, 'Test PC', @hash) RETURNING id", new { org, hash });
        var (outsider, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(outsider).DeleteAsync($"/api/broadcast/nodes/{nodeId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await organizer.DeleteAsync($"/api/broadcast/nodes/{nodeId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        (await client.GetAsync("/api/broadcast/nodes/session")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
