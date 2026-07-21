using System.Net;
using System.Net.Http.Json;
using Esportra.Contracts.Requests;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Infrastructure;

[Collection("Postgres")]
public sealed class SponsorPlacementIntegrationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private EsportraWebApplicationFactory _factory = null!;

    public SponsorPlacementIntegrationTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _factory = new EsportraWebApplicationFactory(_fixture.ConnectionString);
        await SeedRequiredDataAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ─── Authorization Tests ─────────────────────────────────────────────

    [Fact]
    public async Task AnonymousUser_CannotAccessAdminEndpoints()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/admin/placements");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthenticatedUser_WithoutPermission_IsForbidden()
    {
        var client = _factory.CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/api/admin/placements");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SuperAdmin_CanAccessAdminEndpoints()
    {
        var client = _factory.CreateAuthenticatedClient(Guid.NewGuid(), isSuperAdmin: true);

        var response = await client.GetAsync("/api/admin/placements");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UserWithSponsorsEdit_CanAccessAdminEndpoints()
    {
        var client = _factory.CreateAuthenticatedClient(Guid.NewGuid(), permissions: "sponsors:edit");

        var response = await client.GetAsync("/api/admin/placements");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Public Read Tests ───────────────────────────────────────────────

    [Fact]
    public async Task GetTournamentSponsors_ReturnsOk_ForNonexistentTournament()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/tournaments/{Guid.NewGuid()}/sponsors");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("[]");
    }

    [Fact]
    public async Task GetGlobalPlacements_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/placements/global");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Create Placement Validation Tests ───────────────────────────────

    [Fact]
    public async Task CreatePlacement_RejectsInvalidZone()
    {
        var client = _factory.CreateAuthenticatedClient(Guid.NewGuid(), isSuperAdmin: true);
        var request = new CreatePlacementRequest(Guid.NewGuid(), Guid.NewGuid(), "invalid_zone", 1);

        var response = await client.PostAsJsonAsync("/api/admin/placements", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreatePlacement_RejectsInvalidSlot()
    {
        var client = _factory.CreateAuthenticatedClient(Guid.NewGuid(), isSuperAdmin: true);
        var request = new CreatePlacementRequest(Guid.NewGuid(), Guid.NewGuid(), "card_badge", 99);

        var response = await client.PostAsJsonAsync("/api/admin/placements", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreatePlacement_RejectsInvalidSchedule()
    {
        var client = _factory.CreateAuthenticatedClient(Guid.NewGuid(), isSuperAdmin: true);
        var now = DateTimeOffset.UtcNow;
        var request = new CreatePlacementRequest(Guid.NewGuid(), Guid.NewGuid(), "card_badge", 1,
            StartsAt: now.AddHours(2), EndsAt: now.AddHours(1));

        var response = await client.PostAsJsonAsync("/api/admin/placements", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ─── Metadata Update Tests ───────────────────────────────────────────

    [Fact]
    public async Task UpdateMetadata_ReturnsNotFound_ForNonexistentPlacement()
    {
        var client = _factory.CreateAuthenticatedClient(Guid.NewGuid(), isSuperAdmin: true);
        var request = new UpdatePlacementMetadataRequest("New headline", null, null, null, null, null, null);

        var response = await client.PutAsJsonAsync($"/api/admin/placements/{Guid.NewGuid()}/metadata", request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Remove Creative Tests ───────────────────────────────────────────

    [Fact]
    public async Task RemoveCreative_ReturnsNotFound_ForNonexistentPlacement()
    {
        var client = _factory.CreateAuthenticatedClient(Guid.NewGuid(), isSuperAdmin: true);

        var response = await client.PostAsync($"/api/admin/placements/{Guid.NewGuid()}/remove-creative", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Unassign Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task Unassign_ReturnsNotFound_ForNonexistentPlacement()
    {
        var client = _factory.CreateAuthenticatedClient(Guid.NewGuid(), isSuperAdmin: true);

        var response = await client.PostAsync($"/api/admin/placements/{Guid.NewGuid()}/unassign", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Helper Methods ──────────────────────────────────────────────────

    private async Task SeedRequiredDataAsync()
    {
        try
        {
            await using var connection = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
            await connection.OpenAsync();

            await using var cmd = new Npgsql.NpgsqlCommand(
                """
                INSERT INTO auth.users (id, email) VALUES ('00000000-0000-0000-0000-000000000001', 'test@test.com')
                ON CONFLICT DO NOTHING;
                INSERT INTO public.profiles (id, username) VALUES ('00000000-0000-0000-0000-000000000001', 'testuser')
                ON CONFLICT DO NOTHING;
                """, connection);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Schema may not have all tables if bootstrap is incomplete
        }
    }
}
