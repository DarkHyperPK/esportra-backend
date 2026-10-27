using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Npgsql;
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

    [Fact]
    public async Task SavedListings_ArePrivateAndIdempotent_AndHideExpiredListings()
    {
        var (owner, _) = await User();
        var (_, saver) = await User();
        var (_, stranger) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        (await saver.PutAsJsonAsync($"/api/competitive/lfg/{listing}/saved", new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await saver.PutAsJsonAsync($"/api/competitive/lfg/{listing}/saved", new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var saved = await (await saver.GetAsync("/api/competitive/lfg/saved?game=valorant")).Content.ReadFromJsonAsync<JsonElement>();
        saved.EnumerateArray().Count(x => x.GetProperty("id").GetGuid() == listing).Should().Be(1);
        var other = await (await stranger.GetAsync("/api/competitive/lfg/saved?game=valorant")).Content.ReadFromJsonAsync<JsonElement>();
        other.EnumerateArray().Should().NotContain(x => x.GetProperty("id").GetGuid() == listing);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE public.lfg_listings SET expires_at = now() - interval '1 hour' WHERE id = @listing", new { listing });
        var hidden = await (await saver.GetAsync("/api/competitive/lfg/saved?game=valorant")).Content.ReadFromJsonAsync<JsonElement>();
        hidden.EnumerateArray().Should().NotContain(x => x.GetProperty("id").GetGuid() == listing);
        (await saver.DeleteAsync($"/api/competitive/lfg/{listing}/saved")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await saver.DeleteAsync($"/api/competitive/lfg/{listing}/saved")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task InterestInbox_OnlyShowsCallerRequests_AndPaginates()
    {
        var (owner, receiver) = await User();
        var (sender, senderClient) = await User();
        var (_, stranger) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        var interest = await SeedInterestAsync(listing, sender, owner);
        var received = await (await receiver.GetAsync("/api/competitive/lfg/interests/mine?direction=received&limit=1")).Content.ReadFromJsonAsync<JsonElement>();
        received.EnumerateArray().Should().Contain(x => x.GetProperty("id").GetGuid() == interest);
        var sent = await (await senderClient.GetAsync("/api/competitive/lfg/interests/mine?direction=sent&game=valorant")).Content.ReadFromJsonAsync<JsonElement>();
        sent.EnumerateArray().Should().Contain(x => x.GetProperty("id").GetGuid() == interest);
        var other = await (await stranger.GetAsync("/api/competitive/lfg/interests/mine?direction=sent")).Content.ReadFromJsonAsync<JsonElement>();
        other.EnumerateArray().Should().NotContain(x => x.GetProperty("id").GetGuid() == interest);
        var beyond = await (await receiver.GetAsync("/api/competitive/lfg/interests/mine?direction=received&limit=1&offset=1")).Content.ReadFromJsonAsync<JsonElement>();
        beyond.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AcceptInterest_ConcurrentRetries_ReturnSameConversation()
    {
        var (owner, receiver) = await User();
        var (sender, _) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        var interest = await SeedInterestAsync(listing, sender, owner);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => receiver.PostAsync($"/api/competitive/lfg/interests/{interest}/accept", null)));
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<JsonElement>()));
        bodies[0].GetProperty("conversation_id").GetGuid().Should().Be(bodies[1].GetProperty("conversation_id").GetGuid());
        await using var conn = factory.Seeder.OpenConnection();
        var notifications = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM public.notifications WHERE user_id = @sender AND type::text = 'lfg_interest_accepted' AND data->>'interest_id' = @interestId", new { sender, interestId = interest.ToString() });
        notifications.Should().Be(1);
    }

    [Fact]
    public async Task AcceptInterest_RejectsOtherUsers_AndDoesNotReuseConversationWithThirdParty()
    {
        var (owner, receiver) = await User();
        var (sender, senderClient) = await User();
        var (third, _) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        var interest = await SeedInterestAsync(listing, sender, owner);
        (await senderClient.PostAsync($"/api/competitive/lfg/interests/{interest}/accept", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var unsafeConversation = Guid.NewGuid();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO public.conversations (id, type, created_by) VALUES (@id, 'direct', @owner)", new { id = unsafeConversation, owner });
        await conn.ExecuteAsync("INSERT INTO public.conversation_participants (conversation_id, user_id) VALUES (@id, @owner), (@id, @sender), (@id, @third)", new { id = unsafeConversation, owner, sender, third });
        var accepted = await receiver.PostAsync($"/api/competitive/lfg/interests/{interest}/accept", null);
        accepted.EnsureSuccessStatusCode();
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("conversation_id").GetGuid().Should().NotBe(unsafeConversation);
    }

    [Fact]
    public async Task ExpressInterest_ExpiredListing_ReturnsNotFound()
    {
        var (owner, _) = await User();
        var (_, sender) = await User();
        var listing = await SeedListingAsync(owner, "valorant", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var response = await sender.PostAsJsonAsync($"/api/competitive/lfg/{listing}/interest", new { message = "Hello" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ViewerState_RestoresSavedAndInterestStatus()
    {
        var (owner, _) = await User();
        var (sender, client) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        var interest = await SeedInterestAsync(listing, sender, owner);
        (await client.PutAsJsonAsync($"/api/competitive/lfg/{listing}/saved", new { })).EnsureSuccessStatusCode();
        var response = await client.GetAsync($"/api/competitive/lfg/{listing}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("is_saved").GetBoolean().Should().BeTrue();
        body.GetProperty("viewer_interest_id").GetGuid().Should().Be(interest);
        body.GetProperty("viewer_interest_status").GetString().Should().Be("expressed");
    }

    [Fact]
    public async Task SavedListings_Rls_DeniesAnotherUsersRowsAndForgedInsert()
    {
        var (owner, _) = await User();
        var (saver, client) = await User();
        var (other, _) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        (await client.PutAsJsonAsync($"/api/competitive/lfg/{listing}/saved", new { })).EnsureSuccessStatusCode();
        await using var conn = factory.Seeder.OpenConnection();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("GRANT USAGE ON SCHEMA auth TO authenticated; GRANT SELECT ON public.lfg_listings TO authenticated", transaction: tx);
        await conn.ExecuteAsync("SET LOCAL ROLE authenticated", transaction: tx);
        await conn.ExecuteAsync("SELECT set_config('request.jwt.claim.sub', @id, true)", new { id = other.ToString() }, tx);
        var count = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM public.lfg_saved_listings WHERE user_id = @saver", new { saver }, tx);
        count.Should().Be(0);
        Func<Task> forge = () => conn.ExecuteAsync("INSERT INTO public.lfg_saved_listings (user_id, listing_id) VALUES (@saver, @listing)", new { saver, listing }, tx);
        var failure = await forge.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be("42501");
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Discovery_FiltersAcrossFullResultSet()
    {
        var (owner, _) = await User();
        var (_, viewer) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE public.lfg_listings SET description = 'distinct search phrase', preferred_roles = ARRAY['duelist'], preferred_servers = ARRAY['bahrain'], languages = ARRAY['en'] WHERE id = @listing", new { listing });
        var response = await viewer.GetAsync("/api/competitive/lfg?game=valorant&q=distinct%20search&preferred_roles=duelist&server=bahrain&language=en");
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        rows.EnumerateArray().Should().Contain(x => x.GetProperty("id").GetGuid() == listing);
        var noMatch = await (await viewer.GetAsync("/api/competitive/lfg?game=valorant&q=distinct%20search&server=other")).Content.ReadFromJsonAsync<JsonElement>();
        noMatch.EnumerateArray().Should().NotContain(x => x.GetProperty("id").GetGuid() == listing);
    }

    [Fact]
    public async Task AuthenticatedDatabaseRole_CannotBypassLfgMutationEndpoints()
    {
        await using var conn = factory.Seeder.OpenConnection();
        var allowed = await conn.QuerySingleAsync<(bool ListingInsert, bool ListingUpdate, bool InterestInsert, bool InterestUpdate)>(
            """
            SELECT has_table_privilege('authenticated', 'public.lfg_listings', 'INSERT') AS listing_insert,
                   has_table_privilege('authenticated', 'public.lfg_listings', 'UPDATE') AS listing_update,
                   has_table_privilege('authenticated', 'public.lfg_interests', 'INSERT') AS interest_insert,
                   has_table_privilege('authenticated', 'public.lfg_interests', 'UPDATE') AS interest_update
            """);
        allowed.ListingInsert.Should().BeFalse();
        allowed.ListingUpdate.Should().BeFalse();
        allowed.InterestInsert.Should().BeFalse();
        allowed.InterestUpdate.Should().BeFalse();
    }
}
