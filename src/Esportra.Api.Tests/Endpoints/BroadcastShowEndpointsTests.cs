using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

/// <summary>
/// Integration tests for BroadcastShowEndpoints — GET /api/broadcast/shows
/// and POST /api/broadcast/shows.
///
/// Covers:
///   ListShows — happy path (empty), happy path (populated), 401 unauthenticated,
///               400 missing orgId, 403 not org owner
///   CreateShow — happy path, 401 unauthenticated, 400 bad request, 403 not org owner
///   GetShow    — happy path, 401 unauthenticated, 404 unknown show, 403 not org owner
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed partial class BroadcastShowEndpointsTests
{
    private readonly ApiFactory _factory;
    private readonly DbSeeder _seeder;

    public BroadcastShowEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
        _seeder = factory.Seeder;
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    /// <summary>Seeds user → organizer role → organization. Returns (ownerId, orgId).</summary>
    private async Task<(Guid OwnerId, Guid OrgId)> SeedOrgOwnerAsync()
    {
        var ownerId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(ownerId);
        await _seeder.SeedUserRoleAsync(ownerId, "organizer");

        var orgId = Guid.NewGuid();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.organizations (id, owner_id, name, slug, created_at, updated_at)
            VALUES (@id, @ownerId, @name, @slug, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = orgId, ownerId, name = $"Org {orgId:N}"[..20], slug = $"org-{orgId:N}"[..20] });

        return (ownerId, orgId);
    }

    /// <summary>Seeds a broadcast_show row and returns its ID.</summary>
    private async Task<Guid> SeedShowAsync(Guid orgId, Guid ownerId, string title = "Test Show", string status = "draft")
    {
        var showId = Guid.NewGuid();
        await using var conn = _seeder.OpenConnection();
        await conn.ExecuteAsync("""
            INSERT INTO public.broadcast_shows
                (id, org_id, title, status, created_by, created_at, updated_at)
            VALUES (@id, @orgId, @title, @status, @ownerId, NOW(), NOW())
            ON CONFLICT (id) DO NOTHING
            """,
            new { id = showId, orgId, title, status, ownerId });
        return showId;
    }

    // ── ListShows auth guards ─────────────────────────────────────────────────

    [Fact]
    public async Task ListShows_Returns401_WhenUnauthenticated()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/broadcast/shows?orgId={Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListShows_Returns400_WhenOrgIdMissing()
    {
        var (ownerId, _) = await SeedOrgOwnerAsync();
        var client = _factory.CreateAuthenticatedClient(ownerId);

        var response = await client.GetAsync("/api/broadcast/shows");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListShows_Returns403_WhenUserIsNotOrgOwner()
    {
        var (_, orgId) = await SeedOrgOwnerAsync();

        // Different user — not the owner
        var otherId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(otherId);
        await _seeder.SeedUserRoleAsync(otherId, "organizer");

        var client = _factory.CreateAuthenticatedClient(otherId);
        var response = await client.GetAsync($"/api/broadcast/shows?orgId={orgId}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── ListShows happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task ListShows_Returns200_WithEmptyArray_WhenOrgHasNoShows()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var client = _factory.CreateAuthenticatedClient(ownerId);

        var response = await client.GetAsync($"/api/broadcast/shows?orgId={orgId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ListShows_Returns200_WithShows_WhenOrgHasShows()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId, "My Test Show");
        var client = _factory.CreateAuthenticatedClient(ownerId);

        var response = await client.GetAsync($"/api/broadcast/shows?orgId={orgId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var shows = await response.Content.ReadFromJsonAsync<JsonElement>();
        shows.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var show = shows.EnumerateArray()
            .FirstOrDefault(s => s.GetProperty("id").GetGuid() == showId);
        show.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        show.GetProperty("title").GetString().Should().Be("My Test Show");
        show.GetProperty("status").GetString().Should().Be("draft");
    }

    [Fact]
    public async Task ListShows_DoesNotReturnShows_FromOtherOrgs()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var (otherOwnerId, otherOrgId) = await SeedOrgOwnerAsync();
        await SeedShowAsync(otherOrgId, otherOwnerId, "Other Org Show");

        var client = _factory.CreateAuthenticatedClient(ownerId);
        var response = await client.GetAsync($"/api/broadcast/shows?orgId={orgId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var shows = await response.Content.ReadFromJsonAsync<JsonElement>();
        shows.EnumerateArray()
            .All(s => s.GetProperty("title").GetString() != "Other Org Show")
            .Should().BeTrue();
    }

    // ── CreateShow auth guards ────────────────────────────────────────────────

    [Fact]
    public async Task CreateShow_Returns401_WhenUnauthenticated()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/broadcast/shows",
            new { orgId = Guid.NewGuid(), title = "Show" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateShow_Returns400_WhenTitleIsEmpty()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var client = _factory.CreateAuthenticatedClient(ownerId);

        var response = await client.PostAsJsonAsync("/api/broadcast/shows",
            new { orgId, title = "" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateShow_Returns403_WhenUserIsNotOrgOwner()
    {
        var (_, orgId) = await SeedOrgOwnerAsync();

        var otherId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(otherId);
        await _seeder.SeedUserRoleAsync(otherId, "organizer");

        var client = _factory.CreateAuthenticatedClient(otherId);
        var response = await client.PostAsJsonAsync("/api/broadcast/shows",
            new { orgId, title = "Unauthorized Show" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateShow_Returns200_WithShowId_WhenValid()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var client = _factory.CreateAuthenticatedClient(ownerId);

        var response = await client.PostAsJsonAsync("/api/broadcast/shows",
            new { orgId, title = "Esportra Opens Broadcast" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("showId").GetGuid().Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateShow_PersistsRow_InBroadcastShows()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var client = _factory.CreateAuthenticatedClient(ownerId);

        var response = await client.PostAsJsonAsync("/api/broadcast/shows",
            new { orgId, title = "Persistence Check" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var showId = body.GetProperty("showId").GetGuid();

        await using var conn = _seeder.OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT title, status, org_id FROM broadcast_shows WHERE id = @showId",
            new { showId });

        ((object?)row).Should().NotBeNull();
        ((string)row!.title).Should().Be("Persistence Check");
        ((string)row.status).Should().Be("draft");
        ((Guid)row.org_id).Should().Be(orgId);
    }

    // ── GetShow ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetShow_Returns401_WhenUnauthenticated()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/broadcast/shows/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetShow_Returns404_WhenShowDoesNotExist()
    {
        var (ownerId, _) = await SeedOrgOwnerAsync();
        var client = _factory.CreateAuthenticatedClient(ownerId);

        var response = await client.GetAsync($"/api/broadcast/shows/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetShow_Returns403_WhenUserIsNotOrgOwner()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId);

        var otherId = Guid.NewGuid();
        await _seeder.SeedAuthUserAsync(otherId);
        await _seeder.SeedUserRoleAsync(otherId, "organizer");

        var client = _factory.CreateAuthenticatedClient(otherId);
        var response = await client.GetAsync($"/api/broadcast/shows/{showId}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetShow_Returns200_WithShowDetail_WhenOwner()
    {
        var (ownerId, orgId) = await SeedOrgOwnerAsync();
        var showId = await SeedShowAsync(orgId, ownerId, "Detail Show");
        var client = _factory.CreateAuthenticatedClient(ownerId);

        var response = await client.GetAsync($"/api/broadcast/shows/{showId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(showId);
        body.GetProperty("title").GetString().Should().Be("Detail Show");
        body.GetProperty("status").GetString().Should().Be("draft");
    }
}
