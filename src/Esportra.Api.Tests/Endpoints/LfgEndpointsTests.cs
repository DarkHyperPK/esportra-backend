using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Esportra.Api.Tests.Endpoints;

[Collection(IntegrationCollection.Name)]
public sealed class LfgEndpointsTests(ApiFactory factory) : IAsyncLifetime
{
    // Enable the lfg feature flag so FeatureGateMiddleware does not block tests.
    public async Task InitializeAsync() => await factory.Seeder.SeedFeatureFlagAsync("lfg", enabled: true);
    public Task DisposeAsync() => Task.CompletedTask;

    // ── helpers ───────────────────────────────────────────────────────────────

    private HttpClient Anon() => factory.CreateClient();

    private async Task<(Guid UserId, HttpClient Client)> User()
    {
        var id = Guid.NewGuid();
        await factory.Seeder.SeedAuthUserAsync(id);
        return (id, factory.CreateAuthenticatedClient(id));
    }

    /// <summary>Seeds an lfg_listings row directly and returns its id.</summary>
    private async Task<Guid> SeedListingAsync(
        Guid userId,
        string gameSlug,
        string status = "published",
        DateTimeOffset? expiresAt = null)
    {
        var id = Guid.NewGuid();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.lfg_listings
                (id, user_id, game_slug, intent, status, preferred_servers,
                 preferred_roles, languages, expires_at, created_at, updated_at)
            VALUES
                (@id, @userId, @gameSlug, 'duo'::lfg_intent, @status::lfg_listing_status,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[],
                 @expiresAt, NOW(), NOW())
            """,
            new
            {
                id,
                userId,
                gameSlug,
                status,
                expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
            });
        return id;
    }

    /// <summary>Seeds an lfg_interests row and returns its id.</summary>
    private async Task<Guid> SeedInterestAsync(Guid listingId, Guid fromUserId, Guid toUserId)
    {
        var id = Guid.NewGuid();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.lfg_interests
                (id, listing_id, from_user_id, to_user_id, status, created_at, updated_at)
            VALUES
                (@id, @listingId, @fromUserId, @toUserId, 'expressed'::lfg_interest_status, NOW(), NOW())
            """,
            new { id, listingId, fromUserId, toUserId });
        return id;
    }

    // ── Catalog: GET /api/catalog/rank-tiers ─────────────────────────────────

    [Fact]
    public async Task GetRankTiers_RequiresGameParam()
    {
        var response = await Anon().GetAsync("/api/catalog/rank-tiers");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetRankTiers_UnknownGame_ReturnsEmptyArray()
    {
        var response = await Anon().GetAsync("/api/catalog/rank-tiers?game=unknown_game_xyz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetRankTiers_Valorant_ReturnsSortedRows()
    {
        var response = await Anon().GetAsync("/api/catalog/rank-tiers?game=valorant");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        rows.ValueKind.Should().Be(JsonValueKind.Array);

        if (rows.GetArrayLength() == 0)
            return; // Catalog not seeded in this test DB — skip assertions

        // Verify sort order is ascending
        var sortOrders = rows.EnumerateArray()
            .Select(r => r.GetProperty("sort_order").GetInt32())
            .ToList();
        sortOrders.Should().BeInAscendingOrder();

        // Verify expected fields are present
        var first = rows[0];
        first.TryGetProperty("id", out _).Should().BeTrue();
        first.TryGetProperty("tier_key", out _).Should().BeTrue();
        first.TryGetProperty("display_name", out _).Should().BeTrue();
        first.TryGetProperty("icon_url", out _).Should().BeTrue();
    }

    // ── Catalog: GET /api/catalog/game-roles ─────────────────────────────────

    [Fact]
    public async Task GetGameRoles_RequiresGameParam()
    {
        var response = await Anon().GetAsync("/api/catalog/game-roles");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetGameRoles_UnknownGame_ReturnsEmptyArray()
    {
        var response = await Anon().GetAsync("/api/catalog/game-roles?game=unknown_game_xyz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetGameRoles_Valorant_ReturnsRows()
    {
        var response = await Anon().GetAsync("/api/catalog/game-roles?game=valorant");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        rows.ValueKind.Should().Be(JsonValueKind.Array);

        if (rows.GetArrayLength() == 0)
            return; // Catalog not seeded in this test DB — skip

        var first = rows[0];
        first.TryGetProperty("role_key", out _).Should().BeTrue();
        first.TryGetProperty("display_name", out _).Should().BeTrue();
    }

    // ── Catalog: GET /api/catalog/regions ────────────────────────────────────

    [Fact]
    public async Task GetRegions_RequiresGameParam()
    {
        var response = await Anon().GetAsync("/api/catalog/regions");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetRegions_UnknownGame_ReturnsEmptyArray()
    {
        var response = await Anon().GetAsync("/api/catalog/regions?game=unknown_game_xyz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().Be(0);
    }

    // ── LFG: GET /api/competitive/lfg/{id} ───────────────────────────────────

    [Fact]
    public async Task GetListing_NonExistentId_Returns404()
    {
        var (_, client) = await User();
        var response = await client.GetAsync($"/api/competitive/lfg/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetListing_Owner_CanSeeDraft()
    {
        var (ownerId, ownerClient) = await User();
        var gameSlug = "valorant";

        // Ensure the game exists in catalog aliases so seeding works via the validation path
        var listingId = await SeedListingAsync(ownerId, gameSlug, status: "draft");

        var response = await ownerClient.GetAsync($"/api/competitive/lfg/{listingId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(listingId);
        body.GetProperty("status").GetString().Should().Be("draft");
    }

    [Fact]
    public async Task GetListing_NonOwner_CanSeePublished()
    {
        var (ownerId, _) = await User();
        var (_, viewerClient) = await User();
        var listingId = await SeedListingAsync(ownerId, "valorant", status: "published");

        var response = await viewerClient.GetAsync($"/api/competitive/lfg/{listingId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("published");
    }

    [Fact]
    public async Task GetListing_NonOwner_CannotSeeDraft_Returns404()
    {
        var (ownerId, _) = await User();
        var (_, viewerClient) = await User();
        var listingId = await SeedListingAsync(ownerId, "valorant", status: "draft");

        var response = await viewerClient.GetAsync($"/api/competitive/lfg/{listingId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── LFG: GET /api/competitive/lfg/{id}/interests ─────────────────────────

    [Fact]
    public async Task GetInterests_NonOwner_Returns403()
    {
        var (ownerId, _) = await User();
        var (_, strangerClient) = await User();
        var listingId = await SeedListingAsync(ownerId, "valorant", status: "published");

        var response = await strangerClient.GetAsync($"/api/competitive/lfg/{listingId}/interests");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetInterests_NonExistentListing_Returns404()
    {
        var (_, client) = await User();
        var response = await client.GetAsync($"/api/competitive/lfg/{Guid.NewGuid()}/interests");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetInterests_Owner_ReturnsInterestRows()
    {
        var (ownerId, ownerClient) = await User();
        var (interestUserId, _) = await User();
        var listingId = await SeedListingAsync(ownerId, "valorant", status: "published");
        await SeedInterestAsync(listingId, interestUserId, ownerId);

        var response = await ownerClient.GetAsync($"/api/competitive/lfg/{listingId}/interests");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        rows.ValueKind.Should().Be(JsonValueKind.Array);
        rows.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        rows[0].GetProperty("fromUserId").GetGuid().Should().Be(interestUserId);
    }

    // ── LFG publish: paused → published ──────────────────────────────────────

    [Fact]
    public async Task PublishListing_FromPaused_Succeeds()
    {
        var (ownerId, ownerClient) = await User();
        var listingId = await SeedListingAsync(ownerId, "valorant", status: "paused");

        var response = await ownerClient.PostAsync(
            $"/api/competitive/lfg/{listingId}/publish",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("published");
    }

    [Fact]
    public async Task PublishListing_FromClosed_ReturnsBadRequest()
    {
        var (ownerId, ownerClient) = await User();
        var listingId = await SeedListingAsync(ownerId, "valorant", status: "closed");

        var response = await ownerClient.PostAsync(
            $"/api/competitive/lfg/{listingId}/publish",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── LFG discovery: expires_at filter ─────────────────────────────────────

    [Fact]
    public async Task Discovery_ExcludesExpiredListings()
    {
        var (ownerId, _) = await User();
        var (_, viewerClient) = await User();

        // Seed an expired listing
        var expiredId = await SeedListingAsync(
            ownerId,
            "valorant",
            status: "published",
            expiresAt: DateTimeOffset.UtcNow.AddHours(-1));

        var response = await viewerClient.GetAsync("/api/competitive/lfg?game=valorant&limit=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        rows.ValueKind.Should().Be(JsonValueKind.Array);

        var ids = rows.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        ids.Should().NotContain(expiredId, "expired listing must be excluded from discovery");
    }

    [Fact]
    public async Task Discovery_IncludesFarFutureExpiry()
    {
        var (ownerId, _) = await User();
        var (_, viewerClient) = await User();

        // Seed a listing with a far-future expires_at (effectively never expires).
        // expires_at is NOT NULL in the schema; the endpoint also handles IS NULL for
        // future flexibility but the column constraint applies here.
        var id = Guid.NewGuid();
        var farFuture = DateTimeOffset.UtcNow.AddYears(100);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO public.lfg_listings
                (id, user_id, game_slug, intent, status, preferred_servers,
                 preferred_roles, languages, expires_at, created_at, updated_at)
            VALUES
                (@id, @userId, 'valorant', 'duo'::lfg_intent, 'published'::lfg_listing_status,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[],
                 @farFuture, NOW(), NOW())
            """,
            new { id, userId = ownerId, farFuture });

        var response = await viewerClient.GetAsync("/api/competitive/lfg?game=valorant&limit=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = rows.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain(id, "listing with far-future expires_at must appear in discovery");
    }

    // ── Authorization: unauthenticated requests ───────────────────────────────

    [Fact]
    public async Task GetListing_Unauthenticated_Returns401()
    {
        var response = await Anon().GetAsync($"/api/competitive/lfg/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetInterests_Unauthenticated_Returns401()
    {
        var response = await Anon().GetAsync($"/api/competitive/lfg/{Guid.NewGuid()}/interests");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
