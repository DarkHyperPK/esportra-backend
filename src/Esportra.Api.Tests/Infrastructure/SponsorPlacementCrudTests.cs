using System.Net;
using System.Net.Http.Json;
using Esportra.Contracts.Requests;
using Esportra.Contracts.Responses;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Infrastructure;

[Collection("Postgres")]
public sealed class SponsorPlacementCrudTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private EsportraWebApplicationFactory _factory = null!;
    private TestDataSeeder _seeder = null!;
    private Guid _userId;
    private Guid _sponsorId;
    private Guid _tournamentId;

    public SponsorPlacementCrudTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _factory = new EsportraWebApplicationFactory(_fixture.ConnectionString);
        _seeder = new TestDataSeeder(_fixture.ConnectionString);
        _userId = await _seeder.CreateUserAsync();
        await _seeder.GrantSuperAdminAsync(_userId);
        _sponsorId = await _seeder.CreateSponsorAsync(tier: "radiant");
        _tournamentId = await _seeder.CreateTournamentAsync(_userId);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task CreatePlacement_WithValidData_Returns201()
    {
        var client = _factory.CreateAuthenticatedClient(_userId, isSuperAdmin: true);
        var assetId = await _seeder.CreatePlacementAssetAsync(_userId, "wide_partner", "banner");
        var request = new CreatePlacementRequest(
            _sponsorId, _tournamentId, "wide_partner", 1,
            BannerAssetId: assetId);

        var response = await client.PostAsJsonAsync("/api/admin/placements", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body.Should().ContainKey("id");
    }

    [Fact]
    public async Task CreatePlacement_OccupiedSlot_Returns409()
    {
        var client = _factory.CreateAuthenticatedClient(_userId, isSuperAdmin: true);
        var asset1 = await _seeder.CreatePlacementAssetAsync(_userId, "card_badge", "logo");
        var asset2 = await _seeder.CreatePlacementAssetAsync(_userId, "card_badge", "logo");

        var request1 = new CreatePlacementRequest(_sponsorId, _tournamentId, "card_badge", 1, LogoAssetId: asset1);
        var response1 = await client.PostAsJsonAsync("/api/admin/placements", request1);
        response1.StatusCode.Should().Be(HttpStatusCode.Created);

        var sponsor2 = await _seeder.CreateSponsorAsync("Sponsor 2", "radiant");
        var request2 = new CreatePlacementRequest(sponsor2, _tournamentId, "card_badge", 1, LogoAssetId: asset2);
        var response2 = await client.PostAsJsonAsync("/api/admin/placements", request2);

        response2.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateMetadata_PreservesAssetIds()
    {
        var client = _factory.CreateAuthenticatedClient(_userId, isSuperAdmin: true);
        var assetId = await _seeder.CreatePlacementAssetAsync(_userId, "wide_partner", "banner");
        var createReq = new CreatePlacementRequest(_sponsorId, _tournamentId, "wide_partner", 2, BannerAssetId: assetId);
        var createResp = await client.PostAsJsonAsync("/api/admin/placements", createReq);
        var created = await createResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var placementId = created!["id"].ToString();

        var metadataReq = new UpdatePlacementMetadataRequest("Updated headline", "Click me", "https://example.com", null, null, null, null);
        var updateResp = await client.PutAsJsonAsync($"/api/admin/placements/{placementId}/metadata", metadataReq);

        updateResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await client.GetFromJsonAsync<PlacementDto>($"/api/admin/placements/{placementId}");
        getResp!.BannerAssetId.Should().Be(assetId);
        getResp.Headline.Should().Be("Updated headline");
    }

    [Fact]
    public async Task RemoveCreative_ClearsUrlAndAsset_KeepsSlot()
    {
        var client = _factory.CreateAuthenticatedClient(_userId, isSuperAdmin: true);
        var assetId = await _seeder.CreatePlacementAssetAsync(_userId, "wide_partner", "banner");
        var createReq = new CreatePlacementRequest(_sponsorId, _tournamentId, "wide_partner", 3, BannerAssetId: assetId);
        var createResp = await client.PostAsJsonAsync("/api/admin/placements", createReq);
        var created = await createResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var placementId = created!["id"].ToString();

        var removeResp = await client.PostAsync($"/api/admin/placements/{placementId}/remove-creative", null);
        removeResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await client.GetFromJsonAsync<PlacementDto>($"/api/admin/placements/{placementId}");
        getResp!.BannerUrl.Should().BeNull();
        getResp.BannerAssetId.Should().BeNull();
        getResp.Lifecycle.Should().Be("draft");
    }

    [Fact]
    public async Task UnassignPlacement_DeletesRecord()
    {
        var client = _factory.CreateAuthenticatedClient(_userId, isSuperAdmin: true);
        var assetId = await _seeder.CreatePlacementAssetAsync(_userId, "wide_partner", "banner");
        var createReq = new CreatePlacementRequest(_sponsorId, _tournamentId, "wide_partner", 4, BannerAssetId: assetId);
        var createResp = await client.PostAsJsonAsync("/api/admin/placements", createReq);
        var created = await createResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var placementId = created!["id"].ToString();

        var unassignResp = await client.PostAsync($"/api/admin/placements/{placementId}/unassign", null);
        unassignResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await client.GetAsync($"/api/admin/placements/{placementId}");
        getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReplaceCreative_SwapsAsset()
    {
        var client = _factory.CreateAuthenticatedClient(_userId, isSuperAdmin: true);
        var oldAsset = await _seeder.CreatePlacementAssetAsync(_userId, "wide_partner", "banner");
        var createReq = new CreatePlacementRequest(_sponsorId, _tournamentId, "wide_partner", 1, BannerAssetId: oldAsset);
        var createResp = await client.PostAsJsonAsync("/api/admin/placements", createReq);
        var created = await createResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var placementId = created!["id"].ToString();

        var newAsset = await _seeder.CreatePlacementAssetAsync(_userId, "wide_partner", "banner");
        var replaceResp = await client.PutAsJsonAsync($"/api/admin/placements/{placementId}/replace-creative", new { assetId = newAsset });
        replaceResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await client.GetFromJsonAsync<PlacementDto>($"/api/admin/placements/{placementId}");
        getResp!.BannerAssetId.Should().Be(newAsset);
        getResp.BannerAssetId.Should().NotBe(oldAsset);
    }

    [Fact]
    public async Task PublicRead_ExcludesReviewPlacements()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/tournaments/{_tournamentId}/sponsors");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("review");
    }
}
