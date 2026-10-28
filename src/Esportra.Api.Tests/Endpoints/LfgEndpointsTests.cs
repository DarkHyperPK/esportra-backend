using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Esportra.Api.Tests.Infrastructure;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Esportra.Api.Endpoints;
using Esportra.Contracts.Auth;
using Esportra.Contracts.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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

    private async Task LinkRiotAsync(Guid userId)
    {
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@userId, @puuid, 'Player', 'TAG') ON CONFLICT (user_id) DO NOTHING",
            new { userId, puuid = userId.ToString() });
    }

    /// <summary>Seeds an lfg_listings row directly and returns its id.</summary>
    private async Task<Guid> SeedListingAsync(
        Guid userId,
        string gameSlug,
        string status = "published",
        DateTimeOffset? expiresAt = null)
    {
        var id = Guid.NewGuid();
        if (gameSlug == "valorant") await LinkRiotAsync(userId);
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

    [Theory]
    [InlineData("looking_for_team", "coach")]
    [InlineData("looking_for_team", "manager")]
    [InlineData("looking_for_team", "duelist")]
    [InlineData("looking_for_player", "coach")]
    [InlineData("competitive_roster", "manager")]
    public async Task ValorantCreation_RequiresLinkedRiotForEveryRoleAndListingType(string intent, string role)
    {
        var (_, client) = await User();
        var response = await client.PostAsJsonAsync("/api/competitive/lfg", new { game_slug = "valorant", intent, preferred_roles = new[] { role } });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("NO_LINKED_RIOT_ACCOUNT");
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("paused")]
    public async Task ValorantPublishAndResume_RecheckRiotLink(string status)
    {
        var (owner, client) = await User();
        var listing = await SeedListingAsync(owner, "valorant", status);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("DELETE FROM public.riot_accounts WHERE user_id = @owner", new { owner });
        var response = await client.PostAsync($"/api/competitive/lfg/{listing}/publish", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("NO_LINKED_RIOT_ACCOUNT");
        (await client.GetAsync($"/api/competitive/lfg/{listing}")).EnsureSuccessStatusCode();
        await LinkRiotAsync(owner);
        (await client.PostAsync($"/api/competitive/lfg/{listing}/publish", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task UnlinkedValorantListing_IsHiddenFromOthersButOwnerCanManage()
    {
        var (owner, ownerClient) = await User();
        var (_, viewer) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        (await viewer.PutAsJsonAsync($"/api/competitive/lfg/{listing}/saved", new { })).EnsureSuccessStatusCode();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("DELETE FROM public.riot_accounts WHERE user_id = @owner", new { owner });
        (await ownerClient.GetAsync($"/api/competitive/lfg/{listing}")).EnsureSuccessStatusCode();
        (await viewer.GetAsync($"/api/competitive/lfg/{listing}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var url in new[] { "/api/competitive/lfg?game=valorant", "/api/competitive/lfg/saved?game=valorant" })
        {
            var rows = await (await viewer.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();
            rows.EnumerateArray().Should().NotContain(row => row.GetProperty("id").GetGuid() == listing);
        }
        (await viewer.PostAsJsonAsync($"/api/competitive/lfg/{listing}/interest", new { message = "Hello" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await LinkRiotAsync(owner);
        (await viewer.GetAsync($"/api/competitive/lfg/{listing}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task CurrentRank_AppearsInOwnStatsDiscoverySavedAndDetails()
    {
        var (owner, ownerClient) = await User();
        var (_, viewer) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            UPDATE public.lfg_listings SET intent = 'looking_for_team' WHERE id = @listing;
            INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@owner, 'peak-test', 'Player', 'TAG') ON CONFLICT (user_id) DO UPDATE SET puuid = EXCLUDED.puuid;
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id,
                competitive_tier, competitive_tier_name, player_card_id, hs_pct, headshot_data_complete, sync_status, synced_at)
            VALUES (@owner, 'valorant', 'peak-test', 10, 'Silver 2', 'c86953ca-4c94-c0f7-fd91-c19e03ff2b9f', 0, true, 'complete', now());
            """, new { owner, listing });
        var own = await (await ownerClient.GetAsync("/api/competitive/game-stats/me?game=valorant")).Content.ReadFromJsonAsync<JsonElement>();
        own.GetProperty("competitive_tier").GetInt32().Should().Be(10);
        (await viewer.PutAsJsonAsync($"/api/competitive/lfg/{listing}/saved", new { })).EnsureSuccessStatusCode();
        foreach (var path in new[] { "/api/competitive/lfg?game=valorant", "/api/competitive/lfg/saved?game=valorant" })
        {
            var rows = await (await viewer.GetAsync(path)).Content.ReadFromJsonAsync<JsonElement>();
            var row = rows.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == listing);
            row.GetProperty("competitiveTier").GetInt32().Should().Be(10);
            row.GetProperty("hsPct").GetDecimal().Should().Be(0);
            row.GetProperty("playerCardId").GetString().Should().Be("c86953ca-4c94-c0f7-fd91-c19e03ff2b9f");
        }
        var detail = await (await viewer.GetAsync($"/api/competitive/lfg/{listing}")).Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("competitiveTier").GetInt32().Should().Be(10);
        detail.TryGetProperty("peakCompetitiveTier", out _).Should().BeFalse();
        var mine = await (await ownerClient.GetAsync("/api/competitive/lfg/mine")).Content.ReadFromJsonAsync<JsonElement>();
        mine.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == listing).GetProperty("competitiveTier").GetInt32().Should().Be(10);
        await conn.ExecuteAsync("UPDATE public.player_game_stats SET headshot_data_complete = false WHERE user_id = @owner AND game_slug = 'valorant'", new { owner });
        var incomplete = await (await viewer.GetAsync($"/api/competitive/lfg/{listing}")).Content.ReadFromJsonAsync<JsonElement>();
        incomplete.GetProperty("hsPct").ValueKind.Should().Be(JsonValueKind.Null);
        await conn.ExecuteAsync("UPDATE public.riot_accounts SET puuid = @puuid WHERE user_id = @owner", new { owner, puuid = $"relinked-{owner}" });
        var relinked = await (await viewer.GetAsync($"/api/competitive/lfg/{listing}")).Content.ReadFromJsonAsync<JsonElement>();
        relinked.GetProperty("hsPct").ValueKind.Should().Be(JsonValueKind.Null);
        relinked.GetProperty("competitiveTier").ValueKind.Should().Be(JsonValueKind.Null);
        relinked.GetProperty("playerCardId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PlayerListing_IgnoresRecruitmentRankRequirementsAndClearsLegacyValues()
    {
        var (owner, client) = await User();
        await LinkRiotAsync(owner);
        var create = await client.PostAsJsonAsync("/api/competitive/lfg", new { game_slug = "valorant", intent = "looking_for_team", min_rank_sort_order = 4 });
        create.EnsureSuccessStatusCode();
        var listing = await SeedListingAsync(owner, "valorant", status: "draft");
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE public.lfg_listings SET intent = 'looking_for_team', min_rank_sort_order = 4, max_rank_sort_order = 7 WHERE id = @listing", new { listing });
        var invalid = await client.PutAsJsonAsync($"/api/competitive/lfg/{listing}", new { max_rank_sort_order = 10 });
        invalid.EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/competitive/lfg/{listing}", new { description = "Updated" })).EnsureSuccessStatusCode();
        var minimum = await conn.ExecuteScalarAsync<int?>("SELECT min_rank_sort_order FROM public.lfg_listings WHERE id = @listing", new { listing });
        minimum.Should().BeNull();
        var maximum = await conn.ExecuteScalarAsync<int?>("SELECT max_rank_sort_order FROM public.lfg_listings WHERE id = @listing", new { listing });
        maximum.Should().BeNull();
    }

    [Fact]
    public async Task Discovery_RanksUsePlayerStatsForLftAndRequirementsForLfp()
    {
        var (owner, _) = await User();
        var (_, viewer) = await User();
        var player = await SeedListingAsync(owner, "valorant");
        var team = await SeedListingAsync(owner, "valorant");
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            UPDATE public.lfg_listings SET intent = 'looking_for_team', min_rank_sort_order = 20, max_rank_sort_order = 22 WHERE id = @player;
            UPDATE public.lfg_listings SET intent = 'looking_for_player', min_rank_sort_order = 3, max_rank_sort_order = 5 WHERE id = @team;
            INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@owner, 'rank-test', 'Player', 'TAG') ON CONFLICT (user_id) DO UPDATE SET puuid = EXCLUDED.puuid;
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, competitive_tier)
                VALUES (@owner, 'valorant', 'rank-test', 7);
            """, new { owner, player, team });
        var response = await viewer.GetAsync("/api/competitive/lfg?game=valorant&min_rank=3&max_rank=5");
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        rows.EnumerateArray().Should().Contain(row => row.GetProperty("id").GetGuid() == player);
        rows.EnumerateArray().Should().Contain(row => row.GetProperty("id").GetGuid() == team);
        await conn.ExecuteAsync("UPDATE public.player_game_stats SET competitive_tier = 12 WHERE user_id = @owner AND game_slug = 'valorant'", new { owner });
        var after = await (await viewer.GetAsync("/api/competitive/lfg?game=valorant&min_rank=3&max_rank=5")).Content.ReadFromJsonAsync<JsonElement>();
        after.EnumerateArray().Should().NotContain(row => row.GetProperty("id").GetGuid() == player);
        after.EnumerateArray().Should().Contain(row => row.GetProperty("id").GetGuid() == team);
    }

    [Fact]
    public async Task StaffRoles_CanBeEditedAndFilteredWithoutAcceptingUnknownRoles()
    {
        var (owner, client) = await User();
        var (_, viewer) = await User();
        var listing = await SeedListingAsync(owner, "valorant", status: "draft");
        var response = await client.PutAsJsonAsync($"/api/competitive/lfg/{listing}",
            new { preferred_roles = new[] { "Coach", "MANAGER" } });
        response.EnsureSuccessStatusCode();
        await using var conn = factory.Seeder.OpenConnection();
        var roles = await conn.QuerySingleAsync<string[]>("SELECT preferred_roles FROM public.lfg_listings WHERE id = @listing", new { listing });
        roles.Should().Equal("coach", "manager");
        await conn.ExecuteAsync("UPDATE public.lfg_listings SET status = 'published' WHERE id = @listing", new { listing });
        var filtered = await (await viewer.GetAsync("/api/competitive/lfg?game=valorant&preferred_roles=coach")).Content.ReadFromJsonAsync<JsonElement>();
        filtered.EnumerateArray().Should().Contain(item => item.GetProperty("id").GetGuid() == listing);
        var invalid = await client.PutAsJsonAsync($"/api/competitive/lfg/{listing}", new { preferred_roles = new[] { "invented" } });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GameStats_QueuesAndReturnsPending_ThenReturnsAgentStats()
    {
        var (user, _) = await User();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line) VALUES (@user, @puuid, 'Player', 'TAG')",
            new { user, puuid = Guid.NewGuid().ToString() });
        var jobs = Substitute.For<IBackgroundJobClient>();
        jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("job");
        // Calling the handler with a fake job client avoids starting a second Hangfire host.
        // Hangfire uses process-wide state, so disposing a second host can disrupt later tests.
        var context = new DefaultHttpContext();
        context.Items["UserContext"] = new UserContext { UserId = user.ToString() };
        var db = factory.Services.GetRequiredService<IDbConnectionFactory>();
        var first = await LfgEndpoints.GetMyGameStatsAsync("valorant", context, db, jobs, CancellationToken.None);
        var pending = JsonSerializer.SerializeToElement(((IValueHttpResult)first).Value);
        pending.GetProperty("sync_status").GetString().Should().Be("pending");
        jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        await conn.ExecuteAsync("""
            UPDATE public.player_game_stats SET sync_status = 'complete',
              popular_agents = '[{"agent_id":"jett","games_played":2,"wins":1,"kills":10,"deaths":5,"kd_ratio":2}]'::jsonb
            WHERE user_id = @user AND game_slug = 'valorant'
            """, new { user });
        var result = await LfgEndpoints.GetMyGameStatsAsync("valorant", context, db, jobs, CancellationToken.None);
        var completed = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
        completed.GetProperty("sync_status").GetString().Should().Be("complete");
        completed.GetProperty("popular_agents")[0].GetProperty("games_played").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Roster_UsesListingGameAndRespectsMemberStatsVisibility()
    {
        var (owner, _) = await User();
        var (member, _) = await User();
        var (_, viewer) = await User();
        var teamId = await factory.Seeder.SeedTeamAsync(owner);
        var listingId = await SeedListingAsync(owner, "valorant");
        var rosterId = Guid.NewGuid();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("""
            UPDATE public.lfg_listings SET team_id = @teamId, intent = 'looking_for_player' WHERE id = @listingId;
            INSERT INTO public.team_members (team_id, user_id, role) VALUES (@teamId, @member, 'member');
            INSERT INTO public.team_rosters (id, team_id, name, game, team_size)
                VALUES (@rosterId, @teamId, 'Valorant', 'valorant', 5),
                       (gen_random_uuid(), @teamId, 'Other game', 'cs2', 5);
            INSERT INTO public.team_roster_members (roster_id, user_id) VALUES (@rosterId, @owner), (@rosterId, @member);
            INSERT INTO public.player_game_profiles (user_id, game_slug, profile_visible) VALUES (@member, 'valorant', true);
            INSERT INTO public.riot_accounts (user_id, puuid, game_name, tag_line)
                VALUES (@owner, 'private', 'Owner', 'TAG'), (@member, 'public', 'Member', 'TAG') ON CONFLICT (user_id) DO UPDATE SET puuid = EXCLUDED.puuid;
            INSERT INTO public.player_game_stats (user_id, game_slug, provider_player_id, kd_ratio, win_rate)
                VALUES (@owner, 'valorant', 'private', 1.11, 44), (@member, 'valorant', 'public', 1.50, 60);
            """, new { teamId, listingId, rosterId, owner, member });
        var response = await viewer.GetAsync($"/api/competitive/lfg/{listingId}/roster");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("status").GetString().Should().Be("available");
        var members = result.GetProperty("members").EnumerateArray().ToArray();
        members.Should().HaveCount(2);
        var hidden = members.Single(item => item.GetProperty("userId").GetGuid() == owner);
        hidden.GetProperty("kdRatio").ValueKind.Should().Be(JsonValueKind.Null);
        var visible = members.Single(item => item.GetProperty("userId").GetGuid() == member);
        visible.GetProperty("kdRatio").GetDecimal().Should().Be(1.50m);
        visible.TryGetProperty("email", out _).Should().BeFalse();
        visible.TryGetProperty("riotPuuid", out _).Should().BeFalse();
        var playerListing = await SeedListingAsync(owner, "valorant");
        await conn.ExecuteAsync("UPDATE public.lfg_listings SET intent = 'looking_for_team' WHERE id = @playerListing", new { playerListing });
        var optedIn = await (await viewer.GetAsync($"/api/competitive/lfg/{listingId}/roster")).Content.ReadFromJsonAsync<JsonElement>();
        optedIn.GetProperty("members").EnumerateArray().Single(item => item.GetProperty("userId").GetGuid() == owner)
            .GetProperty("kdRatio").GetDecimal().Should().Be(1.11m);
        await conn.ExecuteAsync("UPDATE public.lfg_listings SET status = 'paused' WHERE id = @playerListing", new { playerListing });
        var afterPause = await (await viewer.GetAsync($"/api/competitive/lfg/{listingId}/roster")).Content.ReadFromJsonAsync<JsonElement>();
        afterPause.GetProperty("members").EnumerateArray().Single(item => item.GetProperty("userId").GetGuid() == owner)
            .GetProperty("kdRatio").ValueKind.Should().Be(JsonValueKind.Null);
        await conn.ExecuteAsync("INSERT INTO public.team_rosters (team_id, name, game, team_size) VALUES (@teamId, 'Duplicate', 'VALORANT', 5)", new { teamId });
        var duplicate = await (await viewer.GetAsync($"/api/competitive/lfg/{listingId}/roster")).Content.ReadFromJsonAsync<JsonElement>();
        duplicate.GetProperty("status").GetString().Should().Be("ambiguous");
        await conn.ExecuteAsync("UPDATE public.lfg_listings SET status = 'paused' WHERE id = @listingId", new { listingId });
        (await viewer.GetAsync($"/api/competitive/lfg/{listingId}/roster")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Catalog: GET /api/catalog/rank-tiers ─────────────────────────────────

    [Fact]
    public async Task CaptainTeams_DefaultsMissingOffset_AndReturnsOwnedTeam()
    {
        var (owner, client) = await User();
        var teamId = await factory.Seeder.SeedTeamAsync(owner);
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("DELETE FROM public.team_members WHERE team_id = @teamId AND user_id = @owner", new { teamId, owner });
        var response = await client.GetAsync("/api/teams/my-captain-teams?limit=50");
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        rows.EnumerateArray().Any(row => row.GetProperty("id").GetGuid() == teamId).Should().BeTrue();
    }

    [Fact]
    public async Task ValorantListing_RejectsMoreThanFiveOrDuplicateAgents()
    {
        var (_, client) = await User();
        var agents = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var tooMany = await client.PostAsJsonAsync("/api/competitive/lfg", new { game_slug = "valorant", intent = "looking_for_team", preferred_agents = agents });
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var duplicate = await client.PostAsJsonAsync("/api/competitive/lfg", new { game_slug = "valorant", intent = "looking_for_team", preferred_agents = new[] { agents[0], agents[0] } });
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ValorantListing_Update_PersistsFiveAgents()
    {
        var (owner, client) = await User();
        var listingId = await SeedListingAsync(owner, "valorant", status: "draft");
        var agents = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var response = await client.PutAsJsonAsync($"/api/competitive/lfg/{listingId}", new { preferred_agents = agents });
        response.EnsureSuccessStatusCode();
        await using var conn = factory.Seeder.OpenConnection();
        var stored = await conn.QuerySingleAsync<string[]>("SELECT preferred_agents FROM public.lfg_listings WHERE id = @listingId", new { listingId });
        stored.Should().Equal(agents);
    }

    [Fact]
    public async Task ListingAndInterests_ReturnProfileAvatarFields()
    {
        var (owner, ownerClient) = await User();
        var (sender, senderClient) = await User();
        await using var conn = factory.Seeder.OpenConnection();
        await conn.ExecuteAsync("UPDATE public.profiles SET avatar_url = @url, banner_url = @banner, avatar_style = @style WHERE id IN (@owner, @sender)",
            new { url = "https://example.com/avatar.png", banner = "https://example.com/banner.png", style = "critters", owner, sender });
        var listingId = await SeedListingAsync(owner, "valorant");
        await SeedInterestAsync(listingId, sender, owner);

        var detailResponse = await senderClient.GetAsync($"/api/competitive/lfg/{listingId}");
        detailResponse.EnsureSuccessStatusCode();
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("profileAvatarUrl").GetString().Should().Be("https://example.com/avatar.png");
        detail.GetProperty("profileBannerUrl").GetString().Should().Be("https://example.com/banner.png");
        detail.GetProperty("avatarStyle").GetString().Should().Be("critters");
        var discovery = await (await senderClient.GetAsync("/api/competitive/lfg?game=valorant&limit=50")).Content.ReadFromJsonAsync<JsonElement>();
        discovery.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == listingId).GetProperty("profileBannerUrl").GetString().Should().Be("https://example.com/banner.png");
        var mine = await (await ownerClient.GetAsync("/api/competitive/lfg/mine")).Content.ReadFromJsonAsync<JsonElement>();
        mine.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == listingId).GetProperty("profileBannerUrl").GetString().Should().Be("https://example.com/banner.png");
        (await senderClient.PutAsJsonAsync($"/api/competitive/lfg/{listingId}/saved", new { })).EnsureSuccessStatusCode();
        var saved = await (await senderClient.GetAsync("/api/competitive/lfg/saved?game=valorant")).Content.ReadFromJsonAsync<JsonElement>();
        saved.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == listingId).GetProperty("profileBannerUrl").GetString().Should().Be("https://example.com/banner.png");
        var interestsResponse = await ownerClient.GetAsync($"/api/competitive/lfg/{listingId}/interests");
        interestsResponse.EnsureSuccessStatusCode();
        var interests = await interestsResponse.Content.ReadFromJsonAsync<JsonElement>();
        interests[0].GetProperty("profileAvatarUrl").GetString().Should().Be("https://example.com/avatar.png");
        interests[0].GetProperty("avatarStyle").GetString().Should().Be("critters");
    }

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
    public async Task Interest_ExpressRetryWithdrawAndReinvite_PreservesPartiesAndRetiresOldActions()
    {
        var (owner, ownerClient) = await User();
        var (sender, client) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.PostAsJsonAsync($"/api/competitive/lfg/{listing}/interest", new { message = "Hello" })));
        foreach (var response in responses) response.EnsureSuccessStatusCode();
        var first = (await responses[0].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await responses[1].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid().Should().Be(first);
        await using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.notifications WHERE user_id = @owner AND data->>'interest_id' = @interest", new { owner, interest = first.ToString() })).Should().Be(1);
        (await client.DeleteAsync($"/api/competitive/lfg/{listing}/interest")).EnsureSuccessStatusCode();
        var again = await client.PostAsJsonAsync($"/api/competitive/lfg/{listing}/interest", new { message = "Again" });
        again.EnsureSuccessStatusCode();
        var second = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        second.Should().NotBe(first);
        (await ownerClient.PostAsync($"/api/competitive/lfg/interests/{first}/accept", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.notifications WHERE user_id = @owner AND data->>'interest_id' = @interest", new { owner, interest = first.ToString() })).Should().Be(0);
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.notifications WHERE user_id = @owner AND data->>'interest_id' = @interest", new { owner, interest = second.ToString() })).Should().Be(1);
        var detail = await (await client.GetAsync($"/api/competitive/lfg/{listing}")).Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("viewerInterestId").GetGuid().Should().Be(second);
        detail.GetProperty("viewerInterestStatus").GetString().Should().Be("expressed");
        (await ownerClient.PostAsync($"/api/competitive/lfg/interests/{second}/decline", null)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/competitive/lfg/{listing}/interest", new { message = "Third" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Interest_RetryRepairsMissingNotificationForExistingRequest()
    {
        var (owner, _) = await User();
        var (sender, client) = await User();
        var listing = await SeedListingAsync(owner, "valorant");
        var interest = await SeedInterestAsync(listing, sender, owner);
        var response = await client.PostAsJsonAsync($"/api/competitive/lfg/{listing}/interest", new { message = "Retry" });
        response.EnsureSuccessStatusCode();
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid().Should().Be(interest);
        await using var conn = factory.Seeder.OpenConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM public.notifications WHERE user_id = @owner AND data->>'interest_id' = @interest", new { owner, interest = interest.ToString() })).Should().Be(1);
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
        body.GetProperty("isSaved").GetBoolean().Should().BeTrue();
        body.GetProperty("viewerInterestId").GetGuid().Should().Be(interest);
        body.GetProperty("viewerInterestStatus").GetString().Should().Be("expressed");
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
