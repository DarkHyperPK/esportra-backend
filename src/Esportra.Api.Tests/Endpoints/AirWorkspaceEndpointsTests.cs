using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using Esportra.Contracts.Air;
using FluentAssertions;
using Xunit;
using SkiaSharp;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class AirWorkspaceEndpointsTests(ApiFactory factory)
{
    private async Task<(Guid User, Guid Workspace, HttpClient Client)> Account(bool admin = false)
    {
        var id = Guid.NewGuid(); await factory.Seeder.SeedAuthUserAsync(id);
        if (admin) await factory.Seeder.SeedAdminPanelRoleAsync(id);
        var client = factory.CreateAuthenticatedClient(id);
        var response = await client.PostAsJsonAsync("/api/air/session", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (id, body.GetProperty("workspaces")[0].GetProperty("id").GetGuid(), client);
    }
    private static AirProjectDocument Empty() => new(1, [], [], [], new("manual", []));

    [Fact]
    public async Task Trial_is_account_bound_and_unrestricted_without_organizer_role()
    {
        var account = await Account(); var first = await account.Client.PostAsJsonAsync("/api/air/session", new { });
        var original = await first.Content.ReadFromJsonAsync<JsonElement>();
        (await account.Client.PostAsJsonAsync("/api/air/workspaces", new { name = "Second workspace" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await account.Client.PostAsJsonAsync("/api/air/session", new { }); var next = await again.Content.ReadFromJsonAsync<JsonElement>();
        original.GetProperty("account").GetProperty("trialStartedAt").GetString().Should().Be(next.GetProperty("account").GetProperty("trialStartedAt").GetString());
        using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM air_accounts WHERE user_id=@id", new { id = account.User })).Should().Be(1);
        for (var i = 0; i < 3; i++) (await account.Client.PostAsJsonAsync("/api/air/devices", new AirDeviceRequest(Guid.NewGuid(), "Trial PC", "production"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Projects_are_owned_revisioned_and_separate_from_tournaments()
    {
        var owner = await Account(); var stranger = await Account(); var id = Guid.NewGuid();
        var created = await owner.Client.PostAsJsonAsync($"/api/air/workspaces/{owner.Workspace}/projects", new AirProjectRequest("Independent final", Empty(), Id: id));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        (await stranger.Client.GetAsync($"/api/air/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.Client.PutAsJsonAsync($"/api/air/projects/{id}", new AirProjectRequest("Stolen", Empty(), 1))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.Client.PutAsJsonAsync($"/api/air/projects/{id}", new AirProjectRequest("My revision", Empty(), 1))).StatusCode.Should().Be(HttpStatusCode.OK);
        var conflict = await owner.Client.PutAsJsonAsync($"/api/air/projects/{id}", new AirProjectRequest("Outdated draft", Empty(), 1));
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("current").GetProperty("title").GetString().Should().Be("My revision");
        using var conn = factory.Seeder.OpenConnection(); (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM brkt_matches WHERE id=@id", new { id })).Should().Be(0);
    }

    [Fact]
    public async Task Admin_grants_are_authorized_audited_and_enforce_device_limits_and_revocation()
    {
        var owner = await Account(); var admin = await Account(true);
        var denied = await owner.Client.GetAsync("/api/admin/air/customers"); denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var planResponse = await admin.Client.PostAsJsonAsync("/api/admin/air/plans", new AirPlanRequest("One desk", 1, 1));
        planResponse.StatusCode.Should().Be(HttpStatusCode.OK, await planResponse.Content.ReadAsStringAsync());
        var plan = (await planResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.Client.PutAsJsonAsync($"/api/admin/air/customers/{owner.User}/access", new AirGrantRequest(plan, DateTimeOffset.UtcNow.AddDays(2), "active", "Local integration grant"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var device = Guid.NewGuid();
        (await owner.Client.PostAsJsonAsync("/api/air/devices", new AirDeviceRequest(device, "Primary", "production"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.Client.PostAsJsonAsync("/api/air/devices", new AirDeviceRequest(Guid.NewGuid(), "Extra", "production"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var access = await owner.Client.PostAsJsonAsync("/api/air/access", new AirAccessRequest(device)); access.StatusCode.Should().Be(HttpStatusCode.OK);
        var lease = (await access.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("offlineLease");
        var bytes = Convert.FromBase64String(lease.GetProperty("payload").GetString()!); var payload = JsonSerializer.Deserialize<JsonElement>(bytes);
        (payload.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalDays.Should().BeLessThanOrEqualTo(2);
        (await admin.Client.PostAsJsonAsync($"/api/admin/air/devices/{device}/revoke", new { reason = "Test revocation" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.Client.PostAsJsonAsync("/api/air/access", new AirAccessRequest(device))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.Client.PostAsJsonAsync("/api/air/devices", new AirDeviceRequest(device, "Primary", "production"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var conn = factory.Seeder.OpenConnection(); (await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM air_admin_audit WHERE actor_id=@id", new { id = admin.User })).Should().Be(3);
    }

    [Fact]
    public async Task Suspended_access_blocks_new_projects_but_retains_existing_data()
    {
        var owner = await Account(); var admin = await Account(true);
        var created = await owner.Client.PostAsJsonAsync($"/api/air/workspaces/{owner.Workspace}/projects", new AirProjectRequest("Keep my draft", Empty()));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var session = await owner.Client.PostAsJsonAsync("/api/air/session", new { }); var plan = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account").GetProperty("planId").GetGuid();
        (await admin.Client.PutAsJsonAsync($"/api/admin/air/customers/{owner.User}/access", new AirGrantRequest(plan, null, "suspended", "Account under review"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.Client.GetAsync($"/api/air/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.Client.PostAsJsonAsync($"/api/air/workspaces/{owner.Workspace}/projects", new AirProjectRequest("Denied new project", Empty()))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Event_queue_is_idempotent_scoped_and_strips_packets()
    {
        var owner = await Account(); var device = Guid.NewGuid(); await owner.Client.PostAsJsonAsync("/api/air/devices", new AirDeviceRequest(device, "Desk", "production"));
        var created = await owner.Client.PostAsJsonAsync($"/api/air/workspaces/{owner.Workspace}/projects", new AirProjectRequest("Metadata only", Empty())); var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        owner.Client.DefaultRequestHeaders.Add("X-Air-Device-Id", device.ToString()); var eventId = Guid.NewGuid();
        var batch = new { events = new[] { new { id = eventId, projectId = id, at = DateTimeOffset.UtcNow, type = "SegmentTake", data = new { scene = "Opening", rawPacket = new { playerName = "must remain local" } } } } };
        for (var n = 0; n < 2; n++) (await owner.Client.PostAsJsonAsync($"/api/air/projects/{id}/events", batch)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var conn = factory.Seeder.OpenConnection(); var data = await conn.QuerySingleAsync<string>("SELECT metadata::text FROM air_production_events WHERE id=@eventId", new { eventId }); data.Should().Contain("Opening").And.NotContain("rawPacket").And.NotContain("playerName");
    }

    [Fact]
    public async Task Published_release_response_has_typed_contract()
    {
        var admin = await Account(true); var saved = await admin.Client.PostAsJsonAsync("/api/admin/air/releases", new AirReleaseRequest("0.2.0", "preview", "https://esportra.gg/downloads/air", "Integration fixture", true)); saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var releases = await admin.Client.GetAsync("/api/air/releases"); releases.StatusCode.Should().Be(HttpStatusCode.OK, await releases.Content.ReadAsStringAsync()); (await releases.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("published").GetBoolean().Should().BeTrue();
    }
    [Fact]
    public async Task Every_admin_directory_uses_the_expected_contract()
    {
        var admin = await Account(true);
        foreach (var section in new[] { "customers", "workspaces", "plans", "devices", "activity", "releases" })
        {
            var response = await admin.Client.GetAsync($"/api/admin/air/{section}"); response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
        }
    }
    [Fact]
    public async Task Grant_overrides_and_workspace_mutations_remain_owned()
    {
        var owner = await Account(); var other = await Account(); var admin = await Account(true);
        (await other.Client.PutAsJsonAsync($"/api/air/workspaces/{owner.Workspace}", new { name = "Foreign rename" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.Client.PutAsJsonAsync($"/api/air/workspaces/{owner.Workspace}", new { name = "My desk" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.Client.DeleteAsync($"/api/air/workspaces/{owner.Workspace}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var session = await owner.Client.PostAsJsonAsync("/api/air/session", new { }); var plan = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account").GetProperty("planId").GetGuid();
        (await admin.Client.PutAsJsonAsync($"/api/admin/air/customers/{owner.User}/access", new AirGrantRequest(plan, null, "active", "Block new production devices", true, 0, null))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.Client.PostAsJsonAsync("/api/air/devices", new AirDeviceRequest(Guid.NewGuid(), "Denied PC", "production"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await owner.Client.PostAsJsonAsync("/api/air/devices", new AirDeviceRequest(Guid.NewGuid(), "Observer", "capture-only"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }
    [Fact]
    public async Task Uploaded_assets_are_private_sanitized_and_idempotent()
    {
        var owner = await Account(); var other = await Account();
        using var bitmap = new SKBitmap(8, 8); bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100); var bytes = png.ToArray();
        var id = Guid.NewGuid(); owner.Client.DefaultRequestHeaders.Add("X-Air-Asset-Id", id.ToString());
        for (var n = 0; n < 2; n++)
        {
            using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new("image/png");
            var response = await owner.Client.PostAsync($"/api/air/workspaces/{owner.Workspace}/assets", content); response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        (await other.Client.GetAsync($"/api/air/assets/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var download = await owner.Client.GetAsync($"/api/air/assets/{id}"); download.StatusCode.Should().Be(HttpStatusCode.OK); download.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
    }
}
