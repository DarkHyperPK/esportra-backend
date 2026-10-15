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
    public async Task Timeline_ExposesOnlyOwnerScopedMetadata_AndExcludesOlderEventsAndRawFields()
    {
        var (owner, org) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(org, owner);
        await using var conn = _seeder.OpenConnection();
        var node = await conn.QuerySingleAsync<Guid>("INSERT INTO broadcast_nodes (org_id, name, token_hash) VALUES (@org, 'History desk', @hash) RETURNING id", new { org, hash = Guid.NewGuid().ToString("N") });
        var payload = JsonSerializer.Serialize(new { action = "hold", actorKind = "operator", cause = "manual", wallClockMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rawGep = "must stay private", token = "not returned" });
        await conn.ExecuteAsync("""
            INSERT INTO broadcast_events (show_id,node_id,event_id,event_type,payload,source,created_at)
            VALUES (@showId,@node,@id,'ProductionAction',@payload::jsonb,'node',NOW()),
                   (@showId,@node,@oldId,'ProductionAction',@payload::jsonb,'node',NOW()-INTERVAL '61 minutes');
            """, new { showId, node, id = Guid.NewGuid().ToString(), oldId = Guid.NewGuid().ToString(), payload });
        var response = await _factory.CreateAuthenticatedClient(owner).GetFromJsonAsync<JsonElement>($"/api/broadcast/shows/{showId}/timeline");
        response.GetProperty("items").GetArrayLength().Should().Be(1);
        response.GetProperty("items")[0].GetProperty("actorKind").GetString().Should().Be("operator");
        response.GetRawText().Should().NotContain("rawGep").And.NotContain("not returned").And.NotContain("must stay private");
        var (outsider, _) = await SeedOrgOwnerAsync();
        (await _factory.CreateAuthenticatedClient(outsider).GetAsync($"/api/broadcast/shows/{showId}/timeline")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
